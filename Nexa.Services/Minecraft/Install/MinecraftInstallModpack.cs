using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Nexa.Services.Downloads;
using Nexa.Services.Files;
using Nexa.Services.Tasks;
using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Install;

public sealed partial class MinecraftInstallService
{
    public Task<XsrResult<MinecraftInstallResult>> InstallModpackAsync(MinecraftModpackCommand command, CancellationToken token = default) =>
        Task.Run(() => InstallModpackCoreAsync(command, token), token);

    private async Task<XsrResult<MinecraftInstallResult>> InstallModpackCoreAsync(MinecraftModpackCommand command, CancellationToken token)
    {
        using var task = _tasks.Begin(new("modpack:" + Guid.NewGuid().ToString("N"), "安装 " + command.Pack.Name, StagePlan));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, task.CancellationToken);
        token = linked.Token;
        string? stage = null;
        try
        {
            string root = MinecraftLibraryService.NormalizeDirectory(command.RootDirectory);
            using var recoveryOperation = await Management.InstanceRecoveryOperationGate.EnterOperationAsync(root, token).ConfigureAwait(false);
            MinecraftModpackArchive.CheckPath(root);
            stage = ForgeInstallService.Contained(root, ".nexa-pack-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            string archivePath = Path.Combine(stage, "source.pack");
            MinecraftModpackArchive.CheckPath(command.Pack.Path);
            if (new FileInfo(command.Pack.Path).Length > MinecraftModpackArchive.MaxArchive) throw new InvalidDataException("整合包过大。");
            await using (var input = File.OpenRead(command.Pack.Path))
            await using (var output = File.Create(archivePath))
                await ArchiveReadBudget.CopyAsync(input, output, input.Length, MinecraftModpackArchive.MaxArchive,
                    new ArchiveReadBudget(MinecraftModpackArchive.MaxArchive), token).ConfigureAwait(false);
            var plan = await MinecraftModpackArchive.ReadAsync(archivePath, token).ConfigureAwait(false);
            if (plan.Preview != command.Pack with { Path = archivePath }) throw new InvalidDataException("整合包已变化，请重新拖入。");
            var pack = plan.Preview;
            string destination = ForgeInstallService.Contained(root, "versions/" + pack.InstanceId);
            if (Path.Exists(destination)) throw new IOException("此整合包版本已经存在，未覆盖任何文件。");
            string buildRoot = Path.Combine(stage, "game");
            Directory.CreateDirectory(buildRoot);
            var files = plan.Files.Where(file => command.IncludeOptional || !file.Optional).ToList();
            foreach (var reference in plan.CurseFiles.Where(file => command.IncludeOptional || !file.Optional))
                files.Add(await ResolveCurseForgePackFileAsync(reference, token).ConfigureAwait(false));
            if (files.Sum(file => file.Size) > MinecraftModpackArchive.MaxExpanded
                || files.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
                throw new InvalidDataException("整合包包含重复文件或总大小超过限制。");
            foreach (var file in files) ValidatePackDestination(file.Path, pack.InstanceId);
            // Reuse the same manifest, Java/loader installer, libraries, assets and download pipeline.
            var installed = await RunAsync(new(buildRoot, pack.Game, pack.Loader, pack.Build, InstanceName: pack.InstanceId)
            { PreparingEdit = true, ReuseRoot = root, ModsRelativeDirectory = "versions/" + pack.InstanceId + "/mods" }, task, token).ConfigureAwait(false);
            using var packHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
            int completed = 0;
            var expandedBudget = new ArchiveReadBudget(MinecraftModpackArchive.MaxExpanded);
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                string target = ForgeInstallService.Contained(installed.InstanceDirectory, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                bool verified = false;
                foreach (string url in file.Urls)
                {
                    TryDelete(target);
                    var request = new DownloadRequest
                    {
                        Sources = [url],
                        DestinationPath = target,
                        ConnectionFactory = source => new PackSizeConnection(_connectionFactory is { } factory ? factory(source) : new PackHttpConnection(packHttp, source), file.Size),
                    };
                    var transfer = await _downloads.DownloadAsync(request, cancellationToken: token).ConfigureAwait(false);
                    if (transfer.Success && await VerifyPackFileAsync(target, file, token).ConfigureAwait(false)) { verified = true; break; }
                }
                if (!verified) throw new IOException("整合包文件下载或校验失败：" + file.Path);
                expandedBudget.Consume(new FileInfo(target).Length);
                completed++;
                task.Report("附加组件", file.Path, (double)completed / Math.Max(1, files.Count), completed, files.Count, 0);
            }
            using (var archive = ZipFile.OpenRead(archivePath))
                foreach (string prefix in plan.OverrideRoots)
                    foreach (var entry in archive.Entries.Where(entry => entry.Name.Length > 0 && entry.FullName.StartsWith(prefix, StringComparison.Ordinal)))
                    {
                        token.ThrowIfCancellationRequested();
                        string relative = entry.FullName[prefix.Length..];
                        ValidatePackDestination(relative, pack.InstanceId);
                        string target = ForgeInstallService.Contained(installed.InstanceDirectory, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        await using var input = entry.Open();
                        await using var output = File.Create(target);
                        await ArchiveReadBudget.CopyAsync(input, output, entry.Length, MinecraftModpackArchive.MaxFile,
                            expandedBudget, token).ConfigureAwait(false);
                    }
            await new MinecraftInstanceMetadataStore().SaveAsync(installed.InstanceDirectory, new()
            { InstanceIsolation = true, Description = pack.Name, ModpackVersion = pack.Version }, token).ConfigureAwait(false);
            // Shared cache additions are safe to keep on failure; the new instance commits last.
            foreach (string file in Directory.EnumerateFiles(buildRoot, "*", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                if (file.StartsWith(installed.InstanceDirectory + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
                string relative = Path.GetRelativePath(buildRoot, file);
                string target = ForgeInstallService.Contained(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                MinecraftModpackArchive.CheckPath(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    MinecraftModpackArchive.CheckPath(target);
                    if (!await SameFileAsync(file, target, token).ConfigureAwait(false)) throw new IOException("共享文件与整合包依赖冲突，未覆盖：" + relative);
                    continue;
                }
                string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await using (var input = File.OpenRead(file))
                    await using (var output = File.Create(temporary)) await input.CopyToAsync(output, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    File.Move(temporary, target);
                }
                finally { TryDelete(temporary); }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            MinecraftModpackArchive.CheckPath(root);
            MinecraftModpackArchive.CheckPath(Path.GetDirectoryName(destination)!);
            token.ThrowIfCancellationRequested();
            Directory.Move(installed.InstanceDirectory, destination);
            task.Complete("整合包已安装");
            Installed?.Invoke(root);
            return XsrResult.Success(new MinecraftInstallResult(pack.InstanceId, destination));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { task.Canceled(); return XsrResult.Failure<MinecraftInstallResult>(XsrRuntimeErrors.Cancelled()); }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        { task.Fail(error.Message); return XsrResult.Failure<MinecraftInstallResult>(MinecraftErrors.InvalidRequest(error.Message)); }
        finally
        {
            if (stage is not null && Directory.Exists(stage))
                try { MinecraftModpackArchive.CheckPath(stage); Directory.Delete(stage, true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private async Task<JsonNode> ReadCurseForgePackDataAsync(string path, CancellationToken token)
    {
        string? key = Environment.GetEnvironmentVariable("Nexa_CURSEFORGE_API_KEY") ?? Environment.GetEnvironmentVariable("CURSEFORGE_API_KEY");
        JsonObject? document = null;
        foreach (string endpoint in string.IsNullOrWhiteSpace(key) ? new[] { "https://mod.mcimirror.top/curseforge/v1" } : ["https://api.curseforge.com/v1", "https://mod.mcimirror.top/curseforge/v1"])
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint + path);
                if (endpoint == "https://api.curseforge.com/v1") request.Headers.Add("x-api-key", key);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                byte[] bytes = new byte[8192];
                int read;
                while ((read = await source.ReadAsync(bytes, token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > 4 * 1024 * 1024) throw new InvalidDataException("CurseForge 响应过大。");
                    buffer.Write(bytes, 0, read);
                }
                document = JsonNode.Parse(buffer.ToArray()) as JsonObject;
                break;
            }
            catch (Exception error) when (error is HttpRequestException or IOException || (error is OperationCanceledException && !token.IsCancellationRequested)) { }
        }
        return document?["data"] ?? throw new InvalidDataException("无法获取 CurseForge 文件信息。");
    }

    private async Task<ModpackFile> ResolveCurseForgePackFileAsync(CurseForgePackFile reference, CancellationToken token)
    {
        var file = await ReadCurseForgePackDataAsync($"/mods/{reference.ProjectId}/files/{reference.FileId}", token).ConfigureAwait(false);
        if (file?["id"]?.GetValue<long>() != reference.FileId || file?["modId"]?.GetValue<long>() != reference.ProjectId)
            throw new InvalidDataException("无法获取对应的 CurseForge 文件信息。");
        string name = MinecraftModpackArchive.SafeRelative(file["fileName"]?.GetValue<string>() ?? "");
        if (name.Contains('/')) throw new InvalidDataException("CurseForge 文件名无效。");
        string? rawUrl = file["downloadUrl"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(rawUrl)) throw new InvalidDataException("作者未提供第三方下载地址，请在 CurseForge 获取此文件：" + name);
        string url = MinecraftModpackArchive.DownloadUrl(rawUrl);
        long size = file["fileLength"]?.GetValue<long>() ?? -1;
        if (size < 0 || size > MinecraftModpackArchive.MaxFile) throw new InvalidDataException("CurseForge 文件大小无效。");
        string? sha = (file["hashes"] as JsonArray ?? []).FirstOrDefault(item => item?["algo"]?.GetValue<int>() == 1)?["value"]?.GetValue<string>();
        var project = await ReadCurseForgePackDataAsync($"/mods/{reference.ProjectId}", token).ConfigureAwait(false);
        if (project["id"]?.GetValue<long>() != reference.ProjectId || file["isAvailable"]?.GetValue<bool>() == false)
            throw new InvalidDataException("CurseForge 项目不匹配或文件不可用。");
        string folder = project["classId"]?.GetValue<int>() switch
        {
            6 => "mods",
            12 => "resourcepacks",
            6552 => "shaderpacks",
            _ => throw new InvalidDataException("整合包引用了尚不支持的 CurseForge 内容类型。"),
        };
        return new(folder + "/" + name, [url], size, MinecraftModpackArchive.RequireHash(sha, 40), null, reference.Optional);
    }

    private static void ValidatePackDestination(string relative, string instance)
    {
        MinecraftModpackArchive.SafeRelative(relative);
        string first = relative.Split('/')[0];
        if (new[] { "Nexa", "PCL", "versions", "libraries", "runtime", instance + ".json", instance + ".jar" }.Contains(first, StringComparer.OrdinalIgnoreCase)
            || first.StartsWith(".nexa", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("整合包试图覆盖启动器管理文件。");
    }
    private static async Task<bool> VerifyPackFileAsync(string path, ModpackFile file, CancellationToken token)
    {
        if (new FileInfo(path).Length != file.Size) return false;
        await using var stream = File.OpenRead(path);
#pragma warning disable CA5350 // Published pack integrity requires SHA-1 in addition to SHA-512.
        if (!Convert.ToHexString(await SHA1.HashDataAsync(stream, token).ConfigureAwait(false)).Equals(file.Sha1, StringComparison.OrdinalIgnoreCase)) return false;
#pragma warning restore CA5350
        if (file.Sha512 is null) return true;
        stream.Position = 0;
        return Convert.ToHexString(await SHA512.HashDataAsync(stream, token).ConfigureAwait(false)).Equals(file.Sha512, StringComparison.OrdinalIgnoreCase);
    }
    private static async Task<bool> SameFileAsync(string left, string right, CancellationToken token)
    {
        if (new FileInfo(left).Length != new FileInfo(right).Length) return false;
        await using var a = File.OpenRead(left);
        await using var b = File.OpenRead(right);
        byte[] hash = await SHA256.HashDataAsync(a, token).ConfigureAwait(false);
        byte[] other = await SHA256.HashDataAsync(b, token).ConfigureAwait(false);
        return hash.AsSpan().SequenceEqual(other);
    }
    private sealed class PackSizeConnection(IDownloadConnection inner, long size) : IDownloadConnection
    {
        private long _received;
        public async ValueTask<DownloadConnectionInfo> StartAsync(long beginOffset, CancellationToken cancellationToken = default)
        {
            var info = await inner.StartAsync(beginOffset, cancellationToken).ConfigureAwait(false);
            _received = info.BeginOffset;
            if (info.Length > size) throw new InvalidDataException("下载大小超过整合包声明。");
            return info;
        }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if ((_received += read) > size) throw new InvalidDataException("下载内容超过整合包声明。");
            return read;
        }
        public ValueTask StopAsync(CancellationToken cancellationToken = default) => inner.StopAsync(cancellationToken);
    }
    private sealed class PackHttpConnection(HttpClient client, string url) : IDownloadConnection
    {
        private HttpResponseMessage? _response;
        public async ValueTask<DownloadConnectionInfo> StartAsync(long beginOffset, CancellationToken cancellationToken = default)
        {
            string current = url;
            for (int redirects = 0; redirects <= 5; redirects++)
            {
                current = MinecraftModpackArchive.DownloadUrl(current);
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                // Downloads use a fresh staging file; do not advertise unsupported range resume.
                _response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if ((int)_response.StatusCode is >= 300 and < 400 && _response.Headers.Location is { } next)
                {
                    current = new Uri(new Uri(current), next).AbsoluteUri;
                    _response.Dispose(); _response = null;
                    continue;
                }
                _response.EnsureSuccessStatusCode();
                long length = _response.Content.Headers.ContentLength ?? -1;
                return new(length, 0, length - 1, false);
            }
            throw new IOException("下载重定向次数过多。");
        }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            await (await _response!.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false)).ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        public ValueTask StopAsync(CancellationToken cancellationToken = default) { _response?.Dispose(); _response = null; return ValueTask.CompletedTask; }
    }
}

using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Nexa.Services.Downloads;
using Nexa.Services.Minecraft.Downloads;
using Nexa.Services.Minecraft.Java;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Libraries;

namespace Nexa.Services.Minecraft.Install;

public sealed record MinecraftLoaderInstallRequest(string Root, string Game, string InstanceId,
    InstallLoader Loader, string Build, JsonObject VanillaJson)
{
    public LocalJarArtifact? LocalInstaller { get; init; }
}

public interface IMinecraftLoaderInstaller
{
    Task<JsonObject> InstallAsync(MinecraftLoaderInstallRequest request, IProgress<string>? progress, CancellationToken token);
}

/// <summary>Runs the official loader installer in an isolated root, then promotes only loader artifacts.</summary>
public sealed partial class ForgeInstallService(DownloadService downloads, HttpClient http,
    Func<string, IDownloadConnection>? connectionFactory = null,
    Func<MinecraftLoaderInstallRequest, CancellationToken, Task<string>>? resolveJava = null,
    Func<ProcessStartInfo, CancellationToken, Task>? runProcess = null) : IMinecraftLoaderInstaller
{
    public static string InstallerUrl(InstallLoader loader, string game, string build)
    {
        if (loader is not (InstallLoader.Forge or InstallLoader.NeoForge or InstallLoader.Cleanroom or InstallLoader.OptiFine)) throw new ArgumentException("Unsupported installer.");
        if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(build)
            || !(game + build).All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
            throw new InvalidDataException("加载器版本标识无效。");
        if (loader == InstallLoader.Cleanroom && game != "1.12.2") throw new InvalidDataException("Cleanroom 仅支持 Minecraft 1.12.2。");
        if (loader == InstallLoader.Cleanroom)
            return $"https://github.com/CleanroomMC/Cleanroom/releases/download/{build}/cleanroom-{build}-installer.jar";
        if (loader == InstallLoader.OptiFine) return OptiFineUrl(game, build);
        bool forge = loader == InstallLoader.Forge;
        string artifact = forge || game == "1.20.1" ? "forge" : "neoforge";
        string version = forge ? game.Replace('-', '_') + "-" + build : game == "1.20.1" ? game + "-" + build : build;
        return (forge ? "https://maven.minecraftforge.net/net/minecraftforge/" : "https://maven.neoforged.net/releases/net/neoforged/")
            + $"{artifact}/{version}/{artifact}-{version}-installer.jar";
    }

    public async Task<JsonObject> InstallAsync(MinecraftLoaderInstallRequest request, IProgress<string>? progress, CancellationToken token)
    {
        string url = InstallerUrl(request.Loader, request.Game, request.Build);
        string root = Path.GetFullPath(request.Root);
        string stage = Path.Combine(root, ".nexa-install", Guid.NewGuid().ToString("N"));
        _ = Contained(root, Path.GetRelativePath(root, Path.Combine(stage, "installer.jar")));
        Directory.CreateDirectory(stage);
        try
        {
            string installer = Path.Combine(stage, "installer.jar");
            using var installerCache = (File.Exists(Path.Combine(root, InstallTaskJournal.DirectoryName, "plan.json"))
                || Path.GetFileName(root) == "game" && Directory.GetParent(root)?.Parent?.Name == ModpackInstallJournal.DirectoryName
                    && File.Exists(Path.Combine(Directory.GetParent(root)!.FullName, "intent.json")))
                ? new LoaderInstallerCache(root, request) : null;
            if (installerCache is null || !await installerCache.RestoreAsync(installer, token).ConfigureAwait(false))
            {
                string? sha = null;
                string? sha256 = null;
                if (request.LocalInstaller is { } local)
                {
                    if (local.Loader != request.Loader || local.Game != request.Game || local.Build != request.Build)
                        throw new InvalidDataException("本地安装器与请求的版本不一致。");
                    progress?.Report("正在读取本地安装器");
                    await MinecraftLocalJarService.CopyVerifiedAsync(local, installer, token).ConfigureAwait(false);
                }
                else
                {
                    if (request.Loader == InstallLoader.Cleanroom)
                        sha256 = await CleanroomDigestAsync(request.Build, token).ConfigureAwait(false);
                    else if (request.Loader != InstallLoader.OptiFine)
                    {
                        sha = (await http.GetStringAsync(url + ".sha1", token).ConfigureAwait(false)).Trim().Split(' ', '\t', '\r', '\n')[0];
                        if (sha.Length != 40 || !sha.All(char.IsAsciiHexDigit)) throw new InvalidDataException("安装器校验信息无效。");
                    }
                    progress?.Report("正在下载安装器");
                    await TransferAsync(url, installer, sha, 0, token).ConfigureAwait(false);
                }
                if (sha256 is not null)
                {
                    await using var input = File.OpenRead(installer);
                    string actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(input, token).ConfigureAwait(false));
                    if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Cleanroom 安装器校验失败。");
                }
                if (installerCache is not null) await installerCache.SaveAsync(installer, token).ConfigureAwait(false);
            }
            string gameDir = Contained(stage, "versions/" + request.Game);
            Directory.CreateDirectory(gameDir);
            await File.WriteAllTextAsync(Path.Combine(gameDir, request.Game + ".json"), request.VanillaJson.ToJsonString(), token).ConfigureAwait(false);
            File.Copy(Contained(root, "versions/" + request.Game + "/" + request.Game + ".jar"), Path.Combine(gameDir, request.Game + ".jar"));
            await File.WriteAllTextAsync(Path.Combine(stage, "launcher_profiles.json"), "{\"profiles\":{}}", token).ConfigureAwait(false);

            JsonObject profile = new();
            JsonObject version;
            if (request.Loader == InstallLoader.OptiFine)
                version = await InstallOptiFineAsync(request, stage, installer, progress, token).ConfigureAwait(false);
            else
                using (var archive = ZipFile.OpenRead(installer))
                {
                    profile = ReadJson(archive, "install_profile.json");
                    version = profile["versionInfo"] is JsonObject legacy ? (JsonObject)legacy.DeepClone()
                        : ReadJson(archive, profile["json"]?.ToString().TrimStart('/') ?? "version.json");
                    foreach (var entry in archive.Entries.Where(entry => entry.FullName.StartsWith("maven/", StringComparison.Ordinal) && entry.Name.Length > 0))
                    {
                        token.ThrowIfCancellationRequested();
                        if (entry.Length > 512 * 1024 * 1024) throw new InvalidDataException("安装器内嵌文件过大。");
                        string target = Contained(stage, "libraries/" + entry.FullName[6..]);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        entry.ExtractToFile(target, overwrite: true);
                    }
                    // Older Forge installers contain a declarative version and an embedded universal jar.
                    if (profile["versionInfo"] is JsonObject && profile["install"] is JsonObject install)
                    {
                        string coordinate = install["path"]?.ToString() ?? throw new InvalidDataException("缺少 Forge Maven 坐标。");
                        string target = MinecraftLibraryResolver.GetCoordinatePath(coordinate, stage);
                        var entry = archive.GetEntry(install["filePath"]?.ToString() ?? "") ?? throw new InvalidDataException("缺少 Forge 内嵌文件。");
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!); entry.ExtractToFile(target, true);
                    }
                }
            progress?.Report("正在准备安装器依赖");
            await PrefetchAsync(profile, root, stage, token).ConfigureAwait(false);
            await PrefetchAsync(version, root, stage, token).ConfigureAwait(false);
            if (request.Loader != InstallLoader.OptiFine && profile["versionInfo"] is not JsonObject)
            {
                var javaRequest = request.Loader == InstallLoader.Cleanroom
                    ? request with { VanillaJson = new JsonObject { ["javaVersion"] = new JsonObject { ["majorVersion"] = 17 } } }
                    : request;
                string java = await (resolveJava ?? ResolveJavaAsync)(javaRequest, token).ConfigureAwait(false);
                progress?.Report("正在运行加载器安装器");
                var start = new ProcessStartInfo(java)
                {
                    WorkingDirectory = stage,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (string arg in new[] { "-Djava.awt.headless=true", "-jar", installer, "--installClient", stage }) start.ArgumentList.Add(arg);
                await (runProcess ?? RunAsync)(start, token).ConfigureAwait(false);
                string id = version["id"]?.ToString() ?? profile["version"]?.ToString() ?? "";
                if (!MinecraftVersionPaths.IsSafeReference(id)) throw new InvalidDataException("安装器版本标识无效。");
                version = await MinecraftVersionJsonReader.ReadAsync(Contained(stage, $"versions/{id}/{id}.json"), token).ConfigureAwait(false);
            }
            if (string.IsNullOrWhiteSpace(version["mainClass"]?.ToString())) throw new InvalidDataException("安装器没有生成启动入口。");
            if (version["inheritsFrom"] is { } parent && parent.ToString() != request.Game) throw new InvalidDataException("安装器生成了不同 Minecraft 版本的配置。");
            await PrefetchAsync(version, root, stage, token).ConfigureAwait(false);
            // Every runtime artifact, including processor outputs with empty download URLs, must exist.
            foreach (var library in Libraries(version, stage))
                if (!await MinecraftFileVerifier.VerifyAsync(new(library.LocalPath, library.Size > 0 ? library.Size : null, library.Sha1), token).ConfigureAwait(false))
                    throw new InvalidDataException("加载器文件未生成或校验失败：" + library.OriginalName);
            token.ThrowIfCancellationRequested();
            string libraryRoot = Path.Combine(stage, "libraries");
            if (Directory.Exists(libraryRoot))
                foreach (string file in Directory.EnumerateFiles(libraryRoot, "*", SearchOption.AllDirectories))
                {
                    token.ThrowIfCancellationRequested();
                    string target = Contained(root, "libraries/" + Path.GetRelativePath(libraryRoot, file).Replace('\\', '/'));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, overwrite: true);
                }
            return version;
        }
        finally
        {
            // This exact GUID directory is owned by this invocation; user version folders are never removed.
            try { Directory.Delete(stage, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task PrefetchAsync(JsonObject document, string root, string stage, CancellationToken token)
    {
        foreach (var library in Libraries(document, stage))
        {
            if (await MinecraftFileVerifier.VerifyAsync(new(library.LocalPath, library.Size > 0 ? library.Size : null, library.Sha1), token).ConfigureAwait(false)) continue;
            string original = Contained(root, Path.GetRelativePath(stage, library.LocalPath).Replace('\\', '/'));
            if (await MinecraftFileVerifier.VerifyAsync(new(original, library.Size > 0 ? library.Size : null, library.Sha1), token).ConfigureAwait(false))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(library.LocalPath)!);
                File.Copy(original, library.LocalPath, true);
            }
            else if (!string.IsNullOrWhiteSpace(library.Url))
                await TransferAsync(library.Url, library.LocalPath, library.Sha1, library.Size, token).ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<MinecraftLibraryToken> Libraries(JsonObject document, string root)
    {
        var platform = MinecraftLaunchPlatform.Detect();
        return MinecraftLibraryResolver.Resolve(new()
        {
            VersionJson = document,
            MinecraftRootDirectory = root,
            OperatingSystem = platform.OperatingSystem,
            Is64BitArchitecture = platform.Is64BitArchitecture,
            IsArm64Architecture = platform.IsArm64Architecture,
            OperatingSystemVersion = platform.OperatingSystemVersion,
        });
    }

    private async Task TransferAsync(string url, string path, string? sha, long size, CancellationToken token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") throw new InvalidDataException("安装器依赖必须使用 HTTPS。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sources = Nexa.Services.Minecraft.Downloads.MinecraftDownloadSourcePlanner.GetLibrarySources(url, true).Append(url).Distinct(StringComparer.Ordinal).ToArray();
        var result = await downloads.DownloadAsync(new DownloadRequest
        {
            Sources = sources,
            DestinationPath = path,
            ConnectionFactory = source => connectionFactory?.Invoke(source) ?? new MinecraftInstallService.HttpConnection(http, source),
        }, cancellationToken: token).ConfigureAwait(false);
        if (!result.Success || !await MinecraftFileVerifier.VerifyAsync(new(path, size > 0 ? size : null, sha), token).ConfigureAwait(false))
            throw new IOException("加载器文件下载或校验失败：" + Path.GetFileName(path));
    }

    private static JsonObject ReadJson(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidDataException("安装器缺少 " + name);
        if (entry.Length > 8 * 1024 * 1024) throw new InvalidDataException("安装器清单过大。");
        using var reader = new StreamReader(entry.Open());
        return JsonNode.Parse(reader.ReadToEnd()) as JsonObject ?? throw new InvalidDataException("安装器清单无效。");
    }

    internal static string Contained(string root, string relative)
    {
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidDataException("安装器路径超出工作目录。");
        for (string? parent = Path.GetDirectoryName(full); parent is not null && parent.Length >= prefix.Length; parent = Path.GetDirectoryName(parent))
            if (Directory.Exists(parent) && File.GetAttributes(parent).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("安装目录不能包含链接。");
        return full;
    }

    private static async Task<string> ResolveJavaAsync(MinecraftLoaderInstallRequest request, CancellationToken token)
    {
        int major = request.VanillaJson["javaVersion"]?["majorVersion"]?.GetValue<int>() ?? 8;
        var requirement = JavaRequirementResolution.Valid(JavaVersionRange.ForMajor(major), major.ToString(System.Globalization.CultureInfo.InvariantCulture));
        string runtime = Path.Combine(request.Root, "runtime");
        var selected = await new JavaSelectionService(new LocalJavaRuntimeLocator(runtime)).SelectAsync(requirement, new AutoSelectJavaPreference(), token).ConfigureAwait(false);
        if (selected.SelectedJava is { } candidate) return candidate.Installation.JavaExecutablePath;
        using var metadata = new HttpJavaRuntimeMetadataProvider();
        using var installer = new JavaRuntimeInstaller(metadata);
        string java = await installer.InstallAsync(requirement.RecommendedComponent!, runtime, cancellationToken: token).ConfigureAwait(false);
        string console = Path.Combine(Path.GetDirectoryName(java)!, OperatingSystem.IsWindows() ? "java.exe" : "java");
        return File.Exists(console) ? console : java;
    }

    private static async Task RunAsync(ProcessStartInfo start, CancellationToken token)
    {
        using var process = new System.Diagnostics.Process { StartInfo = Nexa.Services.Processes.OwnedInstallerProcess.WorkerStartInfo() };
        if (!process.Start()) throw new IOException("无法启动加载器安装器。");
        try { await Nexa.Services.Processes.OwnedInstallerProcess.WriteRequestAsync(process.StandardInput.BaseStream, start, token).ConfigureAwait(false); }
        catch { process.StandardInput.Close(); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); throw; }
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(token);
        var output = new Queue<string>();
        async Task Drain(StreamReader reader)
        {
            char[] buffer = new char[2048];
            while (await reader.ReadAsync(buffer, reading.Token).ConfigureAwait(false) is int count && count > 0)
                lock (output) { output.Enqueue(new string(buffer, 0, count)); if (output.Count > 16) output.Dequeue(); }
        }
        var drains = Task.WhenAll(Drain(process.StandardOutput), Drain(process.StandardError));
        try { await process.WaitForExitAsync(token).ConfigureAwait(false); await drains.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false); }
        catch
        {
            reading.Cancel();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await drains.ConfigureAwait(false); } catch (OperationCanceledException) { }
            throw;
        }
        if (process.ExitCode != 0) throw new IOException("加载器安装器失败（" + process.ExitCode + "）：" + Nexa.Services.Logging.LogRedactor.Redact(string.Concat(output)));
    }
}

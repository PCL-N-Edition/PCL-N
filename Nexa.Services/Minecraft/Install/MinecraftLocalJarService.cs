using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Nexa.Services.Files;
using Nexa.Services.Minecraft.Process;
using Nexa.Services.Tasks;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Minecraft.Install;

public enum LocalJarAction { Mod, CorePatch, Loader }
public sealed record LocalJarArtifact(string Path, string Sha256, string? Game = null, InstallLoader? Loader = null, string? Build = null);
public sealed record MinecraftLocalJarCommand(LocalJarArtifact Artifact, string Root, string InstanceId, LocalJarAction Action);
public static class MinecraftLocalJarContract
{
    public static readonly XsrSemanticId Import = XsrSemanticId.Parse("minecraft.jar.import");
}

public sealed class MinecraftLocalJarService(TaskCenterService tasks, XsrStateStore store, MinecraftInstallService installer)
{
    private int _mutating;
    private readonly MinecraftInstanceMetadataStore _metadata = new();

    public static Task<LocalJarArtifact> InspectAsync(string path, CancellationToken token = default) => Task.Run(async () =>
    {
        path = Path.GetFullPath(path);
        CheckPath(path);
        await using var stream = OpenArchive(path);
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        ValidateArchive(archive);
        string? game = null, build = null;
        InstallLoader? loader = null;
        if (archive.GetEntry("install_profile.json") is { } profileEntry)
        {
            JsonObject profile = await ReadJsonAsync(profileEntry, token).ConfigureAwait(false);
            JsonObject? version = profile["versionInfo"] as JsonObject;
            if (version is null && archive.GetEntry(profile["json"]?.ToString().TrimStart('/') ?? "version.json") is { } versionEntry)
                version = await ReadJsonAsync(versionEntry, token).ConfigureAwait(false);
            game = profile["minecraft"]?.ToString() ?? profile["install"]?["minecraft"]?.ToString() ?? version?["inheritsFrom"]?.ToString();
            string? coordinate = profile["path"]?.ToString() ?? profile["install"]?["path"]?.ToString();
            var parts = coordinate?.Split(':');
            if (parts is { Length: 3 })
            {
                loader = (parts[0], parts[1]) switch
                {
                    ("net.minecraftforge", "forge") => InstallLoader.Forge,
                    ("net.neoforged", "neoforge" or "forge") => InstallLoader.NeoForge,
                    ("com.cleanroommc", "cleanroom") => InstallLoader.Cleanroom,
                    _ => null,
                };
                build = parts[2];
                if (game is not null && build.StartsWith(game + "-", StringComparison.Ordinal)) build = build[(game.Length + 1)..];
            }
        }
        if (loader is null && archive.GetEntry("optifine/Installer.class") is not null
            && (archive.GetEntry("net/optifine/Config.class") ?? archive.GetEntry("Config.class")) is { } config)
        {
            await using var input = config.Open();
            using var buffer = new MemoryStream();
            await ArchiveReadBudget.CopyAsync(input, buffer, config.Length, 4 * 1024 * 1024,
                new ArchiveReadBudget(4 * 1024 * 1024), token).ConfigureAwait(false);
            if (OptiFineInstallerMetadata.Read(buffer.ToArray()) is { } metadata)
            {
                game = metadata.Game; build = metadata.Build; loader = InstallLoader.OptiFine;
            }
        }
        return new LocalJarArtifact(path, hash, game, loader, build);
    }, token);

    public async Task<XsrResult> ImportAsync(MinecraftLocalJarCommand command, CancellationToken token = default)
    {
        if (Interlocked.CompareExchange(ref _mutating, 1, 0) != 0)
            return XsrResult.Failure(MinecraftErrors.InvalidRequest("已有 JAR 导入正在进行。"));
        try { return await Task.Run(() => ImportCoreAsync(command, token), token).ConfigureAwait(false); }
        finally { Interlocked.Exchange(ref _mutating, 0); }
    }

    private async Task<XsrResult> ImportCoreAsync(MinecraftLocalJarCommand command, CancellationToken token)
    {
        using var task = tasks.Begin(new("jar:" + Guid.NewGuid().ToString("N"), "导入 " + Path.GetFileName(command.Artifact.Path), ["检查文件", "写入文件", "完成"]));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, task.CancellationToken);
        token = linked.Token;
        string? temporary = null;
        try
        {
            LocalJarArtifact inspected = await InspectAsync(command.Artifact.Path, token).ConfigureAwait(false);
            if (inspected != command.Artifact) throw new InvalidDataException("文件已变化，请重新拖入。");
            string root = MinecraftLibraryService.NormalizeDirectory(command.Root);
            using var recoveryOperation = await Management.InstanceRecoveryOperationGate.EnterOperationAsync(root, token).ConfigureAwait(false);
            CheckPath(root);
            if (command.Action == LocalJarAction.Loader)
            {
                if (inspected.Loader is null || string.IsNullOrWhiteSpace(inspected.Game) || string.IsNullOrWhiteSpace(inspected.Build))
                    throw new InvalidDataException("未识别到受支持的加载器安装信息。请从安装页选择加载器。");
                string id = $"{inspected.Game}-{inspected.Loader.ToString()!.ToLowerInvariant()}{inspected.Build}";
                if (!MinecraftVersionPaths.IsSafeReference(id) || Directory.Exists(ForgeInstallService.Contained(root, "versions/" + id)))
                    throw new InvalidDataException("目标版本已存在或安装器版本名称无效。");
                var result = await installer.InstallAsync(new(root, inspected.Game, inspected.Loader, inspected.Build)
                { LocalInstaller = inspected }, token).ConfigureAwait(false);
                if (!result.IsSuccess) { task.Fail(result.Error?.Message ?? "加载器安装失败。"); return XsrResult.Failure(result.Error!); }
                task.Complete(); return XsrResult.Success();
            }
            if (!MinecraftVersionPaths.IsSafeReference(command.InstanceId)) throw new InvalidDataException("请先选择要修改的版本。");
            string instance = ForgeInstallService.Contained(root, "versions/" + command.InstanceId);
            if (store.TryResolve(MinecraftProcessStateComposition.SessionsKey, out var sessions)
                && store.ReadCollection<MinecraftProcessSnapshot>(sessions, cancellationToken: token).Items.Any(item =>
                    string.Equals(item.InstanceDirectory, instance, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                    && item.State is MinecraftProcessState.Created or MinecraftProcessState.Running))
                throw new InvalidDataException("请先结束该版本的游戏进程。");
            CheckPath(instance);
            string manifestPath = ForgeInstallService.Contained(instance, command.InstanceId + ".json");
            CheckPath(manifestPath);
            var metadata = await _metadata.LoadAsync(instance, token).ConfigureAwait(false);
            string destination;
            if (command.Action == LocalJarAction.Mod)
            {
                string modsRoot = metadata.InstanceIsolation ? instance : root;
                destination = ForgeInstallService.Contained(modsRoot, "mods/" + Path.GetFileName(inspected.Path));
                if (Path.Exists(destination)) throw new IOException("已有同名模组，未覆盖任何文件。");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
                await CopyVerifiedAsync(inspected, temporary, token).ConfigureAwait(false);
                CheckPath(Path.GetDirectoryName(destination)!);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, destination);
            }
            else if (command.Action == LocalJarAction.CorePatch)
            {
                var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, token).ConfigureAwait(false))!.AsObject();
                if (manifest["inheritsFrom"] is not null || (manifest["jar"] is { } alias && alias.ToString() != command.InstanceId))
                    throw new InvalidDataException("此版本使用继承的核心，请选择拥有独立核心的版本，避免修改其他版本共用的文件。");
                destination = ForgeInstallService.Contained(instance, command.InstanceId + ".jar");
                CheckPath(destination);
                temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
                string patchCopy = temporary + ".patch";
                try
                {
                    await CopyVerifiedAsync(inspected, patchCopy, token).ConfigureAwait(false);
                    await MergeAsync(destination, patchCopy, temporary, token).ConfigureAwait(false);
                }
                finally { if (File.Exists(patchCopy)) File.Delete(patchCopy); }
                string digest;
                await using (var stream = File.OpenRead(temporary)) digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
                string backup = destination + ".backup-" + Guid.NewGuid().ToString("N");
                token.ThrowIfCancellationRequested();
                File.Replace(temporary, destination, backup);
                try { await _metadata.UpdateAsync(instance, current => current with { CorePatchSha256 = digest }, CancellationToken.None).ConfigureAwait(false); }
                catch { File.Copy(backup, destination, overwrite: true); throw; }
            }
            else throw new InvalidDataException("未知的 JAR 用途。");
            temporary = null;
            task.Complete(command.Action == LocalJarAction.CorePatch ? "核心补丁已应用，原核心已备份。" : "模组已添加。");
            return XsrResult.Success();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { task.Canceled(); return XsrResult.Failure(XsrRuntimeErrors.Cancelled()); }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        { task.Fail(error.Message); return XsrResult.Failure(MinecraftErrors.InvalidRequest(error.Message)); }
        finally { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static async Task CopyVerifiedAsync(LocalJarArtifact source, string destination, CancellationToken token)
    {
        CheckPath(source.Path);
        await using (var input = OpenArchive(source.Path))
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            await input.CopyToAsync(output, token).ConfigureAwait(false);
        await using var verify = File.OpenRead(destination);
        string actual = Convert.ToHexString(await SHA256.HashDataAsync(verify, token).ConfigureAwait(false));
        if (!actual.Equals(source.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("文件已变化，请重新拖入。");
    }

    private static async Task MergeAsync(string original, string patch, string destination, CancellationToken token)
    {
        using var baseZip = ZipFile.OpenRead(original);
        using var patchZip = ZipFile.OpenRead(patch);
        ValidateArchive(baseZip); ValidateArchive(patchZip);
        var additions = patchZip.Entries.Where(entry => entry.Name.Length > 0 && !entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (additions.Length == 0) throw new InvalidDataException("压缩包中没有可应用的补丁文件。");
        var replaced = additions.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
        using var result = ZipFile.Open(destination, ZipArchiveMode.Create);
        ArchiveReadBudget budget = new(2L * 1024 * 1024 * 1024);
        foreach (var entry in baseZip.Entries.Where(entry => entry.Name.Length > 0 && !replaced.Contains(entry.FullName)
            && !IsSignature(entry.FullName)).Concat(additions))
        {
            token.ThrowIfCancellationRequested();
            await using var input = entry.Open();
            await using var output = result.CreateEntry(entry.FullName).Open();
            await ArchiveReadBudget.CopyAsync(input, output, entry.Length, 512L * 1024 * 1024, budget, token).ConfigureAwait(false);
        }
    }

    private static bool IsSignature(string name)
    {
        if (!name.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) return false;
        string leaf = name[9..];
        if (leaf.Contains('/')) return false;
        return leaf.StartsWith("SIG-", StringComparison.OrdinalIgnoreCase)
            || new[] { ".SF", ".RSA", ".DSA", ".EC" }.Contains(Path.GetExtension(leaf), StringComparer.OrdinalIgnoreCase);
    }

    private static FileStream OpenArchive(string path)
    {
        if (new FileInfo(path).Length > 512L * 1024 * 1024) throw new InvalidDataException("JAR 文件超过 512 MiB。");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
    }
    private static async Task<JsonObject> ReadJsonAsync(ZipArchiveEntry entry, CancellationToken token)
    {
        if (entry.Length > 4 * 1024 * 1024) throw new InvalidDataException("安装器清单过大。");
        await using var stream = entry.Open();
        using var buffer = new MemoryStream();
        await ArchiveReadBudget.CopyAsync(stream, buffer, entry.Length, 4 * 1024 * 1024,
            new ArchiveReadBudget(4 * 1024 * 1024), token).ConfigureAwait(false);
        buffer.Position = 0;
        return JsonNode.Parse(buffer) as JsonObject ?? throw new InvalidDataException("安装器清单无效。");
    }
    private static void ValidateArchive(ZipArchive archive)
    {
        if (archive.Entries.Count > 100_000) throw new InvalidDataException("JAR 条目过多。");
        long total = 0;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (!names.Add(entry.FullName) || entry.FullName.Contains('\\') || entry.FullName.StartsWith('/')
                || entry.FullName.Contains(':') || entry.FullName.Split('/').Any(part => part is ".." or ".")
                || entry.Length > 512L * 1024 * 1024 || (total += entry.Length) > 2L * 1024 * 1024 * 1024)
                throw new InvalidDataException("JAR 包含重复、越界或过大的条目。");
        }
    }
    private static void CheckPath(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("不能通过符号链接修改游戏文件。");
    }
}

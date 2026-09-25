using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Nexa.Services.Files;
using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Install;

public sealed record MinecraftModpackPreview(string Path, string Sha256, string Name, string Version,
    string Format, string Game, InstallLoader? Loader, string? Build, string InstanceId, int RequiredFiles, int OptionalFiles);
public sealed record MinecraftModpackCommand(MinecraftModpackPreview Pack, string RootDirectory, bool IncludeOptional = false);
public static class MinecraftModpackContract
{
    public static readonly XsrSemanticId Install = XsrSemanticId.Parse("minecraft.modpack.install");
}
internal sealed record ModpackFile(string Path, string[] Urls, long Size, string Sha1, string? Sha512, bool Optional);
internal sealed record CurseForgePackFile(long ProjectId, long FileId, bool Optional);
internal sealed record ModpackPlan(MinecraftModpackPreview Preview, IReadOnlyList<ModpackFile> Files,
    IReadOnlyList<CurseForgePackFile> CurseFiles, string[] OverrideRoots);

public static class MinecraftModpackArchive
{
    internal const long MaxArchive = 2L * 1024 * 1024 * 1024;
    internal const long MaxFile = 512L * 1024 * 1024;
    internal const long MaxExpanded = 8L * 1024 * 1024 * 1024;
    public static Task<MinecraftModpackPreview> InspectAsync(string path, CancellationToken token = default) =>
        Task.Run(async () => (await ReadAsync(path, token).ConfigureAwait(false)).Preview, token);

    internal static async Task<ModpackPlan> ReadAsync(string path, CancellationToken token)
    {
        path = MinecraftLibraryService.NormalizeDirectory(path);
        CheckPath(path);
        if (new FileInfo(path).Length > MaxArchive) throw new InvalidDataException("整合包超过 2 GiB。");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        ValidateArchive(archive);
        bool mr = archive.GetEntry("modrinth.index.json") is not null;
        if (mr && archive.GetEntry("manifest.json") is not null) throw new InvalidDataException("整合包包含互相冲突的清单。");
        var entry = archive.GetEntry(mr ? "modrinth.index.json" : "manifest.json") ?? throw new InvalidDataException("未找到 Modrinth 或 CurseForge 整合包清单。");
        if (entry.Length > 16 * 1024 * 1024) throw new InvalidDataException("整合包清单过大。");
        await using var manifestStream = entry.Open();
        using var manifestBuffer = new MemoryStream();
        await ArchiveReadBudget.CopyAsync(manifestStream, manifestBuffer, entry.Length, 16 * 1024 * 1024,
            new ArchiveReadBudget(16 * 1024 * 1024), token).ConfigureAwait(false);
        manifestBuffer.Position = 0;
        var manifest = await JsonNode.ParseAsync(manifestBuffer, cancellationToken: token).ConfigureAwait(false) as JsonObject ?? throw new InvalidDataException("整合包清单无效。");
        string name = Text(manifest, "name"), version = Text(manifest, mr ? "versionId" : "version"), game;
        InstallLoader? loader = null;
        string? build = null;
        List<ModpackFile> files = [];
        List<CurseForgePackFile> curseFiles = [];
        string[] overrides;
        if (mr)
        {
            if (manifest["formatVersion"]?.GetValue<int>() != 1 || Text(manifest, "game") != "minecraft") throw new InvalidDataException("不支持此 MRPACK 格式或游戏类型。");
            var dependencies = manifest["dependencies"] as JsonObject ?? throw new InvalidDataException("整合包缺少依赖。");
            game = Text(dependencies, "minecraft");
            foreach (var dependency in dependencies.Where(item => item.Key != "minecraft"))
            {
                if (loader is not null) throw new InvalidDataException("不支持多个基础加载器。");
                loader = ParseLoader(dependency.Key); build = dependency.Value?.GetValue<string>();
            }
            foreach (var item in manifest["files"] as JsonArray ?? throw new InvalidDataException("整合包缺少文件列表。"))
            {
                token.ThrowIfCancellationRequested();
                var file = item as JsonObject ?? throw new InvalidDataException("文件项无效。");
                string relative = SafeRelative(Text(file, "path"));
                if (relative.EndsWith('/')) throw new InvalidDataException("下载文件不能使用目录路径。");
                string client = file["env"]?["client"]?.GetValue<string>() ?? "required";
                if (client is not ("required" or "optional" or "unsupported")) throw new InvalidDataException("未知的客户端依赖类型。");
                if (client == "unsupported") continue;
                string sha1 = RequireHash(file["hashes"]?["sha1"]?.GetValue<string>(), 40);
                string sha512 = RequireHash(file["hashes"]?["sha512"]?.GetValue<string>(), 128);
                long size = file["fileSize"]?.GetValue<long>() ?? -1;
                if (size < 0 || size > MaxFile) throw new InvalidDataException("文件大小无效。");
                string[] urls = (file["downloads"] as JsonArray ?? []).Select(value => DownloadUrl(value?.GetValue<string>() ?? "")).ToArray();
                if (urls.Length is 0 or > 16) throw new InvalidDataException("文件缺少有效下载地址。");
                files.Add(new(relative, urls, size, sha1, sha512, client == "optional"));
            }
            overrides = ["overrides/", "client-overrides/"];
        }
        else
        {
            if (manifest["manifestVersion"]?.GetValue<int>() != 1 || Text(manifest, "manifestType") != "minecraftModpack") throw new InvalidDataException("此 ZIP 不是受支持的 CurseForge 整合包。");
            game = Text(manifest["minecraft"] as JsonObject ?? throw new InvalidDataException("缺少 Minecraft 版本。"), "version");
            var loaders = manifest["minecraft"]?["modLoaders"] as JsonArray ?? [];
            if (loaders.Count > 1) throw new InvalidDataException("不支持多个基础加载器。");
            if (loaders.Count == 1)
            {
                string identity = loaders[0]?["id"]?.GetValue<string>() ?? "";
                int separator = identity.IndexOf('-');
                if (separator <= 0) throw new InvalidDataException("加载器标识无效。");
                loader = ParseLoader(identity[..separator]); build = identity[(separator + 1)..];
            }
            foreach (var item in manifest["files"] as JsonArray ?? throw new InvalidDataException("缺少文件列表。"))
            {
                long project = item?["projectID"]?.GetValue<long>() ?? 0, file = item?["fileID"]?.GetValue<long>() ?? 0;
                if (project <= 0 || file <= 0) throw new InvalidDataException("CurseForge 项目或文件 ID 无效。");
                curseFiles.Add(new(project, file, item?["required"]?.GetValue<bool>() == false));
            }
            overrides = [SafeRelative(manifest["overrides"]?.GetValue<string>() ?? "overrides").TrimEnd('/') + "/"];
        }
        if (!MinecraftVersionPaths.IsSafeReference(game) || (loader is not null && (string.IsNullOrWhiteSpace(build) || !MinecraftVersionPaths.IsSafeReference(build)))) throw new InvalidDataException("Minecraft 或加载器版本无效。");
        if (files.Count + curseFiles.Count > 10000 || files.Sum(file => file.Size) > MaxExpanded) throw new InvalidDataException("整合包下载数量或大小超过限制。");
        if (files.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count || curseFiles.Select(file => (file.ProjectId, file.FileId)).Distinct().Count() != curseFiles.Count) throw new InvalidDataException("整合包包含重复文件。");
        string safeName = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        if (safeName.Length > 60) safeName = safeName[..60];
        if (safeName.Length == 0) safeName = "Modpack";
        int optional = files.Count(file => file.Optional) + curseFiles.Count(file => file.Optional);
        var preview = new MinecraftModpackPreview(path, hash, name, version, mr ? "Modrinth" : "CurseForge", game, loader, build,
            safeName + "-" + hash[..8].ToLowerInvariant(), files.Count + curseFiles.Count - optional, optional);
        return new(preview, files.AsReadOnly(), curseFiles.AsReadOnly(), overrides);
    }

    internal static string SafeRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Contains(':') || path.StartsWith('/')) throw new InvalidDataException("整合包包含无效路径。");
        foreach (string part in path.TrimEnd('/').Split('/'))
        {
            string stem = part.Split('.')[0].ToUpperInvariant();
            if (part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || part.Any(c => c < 32 || "<>\"|?*".Contains(c))
                || stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9'))
                throw new InvalidDataException("整合包包含越界或系统保留路径。");
        }
        return path;
    }
    internal static void ValidateArchive(ZipArchive archive)
    {
        if (archive.Entries.Count > 100000) throw new InvalidDataException("整合包条目过多。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            SafeRelative(entry.FullName);
            if (!names.Add(entry.FullName.TrimEnd('/')) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || entry.Length > MaxFile || (total += entry.Length) > MaxExpanded)
                throw new InvalidDataException("整合包包含重复路径、链接或过大的内容。");
        }
    }
    internal static string DownloadUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0
            || !new[] { "cdn.modrinth.com", "github.com", "raw.githubusercontent.com", "gitlab.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com", "edge.forgecdn.net", "mediafilez.forgecdn.net", "media.forgecdn.net" }.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("整合包使用了不受支持的下载地址。");
        return uri.AbsoluteUri;
    }
    internal static void CheckPath(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("整合包目标或来源不能包含符号链接。");
    }
    internal static string RequireHash(string? hash, int length) => hash is not null && hash.Length == length && hash.All(char.IsAsciiHexDigit) ? hash : throw new InvalidDataException("文件校验信息无效。");
    private static InstallLoader ParseLoader(string name) => name switch
    {
        "forge" => InstallLoader.Forge,
        "neoforge" => InstallLoader.NeoForge,
        "fabric-loader" or "fabric" => InstallLoader.Fabric,
        "quilt-loader" or "quilt" => InstallLoader.Quilt,
        _ => throw new InvalidDataException("整合包声明了尚不支持的依赖：" + name),
    };
    private static string Text(JsonObject value, string name) => value[name]?.GetValue<string>() is { Length: > 0 and <= 1024 } text ? text : throw new InvalidDataException("整合包清单字段无效：" + name);
}

using System.Globalization;
using System.Text.Json.Nodes;
using PCL.Services.Minecraft.Assets;

namespace PCL.Services.Minecraft.Downloads;

public sealed record MinecraftAssetFileState(bool Exists, long Length);
public sealed record MinecraftAssetDownloadFile(string Url, string LocalPath, string Hash, long ActualSize = -1);
public sealed record MinecraftAssetDownloadPlan(IReadOnlyList<MinecraftAssetDownloadFile> Files);

public sealed record MinecraftAssetDownloadPlanRequest
{
    public required IReadOnlyList<MinecraftAssetToken> Assets { get; init; }
    public bool CheckHash { get; init; }
    public IReadOnlyDictionary<string, MinecraftAssetFileState> ExistingFiles { get; init; } = new Dictionary<string, MinecraftAssetFileState>(StringComparer.Ordinal);
}

public enum MinecraftClientDownloadFailureReason
{
    None,
    NoClientJarDownloadInfo,
}

public sealed record MinecraftClientJarDownloadFile
{
    public required string Url { get; init; }
    public required string LocalPath { get; init; }
    public long MinimumSize { get; init; }
    public long ActualSize { get; init; } = -1;
    public string? Sha1 { get; init; }
}

public sealed record MinecraftClientJarDownloadPlan(MinecraftClientJarDownloadFile? File, MinecraftClientDownloadFailureReason FailureReason);

public sealed record MinecraftClientJarDownloadPlanRequest
{
    public required JsonObject VersionJson { get; init; }
    public required string InstanceDirectory { get; init; }
    public required string VersionName { get; init; }
}

public sealed record MinecraftAssetIndexDownloadPlan
{
    public string? IndexId { get; init; }
    public string? Url { get; init; }
    public string? LocalPath { get; init; }
    public bool UsedLegacyFallback { get; init; }
    public bool HasDownload => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(LocalPath);
}

public sealed record MinecraftAssetIndexDownloadPlanRequest
{
    public required JsonObject VersionJson { get; init; }
    public IReadOnlyList<JsonObject> InheritedVersionJsons { get; init; } = [];
    public required string MinecraftRootDirectory { get; init; }
    public bool UseLegacyFallback { get; init; } = true;
    public bool AllowUrlOnlyAssetIndex { get; init; } = true;
}

public static class MinecraftAssetDownloadPlanner
{
    public static MinecraftAssetDownloadPlan CreatePlan(MinecraftAssetDownloadPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<MinecraftAssetDownloadFile> files = [];
        foreach (MinecraftAssetToken asset in request.Assets)
        {
            if (!request.CheckHash && request.ExistingFiles.TryGetValue(asset.LocalPath, out MinecraftAssetFileState? state) && state.Exists && (asset.Size == 0 || state.Length == asset.Size)) continue;
            files.Add(new MinecraftAssetDownloadFile(MinecraftAssetListResolver.GetObjectUrl(asset.Hash), asset.LocalPath, asset.Hash, asset.Size == 0 ? -1 : asset.Size));
        }

        return new MinecraftAssetDownloadPlan(files);
    }
}

public static class MinecraftClientDownloadPlanner
{
    private const long MinimumClientJarSize = 1024;

    public static MinecraftClientJarDownloadPlan CreateClientJarPlan(MinecraftClientJarDownloadPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.VersionJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InstanceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.VersionName);
        if (!MinecraftVersionPaths.IsSafeReference(request.VersionName))
            throw new InvalidDataException($"The version name is not a safe file name: {request.VersionName}");
        string? url = request.VersionJson["downloads"]?["client"]?["url"]?.ToString();
        if (string.IsNullOrWhiteSpace(url)) return new MinecraftClientJarDownloadPlan(null, MinecraftClientDownloadFailureReason.NoClientJarDownloadInfo);
        JsonObject? client = request.VersionJson["downloads"]?["client"]?.AsObject();
        long actualSize = long.TryParse(client?["size"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long size) ? size : -1;
        return new MinecraftClientJarDownloadPlan(new MinecraftClientJarDownloadFile
        {
            Url = url,
            LocalPath = Contained(Path.GetFullPath(request.InstanceDirectory), request.VersionName + ".jar"),
            MinimumSize = MinimumClientJarSize,
            ActualSize = actualSize,
            Sha1 = client?["sha1"]?.ToString(),
        }, MinecraftClientDownloadFailureReason.None);
    }

    public static MinecraftAssetIndexDownloadPlan CreateAssetIndexPlan(MinecraftAssetIndexDownloadPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        MinecraftAssetIndexResolution resolution = MinecraftAssetIndexResolver.ResolveIndex(new MinecraftAssetIndexRequest
        {
            VersionJson = request.VersionJson,
            InheritedVersionJsons = request.InheritedVersionJsons,
            UseLegacyFallback = request.UseLegacyFallback,
            AllowUrlOnlyAssetIndex = request.AllowUrlOnlyAssetIndex,
        });
        if (resolution.IndexJson is null) return new MinecraftAssetIndexDownloadPlan { UsedLegacyFallback = resolution.UsedLegacyFallback };
        string id = resolution.IndexJson["id"]?.ToString() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(id) && !MinecraftVersionPaths.IsSafeReference(id))
            throw new InvalidDataException($"The asset index id is not a safe file name: {id}");
        string? url = string.IsNullOrWhiteSpace(resolution.IndexJson["url"]?.ToString()) ? null : resolution.IndexJson["url"]!.ToString();
        string? localPath = string.IsNullOrWhiteSpace(id)
            ? null
            : Contained(Path.GetFullPath(request.MinecraftRootDirectory), "assets", "indexes", id + ".json");
        return new MinecraftAssetIndexDownloadPlan
        {
            IndexId = string.IsNullOrWhiteSpace(id) ? null : id,
            Url = url,
            LocalPath = localPath,
            UsedLegacyFallback = resolution.UsedLegacyFallback,
        };
    }

    private static string Contained(string root, params string[] parts)
    {
        string fullRoot = Path.GetFullPath(root);
        string candidate = Path.GetFullPath(Path.Combine(new[] { fullRoot }.Concat(parts).ToArray()));
        string prefix = Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(prefix, comparison))
            throw new InvalidDataException("The download path escapes its root.");
        return candidate;
    }
}

public static class MinecraftDownloadSourcePlanner
{
    public static string[] OrderSources(IReadOnlyList<string> officialUrls, IReadOnlyList<string> mirrorUrls, bool preferOfficialSource) => preferOfficialSource ? Merge(officialUrls, mirrorUrls) : Merge(mirrorUrls, officialUrls);

    public static string[] GetAssetSources(string original, bool preferOfficialSource)
    {
        string official = original.Replace("http://resources.download.minecraft.net", "https://resources.download.minecraft.net", StringComparison.Ordinal);
        return OrderSources([official], [ReplaceAssetMirror(official)], preferOfficialSource);
    }

    public static string[] GetLibrarySources(string original, bool preferOfficialSource)
    {
        string[] mirrors = [ReplaceLibraryMirror(original, "https://bmclapi2.bangbang93.com/maven"), ReplaceLibraryMirror(original, "https://bmclapi2.bangbang93.com/libraries"), original];
        return ContainsThirdParty(original) ? mirrors[..2] : OrderSources([original], mirrors, preferOfficialSource);
    }

    public static string[] GetLauncherOrMetaSources(string original, bool preferOfficialSource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(original);
        string mirror = original.Replace("https://piston-data.mojang.com", "https://bmclapi2.bangbang93.com", StringComparison.Ordinal).Replace("https://piston-meta.mojang.com", "https://bmclapi2.bangbang93.com", StringComparison.Ordinal).Replace("https://launcher.mojang.com", "https://bmclapi2.bangbang93.com", StringComparison.Ordinal).Replace("https://launchermeta.mojang.com", "https://bmclapi2.bangbang93.com", StringComparison.Ordinal).Replace("https://zkitefly.github.io/unlisted-versions-of-minecraft", "https://alist.8mi.tech/d/mirror/unlisted-versions-of-minecraft/Auto", StringComparison.Ordinal);
        return OrderSources([original], [mirror], preferOfficialSource);
    }

    private static string ReplaceAssetMirror(string value) => value.Replace("https://piston-data.mojang.com", "https://bmclapi2.bangbang93.com/assets", StringComparison.Ordinal).Replace("https://piston-meta.mojang.com", "https://bmclapi2.bangbang93.com/assets", StringComparison.Ordinal).Replace("https://resources.download.minecraft.net", "https://bmclapi2.bangbang93.com/assets", StringComparison.Ordinal);
    private static string ReplaceLibraryMirror(string value, string host) => value.Replace("https://piston-data.mojang.com", host, StringComparison.Ordinal).Replace("https://piston-meta.mojang.com", host, StringComparison.Ordinal).Replace("https://libraries.minecraft.net", host, StringComparison.Ordinal).Replace("https://maven.minecraftforge.net", host, StringComparison.Ordinal).Replace("https://maven.fabricmc.net", host, StringComparison.Ordinal).Replace("https://maven.neoforged.net/releases", host, StringComparison.Ordinal);
    private static bool ContainsThirdParty(string value) => value.Contains("minecraftforge", StringComparison.OrdinalIgnoreCase) || value.Contains("fabricmc", StringComparison.OrdinalIgnoreCase) || value.Contains("neoforged", StringComparison.OrdinalIgnoreCase);
    private static string[] Merge(IReadOnlyList<string> first, IReadOnlyList<string> second) => first.Concat(second).Distinct(StringComparer.Ordinal).ToArray();
}

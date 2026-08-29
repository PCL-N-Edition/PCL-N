using System.Globalization;
using System.Text.Json.Nodes;

namespace PCL.Services.Minecraft.Assets;

public sealed record MinecraftAssetToken
{
    public required string LocalPath { get; init; }
    public required string SourcePath { get; init; }
    public required string Hash { get; init; }
    public long Size { get; init; }
}

public sealed record MinecraftAssetIndexRequest
{
    public required JsonObject VersionJson { get; init; }
    public IReadOnlyList<JsonObject> InheritedVersionJsons { get; init; } = [];
    public bool UseLegacyFallback { get; init; }
    public bool AllowUrlOnlyAssetIndex { get; init; }
}

public sealed record MinecraftAssetIndexNameRequest
{
    public required JsonObject VersionJson { get; init; }
    public IReadOnlyList<JsonObject> InheritedVersionJsons { get; init; } = [];
}

public sealed record MinecraftAssetIndexResolution(JsonObject? IndexJson, bool UsedLegacyFallback);

public sealed record MinecraftAssetListRequest
{
    public required JsonObject IndexJson { get; init; }
    public required string MinecraftRootDirectory { get; init; }
    public required string InstanceDirectory { get; init; }
}

public sealed record MinecraftAssetDownloadFile(
    string Url,
    string LocalPath,
    string Hash,
    long ExpectedSize);

public sealed record MinecraftAssetDownloadPlan(IReadOnlyList<MinecraftAssetDownloadFile> Files);

public static class MinecraftAssetIndexResolver
{
    public const string LegacyIndexName = "legacy";
    public const string LegacyIndexSha1 = "c0fd82e8ce9fbc93119e40d96d5a4e62cfa3f729";
    public const int LegacyIndexSize = 134284;
    public const string LegacyIndexUrl = "https://launchermeta.mojang.com/mc-staging/assets/legacy/c0fd82e8ce9fbc93119e40d96d5a4e62cfa3f729/legacy.json";
    public const int LegacyIndexTotalSize = 111220701;

    public static MinecraftAssetIndexResolution ResolveIndex(MinecraftAssetIndexRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        JsonObject? index = TryGetIndex(request.VersionJson, request.AllowUrlOnlyAssetIndex);
        if (index is not null) return new(index, false);
        foreach (JsonObject inherited in request.InheritedVersionJsons)
        {
            index = TryGetIndex(inherited, request.AllowUrlOnlyAssetIndex);
            if (index is not null) return new(index, false);
        }

        return request.UseLegacyFallback ? new(CreateLegacyIndex(), true) : new(null, false);
    }

    public static string GetIndexName(MinecraftAssetIndexNameRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? name = TryGetIndexName(request.VersionJson);
        if (name is not null) return name;
        foreach (JsonObject inherited in request.InheritedVersionJsons)
        {
            name = TryGetIndexName(inherited);
            if (name is not null) return name;
        }

        return LegacyIndexName;
    }

    private static JsonObject? TryGetIndex(JsonObject json, bool allowUrlOnly) =>
        json["assetIndex"] is JsonObject index && (index["id"] is not null || allowUrlOnly && index["url"] is not null) ? index : null;

    private static string? TryGetIndexName(JsonObject json) =>
        json["assetIndex"] is JsonObject index && index["id"] is JsonNode id ? id.ToString() : json["assets"]?.ToString();

    private static JsonObject CreateLegacyIndex() => new()
    {
        ["id"] = LegacyIndexName,
        ["sha1"] = LegacyIndexSha1,
        ["size"] = LegacyIndexSize,
        ["url"] = LegacyIndexUrl,
        ["totalSize"] = LegacyIndexTotalSize,
    };
}

public static class MinecraftAssetListResolver
{
    public static IReadOnlyList<MinecraftAssetToken> GetAssetList(MinecraftAssetListRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        JsonObject objects = request.IndexJson["objects"]?.AsObject() ?? throw new FormatException("Asset index does not contain an objects map.");
        string root = Path.GetFullPath(request.MinecraftRootDirectory);
        string instance = Path.GetFullPath(request.InstanceDirectory);
        bool mapToResources = request.IndexJson["map_to_resources"]?.GetValue<bool>() == true;
        bool virtualAssets = request.IndexJson["virtual"]?.GetValue<bool>() == true;
        List<MinecraftAssetToken> result = new(objects.Count);
        foreach ((string sourcePath, JsonNode? node) in objects)
        {
            if (node is not JsonObject asset) throw new FormatException($"Asset '{sourcePath}' is not an object.");
            string hash = asset["hash"]?.ToString() ?? throw new FormatException($"Asset '{sourcePath}' does not contain a hash.");
            if (hash.Length < 2 || hash.Any(char.IsWhiteSpace) || hash.Any(static c => !Uri.IsHexDigit(c))) throw new FormatException($"Asset '{sourcePath}' has an invalid hash.");
            if (!long.TryParse(asset["size"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long size) || size < 0) throw new FormatException($"Asset '{sourcePath}' does not contain a valid size.");
            string normalizedSource = NormalizeRelativePath(sourcePath, "asset source path");
            string localPath = mapToResources
                ? Contained(instance, "resources", normalizedSource)
                : virtualAssets
                    ? Contained(root, "assets", "virtual", "legacy", normalizedSource)
                    : Contained(root, "assets", "objects", hash[..2], hash);
            result.Add(new MinecraftAssetToken { LocalPath = localPath, SourcePath = sourcePath, Hash = hash, Size = size });
        }

        return result;
    }

    public static string GetHashPrefix(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        if (hash.Length < 2) throw new ArgumentException("Asset hash must contain at least two characters.", nameof(hash));
        return hash[..2];
    }

    public static string GetObjectUrl(string hash) => $"https://resources.download.minecraft.net/{GetHashPrefix(hash)}/{hash}";

    public static MinecraftAssetDownloadPlan CreateDownloadPlan(IEnumerable<MinecraftAssetToken> assets, Func<string, bool>? isUsable = null)
    {
        ArgumentNullException.ThrowIfNull(assets);
        List<MinecraftAssetDownloadFile> files = [];
        foreach (MinecraftAssetToken asset in assets)
        {
            if (isUsable?.Invoke(asset.LocalPath) == true) continue;
            files.Add(new MinecraftAssetDownloadFile(GetObjectUrl(asset.Hash), asset.LocalPath, asset.Hash, asset.Size));
        }

        return new MinecraftAssetDownloadPlan(files);
    }

    private static string Contained(string root, params string[] segments)
    {
        string candidate = Path.GetFullPath(Path.Combine(new[] { root }.Concat(segments).ToArray()));
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(prefix, comparison)) throw new InvalidDataException("Asset path escapes its content root.");
        return candidate;
    }

    private static string NormalizeRelativePath(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) throw new InvalidDataException($"Invalid {description}.");
        string normalized = path.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0 || normalized.Split('/').Any(static part => part is "" or "." or "..")) throw new InvalidDataException($"Invalid {description}.");
        return normalized.Replace('/', Path.DirectorySeparatorChar);
    }
}


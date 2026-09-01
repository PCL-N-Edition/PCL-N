using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCL.Services.Minecraft.Launch;

/// <summary>Inputs for resolving the client JAR used by a launch.</summary>
public sealed record MinecraftClientJarResolutionRequest
{
    public required JsonObject VersionJson { get; init; }
    public IReadOnlyList<JsonObject> InheritedVersionJsons { get; init; } = [];
    public required string VersionId { get; init; }
    public required string InstanceDirectory { get; init; }
    public required string MinecraftRootDirectory { get; init; }
    public string? ExplicitClientJarPath { get; init; }
}

public sealed record MinecraftClientJarResolution(string Path, string VersionId, bool IsInherited);

/// <summary>
/// Resolves the executable client artifact from a version id and its inheritance chain. Product
/// callers do not need to know the launcher filesystem layout or manually populate classpath
/// head entries.
/// </summary>
public static class MinecraftClientJarResolver
{
    public static MinecraftClientJarResolution Resolve(MinecraftClientJarResolutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.VersionJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.VersionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InstanceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MinecraftRootDirectory);
        if (!MinecraftVersionPaths.IsSafeReference(request.VersionId))
            throw new InvalidDataException($"The version id is not a safe file name: {request.VersionId}");

        if (!string.IsNullOrWhiteSpace(request.ExplicitClientJarPath))
        {
            string explicitPath = Path.GetFullPath(request.ExplicitClientJarPath!);
            if (!File.Exists(explicitPath))
                throw new FileNotFoundException("The specified Minecraft client JAR is missing.", explicitPath);
            return new(explicitPath, request.VersionId, false);
        }

        string root = Path.GetFullPath(request.MinecraftRootDirectory);
        string instance = Path.GetFullPath(request.InstanceDirectory);
        List<JsonObject> manifests = [request.VersionJson, .. request.InheritedVersionJsons];
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        if (TryResolveChain(
                request.VersionId,
                request.VersionJson,
                manifests,
                root,
                instance,
                inherited: false,
                visited,
                out MinecraftClientJarResolution? chainResolution))
            return chainResolution!;

        foreach (JsonObject inheritedManifest in request.InheritedVersionJsons)
        {
            string? inheritedId = inheritedManifest["id"]?.ToString();
            if (!string.IsNullOrWhiteSpace(inheritedId) && !MinecraftVersionPaths.IsSafeReference(inheritedId))
                throw new InvalidDataException($"The inherited version id is not a safe file name: {inheritedId}");
            if (!string.IsNullOrWhiteSpace(inheritedId) && FindJar(root, instance, inheritedId) is { } inheritedPath)
                return new(inheritedPath, inheritedId, true);
        }

        // A supplied manifest can use "jar" to name the base artifact while keeping a loader id
        // as its own id. Treat that alias as another safe reference before failing.
        string? jarAlias = request.VersionJson["jar"]?.ToString();
        if (!string.IsNullOrWhiteSpace(jarAlias) && !MinecraftVersionPaths.IsSafeReference(jarAlias))
            throw new InvalidDataException($"The base jar alias is not a safe file name: {jarAlias}");
        if (!string.IsNullOrWhiteSpace(jarAlias) && FindJar(root, instance, jarAlias) is { } aliased)
            return new(aliased, jarAlias, true);

        string conventional = Path.Combine(instance, request.VersionId + ".jar");
        throw new FileNotFoundException(
            "The Minecraft client JAR is missing; download the version or its inherited base before launching.",
            conventional);
    }

    private static bool TryResolveChain(
        string versionId,
        JsonObject? knownManifest,
        IReadOnlyList<JsonObject> manifests,
        string root,
        string instance,
        bool inherited,
        HashSet<string> visited,
        out MinecraftClientJarResolution? resolution)
    {
        resolution = null;
        if (!MinecraftVersionPaths.IsSafeReference(versionId))
            throw new InvalidDataException($"The inherited version id is not a safe file name: {versionId}");
        if (!visited.Add(versionId))
            throw new InvalidDataException($"The Minecraft version inheritance chain contains a cycle at '{versionId}'.");

        JsonObject? manifest = knownManifest ?? FindManifest(manifests, versionId, null, inherited);
        if (manifest is null && MinecraftVersionPaths.ResolveJsonPath(root, instance, versionId) is { } jsonPath)
        {
            try { manifest = JsonNode.Parse(File.ReadAllText(jsonPath))?.AsObject(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        }
        string? parent = manifest?["inheritsFrom"]?.ToString();
        if (!string.IsNullOrWhiteSpace(parent))
        {
            if (!MinecraftVersionPaths.IsSafeReference(parent))
                throw new InvalidDataException($"The inherited version id is not a safe file name: {parent}");
            if (TryResolveChain(parent, null, manifests, root, instance, inherited: true, visited, out resolution))
                return true;

            throw new FileNotFoundException(
                "The inherited Minecraft client JAR is missing; download the base version before launching.",
                Path.Combine(root, "versions", parent, parent + ".jar"));
        }

        string? path = FindJar(root, instance, versionId);
        if (path is null) return false;
        resolution = new(path, versionId, inherited);
        return true;
    }

    private static string? FindJar(string root, string instance, string versionId)
    {
        // The local instance path is the first-class install target for this branch, followed by
        // the standard Mojang versions/<id>/<id>.jar layout.
        string local = Path.Combine(instance, versionId + ".jar");
        if (File.Exists(local)) return Path.GetFullPath(local);
        return MinecraftVersionPaths.ResolveJarPath(root, instance, versionId);
    }

    private static JsonObject? FindManifest(
        IReadOnlyList<JsonObject> manifests,
        string id,
        JsonObject? current,
        bool inherited)
    {
        foreach (JsonObject manifest in manifests)
        {
            string? manifestId = manifest["id"]?.ToString();
            if (string.Equals(manifestId, id, StringComparison.OrdinalIgnoreCase)) return manifest;
        }

        // The current manifest is authoritative for the first inheritance edge even when its
        // local id is omitted (some third-party loaders omit it in embedded JSON).
        if (!inherited && current is not null && (string.Equals(current["id"]?.ToString(), id, StringComparison.OrdinalIgnoreCase) || current["id"] is null))
            return current;
        return null;
    }
}

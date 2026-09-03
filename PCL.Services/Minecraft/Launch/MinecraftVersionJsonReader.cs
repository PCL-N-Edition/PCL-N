using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCL.Services.Minecraft.Launch;

/// <summary>
/// The current manifest followed by inherited manifests in nearest-parent-to-root order.
/// </summary>
public sealed record MinecraftResolvedVersionManifests(
    JsonObject Current,
    IReadOnlyList<JsonObject> Inherited);

/// <summary>
/// Reads and validates a complete installed version inheritance chain. Filesystem discovery and
/// inheritance belong to Services launch preparation; Product UI never reads version JSON.
/// </summary>
public static class MinecraftVersionJsonReader
{
    public static async Task<MinecraftResolvedVersionManifests> ResolveAsync(
        MinecraftInstanceDescriptor instance,
        string minecraftRootDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraftRootDirectory);

        string root = Path.GetFullPath(minecraftRootDirectory);
        string versionsRoot = Path.Combine(root, "versions");
        string currentPath = Path.GetFullPath(instance.Version.JsonPath);
        EnsureContained(versionsRoot, currentPath);
        JsonObject current = await ReadAsync(currentPath, cancellationToken).ConfigureAwait(false);
        List<JsonObject> inherited = [];
        HashSet<string> visitedPaths = new(GetPathComparer()) { currentPath };
        string? reference = ReadReference(current, "inheritsFrom");
        string? localDirectory = Path.GetDirectoryName(currentPath);

        while (!string.IsNullOrWhiteSpace(reference))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MinecraftVersionPaths.IsSafeReference(reference))
            {
                throw new InvalidDataException(
                    $"The inherited version id is not a safe file name: {reference}");
            }

            string path = MinecraftVersionPaths.ResolveJsonPath(root, localDirectory, reference)
                ?? throw new FileNotFoundException(
                    $"The inherited Minecraft manifest '{reference}' is missing.",
                    Path.Combine(versionsRoot, reference, reference + ".json"));
            path = Path.GetFullPath(path);
            EnsureContained(versionsRoot, path);
            if (!visitedPaths.Add(path))
            {
                throw new InvalidDataException(
                    $"The Minecraft version inheritance chain contains a cycle at '{reference}'.");
            }

            JsonObject parent = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
            inherited.Add(parent);
            localDirectory = Path.GetDirectoryName(path);
            reference = ReadReference(parent, "inheritsFrom");
        }

        return new MinecraftResolvedVersionManifests(current, inherited);
    }

    public static async Task<JsonObject> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = Path.GetFullPath(path);
        string text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonNode.Parse(text) as JsonObject
                ?? throw new InvalidDataException($"The version JSON '{path}' is not an object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The version JSON '{path}' is invalid.", exception);
        }
    }

    private static string? ReadReference(JsonObject manifest, string property) =>
        manifest[property]?.GetValue<string>() is { Length: > 0 } value ? value : null;

    private static void EnsureContained(string root, string path)
    {
        string canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string prefix = canonicalRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, GetPathComparison()))
        {
            throw new InvalidDataException(
                $"The Minecraft version manifest escapes the versions directory: {path}");
        }
    }

    private static StringComparison GetPathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

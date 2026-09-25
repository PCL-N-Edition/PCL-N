using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Nexa.Services.Files;

namespace Nexa.Services.Minecraft.Process;

public sealed record LaunchModIdentity(string Id, string Version, string Format, bool Enabled,
    IReadOnlyDictionary<string, string> Dependencies, bool DependenciesComplete)
{
    public string? ContentSha256 { get; init; }
    public bool NestedCandidate { get; init; }
}
public sealed record LaunchModInventory(IReadOnlyList<LaunchModIdentity> Mods, int UnknownFiles, bool Complete);
public sealed record JvmRunContext(Guid SessionId, string Loader, string LoaderVersion, LaunchModInventory Inventory)
{
    public string GameVersion { get; init; } = "unknown";
    public IReadOnlyDictionary<string, string> Components { get; init; } = new Dictionary<string, string>();
}

/// <summary>Bounded metadata inspection; never executes JARs or uploads filenames.</summary>
public static partial class LaunchModInventoryReader
{
    private static readonly string[] Formats = ["fabric.mod.json", "quilt.mod.json", "META-INF/neoforge.mods.toml", "META-INF/mods.toml", "mcmod.info"];
    public static async Task<LaunchModInventory> ReadAsync(string gameDirectory, CancellationToken token = default)
    {
        List<LaunchModIdentity> mods = []; int unknown = 0, files = 0, archives = 0; bool complete = true;
        var budget = new ArchiveReadBudget(16 * 1024 * 1024);
        var nestedBudget = new ArchiveReadBudget(64 * 1024 * 1024);
        var hashBudget = new ArchiveReadBudget(2L * 1024 * 1024 * 1024);
        async Task ReadArchiveAsync(Stream input, bool enabled, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (++archives > 2048 || mods.Count >= 4096) { complete = false; return; }
            using var hash = SHA256.Create();
            await using var hashStream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write, leaveOpen: true);
            await ArchiveReadBudget.CopyAsync(input, hashStream, input.Length, 256L * 1024 * 1024, hashBudget, token).ConfigureAwait(false);
            await hashStream.FlushFinalBlockAsync(token).ConfigureAwait(false);
            string digest = Convert.ToHexString(hash.Hash!);
            input.Position = 0;
            using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            string? format = Formats.FirstOrDefault(name => zip.GetEntry(name) is not null);
            if (format is null) { unknown++; return; }
            if (zip.Entries.Count(entry => entry.FullName == format) != 1) { complete = false; return; }
            var entry = zip.GetEntry(format)!;
            await using var content = entry.Open(); using var buffer = new MemoryStream();
            await ArchiveReadBudget.CopyAsync(content, buffer, entry.Length, 256 * 1024, budget, token).ConfigureAwait(false);
            string text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            var parsed = Parse(text, format, enabled);
            if (parsed.Count == 0) unknown++;
            else
            {
                if (parsed.Count > 4096 - mods.Count) complete = false;
                mods.AddRange(parsed.Take(4096 - mods.Count).Select(mod => mod with { ContentSha256 = digest, NestedCandidate = depth > 0 }));
            }
            List<string?> names = [];
            JsonNode? declaration = format switch
            {
                "fabric.mod.json" => JsonNode.Parse(text)?["jars"],
                "quilt.mod.json" => JsonNode.Parse(text)?["quilt_loader"]?["jars"],
                _ => null
            };
            if (declaration is JsonArray jars)
                names.AddRange(jars.Take(129).Select(node => format == "quilt.mod.json"
                    ? node is JsonValue name && name.TryGetValue<string>(out var path) ? path : null
                    : node is JsonObject child && child["file"] is JsonValue value && value.TryGetValue<string>(out var childPath) ? childPath : null));
            else if (declaration is not null) complete = false;
            if (zip.GetEntry("META-INF/jarjar/metadata.json") is { } jarjar)
            {
                if (zip.Entries.Count(item => item.FullName == jarjar.FullName) != 1) { complete = false; return; }
                await using var index = jarjar.Open(); using var indexBuffer = new MemoryStream();
                await ArchiveReadBudget.CopyAsync(index, indexBuffer, jarjar.Length, 256 * 1024, budget, token).ConfigureAwait(false);
                var metadata = JsonNode.Parse(indexBuffer.GetBuffer().AsSpan(0, (int)indexBuffer.Length));
                if (metadata?["jars"] is JsonArray children)
                    names.AddRange(children.Take(129).Select(node => node is JsonObject child && child["path"] is JsonValue value
                        && value.TryGetValue<string>(out var path) ? path : null));
                else complete = false;
            }
            if (names.Count > 128 || depth >= 4 && names.Count > 0)
            { complete = false; return; }
            HashSet<string> visited = new(StringComparer.Ordinal);
            foreach (var name in names)
            {
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(name) || name.Length > 512 || name.StartsWith('/') || name.Contains('\\')
                    || name.Contains(':') || name.Split('/').Any(part => part is "" or "." or "..") || !visited.Add(name))
                { complete = false; continue; }
                var nested = zip.GetEntry(name);
                if (nested is null || zip.Entries.Count(item => item.FullName == name) != 1) { complete = false; continue; }
                try
                {
                    await using var nestedInput = nested.Open(); using var nestedBuffer = new MemoryStream();
                    await ArchiveReadBudget.CopyAsync(nestedInput, nestedBuffer, nested.Length, 16 * 1024 * 1024, nestedBudget, token).ConfigureAwait(false);
                    nestedBuffer.Position = 0;
                    await ReadArchiveAsync(nestedBuffer, enabled, depth + 1).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException)
                { complete = false; unknown++; }
                if (archives >= 2048 || mods.Count >= 4096) { complete = false; break; }
            }
        }
        try
        {
            string directory = Path.Combine(gameDirectory, "mods");
            if (!Directory.Exists(directory)) return new(mods.AsReadOnly(), 0, true);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return new([], 0, false);
            foreach (string path in Directory.EnumerateFiles(directory))
            {
                token.ThrowIfCancellationRequested();
                bool enabled = path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
                if (!enabled && !path.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)) continue;
                if (++files > 2048 || mods.Count >= 4096) { complete = false; break; }
                try
                {
                    var info = new FileInfo(path);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > 256 * 1024 * 1024) { unknown++; continue; }
                    long length = info.Length, stamp = info.LastWriteTimeUtc.Ticks;
                    await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                    await ReadArchiveAsync(input, enabled, 0).ConfigureAwait(false);
                    info.Refresh();
                    if (!info.Exists || info.Length != length || info.LastWriteTimeUtc.Ticks != stamp) complete = false;
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
                { unknown++; }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { complete = false; }
        return new(mods.AsReadOnly(), unknown, complete && unknown == 0);
    }

    internal static IReadOnlyList<LaunchModIdentity> Parse(string text, string format, bool enabled)
    {
        List<LaunchModIdentity> result = [];
        if (format.EndsWith(".toml", StringComparison.Ordinal))
        {
            // Deliberately partial: recognize literal mod tables, never claim a TOML dependency resolver.
            foreach (string block in text.Split("[[mods]]", StringSplitOptions.None).Skip(1).Take(128))
            {
                string id = Literal(block, "modId"), version = Literal(block, "version");
                if (SafeId(id)) result.Add(new(id, SafeVersion(version) ? version : "unknown", format, enabled,
                    new Dictionary<string, string>(), false));
            }
            return result.AsReadOnly();
        }
        JsonNode? root = JsonNode.Parse(text);
        IEnumerable<JsonObject> entries = format == "mcmod.info"
            ? (root as JsonArray ?? root?["modList"] as JsonArray ?? []).OfType<JsonObject>().Take(128)
            : root is JsonObject single ? [single] : [];
        foreach (JsonObject original in entries)
        {
            JsonObject item = format == "quilt.mod.json" ? original["quilt_loader"] as JsonObject ?? new() : original;
            string id = Text(item, format == "mcmod.info" ? "modid" : "id"), version = Text(item, "version");
            if (!SafeId(id)) continue;
            Dictionary<string, string> dependencies = new(StringComparer.Ordinal);
            bool full = format is "fabric.mod.json" or "quilt.mod.json";
            if (item["depends"] is JsonObject depends)
                foreach (var pair in depends.Take(65))
                {
                    string range = pair.Value is JsonValue scalar && scalar.TryGetValue<string>(out string? value) ? value : "";
                    if (dependencies.Count >= 64 || !SafeId(pair.Key) || !SafeRange(range)) { full = false; continue; }
                    dependencies[pair.Key] = range;
                }
            else if (format == "quilt.mod.json" && item["depends"] is JsonArray quiltDependencies)
            {
                if (quiltDependencies.Count > 64) full = false;
                foreach (var node in quiltDependencies.Take(64))
                {
                    if (node is JsonValue scalar && scalar.TryGetValue<string>(out var required) && SafeId(required))
                    { if (!dependencies.TryAdd(required, "*")) full = false; continue; }
                    if (node is not JsonObject dependency) { full = false; continue; }
                    string dependencyId = Text(dependency, "id"), range = dependency["versions"] is null ? "*" : Text(dependency, "versions");
                    if (dependency["unless"] is not null || !SafeId(dependencyId) || !SafeRange(range)) { full = false; continue; }
                    if (dependency["optional"] is JsonValue optional && optional.TryGetValue<bool>(out bool skip) && skip) continue;
                    if (!dependencies.TryAdd(dependencyId, range)) full = false;
                }
            }
            else if (item["depends"] is not null) full = false;
            result.Add(new(id, SafeVersion(version) ? version : "unknown", format, enabled, dependencies, full));
        }
        return result.AsReadOnly();
    }
    private static string Text(JsonObject item, string key) => item[key] is JsonValue value && value.TryGetValue<string>(out string? text) ? text : "";
    private static string Literal(string block, string key)
    {
        foreach (string line in block.Split('\n'))
        {
            string trimmed = line.Trim(); if (trimmed.StartsWith('[')) break;
            int equals = trimmed.IndexOf('='); if (equals < 0 || trimmed[..equals].Trim() != key) continue;
            string value = trimmed[(equals + 1)..].Trim();
            if (value.Length > 1 && value[0] is '\'' or '"' && value.LastIndexOf(value[0]) is int end && end > 0) return value[1..end];
        }
        return "";
    }
    public static bool SafeId(string text) => IdPattern().IsMatch(text);
    public static bool SafeVersion(string text) => VersionPattern().IsMatch(text);
    public static bool SafeGameVersion(string text) => GamePattern().IsMatch(text);
    [GeneratedRegex("^(?:unknown|[0-9]{1,2}\\.[0-9]{1,2}(?:\\.[0-9]{1,2})?(?:-(?:pre|rc)[0-9]{1,2})?|[0-9]{2}w[0-9]{2}[a-z])$")]
    private static partial Regex GamePattern();
    public static bool SafeRange(string text) => RangePattern().IsMatch(text);
    [GeneratedRegex("^[a-zA-Z0-9_][a-zA-Z0-9_.-]{0,95}$")]
    private static partial Regex IdPattern();
    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.+\\-]{0,95}$")]
    private static partial Regex VersionPattern();
    [GeneratedRegex("^[a-zA-Z0-9_.+*<>=~^|, ()\\[\\]\\-]{1,192}$")]
    private static partial Regex RangePattern();
}

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nexa.Services.Minecraft.Management;

/// <summary>Bounded, local-only file discovery. Call on a worker, never while rendering UI.</summary>
internal sealed class RecoveryCapturePlan
{
    private readonly string _root, _instance, _game;
    private readonly CancellationToken _token;
    private readonly Dictionary<string, string[]> _entries = new(MinecraftLibraryService.PathComparer);
    private readonly Dictionary<string, JsonObject> _manifests = new(MinecraftLibraryService.PathComparer);
    private readonly Dictionary<string, RecoverySource> _sources = new(MinecraftLibraryService.PathComparer);
    private readonly HashSet<string> _visiting = new(MinecraftLibraryService.PathComparer);
    private readonly HashSet<string> _visited = new(MinecraftLibraryService.PathComparer);
    private int _entryCount;
    private long _manifestBytes;

    private RecoveryCapturePlan(string root, string instance, string game, CancellationToken token)
    {
        _root = Path.GetFullPath(root); _instance = Path.GetFullPath(instance); _game = Path.GetFullPath(game); _token = token;
        if (!MinecraftLibraryService.PathComparer.Equals(Path.GetDirectoryName(_instance), Path.Combine(_root, "versions")))
            throw new InvalidDataException("实例不属于指定游戏目录。");
        RecoveryBlobStore.CheckLinks(_instance); RecoveryBlobStore.CheckLinks(_game);
    }

    internal static async Task<RecoverySnapshot> CaptureAsync(string root, string instance, string game, string manifest,
        string settingsDocument, CancellationToken token = default)
        => await CaptureAsync(root, instance, game, manifest, settingsDocument, null, token).ConfigureAwait(false);

    internal static async Task<RecoverySnapshot> CaptureAsync(string root, string instance, string game, string manifest,
        string settingsDocument, Func<CancellationToken, Task>? validate, CancellationToken token = default)
    {
        var sources = await BuildAsync(root, instance, game, manifest, token).ConfigureAwait(false);
        var store = new RecoverySnapshotStore(instance, game);
        return await store.CaptureAsync(sources, settingsDocument, async cancellation =>
        {
            var current = await BuildAsync(root, instance, game, manifest, cancellation).ConfigureAwait(false);
            if (!sources.SequenceEqual(current)) throw new IOException("采集期间恢复范围发生变化，已保留上一个快照。");
            if (validate is not null) await validate(cancellation).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyList<RecoverySource>> BuildAsync(string root, string instance, string game, string manifest, CancellationToken token = default)
    {
        var plan = new RecoveryCapturePlan(root, instance, game, token);
        if (!MinecraftLibraryService.PathComparer.Equals(Path.GetDirectoryName(Path.GetFullPath(manifest)), plan._instance))
            throw new InvalidDataException("当前清单不属于指定实例。");
        await plan.VisitManifestAsync(Path.GetFullPath(manifest), 0).ConfigureAwait(false);
        plan.AddOwnFiles();
        return plan.Sources();
    }

    internal static IReadOnlyList<RecoverySource> BuildComparison(string root, RecoverySnapshot baseline, CancellationToken token)
    {
        var plan = new RecoveryCapturePlan(root, baseline.InstanceDirectory, baseline.GameDirectory, token);
        var store = new RecoverySnapshotStore(baseline.InstanceDirectory, baseline.GameDirectory);
        foreach (var file in baseline.Files)
        {
            string path = store.ResolveSource(file.Source);
            RecoveryBlobStore.CheckLinks(path);
            if (Directory.Exists(path)) throw new IOException("原文件已被同名目录替代，请先处理目录冲突。");
            if (File.Exists(path)) plan.Add(path);
        }
        string manifest = Path.Combine(plan._instance, Path.GetFileName(plan._instance) + ".json");
        if (File.Exists(manifest)) plan.Add(manifest);
        plan.AddOwnFiles();
        return plan.Sources();
    }

    private System.Collections.ObjectModel.ReadOnlyCollection<RecoverySource> Sources() =>
        Array.AsReadOnly(_sources.OrderBy(item => item.Key, MinecraftLibraryService.PathComparer).Select(item => item.Value).ToArray());

    private void AddOwnFiles()
    {
        foreach (string file in Entries(_instance).Where(File.Exists))
            if (Path.GetExtension(file).Equals(".jar", StringComparison.OrdinalIgnoreCase)) Add(file);
        string metadata = Path.Combine(_instance, "Nexa", "InstanceMetadata.json");
        RecoveryBlobStore.CheckLinks(metadata);
        if (File.Exists(metadata)) Add(metadata);
        foreach (string name in new[] { "mods", "config", "defaultconfigs", "scripts", "kubejs", "resourcepacks", "shaderpacks" })
            Tree(Path.Combine(_game, name), 0);
        foreach (string file in Entries(_game).Where(File.Exists))
            if (Path.GetFileName(file).StartsWith("options", StringComparison.OrdinalIgnoreCase) && Path.GetExtension(file).Equals(".txt", StringComparison.OrdinalIgnoreCase)) Add(file);
    }

    private string[] Entries(string directory)
    {
        _token.ThrowIfCancellationRequested();
        if (_entries.TryGetValue(directory, out var cached)) return cached;
        RecoveryBlobStore.CheckLinks(directory);
        if (!Directory.Exists(directory)) return [];
        List<string> paths = [];
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            _token.ThrowIfCancellationRequested();
            if (++_entryCount > RecoverySnapshotStore.MaxFiles) throw new InvalidDataException("恢复范围枚举超过条目限制。");
            paths.Add(path);
        }
        return _entries[directory] = paths.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void Tree(string directory, int depth)
    {
        if (depth > 64) throw new InvalidDataException("恢复范围目录层级过深。");
        foreach (string path in Entries(directory))
        {
            RecoveryBlobStore.CheckLinks(path);
            if (Directory.Exists(path)) Tree(path, depth + 1); else Add(path);
        }
    }

    private void Add(string path)
    {
        _token.ThrowIfCancellationRequested(); RecoveryBlobStore.CheckLinks(path);
        if (!File.Exists(path)) throw new FileNotFoundException("恢复依赖文件不存在。", path);
        string area, relative;
        if (Within(_instance, path, out relative)) area = "instance";
        else if (Within(_game, path, out relative)) area = "game";
        else if (Within(_root, path, out relative)) area = "root";
        else throw new InvalidDataException("恢复文件超出游戏目录。");
        // Dependencies outside the instance must not be mistaken for general shared game content.
        if (relative.StartsWith("versions" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) area = "root";
        _sources[path] = new(area, relative);
        if (_sources.Count > RecoverySnapshotStore.MaxFiles) throw new InvalidDataException("恢复文件数量超过限制。");
    }

    private static bool Within(string root, string path, out string relative)
    {
        relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private async Task<JsonObject> ReadAsync(string path)
    {
        if (_manifests.TryGetValue(path, out var cached)) return cached;
        RecoveryBlobStore.CheckLinks(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (input.Length > 4 * 1024 * 1024) throw new InvalidDataException("版本清单超过恢复读取预算。");
        byte[] bytes = new byte[(int)input.Length + 1];
        int count = 0, read;
        while (count < bytes.Length && (read = await input.ReadAsync(bytes.AsMemory(count), _token).ConfigureAwait(false)) != 0)
        {
            count += read; _manifestBytes += read;
            if (_manifestBytes > 64 * 1024 * 1024) throw new InvalidDataException("版本清单总读取预算超限。");
        }
        if (count != bytes.Length - 1) throw new InvalidDataException("版本清单读取期间发生变化。");
        return _manifests[path] = JsonNode.Parse(bytes.AsSpan(0, count)) as JsonObject ?? throw new InvalidDataException("版本清单不是对象。");
    }

    private async Task<string?> ResolveAsync(string reference, string local, string extension)
    {
        if (!MinecraftVersionPaths.IsSafeReference(reference)) throw new InvalidDataException("版本依赖名称无效。");
        string versions = Path.Combine(_root, "versions");
        string[] directories = Entries(versions).Where(Directory.Exists).ToArray();
        string? preferred = directories.FirstOrDefault(path => Path.GetFileName(path).Equals(reference, StringComparison.OrdinalIgnoreCase));
        var search = new[] { preferred, local }.Concat(directories).Where(path => path is not null).Distinct(MinecraftLibraryService.PathComparer);
        foreach (string? directory in search)
        {
            var files = Entries(directory!).Where(File.Exists).ToArray();
            string? exact = files.FirstOrDefault(path => Path.GetFileName(path).Equals(reference + extension, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
            if (extension != ".json") continue;
            foreach (string json in files.Where(path => Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)))
            {
                JsonObject value;
                try { value = await ReadAsync(json).ConfigureAwait(false); }
                catch (JsonException) { continue; }
                if (value["id"] is JsonValue id && id.TryGetValue<string>(out var text) && text.Equals(reference, StringComparison.OrdinalIgnoreCase)) return json;
            }
        }
        return null;
    }

    private async Task VisitManifestAsync(string path, int depth)
    {
        if (depth > 64 || !_visiting.Add(path)) throw new InvalidDataException("版本依赖存在循环或层级过深。");
        if (_visited.Contains(path)) { _visiting.Remove(path); return; }
        Add(path);
        var manifest = await ReadAsync(path).ConfigureAwait(false);
        string local = Path.GetDirectoryName(path)!;
        // Match supported renamed-version layouts: manifest stem, manifest id, then directory name.
        var jarNames = new[] { Path.GetFileNameWithoutExtension(path), manifest["id"]?.GetValue<string>(), Path.GetFileName(local) }
            .Where(MinecraftVersionPaths.IsSafeReference).Select(name => name + ".jar").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string jarPath in Entries(local).Where(file => jarNames.Contains(Path.GetFileName(file)))) Add(jarPath);
        foreach (string property in new[] { "inheritsFrom", "jar" })
        {
            string? reference = manifest[property]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(reference)) continue;
            string? dependency = await ResolveAsync(reference, local, ".json").ConfigureAwait(false);
            if (dependency is not null && !(property == "jar" && MinecraftLibraryService.PathComparer.Equals(path, dependency)))
                await VisitManifestAsync(dependency, depth + 1).ConfigureAwait(false);
            else if (property == "inheritsFrom") throw new FileNotFoundException("继承版本清单缺失。", reference);
            string? jar = await ResolveAsync(reference, local, ".jar").ConfigureAwait(false);
            if (jar is not null) Add(jar);
            else if (property == "jar" && dependency is null) throw new FileNotFoundException("核心依赖缺失。", reference);
        }
        _visiting.Remove(path); _visited.Add(path);
    }
}

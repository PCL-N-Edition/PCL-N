using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nexa.Services.Minecraft.Management;

internal sealed record RecoverySource(string Area, string RelativePath);
internal sealed record RecoverySnapshotFile(RecoverySource Source, RecoveryBlob Blob);
internal sealed record RecoverySnapshot(Guid Revision, string InstanceDirectory, string GameDirectory,
    DateTimeOffset CapturedAt, IReadOnlyList<RecoverySnapshotFile> Files, string SettingsDocument);

/// <summary>
/// Atomically publishes a fully copied capture plan. Policy owns plan enumeration, launch success
/// admission, settings extraction and recovery transactions; this store never edits live game files.
/// </summary>
internal sealed class RecoverySnapshotStore
{
    internal const int MaxFiles = 10000;
    private const int MaxManifestBytes = 4 * 1024 * 1024;
    private readonly string _instance, _game, _directory, _manifest;
    private readonly RecoveryBlobStore _blobs;

    internal RecoverySnapshotStore(string instanceDirectory, string gameDirectory)
    {
        _instance = Normalize(instanceDirectory); _game = Normalize(gameDirectory);
        _directory = Path.Combine(_instance, "Nexa", "Recovery");
        _manifest = Path.Combine(_directory, "baseline.json");
        _blobs = new(_directory);
    }

    internal Task<RecoverySnapshot> CaptureAsync(IReadOnlyList<RecoverySource> sources, string settingsDocument, CancellationToken token = default) =>
        CaptureAsync(sources, settingsDocument, null, token);

    internal async Task<RecoverySnapshot> CaptureAsync(IReadOnlyList<RecoverySource> sources, string settingsDocument,
        Func<CancellationToken, Task>? validatePlan, CancellationToken token = default)
    {
        if (sources.Count > MaxFiles) throw new InvalidDataException("快照文件数量超过限制。");
        JsonObject settings = ParseSettings(settingsDocument);
        var paths = sources.Select(ResolveSource).ToArray();
        if (paths.Distinct(MinecraftLibraryService.PathComparer).Count() != paths.Length)
            throw new InvalidDataException("快照计划包含重复文件。");
        await using var lease = await _blobs.AcquireManifestLeaseAsync(token).ConfigureAwait(false);
        List<RecoverySnapshotFile> files = [];
        List<(string Path, long Length, long Modified)> observed = [];
        var budget = new RecoveryByteBudget(RecoveryBlobStore.MaxTransactionBytes);
        for (int index = 0; index < paths.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            string path = paths[index];
            RecoveryBlobStore.CheckLinks(path);
            var info = new FileInfo(path);
            long length = info.Length, modified = info.LastWriteTimeUtc.Ticks;
            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            var blob = await _blobs.StoreAsync(source, length, budget, token).ConfigureAwait(false);
            files.Add(new(sources[index], blob)); observed.Add((path, length, modified));
        }
        if (validatePlan is not null) await validatePlan(token).ConfigureAwait(false);
        foreach (var item in observed)
        {
            token.ThrowIfCancellationRequested();
            RecoveryBlobStore.CheckLinks(item.Path);
            var info = new FileInfo(item.Path);
            if (!info.Exists || info.Length != item.Length || info.LastWriteTimeUtc.Ticks != item.Modified)
                throw new IOException("采集期间文件发生变化，已保留上一个快照。");
        }
        var snapshot = new RecoverySnapshot(Guid.NewGuid(), _instance, _game, DateTimeOffset.UtcNow, files.AsReadOnly(), settings.ToJsonString());
        JsonArray entries = [];
        foreach (var file in snapshot.Files)
            entries.Add((JsonNode)new JsonObject { ["area"] = file.Source.Area, ["path"] = file.Source.RelativePath, ["sha256"] = file.Blob.Sha256, ["length"] = file.Blob.Length });
        var manifest = new JsonObject
        {
            ["version"] = 1,
            ["revision"] = snapshot.Revision.ToString("D"),
            ["instance"] = _instance,
            ["game"] = _game,
            ["capturedAt"] = snapshot.CapturedAt.ToString("O", CultureInfo.InvariantCulture),
            ["files"] = entries,
            ["settings"] = settings,
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, RecoveryJsonContext.Default.JsonObject);
        if (bytes.Length > MaxManifestBytes) throw new InvalidDataException("快照清单超过大小限制。");
        string temporary = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".manifest.part");
        try
        {
            RecoveryBlobStore.CheckLinks(_manifest);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await output.WriteAsync(bytes, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, _manifest, overwrite: true);
            // Cleanup failure cannot undo a committed baseline. Retry on the next capture.
            try { await _blobs.CollectUnreferencedAsync(files.Select(file => file.Blob.Sha256).ToHashSet(StringComparer.Ordinal), token).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException) { }
            return snapshot;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal async Task<RecoverySnapshot?> ReadAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        RecoveryBlobStore.CheckLinks(_manifest);
        if (!File.Exists(_manifest)) return null;
        await using var lease = await _blobs.AcquireManifestLeaseAsync(token).ConfigureAwait(false);
        RecoveryBlobStore.CheckLinks(_manifest);
        if (!File.Exists(_manifest)) return null;
        await using var input = new FileStream(_manifest, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        long length = input.Length;
        if (length > MaxManifestBytes) throw new InvalidDataException("快照清单超过大小限制。");
        byte[] bytes = new byte[(int)length + 1];
        int total = 0, read;
        while (total < bytes.Length && (read = await input.ReadAsync(bytes.AsMemory(total), token).ConfigureAwait(false)) != 0) total += read;
        if (total != length) throw new InvalidDataException("快照清单读取期间发生变化。");
        var document = JsonNode.Parse(bytes.AsSpan(0, total)) as JsonObject ?? throw new InvalidDataException("快照清单无效。");
        if (document["version"]?.GetValue<int>() != 1
            || !MinecraftLibraryService.PathComparer.Equals(document["instance"]?.GetValue<string>(), _instance)
            || !MinecraftLibraryService.PathComparer.Equals(document["game"]?.GetValue<string>(), _game)
            || document["files"] is not JsonArray entries || entries.Count > MaxFiles)
            throw new InvalidDataException("快照清单不属于当前实例或格式不受支持。");
        List<RecoverySnapshotFile> files = [];
        HashSet<string> unique = new(MinecraftLibraryService.PathComparer);
        long totalLength = 0;
        foreach (var item in entries)
        {
            var source = new RecoverySource(item?["area"]?.GetValue<string>() ?? "", item?["path"]?.GetValue<string>() ?? "");
            if (!unique.Add(ResolveSource(source))) throw new InvalidDataException("快照清单包含重复路径。");
            string hash = item?["sha256"]?.GetValue<string>() ?? "";
            long size = item?["length"]?.GetValue<long>() ?? -1;
            if (size is < 0 or > RecoveryBlobStore.MaxFileBytes || hash.Length != 64 || hash.Any(c => c is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
                throw new InvalidDataException("快照对象记录无效。");
            totalLength += size;
            if (totalLength > RecoveryBlobStore.MaxTransactionBytes) throw new InvalidDataException("快照总大小超过限制。");
            files.Add(new(source, new(hash, size)));
        }
        if (!Guid.TryParse(document["revision"]?.GetValue<string>(), out var revision)
            || !DateTimeOffset.TryParseExact(document["capturedAt"]?.GetValue<string>(), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var capturedAt))
            throw new InvalidDataException("快照版本或时间无效。");
        return new(revision, _instance, _game, capturedAt,
            files.AsReadOnly(), ParseSettings(document["settings"]?.ToJsonString() ?? "").ToJsonString());
    }

    internal string ResolveSource(RecoverySource source)
    {
        string root = source.Area switch
        {
            "instance" => _instance,
            "game" => _game,
            "root" when Directory.GetParent(_instance) is { Name: "versions", Parent: { } parent } => parent.FullName,
            _ => throw new InvalidDataException("快照文件区域无效。")
        };
        string relative = source.RelativePath.Replace('\\', '/');
        if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.Contains(':') || part.TrimEnd(' ', '.') != part))
            throw new InvalidDataException("快照相对路径无效。");
        if (source.Area == "root" && (relative.Split('/') is not ["versions", _, var file]
            || !new[] { ".json", ".jar" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("快照依赖只能包含版本清单与核心文件。");
        string full = Path.GetFullPath(Path.Combine(root, relative));
        string comparisonPath = Path.GetRelativePath(_directory, full);
        if (comparisonPath == "." || !comparisonPath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(comparisonPath))
            throw new InvalidDataException("不能将快照目录本身作为备份来源。");
        return full;
    }

    private static JsonObject ParseSettings(string document)
    {
        if (document.Length > 1024 * 1024) throw new InvalidDataException("启动设置快照超过大小限制。");
        return JsonNode.Parse(document) as JsonObject ?? throw new InvalidDataException("启动设置快照无效。");
    }

    private static string Normalize(string path) => Path.IsPathFullyQualified(path)
        ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) : throw new InvalidDataException("实例目录必须为绝对路径。");
}

[System.Text.Json.Serialization.JsonSerializable(typeof(JsonObject))]
internal partial class RecoveryJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nexa.Services.Minecraft.Management;

internal sealed partial class RecoverySnapshotStore
{
    private string HistoryDirectory => Path.Combine(_directory, "history");

    internal async Task<IReadOnlyList<RecoverySnapshot>> ListAsync(CancellationToken token = default)
    {
        RecoveryBlobStore.CheckLinks(_directory);
        if (!Directory.Exists(_directory)) return [];
        await using var lease = await _blobs.AcquireManifestLeaseAsync(token).ConfigureAwait(false);
        var history = await ReadHistoryUnderLeaseAsync(token).ConfigureAwait(false);
        var latest = await ReadManifestFileAsync(_manifest, token, allowPreviousGame: true).ConfigureAwait(false);
        if (latest is not null) { history.RemoveAll(item => item.Revision == latest.Revision); history.Add(latest); }
        return Array.AsReadOnly(history.OrderByDescending(item => item.CapturedAt).ToArray());
    }

    private async Task<List<RecoverySnapshot>> ReadHistoryUnderLeaseAsync(CancellationToken token)
    {
        RecoveryBlobStore.CheckLinks(HistoryDirectory);
        List<RecoverySnapshot> result = [];
        if (!Directory.Exists(HistoryDirectory)) return result;
        long bytes = 0, entries = 0;
        foreach (string path in Directory.EnumerateFiles(HistoryDirectory, "*.json"))
        {
            token.ThrowIfCancellationRequested();
            RecoveryBlobStore.CheckLinks(path);
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var revision))
                throw new InvalidDataException("历史快照文件名无效。");
            bytes += new FileInfo(path).Length;
            if (result.Count >= 1024 || bytes > 64L * 1024 * 1024) throw new InvalidDataException("历史快照清单超过读取预算。");
            var snapshot = await ReadManifestFileAsync(path, token, allowPreviousGame: true).ConfigureAwait(false)
                ?? throw new IOException("历史快照读取期间发生变化。");
            entries += snapshot.Files.Count;
            if (snapshot.Revision != revision || entries > 100000) throw new InvalidDataException("历史快照内容无效或超过读取预算。");
            result.Add(snapshot);
        }
        return result;
    }

    private async Task ArchiveCurrentUnderLeaseAsync(List<RecoverySnapshot> history, CancellationToken token)
    {
        var current = await ReadManifestFileAsync(_manifest, token, allowPreviousGame: true).ConfigureAwait(false);
        if (current is null) return;
        if (history.FirstOrDefault(item => item.Revision == current.Revision) is { } existing)
        {
            if (!JsonNode.DeepEquals(EncodeManifest(existing), EncodeManifest(current)))
                throw new InvalidDataException("历史快照与当前基线的同一版本记录不一致。");
            return;
        }
        if (history.Count >= 1024 || history.Sum(item => (long)item.Files.Count) + current.Files.Count > 100000)
            throw new InvalidDataException("历史快照数量超过限制，请先清理旧快照。");
        RecoveryBlobStore.CheckLinks(HistoryDirectory); Directory.CreateDirectory(HistoryDirectory);
        RecoveryBlobStore.CheckLinks(HistoryDirectory);
        string target = Path.Combine(HistoryDirectory, current.Revision.ToString("N") + ".json");
        string temporary = Path.Combine(HistoryDirectory, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(EncodeManifest(current), RecoveryJsonContext.Default.JsonObject);
            long existingBytes = Directory.EnumerateFiles(HistoryDirectory, "*.json").Sum(path => new FileInfo(path).Length);
            if (existingBytes + bytes.Length > 64L * 1024 * 1024) throw new InvalidDataException("历史快照清单超过大小限制。");
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await output.WriteAsync(bytes, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
            }
            token.ThrowIfCancellationRequested(); RecoveryBlobStore.CheckLinks(target);
            File.Move(temporary, target);
            history.Add(current);
        }
        finally { RecoveryBlobStore.CheckLinks(temporary); if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void DeleteHistoryUnderLease(IReadOnlyList<RecoverySnapshot> history)
    {
        foreach (var snapshot in history)
        {
            string path = Path.Combine(HistoryDirectory, snapshot.Revision.ToString("N") + ".json");
            RecoveryBlobStore.CheckLinks(path); File.Delete(path);
        }
    }

    private static JsonObject EncodeManifest(RecoverySnapshot snapshot)
    {
        JsonArray entries = [];
        foreach (var file in snapshot.Files)
            entries.Add((JsonNode)new JsonObject { ["area"] = file.Source.Area, ["path"] = file.Source.RelativePath, ["sha256"] = file.Blob.Sha256, ["length"] = file.Blob.Length });
        return new JsonObject
        {
            ["version"] = 1,
            ["revision"] = snapshot.Revision.ToString("D"),
            ["instance"] = snapshot.InstanceDirectory,
            ["game"] = snapshot.GameDirectory,
            ["capturedAt"] = snapshot.CapturedAt.ToString("O", CultureInfo.InvariantCulture),
            ["files"] = entries,
            ["settings"] = JsonNode.Parse(snapshot.SettingsDocument)
        };
    }
}

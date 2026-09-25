using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nexa.Services.Minecraft.Management;

internal sealed record RecoveryPreparedRestore(Guid TransactionId, string Directory, RecoverySnapshot Snapshot);

/// <summary>
/// Preparation only: no live file mutation. A durable prepared record is published only after
/// every baseline blob has been verified and flushed. Apply requires a separate guarded journal.
/// </summary>
internal static class RecoveryRestorePreparation
{
    // Reopen after process interruption. On-disk records and staging files are untrusted inputs;
    // revalidate identity, scope, size and content rather than trusting a completed filename.
    internal static async Task<RecoveryPreparedRestore> ReadAsync(string instance, string game,
        Guid transactionId, CancellationToken token = default)
    {
        var snapshots = new RecoverySnapshotStore(instance, game);
        string directory = Path.Combine(Path.GetFullPath(instance), "Nexa", "Recovery", "transactions", transactionId.ToString("N"));
        string path = Path.Combine(directory, "prepared.json");
        RecoveryBlobStore.CheckLinks(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        long size = input.Length;
        if (size > 4 * 1024 * 1024) throw new InvalidDataException("恢复准备记录超过大小限制。");
        byte[] bytes = new byte[(int)size + 1];
        int count = 0, read;
        while (count < bytes.Length && (read = await input.ReadAsync(bytes.AsMemory(count), token).ConfigureAwait(false)) != 0) count += read;
        if (count != size) throw new InvalidDataException("恢复准备记录读取期间发生变化。");
        var record = JsonNode.Parse(bytes.AsSpan(0, count)) as JsonObject ?? throw new InvalidDataException("恢复准备记录无效。");
        if (record["transaction"]?.GetValue<string>() != transactionId.ToString("D")
            || record["phase"]?.GetValue<string>() != "prepared")
            throw new InvalidDataException("恢复事务身份或阶段无效。");
        record["revision"] = record["baseline"]?.DeepClone();
        var snapshot = snapshots.DecodeManifest(record);
        var verified = new Dictionary<string, RecoveryBlob>(StringComparer.Ordinal);
        var budget = new RecoveryByteBudget(RecoveryBlobStore.MaxTransactionBytes);
        foreach (var file in snapshot.Files)
        {
            token.ThrowIfCancellationRequested();
            if (verified.TryGetValue(file.Blob.Sha256, out var previous))
            {
                if (previous.Length != file.Blob.Length) throw new InvalidDataException("恢复暂存对象长度不一致。");
                continue;
            }
            string staged = Path.Combine(directory, file.Blob.Sha256 + ".data");
            RecoveryBlobStore.CheckLinks(staged);
            await using var content = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            string hash = await RecoveryBlobStore.CopyAndHashAsync(content, Stream.Null, file.Blob.Length, budget, token).ConfigureAwait(false);
            if (hash != file.Blob.Sha256) throw new InvalidDataException("恢复暂存内容校验失败。");
            verified.Add(hash, file.Blob);
        }
        return new(transactionId, directory, snapshot);
    }

    internal static async Task<RecoveryPreparedRestore> PrepareAsync(string instance, string game,
        Guid expectedRevision, CancellationToken token = default)
    {
        // Store validates absolute identity before any directory is created.
        var snapshots = new RecoverySnapshotStore(instance, game);
        string recovery = Path.Combine(Path.GetFullPath(instance), "Nexa", "Recovery");
        var blobs = new RecoveryBlobStore(recovery);
        await using var lease = await blobs.AcquireManifestLeaseAsync(token).ConfigureAwait(false);
        var snapshot = await snapshots.ReadUnderManifestLeaseAsync(token).ConfigureAwait(false)
            ?? throw new InvalidDataException("尚无可恢复的成功快照。");
        if (snapshot.Revision != expectedRevision) throw new InvalidDataException("成功快照已变化，请重新查看更改清单。");

        string parent = Path.Combine(recovery, "transactions");
        RecoveryBlobStore.CheckLinks(parent);
        Directory.CreateDirectory(parent);
        RecoveryBlobStore.CheckLinks(parent);
        Guid transactionId = Guid.NewGuid();
        string directory = Path.Combine(parent, transactionId.ToString("N"));
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("恢复暂存目录已存在。");
        Directory.CreateDirectory(directory);
        RecoveryBlobStore.CheckLinks(directory);
        List<string> created = [];
        bool committed = false;
        try
        {
            var budget = new RecoveryByteBudget(RecoveryBlobStore.MaxTransactionBytes);
            var unique = new Dictionary<string, RecoveryBlob>(StringComparer.Ordinal);
            JsonArray files = [];
            foreach (var file in snapshot.Files)
            {
                token.ThrowIfCancellationRequested();
                if (unique.TryGetValue(file.Blob.Sha256, out var existing))
                {
                    if (existing.Length != file.Blob.Length) throw new InvalidDataException("快照对象长度不一致。");
                }
                else
                {
                    string destination = Path.Combine(directory, file.Blob.Sha256 + ".data");
                    RecoveryBlobStore.CheckLinks(destination);
                    await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        created.Add(destination);
                        await blobs.CopyVerifiedAsync(file.Blob, output, budget, token).ConfigureAwait(false);
                        await output.FlushAsync(token).ConfigureAwait(false);
                        output.Flush(flushToDisk: true);
                    }
                    unique.Add(file.Blob.Sha256, file.Blob);
                }
                files.Add((JsonNode)new JsonObject
                {
                    ["area"] = file.Source.Area,
                    ["path"] = file.Source.RelativePath,
                    ["sha256"] = file.Blob.Sha256,
                    ["length"] = file.Blob.Length
                });
            }
            var record = new JsonObject
            {
                ["version"] = 1,
                ["phase"] = "prepared",
                ["transaction"] = transactionId.ToString("D"),
                ["baseline"] = snapshot.Revision.ToString("D"),
                ["instance"] = snapshot.InstanceDirectory,
                ["game"] = snapshot.GameDirectory,
                ["capturedAt"] = snapshot.CapturedAt.ToString("O", CultureInfo.InvariantCulture),
                ["files"] = files,
                ["settings"] = JsonNode.Parse(snapshot.SettingsDocument)
            };
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record, RecoveryJsonContext.Default.JsonObject);
            if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("恢复准备记录超过大小限制。");
            string pending = Path.Combine(directory, "prepared.part");
            RecoveryBlobStore.CheckLinks(pending);
            await using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                created.Add(pending);
                await output.WriteAsync(bytes, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            string complete = Path.Combine(directory, "prepared.json");
            RecoveryBlobStore.CheckLinks(complete);
            File.Move(pending, complete);
            committed = true;
            return new(transactionId, directory, snapshot);
        }
        finally
        {
            if (!committed)
            {
                // Only files created by this attempt; never recursively delete a supplied path.
                foreach (string path in created)
                {
                    try { RecoveryBlobStore.CheckLinks(path); File.Delete(path); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
                try { RecoveryBlobStore.CheckLinks(directory); Directory.Delete(directory, recursive: false); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                try { RecoveryBlobStore.CheckLinks(parent); Directory.Delete(parent, recursive: false); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}

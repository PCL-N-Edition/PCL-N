using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nexa.Services.Minecraft.Management;

internal sealed record RecoveryFileEdit(RecoverySource Source, RecoveryBlob? Before, RecoveryBlob? After);

/// <summary>File phase only. The service must exclude active games/mutations and coordinate settings.</summary>
internal static class RecoveryFileTransaction
{
    internal static async Task ApplyAsync(RecoveryPreparedRestore prepared, IReadOnlyList<RecoveryFileEdit> edits,
        CancellationToken token = default)
    {
        var store = new RecoverySnapshotStore(prepared.Snapshot.InstanceDirectory, prepared.Snapshot.GameDirectory);
        Validate(prepared, store, edits);
        string journal = Path.Combine(prepared.Directory, "apply.json");
        RecoveryBlobStore.CheckLinks(journal);
        if (File.Exists(journal) || File.Exists(Path.Combine(prepared.Directory, "file-plan.json"))) throw new IOException("此恢复事务已经开始，请先完成或撤回。");
        var budget = new RecoveryByteBudget(RecoveryBlobStore.MaxTransactionBytes);
        // Nothing live changes until every original has been validated and durably backed up.
        foreach (var edit in edits)
        {
            string path = store.ResolveSource(edit.Source);
            await VerifyAsync(path, edit.Before, budget, token).ConfigureAwait(false);
            if (edit.Before is { } before)
            {
                string backup = BackupPath(prepared, before);
                RecoveryBlobStore.CheckLinks(backup);
                if (!File.Exists(backup)) await CopyVerifiedAsync(path, backup, before, token).ConfigureAwait(false);
                else await VerifyAsync(backup, before, new(RecoveryBlobStore.MaxTransactionBytes), token).ConfigureAwait(false);
            }
        }
        string planHash = await WritePlanAsync(prepared, edits, token).ConfigureAwait(false);
        await WriteJournalAsync(prepared, planHash, "applying", 0, token).ConfigureAwait(false);
        try
        {
            for (int index = 0; index < edits.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var edit = edits[index];
                string path = store.ResolveSource(edit.Source);
                await VerifyAsync(path, edit.Before, new(RecoveryBlobStore.MaxFileBytes), token).ConfigureAwait(false);
                await WriteJournalAsync(prepared, planHash, "applying", index + 1, token).ConfigureAwait(false);
                await ReplaceAsync(path, edit.After is { } after ? Path.Combine(prepared.Directory, after.Sha256 + ".data") : null,
                    edit.After, token).ConfigureAwait(false);
            }
            await WriteJournalAsync(prepared, planHash, "files-applied", edits.Count, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            // Once a write intent exists, cancellation must not interrupt its compensating work.
            try { await RollbackAsync(prepared, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception rollbackError) when (rollbackError is not OutOfMemoryException and not AccessViolationException)
            { throw new AggregateException("恢复未完成，撤回遇到冲突；已保留事务备份。", error, rollbackError); }
            throw;
        }
    }

    internal static async Task RollbackAsync(RecoveryPreparedRestore prepared, CancellationToken token = default)
    {
        var (edits, phase, attempted, planHash) = await ReadJournalAsync(prepared, token).ConfigureAwait(false);
        if (phase == "rolled-back") return;
        if (phase is not ("applying" or "files-applied" or "rolling-back")) throw new InvalidDataException("恢复事务阶段不支持撤回。");
        var store = new RecoverySnapshotStore(prepared.Snapshot.InstanceDirectory, prepared.Snapshot.GameDirectory);
        Validate(prepared, store, edits);
        await WriteJournalAsync(prepared, planHash, "rolling-back", attempted, token).ConfigureAwait(false);
        for (int index = attempted - 1; index >= 0; index--)
        {
            var edit = edits[index];
            string path = store.ResolveSource(edit.Source);
            var current = await ObserveAsync(path, new(RecoveryBlobStore.MaxFileBytes), token).ConfigureAwait(false);
            if (current != edit.Before)
            {
                if (current != edit.After) throw new IOException("恢复后文件再次发生变化，已保留备份，请手动处理冲突。");
                await ReplaceAsync(path, edit.Before is { } before ? BackupPath(prepared, before) : null, edit.Before, token).ConfigureAwait(false);
            }
            await WriteJournalAsync(prepared, planHash, "rolling-back", index, token).ConfigureAwait(false);
        }
        await WriteJournalAsync(prepared, planHash, "rolled-back", 0, token).ConfigureAwait(false);
    }

    internal static void Validate(RecoveryPreparedRestore prepared, RecoverySnapshotStore store, IReadOnlyList<RecoveryFileEdit> edits)
    {
        var versions = Directory.GetParent(prepared.Snapshot.InstanceDirectory);
        if (versions?.Name != "versions" || versions.Parent is null
            || !MinecraftLibraryService.PathComparer.Equals(prepared.Snapshot.GameDirectory, prepared.Snapshot.InstanceDirectory)
            && !MinecraftLibraryService.PathComparer.Equals(prepared.Snapshot.GameDirectory, versions.Parent.FullName))
            throw new InvalidDataException("恢复游戏目录不属于当前实例。");
        string expected = Path.Combine(prepared.Snapshot.InstanceDirectory, "Nexa", "Recovery", "transactions", prepared.TransactionId.ToString("N"));
        if (!MinecraftLibraryService.PathComparer.Equals(expected, prepared.Directory) || edits.Count > RecoverySnapshotStore.MaxFiles)
            throw new InvalidDataException("恢复事务身份或大小无效。");
        RecoveryBlobStore.CheckLinks(expected);
        HashSet<string> paths = new(MinecraftLibraryService.PathComparer);
        long beforeBytes = 0, afterBytes = 0;
        var baseline = prepared.Snapshot.Files.ToDictionary(file => store.ResolveSource(file.Source), MinecraftLibraryService.PathComparer);
        foreach (var edit in edits)
        {
            string path = store.ResolveSource(edit.Source);
            if (!paths.Add(path) || !Allowed(edit.Source, prepared.Snapshot)) throw new InvalidDataException("恢复条目重复或超出允许范围。");
            foreach (var blob in new[] { edit.Before, edit.After })
                if (blob is not null && (blob.Length is < 0 or > RecoveryBlobStore.MaxFileBytes || blob.Sha256.Length != 64
                    || blob.Sha256.Any(c => c is not (>= '0' and <= '9' or >= 'A' and <= 'F')))) throw new InvalidDataException("恢复摘要无效。");
            beforeBytes += edit.Before?.Length ?? 0; afterBytes += edit.After?.Length ?? 0;
            if (beforeBytes > RecoveryBlobStore.MaxTransactionBytes || afterBytes > RecoveryBlobStore.MaxTransactionBytes)
                throw new InvalidDataException("恢复内容超过事务预算。");
            if (edit.After is { } target)
            {
                if (!baseline.TryGetValue(path, out var file) || file.Blob != target) throw new InvalidDataException("恢复目标不属于准备快照。");
            }
            else if (edit.Before is null || baseline.ContainsKey(path) || edit.Source.Area == "root")
                throw new InvalidDataException("只能移除本实例恢复范围内的新增文件。");
            else if (edit.Source.Area == "instance" && MinecraftLibraryService.PathComparer.Equals(Path.GetDirectoryName(path), prepared.Snapshot.InstanceDirectory)
                && Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)
                && !MinecraftLibraryService.PathComparer.Equals(Path.GetFileName(path), Path.GetFileName(prepared.Snapshot.InstanceDirectory) + ".json"))
                throw new InvalidDataException("不能移除无关的实例清单。");
        }
    }

    private static bool Allowed(RecoverySource source, RecoverySnapshot snapshot)
    {
        string[] parts = source.RelativePath.Replace('\\', '/').Split('/');
        if (source.Area == "root") return parts is ["versions", _, var file] && Path.GetExtension(file).ToLowerInvariant() is ".json" or ".jar";
        if (source.Area == "instance" && (parts is [var name] && Path.GetExtension(name).ToLowerInvariant() is ".json" or ".jar"
            || parts is ["Nexa", "InstanceMetadata.json"])) return true;
        if (source.Area != "game" && !(source.Area == "instance"
            && MinecraftLibraryService.PathComparer.Equals(snapshot.InstanceDirectory, snapshot.GameDirectory))) return false;
        return parts.Length >= 2 && parts[0] is "mods" or "config" or "defaultconfigs" or "scripts" or "kubejs" or "resourcepacks" or "shaderpacks"
            || parts is [var option] && option.StartsWith("options", StringComparison.OrdinalIgnoreCase) && Path.GetExtension(option).Equals(".txt", StringComparison.OrdinalIgnoreCase);
    }

    private static string BackupPath(RecoveryPreparedRestore prepared, RecoveryBlob blob) => Path.Combine(prepared.Directory, blob.Sha256 + ".before");

    internal static async Task<RecoveryBlob?> ObserveAsync(string path, RecoveryByteBudget budget, CancellationToken token)
    {
        RecoveryBlobStore.CheckLinks(path);
        if (Directory.Exists(path)) throw new IOException("恢复文件被同名目录替代。");
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        long length = stream.Length;
        if (length > RecoveryBlobStore.MaxFileBytes) throw new InvalidDataException("恢复文件过大。");
        string hash = await RecoveryBlobStore.CopyAndHashAsync(stream, Stream.Null, length, budget, token).ConfigureAwait(false);
        return new(hash, length);
    }

    private static async Task VerifyAsync(string path, RecoveryBlob? expected, RecoveryByteBudget budget, CancellationToken token)
    {
        if (await ObserveAsync(path, budget, token).ConfigureAwait(false) != expected) throw new IOException("恢复范围已变化，请重新比较。");
    }

    private static async Task CopyVerifiedAsync(string source, string target, RecoveryBlob blob, CancellationToken token)
    {
        RecoveryBlobStore.CheckLinks(source); RecoveryBlobStore.CheckLinks(target);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        string hash = await RecoveryBlobStore.CopyAndHashAsync(input, output, blob.Length, new(RecoveryBlobStore.MaxFileBytes), token).ConfigureAwait(false);
        if (hash != blob.Sha256) throw new InvalidDataException("恢复文件校验失败。");
        await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
    }

    private static async Task ReplaceAsync(string target, string? source, RecoveryBlob? content, CancellationToken token)
    {
        RecoveryBlobStore.CheckLinks(target); token.ThrowIfCancellationRequested();
        if (content is null) { File.Delete(target); return; }
        string parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent); RecoveryBlobStore.CheckLinks(parent);
        string temporary = Path.Combine(parent, ".nexa-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            await CopyVerifiedAsync(source!, temporary, content, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); RecoveryBlobStore.CheckLinks(target);
            File.Move(temporary, target, overwrite: true);
        }
        finally { RecoveryBlobStore.CheckLinks(temporary); if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<string> WritePlanAsync(RecoveryPreparedRestore prepared, IReadOnlyList<RecoveryFileEdit> edits, CancellationToken token)
    {
        static JsonNode? Blob(RecoveryBlob? blob) => blob is null ? null : new JsonObject { ["sha256"] = blob.Sha256, ["length"] = blob.Length };
        JsonArray entries = [];
        foreach (var edit in edits) entries.Add((JsonNode)new JsonObject { ["area"] = edit.Source.Area, ["path"] = edit.Source.RelativePath, ["before"] = Blob(edit.Before), ["after"] = Blob(edit.After) });
        var document = new JsonObject
        {
            ["version"] = 1,
            ["transaction"] = prepared.TransactionId.ToString("D"),
            ["baseline"] = prepared.Snapshot.Revision.ToString("D"),
            ["files"] = entries
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, RecoveryJsonContext.Default.JsonObject);
        if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("恢复应用计划过大。");
        await WriteRecordAsync(prepared, "file-plan.json", bytes, token).ConfigureAwait(false);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static Task WriteJournalAsync(RecoveryPreparedRestore prepared, string planHash, string phase, int attempted, CancellationToken token)
    {
        var document = new JsonObject
        {
            ["version"] = 1,
            ["transaction"] = prepared.TransactionId.ToString("D"),
            ["planSha256"] = planHash,
            ["phase"] = phase,
            ["attempted"] = attempted
        };
        return WriteRecordAsync(prepared, "apply.json", JsonSerializer.SerializeToUtf8Bytes(document, RecoveryJsonContext.Default.JsonObject), token);
    }

    internal static async Task WriteRecordAsync(RecoveryPreparedRestore prepared, string name, byte[] bytes, CancellationToken token)
    {
        string target = Path.Combine(prepared.Directory, name), temporary = Path.Combine(prepared.Directory, Guid.NewGuid().ToString("N") + ".journal.part");
        RecoveryBlobStore.CheckLinks(temporary);
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            { await output.WriteAsync(bytes, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true); }
            token.ThrowIfCancellationRequested(); RecoveryBlobStore.CheckLinks(target);
            File.Move(temporary, target, overwrite: true);
        }
        finally { RecoveryBlobStore.CheckLinks(temporary); if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static async Task<byte[]> ReadRecordAsync(string path, int limit, CancellationToken token)
    {
        RecoveryBlobStore.CheckLinks(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        long length = stream.Length;
        if (length > limit) throw new InvalidDataException("恢复应用日志过大。");
        byte[] bytes = new byte[(int)length + 1]; int count = 0, read;
        while (count < bytes.Length && (read = await stream.ReadAsync(bytes.AsMemory(count), token).ConfigureAwait(false)) != 0) count += read;
        if (count != length) throw new InvalidDataException("恢复应用日志读取期间变化。");
        return bytes.AsSpan(0, count).ToArray();
    }

    private static async Task<(IReadOnlyList<RecoveryFileEdit> Edits, string Phase, int Attempted, string PlanHash)> ReadJournalAsync(RecoveryPreparedRestore prepared, CancellationToken token)
    {
        byte[] progress = await ReadRecordAsync(Path.Combine(prepared.Directory, "apply.json"), 4096, token).ConfigureAwait(false);
        var marker = JsonNode.Parse(progress) as JsonObject ?? throw new InvalidDataException("恢复进度记录无效。");
        if (marker["version"]?.GetValue<int>() != 1 || marker["transaction"]?.GetValue<string>() != prepared.TransactionId.ToString("D"))
            throw new InvalidDataException("恢复进度身份无效。");
        byte[] plan = await ReadRecordAsync(Path.Combine(prepared.Directory, "file-plan.json"), 4 * 1024 * 1024, token).ConfigureAwait(false);
        string planHash = Convert.ToHexString(SHA256.HashData(plan));
        if (marker["planSha256"]?.GetValue<string>() != planHash) throw new InvalidDataException("恢复应用计划已变化。");
        var doc = JsonNode.Parse(plan) as JsonObject ?? throw new InvalidDataException("恢复计划无效。");
        if (doc["version"]?.GetValue<int>() != 1 || doc["transaction"]?.GetValue<string>() != prepared.TransactionId.ToString("D")
            || doc["baseline"]?.GetValue<string>() != prepared.Snapshot.Revision.ToString("D") || doc["files"] is not JsonArray files
            || files.Count > RecoverySnapshotStore.MaxFiles) throw new InvalidDataException("恢复日志身份无效。");
        static RecoveryBlob? Blob(JsonNode? node) => node is null ? null : new(node["sha256"]!.GetValue<string>(), node["length"]!.GetValue<long>());
        var edits = files.Select(item => new RecoveryFileEdit(new(item!["area"]!.GetValue<string>(), item["path"]!.GetValue<string>()), Blob(item["before"]), Blob(item["after"]))).ToArray();
        int attempted = marker["attempted"]!.GetValue<int>();
        if (attempted < 0 || attempted > edits.Length) throw new InvalidDataException("恢复日志进度无效。");
        return (edits, marker["phase"]!.GetValue<string>(), attempted, planHash);
    }
}

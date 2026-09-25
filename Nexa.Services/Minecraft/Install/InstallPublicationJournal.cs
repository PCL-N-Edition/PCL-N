using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Management;

namespace Nexa.Services.Minecraft.Install;

internal sealed record InstallPublicationFile(string RelativePath, RecoveryBlob? Before, RecoveryBlob? After);

/// <summary>Durable publish/undo phase. Callers hold the root lease and exclude running games.</summary>
internal sealed class InstallPublicationJournal
{
    private readonly string _root, _stage, _journal;
    private readonly string _instance;
    private readonly IReadOnlyList<InstallPublicationFile> _files;
    private readonly string _hash;
    private const int RecordLimit = 16 * 1024 * 1024;
    internal string InstanceId => _instance;
    internal async Task<string> ReadPhaseAsync(CancellationToken token) => (await ReadProgressAsync(token).ConfigureAwait(false)).Phase;

    private InstallPublicationJournal(string root, string stage, string instance, IReadOnlyList<InstallPublicationFile> files, string hash)
    { _root = root; _stage = stage; _journal = Path.Combine(stage, ".publication"); _instance = instance; _files = files; _hash = hash; }

    internal static async Task<InstallPublicationJournal> PrepareAsync(string root, string stage, string instance,
        IReadOnlyList<string> generated, IReadOnlyDictionary<string, string> removals, CancellationToken token)
    {
        ValidateIdentity(root, stage, instance);
        string journal = Path.Combine(stage, ".publication");
        RecoveryBlobStore.CheckLinks(journal);
        if (Directory.Exists(journal)) throw new IOException("安装已有发布记录，必须先恢复该事务。");
        RecoveryRecordAuthority.VerifyAbsent(Path.Combine(journal, "plan.json"));
        Directory.CreateDirectory(journal);
        var budget = new RecoveryByteBudget(RecoveryBlobStore.MaxTransactionBytes);
        Dictionary<string, InstallPublicationFile> files = new(MinecraftLibraryService.PathComparer);
        foreach (var removed in removals)
        {
            string target = Target(root, instance, removed.Key);
            if (!IsMod(removed.Key, instance)) throw new InvalidDataException("安装不能删除非组件文件。");
            var before = await ObserveAsync(target, budget, token).ConfigureAwait(false);
            if (before is null || before.Sha256 != removed.Value) continue;
            files.Add(removed.Key, new(removed.Key, before, null));
        }
        foreach (string relative in generated)
        {
            if (files.Count >= 100000) throw new InvalidDataException("安装发布文件过多。");
            string source = ForgeInstallService.Contained(stage, relative), target = Target(root, instance, relative);
            var after = await ObserveAsync(source, budget, token).ConfigureAwait(false) ?? throw new FileNotFoundException("安装产物丢失。", source);
            var before = files.TryGetValue(relative, out var existing) ? existing.Before
                : await ObserveAsync(target, budget, token).ConfigureAwait(false);
            if (before == after) { files.Remove(relative); continue; }
            if (IsMod(relative, instance) && before is not null && !files.ContainsKey(relative))
                throw new IOException("已有同名 Mod 未受此安装管理或已被修改：" + Path.GetFileName(relative));
            files[relative] = new(relative, before, after);
        }
        string manifest = $"versions/{instance}/{instance}.json";
        var ordered = files.Values.OrderBy(file => file.RelativePath == manifest ? 1 : 0).ThenBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
        foreach (var file in ordered)
        {
            if (file.Before is { } before) await SaveObjectAsync(Target(root, instance, file.RelativePath), journal, before, token).ConfigureAwait(false);
            if (file.After is { } after) await SaveObjectAsync(ForgeInstallService.Contained(stage, file.RelativePath), journal, after, token).ConfigureAwait(false);
        }
        var document = new JsonObject
        {
            ["version"] = 1,
            ["root"] = root,
            ["stage"] = stage,
            ["instance"] = instance,
            ["files"] = new JsonArray(ordered.Select(file => (JsonNode)new JsonObject { ["path"] = file.RelativePath, ["before"] = Encode(file.Before), ["after"] = Encode(file.After) }).ToArray())
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, RecoveryJsonContext.Default.JsonObject);
        if (bytes.Length > RecordLimit) throw new InvalidDataException("安装发布记录过大。");
        await WriteAsync(journal, "plan.json", bytes, token).ConfigureAwait(false);
        var result = new InstallPublicationJournal(root, stage, instance, ordered, Convert.ToHexString(SHA256.HashData(bytes)));
        await result.ProgressAsync("prepared", 0, token).ConfigureAwait(false);
        return result;
    }

    internal static async Task<InstallPublicationJournal> OpenAsync(string root, string stage, CancellationToken token)
    {
        byte[] bytes = await ReadAsync(Path.Combine(stage, ".publication", "plan.json"), RecordLimit, token).ConfigureAwait(false);
        var document = JsonNode.Parse(bytes)!.AsObject();
        string instance = document["instance"]!.GetValue<string>();
        ValidateIdentity(root, stage, instance);
        if (document["version"]!.GetValue<int>() != 1 || document["root"]!.GetValue<string>() != root || document["stage"]!.GetValue<string>() != stage)
            throw new InvalidDataException("安装发布记录不属于当前目录。");
        var nodes = document["files"]!.AsArray();
        if (nodes.Count > 100000) throw new InvalidDataException("安装发布文件过多。");
        List<InstallPublicationFile> files = []; HashSet<string> paths = new(MinecraftLibraryService.PathComparer);
        long total = 0;
        foreach (var node in nodes)
        {
            string path = node!["path"]!.GetValue<string>(); _ = Target(root, instance, path);
            var before = Decode(node["before"]); var after = Decode(node["after"]);
            if (!paths.Add(path) || before == after || after is null && !IsMod(path, instance)) throw new InvalidDataException("安装发布条目无效。");
            total += (before?.Length ?? 0) + (after?.Length ?? 0);
            if (total > RecoveryBlobStore.MaxTransactionBytes) throw new InvalidDataException("安装发布超过大小限制。");
            files.Add(new(path, before, after));
        }
        return new(root, stage, instance, files.AsReadOnly(), Convert.ToHexString(SHA256.HashData(bytes)));
    }

    internal async Task ApplyAsync(CancellationToken token, Action<int, int>? progress = null)
    {
        var (phase, attempted) = await ReadProgressAsync(token).ConfigureAwait(false);
        // A completed prefix may have been changed while the launcher was stopped.
        // Validate it before touching another file, including a previously committed transaction.
        int completed = phase == "committed" ? _files.Count : Math.Max(0, attempted - 1);
        for (int i = 0; i < completed; i++)
            if (await ObserveAsync(Target(_root, _instance, _files[i].RelativePath), new(RecoveryBlobStore.MaxFileBytes), token).ConfigureAwait(false) != _files[i].After)
                throw new IOException("已发布文件在中断后发生变化，已保留恢复记录。");
        if (phase == "committed") return;
        if (phase is not ("prepared" or "applying")) throw new IOException("该安装正在回滚，不能继续发布。");
        // attempted includes the operation whose intent was flushed before an interrupted write.
        for (int i = Math.Max(0, attempted - 1); i < _files.Count; i++)
        {
            var file = _files[i]; string target = Target(_root, _instance, file.RelativePath);
            var current = await ObserveAsync(target, new(RecoveryBlobStore.MaxFileBytes), token).ConfigureAwait(false);
            if (current != file.Before && current != file.After) throw new IOException("安装目标在中断后被修改，已保留恢复记录。");
            await ProgressAsync("applying", i + 1, token).ConfigureAwait(false);
            if (current != file.After) await ReplaceAsync(target, file.After, token).ConfigureAwait(false);
            progress?.Invoke(i + 1, _files.Count);
        }
        await ProgressAsync("committed", _files.Count, token).ConfigureAwait(false);
    }

    internal async Task RollbackAsync(CancellationToken token, bool enclosingRenamePending = false)
    {
        var (phase, attempted) = await ReadProgressAsync(token).ConfigureAwait(false);
        if (phase == "rolled-back") return;
        if (phase == "committed" && !enclosingRenamePending) throw new IOException("安装已提交，不能作为未完成任务回滚。");
        await ProgressAsync("rolling-back", attempted, token).ConfigureAwait(false);
        for (int i = attempted - 1; i >= 0; i--)
        {
            var file = _files[i]; string target = Target(_root, _instance, file.RelativePath);
            var current = await ObserveAsync(target, new(RecoveryBlobStore.MaxFileBytes), token).ConfigureAwait(false);
            if (current != file.Before)
            {
                if (current != file.After) throw new IOException("回滚目标已被再次修改，已保留备份。");
                await ReplaceAsync(target, file.Before, token).ConfigureAwait(false);
            }
            await ProgressAsync("rolling-back", i, token).ConfigureAwait(false);
        }
        await ProgressAsync("rolled-back", 0, token).ConfigureAwait(false);
    }

    private async Task ReplaceAsync(string target, RecoveryBlob? blob, CancellationToken token)
    {
        RecoveryBlobStore.CheckLinks(target);
        if (blob is null) { File.Delete(target); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temporary = target + ".nexa-publish-" + Guid.NewGuid().ToString("N");
        try
        {
            await CopyAsync(Path.Combine(_journal, blob.Sha256), temporary, blob, token).ConfigureAwait(false);
            RecoveryBlobStore.CheckLinks(target); token.ThrowIfCancellationRequested(); File.Move(temporary, target, true);
        }
        finally { RecoveryBlobStore.CheckLinks(temporary); if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<(string Phase, int Attempted)> ReadProgressAsync(CancellationToken token)
    {
        var value = JsonNode.Parse(await ReadAsync(Path.Combine(_journal, "progress.json"), 4096, token).ConfigureAwait(false))!;
        string phase = value["phase"]!.GetValue<string>(); int attempted = value["attempted"]!.GetValue<int>();
        if (value["plan"]!.GetValue<string>() != _hash || attempted < 0 || attempted > _files.Count
            || phase is not ("prepared" or "applying" or "committed" or "rolling-back" or "rolled-back")
            || phase is "prepared" or "rolled-back" && attempted != 0 || phase == "committed" && attempted != _files.Count)
            throw new InvalidDataException("安装发布进度无效。");
        return (phase, attempted);
    }

    private Task ProgressAsync(string phase, int attempted, CancellationToken token) => WriteAsync(_journal, "progress.json",
        JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["plan"] = _hash, ["phase"] = phase, ["attempted"] = attempted }, RecoveryJsonContext.Default.JsonObject), token);
    private static JsonObject? Encode(RecoveryBlob? blob) => blob is null ? null : new JsonObject { ["sha256"] = blob.Sha256, ["length"] = blob.Length };
    private static RecoveryBlob? Decode(JsonNode? node)
    {
        if (node is null) return null;
        string hash = node["sha256"]!.GetValue<string>(); long length = node["length"]!.GetValue<long>();
        if (length < 0 || length > RecoveryBlobStore.MaxFileBytes || hash.Length != 64 || hash.Any(c => c is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
            throw new InvalidDataException("安装发布摘要无效。");
        return new(hash, length);
    }
    private static bool IsMod(string path, string instance) => path.StartsWith("mods/", StringComparison.Ordinal) || path.StartsWith($"versions/{instance}/mods/", StringComparison.Ordinal);
    private static string Target(string root, string instance, string relative)
    {
        if (relative.Contains('\\') || relative.Split('/').Any(part => part is "" or "." or "..")
            || !(relative.StartsWith("libraries/", StringComparison.Ordinal) || relative.StartsWith("assets/", StringComparison.Ordinal)
                || relative.StartsWith("runtime/", StringComparison.Ordinal) || IsMod(relative, instance)
                || relative.Split('/') is ["versions", var id, var file] && MinecraftVersionPaths.IsSafeReference(id)
                    && (file == id + ".json" || file == id + ".jar"))) throw new InvalidDataException("安装发布路径超出范围。");
        string path = ForgeInstallService.Contained(root, relative); RecoveryBlobStore.CheckLinks(path); return path;
    }
    private static void ValidateIdentity(string root, string stage, string instance)
    {
        if (root != Path.GetFullPath(root) || stage != Path.GetFullPath(stage) || !MinecraftVersionPaths.IsSafeReference(instance)
            || !MinecraftLibraryService.PathComparer.Equals(Directory.GetParent(stage)?.FullName, Path.Combine(root, ".nexa-modify"))
                && !MinecraftLibraryService.PathComparer.Equals(Directory.GetParent(stage)?.FullName, Path.Combine(root, ".nexa-install-jobs"))
            || !Guid.TryParseExact(Path.GetFileName(stage), "N", out _)) throw new InvalidDataException("安装事务目录无效。");
        RecoveryBlobStore.CheckLinks(root); RecoveryBlobStore.CheckLinks(stage);
    }
    private static async Task<RecoveryBlob?> ObserveAsync(string path, RecoveryByteBudget budget, CancellationToken token)
    {
        RecoveryBlobStore.CheckLinks(path); if (Directory.Exists(path)) throw new IOException("文件目标是目录。"); if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        long length = stream.Length; if (length > RecoveryBlobStore.MaxFileBytes) throw new InvalidDataException("安装文件过大。");
        return new(await RecoveryBlobStore.CopyAndHashAsync(stream, Stream.Null, length, budget, token).ConfigureAwait(false), length);
    }
    private static async Task SaveObjectAsync(string source, string journal, RecoveryBlob blob, CancellationToken token)
    {
        string target = Path.Combine(journal, blob.Sha256);
        if (File.Exists(target))
        { if (await ObserveAsync(target, new(RecoveryBlobStore.MaxFileBytes), token).ConfigureAwait(false) != blob) throw new InvalidDataException("安装备份校验失败。"); return; }
        await CopyAsync(source, target, blob, token).ConfigureAwait(false);
    }
    private static async Task CopyAsync(string source, string target, RecoveryBlob blob, CancellationToken token)
    {
        RecoveryBlobStore.CheckLinks(source); RecoveryBlobStore.CheckLinks(target);
        await using var input = File.OpenRead(source);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        if (await RecoveryBlobStore.CopyAndHashAsync(input, output, blob.Length, new(RecoveryBlobStore.MaxFileBytes), token).ConfigureAwait(false) != blob.Sha256)
            throw new InvalidDataException("安装文件校验失败。");
        await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
    }
    private static async Task<byte[]> ReadAsync(string path, int limit, CancellationToken token)
    {
        RecoveryBlobStore.CheckLinks(path); await using var input = File.OpenRead(path);
        if (input.Length > limit) throw new InvalidDataException("安装记录过大。");
        byte[] bytes = new byte[(int)input.Length]; await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (input.ReadByte() != -1) throw new InvalidDataException("安装记录发生变化。");
        RecoveryRecordAuthority.Verify(path, bytes); return bytes;
    }
    private static async Task WriteAsync(string directory, string name, byte[] bytes, CancellationToken token)
    {
        string target = Path.Combine(directory, name), temp = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".part");
        RecoveryBlobStore.CheckLinks(temp);
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            { await output.WriteAsync(bytes, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true); }
            await RecoveryRecordAuthority.AuthorizeAsync(target, bytes, token).ConfigureAwait(false);
            RecoveryBlobStore.CheckLinks(target); File.Move(temp, target, true);
        }
        finally { RecoveryBlobStore.CheckLinks(temp); if (File.Exists(temp)) File.Delete(temp); }
    }
}

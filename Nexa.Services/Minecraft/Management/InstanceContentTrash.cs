using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Process;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Minecraft.Management;

public sealed record InstanceContentRemoveCommand(string InstanceDirectory, string PageId, string Name, bool IsDirectory, long? ExpectedSize, long ExpectedModifiedUtcTicks);
public sealed record InstanceContentRestoreCommand(string InstanceDirectory, string TrashId);
public sealed record InstanceTrashedContent(string Id, string PageId, string Name, DateTimeOffset RemovedAt);

public static class InstanceContentTrash
{
    private const string TrashFolder = ".nexa-content-trash";
    private static readonly HashSet<string> Pages = new(StringComparer.Ordinal) { "mods", "resourcepacks", "shaderpacks", "schematics", "saves", "screenshots" };

    public static async Task<XsrResult> RemoveAsync(InstanceContentRemoveCommand command, XsrStateStore store, CancellationToken token = default)
    {
        try
        {
            var snapshot = await InstanceManagementService.ReadAsync(new(command.InstanceDirectory), token).ConfigureAwait(false);
            using var lease = await InstanceRecoveryOperationGate.EnterRestoreAsync(Directory.GetParent(snapshot.InstanceDirectory)!.Parent!.FullName, token).ConfigureAwait(false);
            snapshot = await InstanceManagementService.ReadAsync(new(command.InstanceDirectory), token).ConfigureAwait(false);
            RejectRunning(snapshot, store, token);
            if (!Pages.Contains(command.PageId) || !MinecraftVersionPaths.IsSafeReference(command.Name)) throw new InvalidDataException("请选择有效的内容项。");
            string directory = snapshot.Pages.SingleOrDefault(page => page.Id == command.PageId)?.Directory ?? throw new InvalidDataException("当前版本不提供此内容页。");
            string source = Path.Combine(directory, command.Name);
            RecoveryBlobStore.CheckLinks(source);
            FileSystemInfo item = command.IsDirectory ? new DirectoryInfo(source) : new FileInfo(source);
            if (!item.Exists || item.LastWriteTimeUtc.Ticks != command.ExpectedModifiedUtcTicks
                || item is FileInfo file && file.Length != command.ExpectedSize)
                throw new IOException("内容已变化，请刷新后再移除。");
            string trash = Path.Combine(snapshot.GameDirectory, TrashFolder);
            RecoveryBlobStore.CheckLinks(trash);
            Directory.CreateDirectory(trash);
            string id = Guid.NewGuid().ToString("N"), transaction = Path.Combine(trash, id);
            Directory.CreateDirectory(transaction);
            JsonObject record = new()
            {
                ["version"] = 1,
                ["instance"] = snapshot.InstanceDirectory,
                ["game"] = snapshot.GameDirectory,
                ["page"] = command.PageId,
                ["name"] = command.Name,
                ["directory"] = command.IsDirectory,
                ["removedAt"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            };
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(record.ToJsonString());
            using (var journal = new FileStream(Path.Combine(transaction, "record.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { journal.Write(bytes); journal.Flush(true); }
            token.ThrowIfCancellationRequested();
            Move(source, Path.Combine(transaction, "content"), command.IsDirectory);
            return XsrResult.Success();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return XsrResult.Failure(XsrRuntimeErrors.Cancelled()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
        { return XsrResult.Failure(MinecraftErrors.InvalidRequest(error.Message)); }
    }

    public static async Task<XsrResult> RestoreAsync(InstanceContentRestoreCommand command, XsrStateStore store, CancellationToken token = default)
    {
        try
        {
            if (!Guid.TryParseExact(command.TrashId, "N", out _)) throw new InvalidDataException("回收记录无效。");
            var snapshot = await InstanceManagementService.ReadAsync(new(command.InstanceDirectory), token).ConfigureAwait(false);
            using var lease = await InstanceRecoveryOperationGate.EnterRestoreAsync(Directory.GetParent(snapshot.InstanceDirectory)!.Parent!.FullName, token).ConfigureAwait(false);
            snapshot = await InstanceManagementService.ReadAsync(new(command.InstanceDirectory), token).ConfigureAwait(false);
            RejectRunning(snapshot, store, token);
            string transaction = Path.Combine(snapshot.GameDirectory, TrashFolder, command.TrashId);
            var record = ReadRecord(transaction, snapshot.InstanceDirectory, snapshot.GameDirectory);
            string page = record["page"]!.GetValue<string>(), name = record["name"]!.GetValue<string>();
            // An installed loader may have changed since removal; the fixed content-directory mapping remains valid.
            string directory = Path.Combine(snapshot.GameDirectory, page), destination = Path.Combine(directory, name);
            string source = Path.Combine(transaction, "content");
            RecoveryBlobStore.CheckLinks(destination); RecoveryBlobStore.CheckLinks(source);
            if (Path.Exists(destination)) throw new IOException("同名内容已存在，未覆盖任何文件。");
            Directory.CreateDirectory(directory);
            token.ThrowIfCancellationRequested();
            Move(source, destination, record["directory"]!.GetValue<bool>());
            return XsrResult.Success();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return XsrResult.Failure(XsrRuntimeErrors.Cancelled()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        { return XsrResult.Failure(MinecraftErrors.InvalidRequest(error.Message)); }
    }

    internal static IReadOnlyList<InstanceTrashedContent> Read(string instance, string game)
    {
        string root = Path.Combine(game, TrashFolder);
        RecoveryBlobStore.CheckLinks(root);
        if (!Directory.Exists(root)) return [];
        List<InstanceTrashedContent> entries = [];
        foreach (string directory in Directory.EnumerateDirectories(root).Take(1001))
        {
            if (entries.Count >= 1000) break;
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
            try
            {
                var record = ReadRecord(directory, instance, game);
                string content = Path.Combine(directory, "content");
                RecoveryBlobStore.CheckLinks(content);
                if (Path.Exists(content)) entries.Add(new(Path.GetFileName(directory), record["page"]!.GetValue<string>(), record["name"]!.GetValue<string>(),
                    DateTimeOffset.Parse(record["removedAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture)));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or FormatException or System.Text.Json.JsonException) { }
        }
        return entries.OrderByDescending(item => item.RemovedAt).ToArray();
    }

    private static JsonObject ReadRecord(string directory, string instance, string game)
    {
        string path = Path.Combine(directory, "record.json");
        RecoveryBlobStore.CheckLinks(path);
        using var stream = File.OpenRead(path);
        if (stream.Length > 16384) throw new InvalidDataException("回收记录超过限制。");
        byte[] bytes = new byte[16385];
        int count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (count > 16384) throw new InvalidDataException("回收记录超过限制。");
        var record = JsonNode.Parse(bytes.AsSpan(0, count)) as JsonObject ?? throw new InvalidDataException("回收记录无效。");
        if (record["version"]?.GetValue<int>() != 1 || record["instance"]?.GetValue<string>() != instance || record["game"]?.GetValue<string>() != game
            || record["page"]?.GetValue<string>() is not { } page || !Pages.Contains(page)
            || record["name"]?.GetValue<string>() is not { } name || !MinecraftVersionPaths.IsSafeReference(name)
            || record["directory"] is not JsonValue || record["removedAt"] is not JsonValue)
            throw new InvalidDataException("回收记录不属于此实例。");
        return record;
    }

    private static void Move(string source, string destination, bool directory)
    { if (directory) Directory.Move(source, destination); else File.Move(source, destination, false); }

    private static void RejectRunning(InstanceManagementSnapshot snapshot, XsrStateStore store, CancellationToken token)
    {
        if (store.TryResolve(MinecraftProcessStateComposition.SessionsKey, out var sessions)
            && store.ReadCollection<MinecraftProcessSnapshot>(sessions, cancellationToken: token).Items.Any(item => item.State is MinecraftProcessState.Created or MinecraftProcessState.Running
                && (MinecraftLibraryService.PathComparer.Equals(item.InstanceDirectory, snapshot.InstanceDirectory)
                    || MinecraftLibraryService.PathComparer.Equals(item.GameDirectory, snapshot.GameDirectory))))
            throw new InvalidOperationException("有游戏正在使用此目录，请先结束游戏进程。");
    }
}

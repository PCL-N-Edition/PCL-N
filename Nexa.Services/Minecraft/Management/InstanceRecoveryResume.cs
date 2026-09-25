using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Process;
using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Management;

public sealed record InstanceRecoveryResumeCommand(IReadOnlyList<string> Roots);

public sealed partial class InstanceRecoveryService
{
    public Task<XsrResult> RecoverAsync(InstanceRecoveryResumeCommand command, CancellationToken token = default) => Task.Run(async () =>
    {
        int failures = 0;
        if (command.Roots.Count > 128) return XsrResult.Failure(MinecraftErrors.InvalidRequest("恢复根目录过多。"));
        foreach (string root in command.Roots.Distinct(MinecraftLibraryService.PathComparer))
        {
            try
            {
                if (!Path.IsPathFullyQualified(root)) throw new InvalidDataException("恢复根目录无效。");
                using var exclusive = await InstanceRecoveryOperationGate.EnterRestoreAsync(root, token).ConfigureAwait(false);
                string versions = Path.Combine(root, "versions"); RecoveryBlobStore.CheckLinks(versions);
                if (!Directory.Exists(versions)) continue;
                int count = 0;
                foreach (string instance in Directory.EnumerateDirectories(versions))
                {
                    token.ThrowIfCancellationRequested();
                    if (++count > 10000) throw new InvalidDataException("恢复实例数量超过预算。");
                    string transactions = Path.Combine(instance, "Nexa", "Recovery", "transactions");
                    RecoveryBlobStore.CheckLinks(transactions);
                    if (!Directory.Exists(transactions)) continue;
                    using var instanceLease = AcquireRestoreLease(instance);
                    int transactionCount = 0;
                    foreach (string directory in Directory.EnumerateDirectories(transactions))
                    {
                        if (++transactionCount > 1024) throw new InvalidDataException("恢复事务数量超过预算。");
                        try
                        {
                            RecoveryBlobStore.CheckLinks(directory);
                            string marker = Path.Combine(directory, "transaction.json");
                            if (!File.Exists(marker)) continue;
                            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var id)) throw new InvalidDataException("恢复事务标识无效。");
                            var state = JsonNode.Parse(await RecoveryFileTransaction.ReadRecordAsync(marker, 4096, token).ConfigureAwait(false));
                            if (state?["version"]?.GetValue<int>() != 1 || state["transaction"]?.GetValue<string>() != id.ToString("D"))
                                throw new InvalidDataException("恢复事务身份无效。");
                            if (state?["phase"]?.GetValue<string>() is "committed" or "rolled-back") { CleanupCompletedRestore(directory); continue; }
                            RecoveryPreparedRestore prepared;
                            try { prepared = await RecoveryRestorePreparation.ReadAsync(instance, instance, id, token).ConfigureAwait(false); }
                            catch (InvalidDataException) { prepared = await RecoveryRestorePreparation.ReadAsync(instance, root, id, token).ConfigureAwait(false); }
                            await RecoveryTransactionCoordinator.RecoverUnderLeaseAsync(prepared, settings, ct =>
                            {
                                EnsureNoActiveGame(root, ct);
                                return Task.CompletedTask;
                            }, true, token).ConfigureAwait(false);
                            CleanupCompletedRestore(directory);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
                        { failures++; log?.Warn("Recovery", $"恢复事务需要处理：{error.GetType().Name}"); }
                    }
                }
            }
            catch (OperationCanceledException) { return XsrResult.Failure(XsrRuntimeErrors.Cancelled()); }
            catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
            { failures++; log?.Warn("Recovery", $"恢复目录未完成：{error.GetType().Name}"); }
        }
        return failures == 0 ? XsrResult.Success() : XsrResult.Failure(MinecraftErrors.InvalidRequest("部分快照恢复事务存在冲突，备份已保留。"));
    }, token);

    private static FileStream AcquireRestoreLease(string instance)
    {
        string directory = Path.Combine(instance, "Nexa", "Recovery");
        RecoveryBlobStore.CheckLinks(directory); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "restore.lock"); RecoveryBlobStore.CheckLinks(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private void EnsureNoActiveGame(string root, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (store.TryResolve(MinecraftProcessStateComposition.SessionsKey, out var id)
            && store.ReadCollection<MinecraftProcessSnapshot>(id, cancellationToken: token).Items.Any(item =>
                item.State is MinecraftProcessState.Created or MinecraftProcessState.Running
                && (MinecraftLibraryService.PathComparer.Equals(item.GameDirectory, root)
                    || item.InstanceDirectory is { } active && MinecraftLibraryService.PathComparer.Equals(Directory.GetParent(active)?.Parent?.FullName, root))))
            throw new IOException("请先结束此游戏目录中的游戏进程再恢复快照。");
    }
}

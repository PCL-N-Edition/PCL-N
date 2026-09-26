using Nexa.Services.Minecraft.Management;
using Nexa.Services.Tasks;
using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Install;

public sealed record MinecraftInstallRecoveryCommand(IReadOnlyList<string> RootDirectories);

public sealed partial class MinecraftInstallService
{
    public async Task<XsrResult> RecoverPendingAsync(MinecraftInstallRecoveryCommand command, CancellationToken token = default)
    {
        if (command.RootDirectories is null || command.RootDirectories.Count > 64)
            return XsrResult.Failure(MinecraftErrors.InvalidRequest("恢复目录数量无效。"));
        var roots = command.RootDirectories.ToArray();
        lock (_executionGate)
        {
            if (_stopping) return XsrResult.Failure(XsrRuntimeErrors.Cancelled());
            foreach (string root in roots.Where(root => !string.IsNullOrWhiteSpace(root) && Path.IsPathFullyQualified(root)))
                _recoveryRoots.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
        }
        string queueId = "install-recovery:queue:" + Guid.NewGuid().ToString("N");
        ITaskCenterTask? queue = null;
        void BeginQueue() => queue ??= _tasks.Begin(new(queueId, "继续安装与修改", StagePlan, CanCancel: false));
        try
        {
            await _recoveryDiscoveryGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                bool stopping;
                lock (_executionGate) stopping = _stopping;
                if (stopping) { queue?.Paused(); return XsrResult.Failure(XsrRuntimeErrors.Cancelled()); }
                var result = await RecoverPendingCoreAsync(new(roots), BeginQueue, token).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    if (queue is not null) { queue.Complete("安装恢复检查完成"); _tasks.Dismiss(queueId); }
                }
                else if (_stopping) queue?.Paused();
                else queue?.Fail("部分安装恢复需要处理。");
                return result;
            }
            finally { _recoveryDiscoveryGate.Release(); }
        }
        catch (OperationCanceledException)
        {
            if (_stopping)
            {
                if (_pauseForExit) queue?.Paused(); else queue?.Canceled();
                return XsrResult.Failure(XsrRuntimeErrors.Cancelled());
            }
            queue?.Paused();
            throw;
        }
        finally { queue?.Dispose(); }
    }

    private async Task<XsrResult> RecoverPendingCoreAsync(MinecraftInstallRecoveryCommand command, Action beginQueue, CancellationToken token)
    {
        int visited = 0, pending = 0, failures = 0;
        void Fail(string detail)
        {
            failures++;
            using var task = _tasks.Begin(new("install-recovery-error:" + Guid.NewGuid().ToString("N"), "安装恢复需要处理", StagePlan, CanCancel: false));
            task.Fail(detail);
        }
        foreach (string requested in command.RootDirectories.ToArray().Distinct(MinecraftLibraryService.PathComparer))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (!Path.IsPathFullyQualified(requested)) throw new ArgumentException("安装恢复需要绝对目录路径。");
                string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requested));
                foreach (var pack in await ReadModpackJobsAsync(root, token).ConfigureAwait(false))
                {
                    lock (_executionGate) if (_stopping) return XsrResult.Failure(XsrRuntimeErrors.Cancelled());
                    beginQueue();
                    var result = await InstallModpackCoreAsync(pack.Command, token, pack).ConfigureAwait(false);
                    if (!result.IsSuccess) Fail("整合包未能恢复：" + result.Error?.Message);
                }
                foreach (bool newInstallation in new[] { false, true })
                {
                    string directory = Path.Combine(root, newInstallation ? ".nexa-install-jobs" : ".nexa-modify"); RecoveryBlobStore.CheckLinks(directory);
                    if (!Directory.Exists(directory)) continue;
                    foreach (string stage in Directory.EnumerateDirectories(directory))
                    {
                        token.ThrowIfCancellationRequested();
                        lock (_executionGate) if (_stopping) return XsrResult.Failure(XsrRuntimeErrors.Cancelled());
                        if (++visited > 4096)
                        {
                            Fail("待检查的安装目录过多，已保留剩余任务。");
                            return XsrResult.Failure(XsrRuntimeErrors.HandlerFaulted());
                        }
                        if (!Guid.TryParseExact(Path.GetFileName(stage), "N", out Guid id)) continue;
                        try
                        {
                            var saved = await InstallTaskJournal.ReadAsync(root, stage, token).ConfigureAwait(false);
                            var status = await InstallTaskJournal.ReadStatusAsync(stage, saved, token).ConfigureAwait(false);
                            if (status is InstallTaskStatus.Completed or InstallTaskStatus.RolledBack) continue;
                            if (status == InstallTaskStatus.Pending && await ReconcileCommittedTaskAsync(root, stage, saved, token).ConfigureAwait(false)) continue;
                            if (++pending > 256)
                            {
                                Fail("待恢复的安装任务过多，已保留剩余任务。");
                                return XsrResult.Failure(XsrRuntimeErrors.HandlerFaulted());
                            }
                            beginQueue();
                            if (status == InstallTaskStatus.RollbackRequested)
                                await RollbackInstallationAsync(root, id, newInstallation, token).ConfigureAwait(false);
                            else
                                await ResumeInstallationAsync(root, id, newInstallation, token).ConfigureAwait(false);
                        }
                        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException and not AccessViolationException)
                        { Fail("任务 " + id.ToString("N") + " 未能恢复：" + error.Message); }
                    }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException and not AccessViolationException)
            { Fail("无法检查安装恢复目录：" + error.Message); }
        }
        return failures == 0 ? XsrResult.Success() : XsrResult.Failure(XsrRuntimeErrors.HandlerFaulted());
    }

    private static async Task<bool> ReconcileCommittedTaskAsync(string root, string stage, InstallTaskPlan plan, CancellationToken token)
    {
        if (plan.Command.NewInstanceName is { } name && name != plan.Command.InstanceName) return false;
        string publication = Path.Combine(stage, ".publication");
        RecoveryBlobStore.CheckLinks(publication);
        if (!Directory.Exists(publication)) return false;
        using var operation = await InstanceRecoveryOperationGate.EnterRestoreAsync(root, token).ConfigureAwait(false);
        string lockPath = Path.Combine(stage, InstallTaskJournal.DirectoryName, "execution.lock");
        RecoveryBlobStore.CheckLinks(lockPath);
        using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (await InstallTaskJournal.ReadStatusAsync(stage, plan, token).ConfigureAwait(false) != InstallTaskStatus.Pending) return false;
        var journal = await InstallPublicationJournal.OpenAsync(root, stage, token).ConfigureAwait(false);
        if (journal.InstanceId != plan.Command.InstanceName) throw new InvalidDataException("安装发布记录与实例不一致。");
        if (await journal.ReadPhaseAsync(token).ConfigureAwait(false) != "committed") return false;
        // An authenticated committed receipt describes completed work, not a request to
        // overwrite later user changes or resurrect its task card.
        await InstallTaskJournal.WriteStatusAsync(stage, plan, InstallTaskStatus.Completed, CancellationToken.None).ConfigureAwait(false);
        return true;
    }
}

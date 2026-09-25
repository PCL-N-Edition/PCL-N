using Nexa.Services.Minecraft.Management;
using Nexa.Services.Tasks;

namespace Nexa.Services.Minecraft.Install;

public sealed partial class MinecraftInstallService
{
    /// <summary>Internal runner; discovery and user pause/rollback orchestration are separate.</summary>
    internal async Task<MinecraftInstallResult> ResumeModificationAsync(string root, Guid taskId, CancellationToken token = default)
    {
        root = Path.GetFullPath(root);
        string stage = ForgeInstallService.Contained(root, ".nexa-modify/" + taskId.ToString("N"));
        var saved = await InstallTaskJournal.ReadAsync(root, stage, token).ConfigureAwait(false);
        if (saved.Command.NewInstanceName is { } renamed && renamed != saved.Command.InstanceName)
            throw new InvalidDataException("包含改名的安装任务尚不能自动恢复，已保留原记录。");
        using var operation = await InstanceRecoveryOperationGate.EnterRestoreAsync(root, token).ConfigureAwait(false);
        if (_hostStore is null) throw new InvalidOperationException("恢复安装需要检查活动游戏进程。");
        MinecraftInstanceRenamer.EnsureIdle(root, _hostStore, token);
        string lockPath = Path.Combine(stage, InstallTaskJournal.DirectoryName, "execution.lock");
        RecoveryBlobStore.CheckLinks(lockPath);
        await using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var status = await InstallTaskJournal.ReadStatusAsync(stage, saved, token).ConfigureAwait(false);
        if (status is InstallTaskStatus.RollbackRequested or InstallTaskStatus.RolledBack)
            throw new InvalidOperationException("此任务已选择回滚，不能继续安装。");
        using var task = _tasks.Begin(new("install-recovery:" + taskId.ToString("N"), "继续修改 " + saved.Command.InstanceName, StagePlan));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, task.CancellationToken);
        try
        {
            string publication = Path.Combine(stage, ".publication"); RecoveryBlobStore.CheckLinks(publication);
            MinecraftInstallResult result;
            if (!Directory.Exists(publication))
            {
                if (status == InstallTaskStatus.Completed) throw new InvalidDataException("已完成任务缺少发布记录，已保留原目录。");
                result = await ReinstallAsync(saved.Command, task, linked.Token, stage).ConfigureAwait(false);
            }
            else
            {
                // A partial publication cannot pass the original manifest fingerprint check. Resume its journal directly.
                var journal = await InstallPublicationJournal.OpenAsync(root, stage, linked.Token).ConfigureAwait(false);
                if (journal.InstanceId != saved.Command.InstanceName) throw new InvalidDataException("安装发布记录与任务实例不一致。");
                await journal.ApplyAsync(linked.Token, (done, total) => task.Report(StagePlan[4], "正在恢复安装文件", (double)done / Math.Max(1, total), done, total, 0)).ConfigureAwait(false);
                result = new(saved.Command.InstanceName!, Path.Combine(root, "versions", saved.Command.InstanceName!));
            }
            await InstallTaskJournal.WriteStatusAsync(stage, saved, InstallTaskStatus.Completed, CancellationToken.None).ConfigureAwait(false);
            task.Complete("已修改 " + saved.Command.InstanceName);
            Installed?.Invoke(root);
            return result;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        { task.Canceled(); throw; }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        { task.Fail(error.Message); throw; }
    }

    internal async Task RollbackModificationAsync(string root, Guid taskId, CancellationToken token = default)
    {
        root = Path.GetFullPath(root);
        string stage = ForgeInstallService.Contained(root, ".nexa-modify/" + taskId.ToString("N"));
        var saved = await InstallTaskJournal.ReadAsync(root, stage, token).ConfigureAwait(false);
        if (saved.Command.NewInstanceName is { } renamed && renamed != saved.Command.InstanceName)
            throw new InvalidDataException("包含改名的安装任务尚不能自动回滚，已保留原记录。");
        using var operation = await InstanceRecoveryOperationGate.EnterRestoreAsync(root, token).ConfigureAwait(false);
        if (_hostStore is null) throw new InvalidOperationException("回滚安装需要检查活动游戏进程。");
        MinecraftInstanceRenamer.EnsureIdle(root, _hostStore, token);
        string lockPath = Path.Combine(stage, InstallTaskJournal.DirectoryName, "execution.lock"); RecoveryBlobStore.CheckLinks(lockPath);
        await using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var status = await InstallTaskJournal.ReadStatusAsync(stage, saved, token).ConfigureAwait(false);
        if (status == InstallTaskStatus.RolledBack) return;
        if (status == InstallTaskStatus.Completed) throw new InvalidOperationException("安装已完成，不能作为未完成任务回滚。");
        using var task = _tasks.Begin(new("install-recovery:" + taskId.ToString("N"), "回滚修改 " + saved.Command.InstanceName, StagePlan, CanCancel: false));
        try
        {
            string publication = Path.Combine(stage, ".publication"); RecoveryBlobStore.CheckLinks(publication);
            InstallPublicationJournal? journal = null;
            if (Directory.Exists(publication))
            {
                journal = await InstallPublicationJournal.OpenAsync(root, stage, token).ConfigureAwait(false);
                if (journal.InstanceId != saved.Command.InstanceName) throw new InvalidDataException("安装发布记录与任务实例不一致。");
                if (await journal.ReadPhaseAsync(token).ConfigureAwait(false) == "committed")
                {
                    await journal.ApplyAsync(token).ConfigureAwait(false); // Verify what was committed before repairing the missing terminal marker.
                    await InstallTaskJournal.WriteStatusAsync(stage, saved, InstallTaskStatus.Completed, CancellationToken.None).ConfigureAwait(false);
                    throw new InvalidOperationException("安装文件已提交，不能作为未完成任务回滚。");
                }
            }
            await InstallTaskJournal.WriteStatusAsync(stage, saved, InstallTaskStatus.RollbackRequested, token).ConfigureAwait(false);
            if (journal is not null) await journal.RollbackAsync(token).ConfigureAwait(false);
            await InstallTaskJournal.WriteStatusAsync(stage, saved, InstallTaskStatus.RolledBack, CancellationToken.None).ConfigureAwait(false);
            task.Complete("已回滚修改");
            Installed?.Invoke(root);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { task.Canceled(); throw; }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException) { task.Fail(error.Message); throw; }
    }
}

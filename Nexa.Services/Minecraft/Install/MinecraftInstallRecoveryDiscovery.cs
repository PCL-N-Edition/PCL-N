using Nexa.Services.Minecraft.Management;
using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Install;

public sealed record MinecraftInstallRecoveryCommand(IReadOnlyList<string> RootDirectories);

public sealed partial class MinecraftInstallService
{
    public async Task<XsrResult> RecoverPendingAsync(MinecraftInstallRecoveryCommand command, CancellationToken token = default)
    {
        if (command.RootDirectories is null || command.RootDirectories.Count > 64)
            return XsrResult.Failure(MinecraftErrors.InvalidRequest("恢复目录数量无效。"));
        int visited = 0, failures = 0;
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
                foreach (bool newInstallation in new[] { false, true })
                {
                    string directory = Path.Combine(root, newInstallation ? ".nexa-install-jobs" : ".nexa-modify"); RecoveryBlobStore.CheckLinks(directory);
                    if (!Directory.Exists(directory)) continue;
                    foreach (string stage in Directory.EnumerateDirectories(directory))
                    {
                        token.ThrowIfCancellationRequested();
                        if (++visited > 256)
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
                            if (saved.Command.Loader is InstallLoader.Forge or InstallLoader.NeoForge or InstallLoader.Cleanroom or InstallLoader.OptiFine
                                && !Directory.Exists(Path.Combine(stage, ".publication")))
                                throw new InvalidOperationException("此加载器的准备阶段暂不支持自动恢复，已保留任务。");
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
}

using Nexa.Services.Minecraft.Management;

namespace Nexa.Services.Minecraft.Install;

public sealed partial class MinecraftInstallService
{
    private async Task CancelPendingInstallationsAsync(string[] roots)
    {
        if (roots.Length > 64) throw new InvalidDataException("恢复目录过多，未完成全部撤回。");
        int visited = 0;
        foreach (string root in roots)
            foreach (bool newInstallation in new[] { false, true })
            {
                string directory = Path.Combine(root, newInstallation ? ".nexa-install-jobs" : ".nexa-modify");
                RecoveryBlobStore.CheckLinks(directory);
                if (!Directory.Exists(directory)) continue;
                foreach (string stage in Directory.EnumerateDirectories(directory))
                {
                    if (++visited > 256) throw new InvalidDataException("安装记录过多，未完成全部撤回。");
                    if (!Guid.TryParseExact(Path.GetFileName(stage), "N", out Guid id)) continue;
                    var plan = await InstallTaskJournal.ReadAsync(root, stage, CancellationToken.None).ConfigureAwait(false);
                    var status = await InstallTaskJournal.ReadStatusAsync(stage, plan, CancellationToken.None).ConfigureAwait(false);
                    if (status is InstallTaskStatus.Completed or InstallTaskStatus.RolledBack) continue;
                    try { await RollbackInstallationAsync(root, id, newInstallation, CancellationToken.None).ConfigureAwait(false); }
                    catch (InvalidOperationException)
                    {
                        // Publication may already be committed while its terminal marker was lost in a crash.
                        // The rollback runner verifies committed files and repairs that marker before rejecting rollback.
                        if (await InstallTaskJournal.ReadStatusAsync(stage, plan, CancellationToken.None).ConfigureAwait(false) != InstallTaskStatus.Completed) throw;
                    }
                }
            }
    }
}

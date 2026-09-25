using Nexa.Services.Minecraft.Management;

namespace Nexa.Services.Minecraft.Install;

public sealed partial class MinecraftInstallService
{
    private static async Task<IReadOnlyList<ModpackInstallJournal>> ReadModpackJobsAsync(string root, CancellationToken token)
    {
        string directory = Path.Combine(root, ModpackInstallJournal.DirectoryName);
        RecoveryBlobStore.CheckLinks(directory);
        if (!Directory.Exists(directory)) return [];
        List<ModpackInstallJournal> pending = [];
        int count = 0;
        foreach (string stage in Directory.EnumerateDirectories(directory))
        {
            if (++count > 256) throw new InvalidDataException("整合包恢复记录过多。");
            if (!Guid.TryParseExact(Path.GetFileName(stage), "N", out _)) continue;
            var journal = await ModpackInstallJournal.OpenAsync(root, stage, token).ConfigureAwait(false);
            if (!journal.Complete && !journal.Canceled) pending.Add(journal);
        }
        return pending;
    }
}

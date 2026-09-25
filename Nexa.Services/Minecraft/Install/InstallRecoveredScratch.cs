using Nexa.Services.Minecraft.Management;

namespace Nexa.Services.Minecraft.Install;

internal static class InstallRecoveredScratch
{
    // Rebuild scratch from verified completed downloads. Never certify arbitrary residual files
    // merely because they survived in an approved task's directory.
    internal static async Task ResetAsync(string stage, CancellationToken token)
    {
        RecoveryBlobStore.CheckLinks(stage);
        if (!Directory.Exists(stage)) return;
        string records = Path.Combine(stage, InstallTaskJournal.DirectoryName);
        Queue<string> directories = new(); directories.Enqueue(stage);
        int entries = 0;
        while (directories.TryDequeue(out var directory))
        {
            RecoveryBlobStore.CheckLinks(directory);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                if (++entries > 200000) throw new InvalidDataException("安装暂存条目过多。");
                RecoveryBlobStore.CheckLinks(path);
                if (MinecraftLibraryService.PathComparer.Equals(path, records)) continue;
                if (Directory.Exists(path)) directories.Enqueue(path);
                else if (!await RecoveryRecordAuthority.IsAuthorizedFileAsync(path, token).ConfigureAwait(false))
                {
                    RecoveryBlobStore.CheckLinks(path);
                    File.Delete(path);
                }
            }
        }
    }
}

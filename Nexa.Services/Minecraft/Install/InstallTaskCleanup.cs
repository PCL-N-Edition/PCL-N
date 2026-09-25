using Nexa.Services.Minecraft.Management;

namespace Nexa.Services.Minecraft.Install;

/// <summary>Prunes only terminal task-owned scratch; keeps the small identity and terminal receipts.</summary>
internal static class InstallTaskCleanup
{
    internal static void TryPrune(string stage, params string[] keep)
    {
        if (!Guid.TryParseExact(Path.GetFileName(stage), "N", out _)
            || Directory.GetParent(stage)?.Name is not (".nexa-java-jobs" or ".nexa-pack-jobs" or ".nexa-modify" or ".nexa-install-jobs"))
            throw new InvalidDataException("安装清理目录无效。");
        try
        {
            RecoveryBlobStore.CheckLinks(stage);
            HashSet<string> retained = new(keep, MinecraftLibraryService.PathComparer);
            List<string> files = [], directories = [];
            Queue<string> pending = new(); pending.Enqueue(stage);
            while (pending.TryDequeue(out string? directory))
            {
                RecoveryBlobStore.CheckLinks(directory);
                foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (files.Count + directories.Count > 200000) throw new IOException("安装清理条目过多。");
                    RecoveryBlobStore.CheckLinks(path);
                    if (Directory.Exists(path)) { directories.Add(path); pending.Enqueue(path); }
                    else files.Add(path);
                }
            }
            foreach (string file in files)
            {
                if (retained.Contains(Path.GetRelativePath(stage, file).Replace('\\', '/'))) continue;
                RecoveryBlobStore.CheckLinks(file); File.Delete(file);
            }
            foreach (string directory in directories.OrderByDescending(path => path.Length))
            {
                RecoveryBlobStore.CheckLinks(directory);
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}

namespace Nexa.Services.Minecraft.Management;

public sealed record InstanceRecoverySnapshotSummary(Guid Revision, DateTimeOffset CapturedAt, int Files, long ContentBytes);
public sealed record InstanceRecoveryStorage(long VersionBytes, long SnapshotBytes, bool Complete,
    IReadOnlyList<InstanceRecoverySnapshotSummary> Snapshots, string? Error = null);

internal static class InstanceRecoveryStorageReader
{
    internal static async Task<InstanceRecoveryStorage> ReadAsync(string instance, string game, CancellationToken token)
    {
        long versionBytes = 0, snapshotBytes = 0;
        try
        {
            string recovery = Path.Combine(instance, "Nexa", "Recovery");
            int entries = 0;
            var pending = new Stack<(string Path, bool Snapshot, int Depth)>();
            pending.Push((instance, false, 0));
            while (pending.TryPop(out var folder))
            {
                token.ThrowIfCancellationRequested(); RecoveryBlobStore.CheckLinks(folder.Path);
                if (folder.Depth > 64) throw new IOException("版本目录层级过深。");
                foreach (var entry in new DirectoryInfo(folder.Path).EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    if (++entries > 250000) throw new IOException("版本目录条目超过统计预算。");
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("版本目录包含链接，无法完整统计。");
                    bool snapshot = folder.Snapshot || MinecraftLibraryService.PathComparer.Equals(entry.FullName, recovery);
                    if (entry is DirectoryInfo) pending.Push((entry.FullName, snapshot, folder.Depth + 1));
                    else if (entry is FileInfo file)
                    {
                        if (snapshot) snapshotBytes = checked(snapshotBytes + file.Length);
                        else versionBytes = checked(versionBytes + file.Length);
                    }
                }
            }
            var snapshots = await new RecoverySnapshotStore(instance, game).ListAsync(token).ConfigureAwait(false);
            return new(versionBytes, snapshotBytes, true, Array.AsReadOnly(snapshots.Select(item =>
                new InstanceRecoverySnapshotSummary(item.Revision, item.CapturedAt, item.Files.Count, item.Files.Sum(file => file.Blob.Length))).ToArray()));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OverflowException or System.Text.Json.JsonException)
        {
            return new(versionBytes, snapshotBytes, false, [], "占用统计或快照列表读取不完整，请检查目录后刷新。");
        }
    }
}

using System.Security.Cryptography;
using System.Text;
using Nexa.Services.Minecraft.Management;

namespace Nexa.Services.Minecraft.Process;

internal static class GameOptionsFingerprint
{
    public static string? Read(string directory)
    {
        try
        {
            RecoveryBlobStore.CheckLinks(directory);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            string[] ListFiles()
            {
                List<string> files = [Path.Combine(directory, "options.txt")];
                foreach (string scope in new[] { "config", "defaultconfigs", "scripts", "kubejs" })
                {
                    string root = Path.Combine(directory, scope); RecoveryBlobStore.CheckLinks(root);
                    if (!Directory.Exists(root)) continue;
                    Stack<(string Path, int Depth)> pending = new(); pending.Push((root, 0));
                    int directories = 0;
                    while (pending.TryPop(out var item))
                    {
                        if (++directories > 4096 || item.Depth > 16) throw new IOException("配置目录超过预算。");
                        foreach (string child in Directory.EnumerateFileSystemEntries(item.Path))
                        {
                            RecoveryBlobStore.CheckLinks(child);
                            if (Directory.Exists(child)) pending.Push((child, item.Depth + 1));
                            else { if (files.Count >= 4096) throw new IOException("配置文件超过预算。"); files.Add(child); }
                        }
                    }
                }
                return files.Order(StringComparer.Ordinal).ToArray();
            }
            string[] files = ListFiles();
            byte[] bytes = new byte[65536];
            long total = 0;
            List<(string Path, long Length, long Stamp)> stamps = [];
            foreach (string path in files)
            {
                RecoveryBlobStore.CheckLinks(path);
                var info = new FileInfo(path);
                long length = info.Length, stamp = info.LastWriteTimeUtc.Ticks;
                int limit = Path.GetFileName(path) == "options.txt" ? 65536 : 1024 * 1024;
                if (length > limit || (total += length) > 16 * 1024 * 1024) return null;
                byte[] name = Encoding.UTF8.GetBytes(Path.GetRelativePath(directory, path).Replace('\\', '/'));
                hash.AppendData(BitConverter.GetBytes(name.Length)); hash.AppendData(name);
                hash.AppendData(BitConverter.GetBytes(length));
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                long received = 0;
                int read;
                while ((read = stream.Read(bytes, 0, (int)Math.Min(bytes.Length, length - received + 1))) > 0)
                {
                    received += read; if (received > length) return null;
                    hash.AppendData(bytes.AsSpan(0, read));
                }
                if (received != length) return null;
                stamps.Add((path, length, stamp));
            }
            if (!files.SequenceEqual(ListFiles())) return null;
            foreach (var item in stamps)
            {
                var info = new FileInfo(item.Path);
                if (!info.Exists || info.Length != item.Length || info.LastWriteTimeUtc.Ticks != item.Stamp) return null;
            }
            return "game-config-2:" + Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }
}

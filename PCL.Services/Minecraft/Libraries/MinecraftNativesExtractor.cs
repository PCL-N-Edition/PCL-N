using System.IO.Compression;

namespace PCL.Services.Minecraft.Libraries;

/// <summary>
/// Extracts native libraries into the natives directory: every entry of every native JAR
/// lands under the natives root, META-INF and code-signing metadata are excluded, and any
/// path that would escape the directory is refused. Extraction is idempotent — existing
/// files with matching length are overwritten unconditionally so a stale native set cannot
/// survive.
/// </summary>
public static class MinecraftNativesExtractor
{
    public static async Task ExtractAsync(
        IReadOnlyList<string> nativeJarPaths,
        string nativesDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nativeJarPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(nativesDirectory);
        Directory.CreateDirectory(nativesDirectory);
        string fullRoot = Path.GetFullPath(nativesDirectory);

        foreach (string jarPath in nativeJarPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(jarPath);
            if (!File.Exists(jarPath))
                throw new FileNotFoundException("The native library archive is missing.", jarPath);
            await using FileStream stream = new(
                jarPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            using ZipArchive archive = new(stream, ZipArchiveMode.Read);
            // Validate every entry before writing any file. A malicious archive therefore cannot
            // leave a partially extracted native set behind when a later entry is unsafe.
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string normalized = entry.FullName.Replace('\\', '/');
                if (normalized.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)
                    || normalized.Equals("META-INF", StringComparison.OrdinalIgnoreCase)
                    || normalized.EndsWith('/'))
                    continue;

                string destination = Path.GetFullPath(Path.Combine(fullRoot, normalized));
                string prefix = Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar;
                StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!destination.StartsWith(prefix, comparison))
                    throw new InvalidDataException($"Native archive entry escapes the natives directory: {entry.FullName}");
            }

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string normalized = entry.FullName.Replace('\\', '/');
                if (normalized.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)
                    || normalized.Equals("META-INF", StringComparison.OrdinalIgnoreCase)
                    || normalized.EndsWith('/'))
                {
                    continue;
                }

                string destination = Path.GetFullPath(Path.Combine(fullRoot, normalized));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using (Stream source = entry.Open())
                await using (FileStream output = File.Create(destination))
                {
                    await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }
}

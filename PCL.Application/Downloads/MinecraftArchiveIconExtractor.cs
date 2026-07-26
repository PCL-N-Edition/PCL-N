// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.IO.Compression;
using System.Security.Cryptography;

namespace PCL.Application.Downloads;

public static class MinecraftArchiveIconExtractor
{
    private const long MaximumIconSize = 8L * 1024L * 1024L;
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] IconSignature = [0x00, 0x00, 0x01, 0x00];
    private static readonly string CacheDirectory = Path.Combine(
        Path.GetTempPath(),
        "PCL-N",
        "Cache",
        "LocalResourceIcons");

    public static string? TryExtract(string sourcePath, string? entryPath) =>
        TryExtract(sourcePath, entryPath, CacheDirectory);

    internal static string? TryExtract(string sourcePath, string? entryPath, string cacheDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            string.IsNullOrWhiteSpace(entryPath) ||
            string.IsNullOrWhiteSpace(cacheDirectory))
        {
            return null;
        }

        string? normalizedEntryPath = NormalizeEntryPath(entryPath);
        if (normalizedEntryPath is null)
            return null;

        try
        {
            if (Directory.Exists(sourcePath))
            {
                string directPath = Path.GetFullPath(Path.Combine(
                    sourcePath,
                    normalizedEntryPath.Replace('/', Path.DirectorySeparatorChar)));
                string sourceRoot = Path.GetFullPath(sourcePath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
                return directPath.StartsWith(sourceRoot, PathComparison) && IsSafeImageFile(directPath)
                    ? directPath
                    : null;
            }

            if (!File.Exists(sourcePath))
                return null;

            using ZipArchive archive = ZipFile.OpenRead(sourcePath);
            ZipArchiveEntry? entry = archive.GetEntry(normalizedEntryPath) ??
                                     archive.Entries.FirstOrDefault(candidate =>
                                         string.Equals(
                                             candidate.FullName.Replace('\\', '/'),
                                             normalizedEntryPath,
                                             StringComparison.OrdinalIgnoreCase));
            if (entry is null || entry.Length <= 0 || entry.Length > MaximumIconSize)
                return null;

            byte[]? bytes = ReadEntryBytes(entry);

            if (bytes is null || !LooksLikeImage(bytes))
                return null;

            string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            Directory.CreateDirectory(cacheDirectory);
            string targetPath = Path.Combine(cacheDirectory, hash + ".img");
            if (File.Exists(targetPath))
                return targetPath;

            string temporaryPath = targetPath + "." + Environment.ProcessId + "." +
                                   Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporaryPath, bytes);
                try
                {
                    File.Move(temporaryPath, targetPath);
                }
                catch (IOException) when (File.Exists(targetPath))
                {
                    File.Delete(temporaryPath);
                }

                temporaryPath = string.Empty;
                return targetPath;
            }
            finally
            {
                if (!string.IsNullOrEmpty(temporaryPath))
                    TryDelete(temporaryPath);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                          UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? NormalizeEntryPath(string entryPath)
    {
        string normalized = entryPath.Trim().Replace('\\', '/').TrimStart('/');
        if (normalized.Length == 0 ||
            Path.IsPathRooted(normalized) ||
            normalized.Split('/').Any(static segment => segment is "" or "." or ".."))
        {
            return null;
        }

        return normalized;
    }

    private static bool LooksLikeImage(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith(PngSignature) ||
        bytes.StartsWith(JpegSignature) ||
        bytes.StartsWith("GIF87a"u8) ||
        bytes.StartsWith("GIF89a"u8) ||
        bytes.StartsWith("BM"u8) ||
        bytes.StartsWith(IconSignature) ||
        bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) &&
        bytes.Slice(8, 4).SequenceEqual("WEBP"u8);

    private static byte[]? ReadEntryBytes(ZipArchiveEntry entry)
    {
        using Stream source = entry.Open();
        using MemoryStream target = new((int)entry.Length);
        byte[] buffer = new byte[81920];
        while (true)
        {
            int read = source.Read(buffer.AsSpan());
            if (read == 0)
                break;
            if (target.Length + read > MaximumIconSize)
                return null;
            target.Write(buffer.AsSpan(0, read));
        }
        return target.Length > 0 ? target.ToArray() : null;
    }

    private static bool IsSafeImageFile(string path)
    {
        if (!File.Exists(path))
            return false;

        FileInfo file = new(path);
        if (file.Length is <= 0 or > MaximumIconSize)
            return false;

        Span<byte> signature = stackalloc byte[12];
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        int read = stream.Read(signature);
        return LooksLikeImage(signature[..read]);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

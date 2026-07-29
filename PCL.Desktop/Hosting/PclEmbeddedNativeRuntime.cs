// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.IO.Compression;
using System.Security.Cryptography;

namespace PCL.Desktop.Hosting;

/// <summary>
/// Installs the RID-specific native libraries embedded in the NativeAOT host.
/// This keeps the distributed launcher compatible with the legacy single-file
/// updater while the actual launcher remains NativeAOT.
/// </summary>
internal static class PclEmbeddedNativeRuntime
{
    public const string ResourceName = "PCL.Desktop.Embedded.NativeRuntime.zip";
    private const string InstalledFilesName = ".pcln-native-runtime-files";
    private const string MarkerPrefix = ".pcln-native-runtime.";

    public static void EnsureInstalled()
    {
        using Stream? resource = typeof(PclEmbeddedNativeRuntime).Assembly
            .GetManifestResourceStream(ResourceName);
        if (resource is null)
            return;

        using MemoryStream buffer = new();
        resource.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定 NativeAOT 启动器路径。");
        string installDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))
            ?? throw new InvalidOperationException("NativeAOT 启动器路径缺少父目录。");
        using FileStream installLock = AcquireInstallLock(installDirectory);
        string markerPath = Path.Combine(installDirectory, MarkerPrefix + hash[..16] + ".ready");
        if (File.Exists(markerPath) && InstalledFilesExist(installDirectory))
            return;

        string temporaryDirectory = Path.Combine(
            installDirectory,
            MarkerPrefix + "extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            List<string> newFiles = Extract(bytes, temporaryDirectory);
            string oldManifestPath = Path.Combine(installDirectory, InstalledFilesName);
            HashSet<string> newSet = new(newFiles, PathComparer);
            string[] oldFiles = ReadInstalledFiles(oldManifestPath);

            foreach (string relative in newFiles)
            {
                string source = ResolveContainedPath(temporaryDirectory, relative);
                string destination = ResolveContainedPath(installDirectory, relative);
                string? parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrWhiteSpace(parent))
                    Directory.CreateDirectory(parent);
                File.Move(source, destination, overwrite: true);
            }

            foreach (string relative in oldFiles)
            {
                if (newSet.Contains(relative))
                    continue;
                string stale = ResolveContainedPath(installDirectory, relative);
                if (File.Exists(stale))
                    File.Delete(stale);
            }

            WriteInstalledFiles(oldManifestPath, newFiles);
            File.WriteAllText(markerPath, hash);
            foreach (string oldMarker in Directory.EnumerateFiles(
                         installDirectory,
                         MarkerPrefix + "*.ready",
                         SearchOption.TopDirectoryOnly))
            {
                if (!string.Equals(oldMarker, markerPath, PathComparison))
                    File.Delete(oldMarker);
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporaryDirectory))
                    Directory.Delete(temporaryDirectory, recursive: true);
            }
            catch
            {
                // Best effort: a later startup uses a fresh unique directory.
            }
        }
    }

    private static FileStream AcquireInstallLock(string directory)
    {
        string path = Path.Combine(directory, ".pcln-native-runtime.lock");
        Exception? lastError = null;
        for (int attempt = 0; attempt < 300; attempt++)
        {
            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                lastError = exception;
                Thread.Sleep(100);
            }
        }

        throw new IOException("等待另一个启动器完成 NativeAOT 运行时安装超时。", lastError);
    }

    private static List<string> Extract(byte[] bytes, string root)
    {
        List<string> files = [];
        using MemoryStream stream = new(bytes, writable: false);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.FullName) ||
                entry.FullName.EndsWith('/') ||
                entry.FullName.EndsWith('\\'))
            {
                continue;
            }

            string relative = NormalizeRelativePath(entry.FullName);
            string destination = ResolveContainedPath(root, relative);
            string? parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(parent))
                Directory.CreateDirectory(parent);
            using Stream source = entry.Open();
            using FileStream target = new(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            source.CopyTo(target);
            files.Add(relative);
        }

        if (files.Count == 0)
            throw new InvalidDataException("内置 NativeAOT 运行时为空。");
        return files;
    }

    private static bool InstalledFilesExist(string root)
    {
        string[] files = ReadInstalledFiles(Path.Combine(root, InstalledFilesName));
        return files.Length > 0 && files.All(relative => File.Exists(ResolveContainedPath(root, relative)));
    }

    private static string[] ReadInstalledFiles(string path)
    {
        if (!File.Exists(path))
            return [];
        return File.ReadAllLines(path)
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0)
            .Select(NormalizeRelativePath)
            .ToArray();
    }

    private static void WriteInstalledFiles(string path, IEnumerable<string> files)
    {
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllLines(temporary, files.Order(StringComparer.Ordinal));
        File.Move(temporary, path, overwrite: true);
    }

    private static string NormalizeRelativePath(string value)
    {
        string relative = value.Replace('\\', '/').TrimStart('/');
        if (relative.Length == 0 ||
            Path.IsPathRooted(relative) ||
            relative.Split('/').Any(static segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException("NativeAOT 运行时包含不安全路径：" + value);
        }
        return relative;
    }

    private static string ResolveContainedPath(string root, string relative)
    {
        string fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(
            fullRoot,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(fullRoot, PathComparison))
            throw new InvalidDataException("NativeAOT 运行时路径越界：" + relative);
        return candidate;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

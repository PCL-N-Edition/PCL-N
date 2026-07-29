// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PCL.Core.Logging;
using PCL.Desktop.Paths;

namespace PCL.Desktop.Hosting;

/// <summary>
/// Installs the RID-specific native libraries embedded in the NativeAOT host.
/// The payload lives under the OOBE-selected launcher data directory so opening
/// a single-file launcher never litters or requires write access to the host directory.
/// </summary>
internal static class PclEmbeddedNativeRuntime
{
    public const string ResourceName = "PCL.Desktop.Embedded.NativeRuntime.zip";
    public const string RelativeRuntimeFolder = "runtime/native";

    private const string InstalledFilesName = ".pcln-native-runtime-files";
    private const string ReadyMarkerName = ".ready";
    private const string InstallLockName = ".pcln-native-runtime.lock";
    private const string ExtractDirectoryPrefix = ".pcln-extract-";
    private static readonly object ActivationGate = new();
    private static readonly List<IntPtr> LoadedLibraries = [];
    private static string? _installedDirectory;

    /// <summary>
    /// Directory containing the native payload activated for this launcher process.
    /// Null for ordinary framework-dependent development builds without an embedded payload.
    /// </summary>
    public static string? InstalledDirectory
    {
        get
        {
            lock (ActivationGate)
                return _installedDirectory;
        }
    }

    public static void EnsureInstalled()
    {
        using Stream? resource = typeof(PclEmbeddedNativeRuntime).Assembly
            .GetManifestResourceStream(ResourceName);
        if (resource is null)
            return;

        using MemoryStream buffer = new();
        resource.CopyTo(buffer);
        string installDirectory = EnsurePayloadInstalled(
            buffer.ToArray(),
            LauncherPathLayout.ResolveDataDirectory(),
            RuntimeInformation.RuntimeIdentifier);

        Activate(installDirectory);
        CleanupLegacyHostLayout(LauncherPathLayout.GetHostDirectory());
        PortableLog.Info("NativeRuntime", "已从 OOBE 数据目录加载 NativeAOT 运行时：" + installDirectory);
    }

    internal static string EnsurePayloadInstalled(
        byte[] bytes,
        string dataDirectory,
        string runtimeIdentifier)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);
        if (bytes.Length == 0)
            throw new InvalidDataException("内置 NativeAOT 运行时为空。");

        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        string runtimeRoot = GetRuntimeRoot(dataDirectory, runtimeIdentifier);
        Directory.CreateDirectory(runtimeRoot);

        using FileStream installLock = AcquireInstallLock(runtimeRoot);
        CleanupInterruptedExtracts(runtimeRoot);

        string installDirectory = Path.Combine(runtimeRoot, hash[..16]);
        string markerPath = Path.Combine(installDirectory, ReadyMarkerName);
        if (File.Exists(markerPath) && InstalledFilesExist(installDirectory))
            return installDirectory;

        if (Directory.Exists(installDirectory))
            Directory.Delete(installDirectory, recursive: true);

        string temporaryDirectory = Path.Combine(
            runtimeRoot,
            ExtractDirectoryPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            List<string> files = Extract(bytes, temporaryDirectory);
            WriteInstalledFiles(
                Path.Combine(temporaryDirectory, InstalledFilesName),
                files);
            File.WriteAllText(Path.Combine(temporaryDirectory, ReadyMarkerName), hash);
            Directory.Move(temporaryDirectory, installDirectory);
            temporaryDirectory = string.Empty;
            return installDirectory;
        }
        finally
        {
            try
            {
                if (!string.IsNullOrEmpty(temporaryDirectory) &&
                    Directory.Exists(temporaryDirectory))
                {
                    Directory.Delete(temporaryDirectory, recursive: true);
                }
            }
            catch
            {
                // Best effort: a later startup removes interrupted extraction directories.
            }
        }
    }

    internal static string GetRuntimeRoot(string dataDirectory, string runtimeIdentifier)
    {
        string rid = runtimeIdentifier.Trim();
        if (rid.Length == 0 ||
            rid.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 ||
            rid is "." or "..")
        {
            throw new InvalidDataException("NativeAOT 运行时 RID 不安全：" + runtimeIdentifier);
        }

        return Path.GetFullPath(Path.Combine(
            dataDirectory,
            RelativeRuntimeFolder.Replace('/', Path.DirectorySeparatorChar),
            rid));
    }

    private static void Activate(string installDirectory)
    {
        string fullInstallDirectory = Path.GetFullPath(installDirectory);
        lock (ActivationGate)
        {
            if (string.Equals(_installedDirectory, fullInstallDirectory, PathComparison))
                return;

            string[] searchDirectories = EnumerateNativeSearchDirectories(fullInstallDirectory)
                .Distinct(PathComparer)
                .ToArray();
            ConfigureNativeSearchDirectories(searchDirectories);

            foreach (string library in EnumerateTopLevelNativeLibraries(fullInstallDirectory))
            {
                try
                {
                    LoadedLibraries.Add(NativeLibrary.Load(library));
                }
                catch (Exception ex)
                {
                    throw new DllNotFoundException(
                        $"无法从 OOBE 数据目录加载 NativeAOT 原生库：{library}",
                        ex);
                }
            }

            _installedDirectory = fullInstallDirectory;
        }
    }

    private static IEnumerable<string> EnumerateNativeSearchDirectories(string installDirectory)
    {
        yield return installDirectory;

        foreach (string fileName in GetLibVlcLibraryNames())
        {
            foreach (string file in Directory.EnumerateFiles(
                         installDirectory,
                         fileName,
                         SearchOption.AllDirectories))
            {
                string? directory = Path.GetDirectoryName(file);
                if (!string.IsNullOrWhiteSpace(directory))
                    yield return directory;
            }
        }
    }

    private static IEnumerable<string> EnumerateTopLevelNativeLibraries(string installDirectory)
    {
        return Directory.EnumerateFiles(installDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(IsNativeLibraryForCurrentPlatform)
            .OrderBy(GetNativeLibraryLoadPriority)
            .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsNativeLibraryForCurrentPlatform(string path)
    {
        string name = Path.GetFileName(path);
        if (OperatingSystem.IsWindows())
            return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        if (OperatingSystem.IsMacOS())
            return name.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase);
        return name.Contains(".so", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetNativeLibraryLoadPriority(string path)
    {
        string name = Path.GetFileName(path);
        if (name.Contains("gles", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (name.Contains("harfbuzz", StringComparison.OrdinalIgnoreCase))
            return 10;
        if (name.Contains("skia", StringComparison.OrdinalIgnoreCase))
            return 20;
        if (name.Contains("avalonia", StringComparison.OrdinalIgnoreCase))
            return 30;
        if (name.Contains("vlc", StringComparison.OrdinalIgnoreCase))
            return 40;
        return 100;
    }

    private static string[] GetLibVlcLibraryNames()
    {
        if (OperatingSystem.IsWindows())
            return ["libvlc.dll"];
        if (OperatingSystem.IsMacOS())
            return ["libvlc.dylib"];
        return ["libvlc.so", "libvlc.so.5"];
    }

    private static void ConfigureNativeSearchDirectories(IReadOnlyList<string> directories)
    {
        string? currentNative = AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string;
        string nativeSearchPath = MergeSearchPath(directories, currentNative);
        AppContext.SetData("NATIVE_DLL_SEARCH_DIRECTORIES", nativeSearchPath);

        // Windows consults PATH for dependent DLLs loaded by an absolute path.
        // Unix loaders do not reliably re-read LD_LIBRARY_PATH/DYLD_LIBRARY_PATH
        // after process start, so top-level Unix libraries are preloaded above.
        if (OperatingSystem.IsWindows())
        {
            string path = MergeSearchPath(
                directories,
                Environment.GetEnvironmentVariable("PATH"));
            Environment.SetEnvironmentVariable("PATH", path);
        }
    }

    private static string MergeSearchPath(
        IEnumerable<string> preferredDirectories,
        string? existingValue)
    {
        List<string> paths = [];
        HashSet<string> seen = new(PathComparer);
        foreach (string directory in preferredDirectories)
        {
            string full = Path.GetFullPath(directory);
            if (seen.Add(full))
                paths.Add(full);
        }

        if (!string.IsNullOrWhiteSpace(existingValue))
        {
            foreach (string entry in existingValue.Split(
                         Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (seen.Add(entry))
                    paths.Add(entry);
            }
        }

        return string.Join(Path.PathSeparator, paths);
    }

    private static FileStream AcquireInstallLock(string directory)
    {
        string path = Path.Combine(directory, InstallLockName);
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

    private static void CleanupInterruptedExtracts(string runtimeRoot)
    {
        foreach (string directory in Directory.EnumerateDirectories(
                     runtimeRoot,
                     ExtractDirectoryPrefix + "*",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // Best effort. A unique directory is used for this extraction.
            }
        }
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

    private static void CleanupLegacyHostLayout(string hostDirectory)
    {
        string manifestPath = Path.Combine(hostDirectory, InstalledFilesName);
        string[] oldFiles;
        try
        {
            oldFiles = ReadInstalledFiles(manifestPath);
        }
        catch
        {
            return;
        }

        foreach (string relative in oldFiles)
        {
            try
            {
                string path = ResolveContainedPath(hostDirectory, relative);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Old launchers may still have a native module open. Retry next start.
            }
        }

        foreach (string relative in oldFiles.OrderByDescending(static path => path.Count(c => c == '/')))
        {
            string? directory = Path.GetDirectoryName(ResolveContainedPath(hostDirectory, relative));
            while (!string.IsNullOrWhiteSpace(directory) &&
                   !string.Equals(directory, hostDirectory, PathComparison))
            {
                try
                {
                    if (!Directory.Exists(directory) ||
                        Directory.EnumerateFileSystemEntries(directory).Any())
                    {
                        break;
                    }
                    Directory.Delete(directory);
                    directory = Path.GetDirectoryName(directory);
                }
                catch
                {
                    break;
                }
            }
        }

        TryDeleteFile(manifestPath);
        foreach (string marker in Directory.EnumerateFiles(
                     hostDirectory,
                     ".pcln-native-runtime.*.ready",
                     SearchOption.TopDirectoryOnly))
        {
            TryDeleteFile(marker);
        }
        TryDeleteFile(Path.Combine(hostDirectory, InstallLockName));
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort legacy cleanup.
        }
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

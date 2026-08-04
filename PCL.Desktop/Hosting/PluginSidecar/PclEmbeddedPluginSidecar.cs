// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.IO.Compression;
using System.Security.Cryptography;
using PCL.Core.Logging;
using PCL.Desktop.Paths;

namespace PCL.Desktop.Hosting.PluginSidecar;

/// <summary>
/// Resolves the CoreCLR plugin sidecar executable.
/// Preferred path: C <c>pcln-launcher</c> points at the expanded <c>sidecar/</c> tree
/// via <c>PCL_PLUGIN_SIDECAR_DIR</c> / <c>PCL_PLUGIN_SIDECAR_EXE</c> (release scatter).
/// Fallback: extract the zip embedded in the host (dev / no bootstrap).
/// </summary>
internal static class PclEmbeddedPluginSidecar
{
    public const string ResourceName = "PCL.Desktop.Embedded.PluginSidecar.zip";
    public const string RelativeRuntimeFolder = "runtime/sidecar";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _cachedExecutable;

    /// <summary>
    /// Ensure the embedded sidecar payload is extracted under the current data directory.
    /// Returns the path to <c>PCL.Plugin.Sidecar(.exe)</c>, or null when no payload is embedded.
    /// </summary>
    public static async Task<string?> EnsureExtractedAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(_cachedExecutable) && File.Exists(_cachedExecutable))
            return _cachedExecutable;

        // C bootstrap may pre-extract the sidecar tree and pass env.
        string? preExe = Environment.GetEnvironmentVariable("PCL_PLUGIN_SIDECAR_EXE");
        if (!string.IsNullOrWhiteSpace(preExe) && File.Exists(preExe))
        {
            _cachedExecutable = preExe;
            PortableLog.Info("PluginSidecar", "使用 C launcher 指定的侧车：" + preExe);
            return preExe;
        }

        string? preDir = Environment.GetEnvironmentVariable("PCL_PLUGIN_SIDECAR_DIR");
        if (!string.IsNullOrWhiteSpace(preDir) && Directory.Exists(preDir))
        {
            string candidate = Path.Combine(preDir, PluginSidecarPaths.ExecutableFileName);
            if (File.Exists(candidate))
            {
                _cachedExecutable = candidate;
                PortableLog.Info("PluginSidecar", "使用 C launcher 预解压的侧车目录：" + candidate);
                return candidate;
            }
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(_cachedExecutable) && File.Exists(_cachedExecutable))
                return _cachedExecutable;

            await using Stream? resource = typeof(PclEmbeddedPluginSidecar).Assembly
                .GetManifestResourceStream(ResourceName);
            if (resource is null)
                return null;

            await using MemoryStream buffer = new();
            await resource.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            buffer.Position = 0;
            string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(buffer, cancellationToken).ConfigureAwait(false));
            buffer.Position = 0;

            string dataRoot = LauncherPathLayout.ResolveDataDirectory();
            string runtimeRoot = Path.Combine(
                dataRoot,
                RelativeRuntimeFolder.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(runtimeRoot);

            string installDir = Path.Combine(runtimeRoot, hash[..16]);
            string exeName = PluginSidecarPaths.ExecutableFileName;
            string exePath = Path.Combine(installDir, exeName);
            string stampPath = Path.Combine(installDir, ".extracted");

            if (File.Exists(exePath) && File.Exists(stampPath))
            {
                _cachedExecutable = exePath;
                PortableLog.Info("PluginSidecar", "使用已解压的内置侧车：" + exePath);
                return exePath;
            }

            string path = await ExtractZipAsync(buffer, installDir, exeName, stampPath, cancellationToken)
                .ConfigureAwait(false);
            _cachedExecutable = path;
            PortableLog.Info("PluginSidecar", "已解压内置侧车到配置目录：" + path);
            return path;
        }
        catch (Exception ex)
        {
            PortableLog.Warn("PluginSidecar", "解压内置侧车失败：" + ex.Message);
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Invalidate cached path (e.g. after OOBE path migrate + restart).</summary>
    public static void InvalidateCache() => _cachedExecutable = null;

    private static async Task<string> ExtractZipAsync(
        Stream zipStream,
        string installDir,
        string exeName,
        string stampPath,
        CancellationToken cancellationToken)
    {
        string tempDir = installDir + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
            Directory.CreateDirectory(tempDir);

            if (zipStream.CanSeek)
                zipStream.Position = 0;

            using (ZipArchive archive = new(zipStream, ZipArchiveMode.Read, leaveOpen: true))
            {
                string installRoot = Path.GetFullPath(tempDir);
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(entry.FullName) ||
                        entry.FullName.EndsWith('/') ||
                        entry.FullName.EndsWith('\\'))
                        continue;

                    string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar)
                        .Replace('\\', Path.DirectorySeparatorChar);
                    string destination = Path.GetFullPath(Path.Combine(tempDir, relative));
                    if (!destination.StartsWith(installRoot, OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "Sidecar zip contains path outside extract root: " + entry.FullName);
                    }

                    string? parent = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrWhiteSpace(parent))
                        Directory.CreateDirectory(parent);

                    await using Stream entryStream = entry.Open();
                    await using FileStream file = new(
                        destination,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        64 * 1024,
                        useAsync: true);
                    await entryStream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                }
            }

            string tempExe = Path.Combine(tempDir, exeName);
            if (!File.Exists(tempExe))
            {
                string[] nested = Directory.GetFiles(tempDir, exeName, SearchOption.AllDirectories);
                if (nested.Length == 0)
                    throw new InvalidDataException("Sidecar zip missing " + exeName);

                string nestedExe = nested[0];
                string nestedRoot = Path.GetDirectoryName(nestedExe)!;
                if (!PathsEqual(nestedRoot, tempDir))
                {
                    foreach (string path in Directory.GetFileSystemEntries(nestedRoot))
                    {
                        string name = Path.GetFileName(path);
                        string dest = Path.Combine(tempDir, name);
                        if (Directory.Exists(path))
                            MoveDirectoryReplace(path, dest);
                        else
                            File.Move(path, dest, overwrite: true);
                    }
                }

                tempExe = Path.Combine(tempDir, exeName);
                if (!File.Exists(tempExe))
                    tempExe = nestedExe;
            }

            if (!File.Exists(tempExe))
                throw new InvalidDataException("Sidecar extract failed; executable not found.");

            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(
                        tempExe,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
                catch
                {
                    // best-effort
                }
            }

            if (Directory.Exists(installDir))
            {
                try
                {
                    Directory.Delete(installDir, recursive: true);
                }
                catch
                {
                    string existing = Path.Combine(installDir, exeName);
                    if (File.Exists(existing))
                    {
                        try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
                        return existing;
                    }

                    throw;
                }
            }

            Directory.Move(tempDir, installDir);
            tempDir = string.Empty;

            string finalExe = Path.Combine(installDir, exeName);
            if (!File.Exists(finalExe))
            {
                string[] found = Directory.GetFiles(installDir, exeName, SearchOption.AllDirectories);
                if (found.Length == 0)
                    throw new InvalidDataException("Sidecar install missing " + exeName);
                finalExe = found[0];
            }

            await File.WriteAllTextAsync(stampPath, DateTimeOffset.UtcNow.ToString("O"), cancellationToken)
                .ConfigureAwait(false);
            return finalExe;
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
            }
        }
    }

    private static void MoveDirectoryReplace(string source, string destination)
    {
        if (Directory.Exists(destination))
            Directory.Delete(destination, recursive: true);
        Directory.Move(source, destination);
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

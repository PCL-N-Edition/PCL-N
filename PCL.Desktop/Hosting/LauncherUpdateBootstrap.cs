// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

namespace PCL.Desktop.Hosting;

/// <summary>
/// Runs the verified target launcher as the replacement helper. This follows the
/// upstream launcher's update model: the new executable waits for the old process
/// to exit, replaces it, and only then starts the installed copy.
/// </summary>
internal static class LauncherUpdateBootstrap
{
    private const string ApplyCommand = "--pcln-apply-update";
    private const string CleanupCommand = "--pcln-update-finished";
    private const int ReplacementAttempts = 240;
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static bool TryRunUpdateHelper(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length != 6 || !string.Equals(args[0], ApplyCommand, StringComparison.Ordinal))
            return false;

        try
        {
            int oldProcessId = int.Parse(args[1], CultureInfo.InvariantCulture);
            string current = Path.GetFullPath(args[2]);
            string staged = Path.GetFullPath(args[3]);
            string workDirectory = Path.GetFullPath(args[4]);
            bool restart = args[5] == "1";
            ValidateUpdatePaths(current, staged, workDirectory);
            ValidateRunningHelper(staged);
            WaitForProcessExit(oldProcessId);
            exitCode = ReplaceAndOptionallyRestart(
                current,
                staged,
                workDirectory,
                restart);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("PCL N update helper failed: " + exception);
            exitCode = 1;
        }

        return true;
    }

    public static string[] ProcessStartupCleanup(string[] args)
    {
        if (args.Length != 5 || !string.Equals(args[0], CleanupCommand, StringComparison.Ordinal))
        {
            CleanupStaleSiblingUpdates();
            return args;
        }

        try
        {
            int helperProcessId = int.Parse(args[1], CultureInfo.InvariantCulture);
            string staged = Path.GetFullPath(args[2]);
            string backup = Path.GetFullPath(args[3]);
            string workDirectory = Path.GetFullPath(args[4]);
            string current = Path.GetFullPath(Environment.ProcessPath
                ?? throw new InvalidOperationException("无法确定更新后启动器路径。"));
            ValidateUpdatePaths(current, staged, workDirectory);
            if (!string.Equals(backup, current + ".pcln-old", PathComparison))
                throw new InvalidOperationException("更新清理备份路径无效。");

            WaitForProcessExit(helperProcessId);
            DeleteWithRetry(staged);
            DeleteWithRetry(backup);
            DeleteDirectoryWithRetry(workDirectory);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("PCL N update cleanup failed: " + exception.Message);
        }

        return [];
    }

    private static int ReplaceAndOptionallyRestart(
        string current,
        string staged,
        string workDirectory,
        bool restart)
    {
        string backup = current + ".pcln-old";
        string replacement = current + ".pcln-new";
        string expectedSha256 = CalculateSha256(staged);
        Exception? lastError = null;

        for (int attempt = 0; attempt < ReplacementAttempts; attempt++)
        {
            try
            {
                TryRestoreCurrent(current, backup);
                DeleteIfExists(replacement);
                DeleteIfExists(backup);

                // Copy instead of moving: on Windows the helper executable is its own
                // staged source and remains image-mapped until this process exits.
                File.Copy(staged, replacement, overwrite: true);
                PreserveExecutableMode(staged, replacement);
                VerifySha256(replacement, expectedSha256);

                File.Move(current, backup, overwrite: true);
                try
                {
                    File.Move(replacement, current, overwrite: true);
                    PreserveExecutableMode(staged, current);
                    VerifySha256(current, expectedSha256);
                }
                catch
                {
                    DeleteIfExists(current);
                    TryRestoreCurrent(current, backup);
                    throw;
                }

                if (restart)
                    StartInstalledLauncher(current, staged, backup, workDirectory);
                else
                {
                    DeleteIfExists(backup);
                    DeleteDirectoryIfSafe(workDirectory);
                }
                return 0;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or CryptographicException)
            {
                lastError = exception;
                TryRestoreCurrent(current, backup);
                Thread.Sleep(250);
            }
        }

        throw new IOException(
            $"等待旧启动器释放文件后仍无法完成替换（已重试 {ReplacementAttempts} 次）。",
            lastError);
    }

    private static void StartInstalledLauncher(
        string current,
        string staged,
        string backup,
        string workDirectory)
    {
        ProcessStartInfo startInfo = new(current)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(current) ?? Environment.CurrentDirectory
        };
        startInfo.ArgumentList.Add(CleanupCommand);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(staged);
        startInfo.ArgumentList.Add(backup);
        startInfo.ArgumentList.Add(workDirectory);
        Process? process = Process.Start(startInfo);
        if (process is null)
            throw new InvalidOperationException("更新已替换，但无法重新启动启动器。");
        process.Dispose();
    }

    private static void ValidateRunningHelper(string staged)
    {
        string processPath = Path.GetFullPath(Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定更新帮助程序路径。"));
        if (!string.Equals(processPath, staged, PathComparison))
            throw new InvalidOperationException("更新帮助程序不是已校验的暂存启动器。");
    }

    private static void ValidateUpdatePaths(string current, string staged, string workDirectory)
    {
        string? currentDirectory = Path.GetDirectoryName(current);
        if (string.IsNullOrWhiteSpace(currentDirectory) ||
            !string.Equals(currentDirectory, Path.GetDirectoryName(staged), PathComparison))
        {
            throw new InvalidOperationException("暂存启动器必须与当前启动器位于同一目录。");
        }

        string expectedPrefix = "." + Path.GetFileName(current) + ".";
        string stagedName = Path.GetFileName(staged);
        if (!stagedName.StartsWith(expectedPrefix, PathComparison) ||
            !stagedName.EndsWith(".update", PathComparison))
        {
            throw new InvalidOperationException("暂存启动器文件名无效。");
        }

        if (!IsSafeUpdateWorkDirectory(workDirectory))
            throw new InvalidOperationException("更新工作目录无效。");
    }

    private static bool IsSafeUpdateWorkDirectory(string path)
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PCL-N", "updates"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        return candidate.StartsWith(root, PathComparison);
    }

    private static void WaitForProcessExit(int processId)
    {
        if (processId <= 0 || processId == Environment.ProcessId)
            return;
        try
        {
            using Process process = Process.GetProcessById(processId);
            process.WaitForExit();
        }
        catch (ArgumentException)
        {
            // The process already exited.
        }
    }

    private static void TryRestoreCurrent(string current, string backup)
    {
        try
        {
            if (!File.Exists(current) && File.Exists(backup))
                File.Move(backup, current, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Unable to restore previous launcher: " + exception.Message);
        }
    }

    private static string CalculateSha256(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void VerifySha256(string path, string expected)
    {
        string actual = CalculateSha256(path);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("更新替换后的程序校验失败。");
    }

    private static void PreserveExecutableMode(string source, string target)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(target, File.GetUnixFileMode(source));
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static void DeleteWithRetry(string path)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                DeleteIfExists(path);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt == 39)
                    throw;
                Thread.Sleep(100);
            }
        }
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        if (!IsSafeUpdateWorkDirectory(path))
            return;
        for (int attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt == 39)
                    throw;
                Thread.Sleep(100);
            }
        }
    }

    private static void DeleteDirectoryIfSafe(string path)
    {
        try
        {
            DeleteDirectoryWithRetry(path);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Unable to clean update directory: " + exception.Message);
        }
    }

    private static void CleanupStaleSiblingUpdates()
    {
        try
        {
            string current = Path.GetFullPath(Environment.ProcessPath ?? string.Empty);
            string? directory = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(directory))
                return;
            string pattern = "." + Path.GetFileName(current) + ".*.update";
            DateTime cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (string candidate in Directory.EnumerateFiles(directory, pattern))
            {
                if (File.GetLastWriteTimeUtc(candidate) < cutoff)
                    DeleteIfExists(candidate);
            }
            DeleteIfExists(current + ".pcln-old");
            DeleteIfExists(current + ".pcln-new");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Unable to clean stale update files: " + exception.Message);
        }
    }
}

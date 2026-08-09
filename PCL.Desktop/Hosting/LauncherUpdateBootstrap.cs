// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PCL.Application.Updates;

namespace PCL.Desktop.Hosting;

/// <summary>
/// Runs the verified target launcher as the replacement helper. This follows the
/// upstream launcher's update model: the new executable waits for the old process
/// to exit, replaces it, and only then starts the installed copy.
/// </summary>
internal static class LauncherUpdateBootstrap
{
    private const string ApplyCommand = "--pcln-apply-update";
    private const string ApplyTreeCommand = "--pcln-apply-tree-update";
    private const string CleanupCommand = "--pcln-update-finished";
    private const string CleanupTreeCommand = "--pcln-tree-update-finished";
    private const string CompletedTreeMarker = "tree-update-complete";
    private const int ReplacementAttempts = 240;
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static bool TryRunUpdateHelper(string[] args, out int exitCode)
    {
        exitCode = 0;
        string? workDirectory = null;
        bool isLegacy = args.Length == 6 && string.Equals(args[0], ApplyCommand, StringComparison.Ordinal);
        bool isTree = args.Length == 6 && string.Equals(args[0], ApplyTreeCommand, StringComparison.Ordinal);
        if (!isLegacy && !isTree)
            return false;

        try
        {
            int oldProcessId = int.Parse(args[1], CultureInfo.InvariantCulture);
            string current = Path.GetFullPath(args[2]);
            string stagedOrPlan = Path.GetFullPath(args[3]);
            workDirectory = Path.GetFullPath(args[4]);
            bool restart = args[5] == "1";
            if (isTree)
            {
                LauncherInstallPlan plan = ReadAndValidateInstallPlan(current, stagedOrPlan, workDirectory);
                ValidateRunningTreeHelper(plan, workDirectory);
                WaitForProcessExit(oldProcessId);
                WaitForBootstrapExit(oldProcessId);
                exitCode = ReplaceTreeAndOptionallyRestart(plan, workDirectory, restart);
            }
            else
            {
                ValidateUpdatePaths(current, stagedOrPlan, workDirectory);
                ValidateRunningHelper(stagedOrPlan);
                WaitForProcessExit(oldProcessId);
                WaitForBootstrapExit(oldProcessId);
                exitCode = ReplaceAndOptionallyRestart(
                    current,
                    stagedOrPlan,
                    workDirectory,
                    restart);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("PCL N update helper failed: " + exception);
            WriteUpdateFailureLog(workDirectory, exception);
            exitCode = 1;
        }

        return true;
    }

    private static void WriteUpdateFailureLog(string? workDirectory, Exception exception)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(workDirectory) || !IsSafeUpdateWorkDirectory(workDirectory))
                return;

            Directory.CreateDirectory(workDirectory);
            File.WriteAllText(
                Path.Combine(workDirectory, "update-error.log"),
                $"{DateTimeOffset.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}");
        }
        catch
        {
            // The update exception remains the primary failure. Diagnostics are best-effort.
        }
    }

    public static string[] ProcessStartupCleanup(string[] args)
    {
        if (args.Length == 3 && string.Equals(args[0], CleanupTreeCommand, StringComparison.Ordinal))
        {
            try
            {
                int helperProcessId = int.Parse(args[1], CultureInfo.InvariantCulture);
                string workDirectory = Path.GetFullPath(args[2]);
                if (!IsSafeUpdateWorkDirectory(workDirectory))
                    throw new InvalidOperationException("散包更新清理目录无效。");
                WaitForProcessExit(helperProcessId);
                DeleteDirectoryWithRetry(workDirectory);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("PCL N tree update cleanup failed: " + exception.Message);
            }
            return [];
        }

        if (args.Length != 5 || !string.Equals(args[0], CleanupCommand, StringComparison.Ordinal))
        {
            CleanupCompletedTreeUpdates();
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

    private static LauncherInstallPlan ReadAndValidateInstallPlan(
        string currentEntry,
        string planPath,
        string workDirectory)
    {
        if (!IsSafeUpdateWorkDirectory(workDirectory) ||
            !IsPathWithin(planPath, workDirectory) ||
            !File.Exists(planPath))
        {
            throw new InvalidOperationException("散包更新计划路径无效。");
        }

        LauncherInstallPlan plan = JsonSerializer.Deserialize(
                File.ReadAllBytes(planPath),
                LauncherUpdateBootstrapJsonContext.Default.LauncherInstallPlan)
            ?? throw new InvalidDataException("无法读取散包更新计划。");
        if (plan.FormatVersion != 1 ||
            string.IsNullOrWhiteSpace(plan.InstallRoot) ||
            string.IsNullOrWhiteSpace(plan.StagedRoot) ||
            string.IsNullOrWhiteSpace(plan.EntryRelativePath) ||
            plan.Files.Count == 0)
        {
            throw new InvalidDataException("散包更新计划格式无效。");
        }

        string installRoot = Path.GetFullPath(plan.InstallRoot);
        string stagedRoot = Path.GetFullPath(plan.StagedRoot);
        if (!IsPathWithin(stagedRoot, workDirectory))
            throw new InvalidOperationException("散包暂存目录不在更新工作目录内。");
        string expectedEntry = ResolveSafeRelativePath(installRoot, plan.EntryRelativePath);
        if (!string.Equals(expectedEntry, currentEntry, PathComparison))
            throw new InvalidOperationException("散包更新入口与当前产品入口不一致。");

        HashSet<string> paths = new(PathComparison == StringComparison.OrdinalIgnoreCase
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        foreach (LauncherUpdateFileEntry file in plan.Files)
        {
            file.Path = NormalizeRelativePath(file.Path);
            if (file.Size < 0 || !IsSha256(file.Sha256) || !paths.Add(file.Path))
                throw new InvalidDataException($"散包安装文件条目无效：{file.Path}。");
            _ = ResolveSafeRelativePath(stagedRoot, file.Path);
            _ = ResolveSafeRelativePath(installRoot, file.Path);
        }
        foreach (string deletePath in plan.DeletePaths)
        {
            string relative = NormalizeRelativePath(deletePath);
            if (!paths.Add(relative))
                throw new InvalidDataException($"散包删除路径重复：{relative}。");
            _ = ResolveSafeRelativePath(installRoot, relative);
        }

        plan.InstallRoot = installRoot;
        plan.StagedRoot = stagedRoot;
        plan.EntryRelativePath = NormalizeRelativePath(plan.EntryRelativePath);
        return plan;
    }

    private static void ValidateRunningTreeHelper(LauncherInstallPlan plan, string workDirectory)
    {
        string processPath = Path.GetFullPath(Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定散包更新帮助程序路径。"));
        if (!IsPathWithin(processPath, plan.StagedRoot!) || !IsPathWithin(processPath, workDirectory))
            throw new InvalidOperationException("散包更新帮助程序不在已校验暂存目录内。");
        LauncherUpdateFileEntry? helper = plan.Files.FirstOrDefault(file =>
            string.Equals(
                ResolveSafeRelativePath(plan.StagedRoot!, file.Path!),
                processPath,
                PathComparison));
        if (helper is null)
            throw new InvalidOperationException("散包更新帮助程序不属于目标文件清单。");
        VerifyFileEntry(processPath, helper);
    }

    private static int ReplaceTreeAndOptionallyRestart(
        LauncherInstallPlan plan,
        string workDirectory,
        bool restart)
    {
        string installRoot = plan.InstallRoot!;
        string stagedRoot = plan.StagedRoot!;
        string backupRoot = Path.Combine(workDirectory, "rollback");
        Directory.CreateDirectory(backupRoot);

        // Verify the complete target before touching the installation.
        foreach (LauncherUpdateFileEntry file in plan.Files)
            VerifyFileEntry(ResolveSafeRelativePath(stagedRoot, file.Path!), file);

        List<string> touched = [];
        try
        {
            IEnumerable<LauncherUpdateFileEntry> orderedFiles = plan.Files
                .OrderBy(file => string.Equals(file.Path, plan.EntryRelativePath, PathComparison) ? 1 : 0)
                .ThenBy(static file => file.Path, StringComparer.Ordinal);
            foreach (LauncherUpdateFileEntry file in orderedFiles)
            {
                string relative = file.Path!;
                string source = ResolveSafeRelativePath(stagedRoot, relative);
                string destination = ResolveSafeRelativePath(installRoot, relative);
                string backup = ResolveSafeRelativePath(backupRoot, relative);
                BackupExistingFile(destination, backup);
                touched.Add(relative);
                ReplaceFromStageWithRetry(source, destination, file);
            }

            foreach (string raw in plan.DeletePaths)
            {
                string relative = NormalizeRelativePath(raw);
                string destination = ResolveSafeRelativePath(installRoot, relative);
                if (!File.Exists(destination))
                    continue;
                string backup = ResolveSafeRelativePath(backupRoot, relative);
                BackupExistingFile(destination, backup);
                touched.Add(relative);
            }

            string currentEntry = ResolveSafeRelativePath(installRoot, plan.EntryRelativePath!);
            if (restart)
                StartInstalledTreeLauncher(currentEntry, workDirectory);
            else
                File.WriteAllText(Path.Combine(workDirectory, CompletedTreeMarker), string.Empty);
            return 0;
        }
        catch
        {
            RollbackTree(installRoot, backupRoot, touched);
            throw;
        }
    }

    private static void BackupExistingFile(string destination, string backup)
    {
        if (!File.Exists(destination))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        MoveWithRetry(destination, backup, overwrite: true);
    }

    private static void ReplaceFromStageWithRetry(
        string source,
        string destination,
        LauncherUpdateFileEntry expected)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + ".pcln-new";
        DeleteIfExists(temporary);
        File.Copy(source, temporary, overwrite: true);
        PreserveExecutableMode(source, temporary);
        VerifyFileEntry(temporary, expected);
        MoveWithRetry(temporary, destination, overwrite: true);
        PreserveExecutableMode(source, destination);
        VerifyFileEntry(destination, expected);
    }

    private static void MoveWithRetry(string source, string destination, bool overwrite)
    {
        Exception? lastError = null;
        for (int attempt = 0; attempt < ReplacementAttempts; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                lastError = exception;
                Thread.Sleep(250);
            }
        }
        throw new IOException($"等待文件释放后仍无法替换：{destination}", lastError);
    }

    private static void RollbackTree(string installRoot, string backupRoot, IEnumerable<string> touched)
    {
        foreach (string relative in touched.Reverse())
        {
            string destination = ResolveSafeRelativePath(installRoot, relative);
            string backup = ResolveSafeRelativePath(backupRoot, relative);
            try
            {
                DeleteIfExists(destination);
                if (File.Exists(backup))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Move(backup, destination, overwrite: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("Unable to roll back " + relative + ": " + exception.Message);
            }
        }
    }

    private static void StartInstalledTreeLauncher(string currentEntry, string workDirectory)
    {
        ProcessStartInfo startInfo = new(currentEntry)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(currentEntry) ?? Environment.CurrentDirectory
        };
        startInfo.ArgumentList.Add(CleanupTreeCommand);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(workDirectory);
        Process? process = Process.Start(startInfo);
        if (process is null)
            throw new InvalidOperationException("散包更新已替换，但无法重新启动启动器。");
        process.Dispose();
    }

    private static void VerifyFileEntry(string path, LauncherUpdateFileEntry entry)
    {
        FileInfo info = new(path);
        if (!info.Exists || info.Length != entry.Size)
            throw new CryptographicException($"散包文件大小校验失败：{entry.Path}。");
        VerifySha256(path, entry.Sha256!);
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

    private static bool IsPathWithin(string path, string root)
    {
        string rootPrefix = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(path);
        return candidate.StartsWith(rootPrefix, PathComparison);
    }

    private static string ResolveSafeRelativePath(string root, string relativePath)
    {
        string normalized = NormalizeRelativePath(relativePath);
        string rootPrefix = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(
            rootPrefix,
            normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(rootPrefix, PathComparison))
            throw new InvalidDataException($"更新路径越界：{relativePath}。");
        return candidate;
    }

    private static string NormalizeRelativePath(string? path)
    {
        string normalized = (path ?? string.Empty).Replace('\\', '/').Trim('/');
        if (normalized.Length == 0 || Path.IsPathRooted(normalized) ||
            normalized.Split('/').Any(static segment => segment is "" or "." or ".." || segment.Contains(':')))
        {
            throw new InvalidDataException($"更新包含无效相对路径：{path}。");
        }
        return normalized;
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

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

    private static void WaitForBootstrapExit(int oldProcessId)
    {
        string? raw = Environment.GetEnvironmentVariable("PCL_LAUNCHER_BOOTSTRAP_PID");
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int bootstrapProcessId) ||
            bootstrapProcessId <= 0 ||
            bootstrapProcessId == oldProcessId ||
            bootstrapProcessId == Environment.ProcessId)
        {
            return;
        }

        WaitForProcessExit(bootstrapProcessId);
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

    private static void CleanupCompletedTreeUpdates()
    {
        try
        {
            string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PCL-N", "updates"));
            if (!Directory.Exists(root))
                return;
            string current = Path.GetFullPath(Environment.ProcessPath ?? string.Empty);
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                if (IsPathWithin(current, directory) ||
                    !File.Exists(Path.Combine(directory, CompletedTreeMarker)))
                {
                    continue;
                }
                DeleteDirectoryWithRetry(directory);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Unable to clean completed tree update: " + exception.Message);
        }
    }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(LauncherInstallPlan))]
internal sealed partial class LauncherUpdateBootstrapJsonContext : JsonSerializerContext;

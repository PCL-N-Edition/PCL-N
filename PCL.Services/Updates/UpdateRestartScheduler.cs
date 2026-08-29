using System.Diagnostics;

namespace PCL.Services.Updates;

/// <summary>
/// One downloaded-and-verified launcher update, ready to hand off to the replacement
/// process. The staged executable is complete and GPG-verified; the optional install plan
/// file makes the replacement run in tree-update mode.
/// </summary>
public sealed record PreparedLauncherUpdate(
    UpdatePackage Package,
    string CurrentExecutablePath,
    string StagedExecutablePath,
    string WorkDirectory,
    bool UsedPatch,
    bool UsedBlockMap = false)
{
    /// <summary>
    /// The install plan file for tree updates (scatter/block payloads); null for plain
    /// single-binary updates.
    /// </summary>
    public string? InstallPlanPath { get; init; }
}

/// <summary>
/// Process launch port. The real implementation starts the process and releases the handle
/// immediately — the replacement process outlives the updater by design.
/// </summary>
public interface IProcessLauncher
{
    void Launch(ProcessStartInfo startInfo);
}

/// <summary>Real process launch over <see cref="Process"/>.</summary>
public sealed class ProcessLauncher : IProcessLauncher
{
    public void Launch(ProcessStartInfo startInfo)
    {
        using Process? process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动更新替换进程。");
    }
}

/// <summary>
/// Helper-process hand-off and restart scheduling: validates the staged update, then starts
/// the staged executable as a replacement process that waits for this process to exit,
/// applies the install plan (or swaps the single binary), and optionally restarts the
/// launcher. The argument order is the helper's contract and never changes.
/// </summary>
public sealed class UpdateRestartScheduler
{
    private readonly IProcessLauncher _launcher;

    public UpdateRestartScheduler(IProcessLauncher launcher)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
    }

    /// <summary>Schedules the install; the replacement restarts the launcher afterwards.</summary>
    public void ScheduleInstallAndRestart(PreparedLauncherUpdate update, int processId) =>
        ScheduleInstall(update, processId, restartAfterInstall: true);

    /// <summary>Schedules the install without restarting afterwards.</summary>
    public void ScheduleInstallOnExit(PreparedLauncherUpdate update, int processId) =>
        ScheduleInstall(update, processId, restartAfterInstall: false);

    private void ScheduleInstall(PreparedLauncherUpdate update, int processId, bool restartAfterInstall)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!File.Exists(update.StagedExecutablePath) ||
            (!string.IsNullOrWhiteSpace(update.InstallPlanPath) && !File.Exists(update.InstallPlanPath)))
        {
            throw new FileNotFoundException("已下载的启动器更新不存在。", update.StagedExecutablePath);
        }

        Directory.CreateDirectory(update.WorkDirectory);
        ProcessStartInfo startInfo = CreateReplacementProcess(update, processId, restartAfterInstall);
        _launcher.Launch(startInfo);
    }

    /// <summary>
    /// Builds the replacement process start info. Tree updates pass the install plan file;
    /// plain updates pass the staged executable; both end with the wait-for-pid, the work
    /// directory, and the restart flag.
    /// </summary>
    public static ProcessStartInfo CreateReplacementProcess(PreparedLauncherUpdate update, int processId, bool restartAfterInstall)
    {
        ArgumentNullException.ThrowIfNull(update);
        ProcessStartInfo startInfo = new(update.StagedExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(update.CurrentExecutablePath) ?? Environment.CurrentDirectory,
        };
        if (!string.IsNullOrWhiteSpace(update.InstallPlanPath))
        {
            startInfo.ArgumentList.Add("--pcln-apply-tree-update");
            startInfo.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(update.CurrentExecutablePath);
            startInfo.ArgumentList.Add(update.InstallPlanPath);
            startInfo.ArgumentList.Add(update.WorkDirectory);
            startInfo.ArgumentList.Add(restartAfterInstall ? "1" : "0");
            return startInfo;
        }

        startInfo.ArgumentList.Add("--pcln-apply-update");
        startInfo.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(update.CurrentExecutablePath);
        startInfo.ArgumentList.Add(update.StagedExecutablePath);
        startInfo.ArgumentList.Add(update.WorkDirectory);
        startInfo.ArgumentList.Add(restartAfterInstall ? "1" : "0");
        return startInfo;
    }
}

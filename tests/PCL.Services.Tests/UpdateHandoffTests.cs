using System.Diagnostics;
using PCL.Services.Updates;

namespace PCL.Services.Tests;

// XSR-518: helper-process hand-off and restart scheduling — the replacement process
// argument contract, staged artifact validation, and work-directory preparation, exercised
// through a recording launch port.
internal static partial class Program
{
    private sealed class RecordingLauncher : IProcessLauncher
    {
        public List<ProcessStartInfo> Launched { get; } = [];

        public void Launch(ProcessStartInfo startInfo) => Launched.Add(startInfo);
    }

    private static PreparedLauncherUpdate SampleUpdate(string directory, bool withPlan)
    {
        string staged = Path.Combine(directory, "staged", "PCL-N-Edition.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.WriteAllBytes(staged, [0x01]);
        string? planPath = null;
        if (withPlan)
        {
            planPath = Path.Combine(directory, "staged", "install-plan.json");
            File.WriteAllText(planPath, "{}");
        }

        UpdatePackage package = new(
            "2.0.0.alpha.2",
            "v2.0.0.alpha.2",
            "https://dist.example/pkg",
            "pkg.zip",
            "PCL-N-Edition.exe",
            null,
            null,
            [],
            "win-x64",
            "SelfContained",
            "Release");
        return new PreparedLauncherUpdate(
            package,
            Path.Combine(directory, "current", "PCL-N-Edition.exe"),
            staged,
            Path.Combine(directory, "work"),
            UsedPatch: false)
        {
            InstallPlanPath = planPath,
        };
    }

    internal static void ReplacementProcessArgumentsFollowTheHelperContract()
    {
        string directory = CreateTempDirectory();
        try
        {
            PreparedLauncherUpdate treeUpdate = SampleUpdate(directory, withPlan: true);
            ProcessStartInfo treeStart = UpdateRestartScheduler.CreateReplacementProcess(treeUpdate, 4242, restartAfterInstall: true);

            AssertEqual(treeUpdate.StagedExecutablePath, treeStart.FileName);
            AssertFalse(treeStart.UseShellExecute);
            AssertTrue(treeStart.CreateNoWindow);
            string[] treeArguments = [.. treeStart.ArgumentList];
            AssertEqual(6, treeArguments.Length);
            AssertEqual("--pcln-apply-tree-update", treeArguments[0]);
            AssertEqual("4242", treeArguments[1]);
            AssertEqual(treeUpdate.CurrentExecutablePath, treeArguments[2]);
            AssertEqual(treeUpdate.InstallPlanPath, treeArguments[3]);
            AssertEqual(treeUpdate.WorkDirectory, treeArguments[4]);
            AssertEqual("1", treeArguments[5]);
            AssertTrue(treeStart.WorkingDirectory.StartsWith(directory, StringComparison.Ordinal));

            PreparedLauncherUpdate plainUpdate = SampleUpdate(directory, withPlan: false);
            ProcessStartInfo plainStart = UpdateRestartScheduler.CreateReplacementProcess(plainUpdate, 7, restartAfterInstall: false);
            string[] plainArguments = [.. plainStart.ArgumentList];
            AssertEqual(6, plainArguments.Length);
            AssertEqual("--pcln-apply-update", plainArguments[0]);
            AssertEqual("7", plainArguments[1]);
            AssertEqual(plainUpdate.CurrentExecutablePath, plainArguments[2]);
            AssertEqual(plainUpdate.StagedExecutablePath, plainArguments[3]);
            AssertEqual(plainUpdate.WorkDirectory, plainArguments[4]);
            AssertEqual("0", plainArguments[5]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static void SchedulerValidatesArtifactsBeforeLaunch()
    {
        string directory = CreateTempDirectory();
        try
        {
            RecordingLauncher launcher = new();
            UpdateRestartScheduler scheduler = new(launcher);
            PreparedLauncherUpdate update = SampleUpdate(directory, withPlan: true);

            scheduler.ScheduleInstallAndRestart(update, processId: 1234);

            AssertEqual(1, launcher.Launched.Count);
            AssertEqual("--pcln-apply-tree-update", launcher.Launched[0].ArgumentList[0]);
            AssertTrue(Directory.Exists(update.WorkDirectory));

            // A missing staged executable is refused before any launch.
            PreparedLauncherUpdate broken = SampleUpdate(directory, withPlan: false);
            broken = broken with { StagedExecutablePath = Path.Combine(directory, "missing.exe") };
            bool missingStaged = false;
            try
            {
                scheduler.ScheduleInstallOnExit(broken, processId: 1234);
            }
            catch (FileNotFoundException failure)
            {
                missingStaged = failure.FileName == broken.StagedExecutablePath;
            }

            AssertTrue(missingStaged);

            // A declared but missing install plan file is refused too.
            PreparedLauncherUpdate missingPlan = SampleUpdate(directory, withPlan: true) with { };
            missingPlan = missingPlan with { InstallPlanPath = Path.Combine(directory, "gone.json") };
            bool missingPlanRejected = false;
            try
            {
                scheduler.ScheduleInstallOnExit(missingPlan, processId: 1234);
            }
            catch (FileNotFoundException)
            {
                missingPlanRejected = true;
            }

            AssertTrue(missingPlanRejected);
            AssertEqual(1, launcher.Launched.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static void StagedPathHelpersSanitizeVersions()
    {
        string current = Path.Combine(CreateTempDirectory(), "PCL-N-Edition.exe");
        string staged = UpdateStaging.BuildStagedPath(current, "2.0.0.alpha.1");
        AssertTrue(staged.StartsWith(Path.GetDirectoryName(current)!, StringComparison.Ordinal));
        AssertTrue(staged.EndsWith(".PCL-N-Edition.exe.2.0.0.alpha.1.update", StringComparison.Ordinal));

        // Invalid-file-name characters are platform specific; the separator is universal.
        string sanitized = UpdateStaging.SanitizeFileName("v1:bad/name?x");
        AssertFalse(sanitized.Contains('/', StringComparison.Ordinal));
        AssertTrue(sanitized.Contains("v1", StringComparison.Ordinal));
        AssertTrue(sanitized.Contains("name", StringComparison.Ordinal));
    }
}

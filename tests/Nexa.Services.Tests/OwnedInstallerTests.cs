using System.Diagnostics;
using Nexa.Services.Processes;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static ProcessStartInfo OwnedTestStart(string argument)
    {
        var start = OwnedInstallerProcess.WorkerStartInfo();
        start.ArgumentList[^1] = argument;
        start.WorkingDirectory = Environment.CurrentDirectory;
        return start;
    }

    private static async Task<int> RunOwnedInstallerChild()
    {
        Console.WriteLine(Environment.ProcessId); Console.Out.Flush();
        await Task.Delay(Timeout.Infinite);
        return 0;
    }

    private static async Task<int> RunOwnedInstallerOwner()
    {
        using var worker = Process.Start(OwnedInstallerProcess.WorkerStartInfo())!;
        await OwnedInstallerProcess.WriteRequestAsync(worker.StandardInput.BaseStream, OwnedTestStart("--owned-installer-child"));
        string child = (await worker.StandardOutput.ReadLineAsync())!;
        Console.WriteLine(worker.Id + ":" + child); Console.Out.Flush();
        await Task.Delay(Timeout.Infinite);
        return 0;
    }

    private static async ValueTask OwnedInstallerDiesAfterOwnerIsKilled()
    {
        using var owner = Process.Start(OwnedTestStart("--owned-installer-owner"))!;
        Process? worker = null, child = null;
        try
        {
            string[] ids = (await owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)))!.Split(':');
            worker = Process.GetProcessById(int.Parse(ids[0], System.Globalization.CultureInfo.InvariantCulture));
            child = Process.GetProcessById(int.Parse(ids[1], System.Globalization.CultureInfo.InvariantCulture));
            AssertFalse(child.HasExited);
            owner.Kill(); // Deliberately do NOT kill the process tree: only pipe EOF can stop the child.
            await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            AssertTrue(child.HasExited);
        }
        finally
        {
            if (!owner.HasExited) owner.Kill(entireProcessTree: true);
            if (worker is { HasExited: false }) worker.Kill(entireProcessTree: true);
            if (child is { HasExited: false }) child.Kill(entireProcessTree: true);
            worker?.Dispose(); child?.Dispose();
        }
    }
}

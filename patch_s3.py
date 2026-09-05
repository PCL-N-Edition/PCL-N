# -*- coding: utf-8 -*-
p = "tests/PCL.Services.Tests/LaunchProgressTests.cs"
s = open(p, encoding="utf-8").read()

# Existing stubs move to the new probe contract.
old = """    /// <summary>A probe that always reports the game window as present.</summary>
    private sealed class ImmediateWindowProbe : IMinecraftWindowProbe
    {
        public ValueTask<bool> HasVisibleWindowAsync(int processId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);
    }"""
new = """    /// <summary>A probe that always reports the game window as present.</summary>
    private sealed class ImmediateWindowProbe : IMinecraftWindowProbe
    {
        public ValueTask<MinecraftWindowProbeResult> ProbeAsync(int processId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(MinecraftWindowProbeResult.Visible);
    }

    /// <summary>A probe whose platform has no window detection: the wait must be skipped.</summary>
    private sealed class UnsupportedWindowProbe : IMinecraftWindowProbe
    {
        public ValueTask<MinecraftWindowProbeResult> ProbeAsync(int processId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(MinecraftWindowProbeResult.Unsupported);
    }

    /// <summary>A probe that never sees a window while detection is supported.</summary>
    private sealed class BlindWindowProbe : IMinecraftWindowProbe
    {
        public ValueTask<MinecraftWindowProbeResult> ProbeAsync(int processId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(MinecraftWindowProbeResult.NotVisible);
    }

    /// <summary>A process port returning a long-lived child the test can cancel.</summary>
    private sealed class LongLivedProcessPort : IMinecraftProcessPort
    {
        public System.Diagnostics.Process? LastProcess { get; private set; }

        public ValueTask<System.Diagnostics.Process> StartAsync(
            System.Diagnostics.ProcessStartInfo startInfo,
            CancellationToken cancellationToken = default)
        {
            System.Diagnostics.ProcessStartInfo wait = OperatingSystem.IsWindows()
                ? new System.Diagnostics.ProcessStartInfo("cmd", "/c timeout /t 30 /nobreak")
                : new System.Diagnostics.ProcessStartInfo("/bin/sh", "-c sleep 30");
            wait.UseShellExecute = false;
            wait.CreateNoWindow = true;
            LastProcess = System.Diagnostics.Process.Start(wait)!;
            return ValueTask.FromResult(LastProcess);
        }
    }"""
assert s.count(old) == 1, "stubs"
s = s.replace(old, new)

# The corpus helper: long-lived child so the pipeline survives to wait_window.
old2 = """        MinecraftProcessService processes = new(processPort ?? new ExitingProcessPort(), host.StateStore);"""
assert s.count(old2) == 1
s = s.replace(old2, """        MinecraftProcessService processes = new(processPort ?? new LongLivedProcessPort(), host.StateStore);""")

# The immediate-exit scenario is now a FAILED launch (process died before its window).
old3 = """            XsrResult result = await coordinator.StartAsync("1.20.1", accountIndex: 0);
            Console.WriteLine($"[immediate] done success={result.IsSuccess} {DateTime.Now:HH:mm:ss.fff}");
            AssertTrue(result.IsSuccess,
                "immediate-exit launch failed: " + result.Error?.Code.Value + " " + result.Error?.Message);
            XsrStateStore store = host.StateStore;
            // The JVM in this corpus exits immediately; the subscribe-then-recheck reset must
            // land without any further Changed event, so the truth cannot stay "launched".
            AssertTrue(SpinWait.SpinUntil(
                () => store.ReadAppliedValue(store.Resolve(MinecraftLaunchProgressState.SnapshotKey))
                    is MinecraftLaunchProgressSnapshot snapshot && !snapshot.Active && snapshot.SessionId is not null,
                TimeSpan.FromSeconds(5)));
            AssertFalse(ReadProgressFlag(store, MinecraftLaunchProgressState.LaunchedKey));
            Directory.Delete(root, recursive: true);
    }"""
if s.count(old3) != 1:
    # Indentation variant (test-local inline version).
    old3 = """        XsrResult result = await coordinator.StartAsync("1.20.1", accountIndex: 0);
        Console.WriteLine($"[immediate] done success={result.IsSuccess} {DateTime.Now:HH:mm:ss.fff}");
        if (!result.IsSuccess)
        {
            Console.WriteLine("DIAG immediate failed: " + result.Error?.Code.Value + " " + result.Error?.Message);
        }

        AssertTrue(result.IsSuccess);
        XsrStateStore store = host.StateStore;
        AssertTrue(SpinWait.SpinUntil(
            () => store.ReadAppliedValue(store.Resolve(MinecraftLaunchProgressState.SnapshotKey))
                is MinecraftLaunchProgressSnapshot snapshot && !snapshot.Active && snapshot.SessionId is not null,
            TimeSpan.FromSeconds(5)));
        AssertFalse(ReadProgressFlag(store, MinecraftLaunchProgressState.LaunchedKey));
        Directory.Delete(root, recursive: true);
    }"""
assert s.count(old3) == 1, "immediate exit body"
new3 = """        XsrResult result = await coordinator.StartAsync("1.20.1", accountIndex: 0);
        // A JVM that dies before its window appears is a FAILED launch, not a launched one.
        AssertFalse(result.IsSuccess);
        AssertEqual(MinecraftErrors.ExitedBeforeWindowCode, result.Error!.Code);
        XsrStateStore store = host.StateStore;
        // The subscribe-then-recheck reset still lands: the truth cannot stay "launched".
        AssertTrue(SpinWait.SpinUntil(
            () => store.ReadAppliedValue(store.Resolve(MinecraftLaunchProgressState.SnapshotKey))
                is MinecraftLaunchProgressSnapshot snapshot && !snapshot.Active && snapshot.SessionId is not null,
            TimeSpan.FromSeconds(5)));
        AssertFalse(ReadProgressFlag(store, MinecraftLaunchProgressState.LaunchedKey));
        Directory.Delete(root, recursive: true);
    }"""
s = s.replace(old3, new3)
open(p, "w", encoding="utf-8", newline="\n").write(s)
print("tests moved to new contracts")

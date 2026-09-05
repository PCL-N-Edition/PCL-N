# -*- coding: utf-8 -*-
p = "PCL.Services/Minecraft/Launch/MinecraftLaunchCoordinator.cs"
s = open(p, encoding="utf-8").read()

# The wait reports its outcome; the caller decides success or failure.
old = """            await WaitForGameWindowAsync(session, launchToken).ConfigureAwait(false);
            _progress?.Report(new MinecraftLaunchStageReport(
                MinecraftLaunchStages.End,
                MinecraftLaunchStages.ProgressAt(MinecraftLaunchStages.Total),
                IsLaunched: true,
                Method: method,
                SessionId: sessionId));
            OnSessionChanged(session.Snapshot);"""
new = """            GameWindowWaitResult wait = await WaitForGameWindowAsync(session, launchToken).ConfigureAwait(false);
            if (wait == GameWindowWaitResult.ProcessExited)
            {
                // The JVM died before presenting a window: this is a failed launch, not a
                // launched one — the failure path closes the launching page and feeds crash
                // analysis downstream.
                operation?.Reject(MinecraftErrors.ExitedBeforeWindowCode.Value);
                return XsrResult.Failure(MinecraftErrors.ExitedBeforeWindow());
            }

            _progress?.Report(new MinecraftLaunchStageReport(
                MinecraftLaunchStages.End,
                MinecraftLaunchStages.ProgressAt(MinecraftLaunchStages.Total),
                IsLaunched: true,
                Method: method,
                SessionId: sessionId));
            OnSessionChanged(session.Snapshot);"""
assert s.count(old) == 1, "wait call"
s = s.replace(old, new)

# The wait helper returns the outcome enum and skips on unsupported platforms.
old2 = """    private async ValueTask WaitForGameWindowAsync(
        Process.MinecraftProcessSession session,
        CancellationToken cancellationToken)
    {
        int processId = session.Snapshot.ProcessId;
        long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A JVM that already died will never present a window; the terminal callback below
            // resets the narration, so waiting would only burn the limit.
            if (session.Snapshot.State is MinecraftProcessState.Exited
                or MinecraftProcessState.Failed
                or MinecraftProcessState.Cancelled)
            {
                _log?.Info("Launch", $"Game process already ended before its window appeared pid={processId}.");
                return;
            }

            if (await _windowProbe.HasVisibleWindowAsync(processId, cancellationToken).ConfigureAwait(false))
            {
                _log?.Info("Launch", $"Game window confirmed pid={processId}.");
                return;
            }

            if (System.Diagnostics.Stopwatch.GetElapsedTime(startedAt) >= GameWindowWaitLimit)
            {
                _log?.Warn("Launch", $"No game window appeared within the limit pid={processId}; continuing.");
                return;
            }

            await Task.Delay(GameWindowPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }"""
new2 = """    private async ValueTask<GameWindowWaitResult> WaitForGameWindowAsync(
        Process.MinecraftProcessSession session,
        CancellationToken cancellationToken)
    {
        int processId = session.Snapshot.ProcessId;
        long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A JVM that already died will never present a window; the terminal callback below
            // resets the narration, so waiting would only burn the limit.
            if (session.Snapshot.State is MinecraftProcessState.Exited
                or MinecraftProcessState.Failed
                or MinecraftProcessState.Cancelled)
            {
                _log?.Info("Launch", $"Game process already ended before its window appeared pid={processId}.");
                return GameWindowWaitResult.ProcessExited;
            }

            MinecraftWindowProbeResult probe = await _windowProbe
                .ProbeAsync(processId, cancellationToken)
                .ConfigureAwait(false);
            if (probe == MinecraftWindowProbeResult.Visible)
            {
                _log?.Info("Launch", $"Game window confirmed pid={processId}.");
                return GameWindowWaitResult.Visible;
            }

            if (probe == MinecraftWindowProbeResult.Unsupported)
            {
                // No window detection on this platform: a wait could only burn its limit, so
                // the legacy behavior degrades to "process started counts as launched".
                _log?.Info("Launch", $"Window detection unsupported; skipping the wait pid={processId}.");
                return GameWindowWaitResult.Unsupported;
            }

            if (System.Diagnostics.Stopwatch.GetElapsedTime(startedAt) >= GameWindowWaitLimit)
            {
                _log?.Warn("Launch", $"No game window appeared within the limit pid={processId}; continuing.");
                return GameWindowWaitResult.TimedOut;
            }

            await Task.Delay(GameWindowPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }"""
assert s.count(old2) == 1, "wait helper"
s = s.replace(old2, new2)

# The outcome enum + new error code reference.
old3 = """    private static readonly TimeSpan GameWindowPollInterval = TimeSpan.FromSeconds(2);"""
new3 = """    internal enum GameWindowWaitResult
    {
        Visible,
        Unsupported,
        TimedOut,
        ProcessExited,
    }

    private static readonly TimeSpan GameWindowPollInterval = TimeSpan.FromSeconds(2);"""
assert s.count(old3) == 1
s = s.replace(old3, new3)
open(p, "w", encoding="utf-8", newline="\n").write(s)
print("wait enum + failure wired")

# Error contract.
p2 = "PCL.Services/Minecraft/MinecraftRoutes.cs"
s2 = open(p2, encoding="utf-8").read()
old4 = '    public static readonly XsrSemanticId LaunchAlreadyActiveCode = XsrSemanticId.Parse("minecraft.launch_already_active");'
new4 = old4 + '\n    public static readonly XsrSemanticId ExitedBeforeWindowCode = XsrSemanticId.Parse("minecraft.exited_before_window");'
assert s2.count(old4) == 1
s2 = s2.replace(old4, new4)
old5 = '    public static XsrError LaunchAlreadyActive() => new(XsrErrorKind.Rejected, LaunchAlreadyActiveCode, "A Minecraft launch pipeline is already running; cancel it before starting another.");'
new5 = old5 + '\n    public static XsrError ExitedBeforeWindow() => new(XsrErrorKind.Unavailable, ExitedBeforeWindowCode, "The Minecraft process exited before its window appeared.");'
assert s2.count(old5) == 1
s2 = s2.replace(old5, new5)
open(p2, "w", encoding="utf-8", newline="\n").write(s2)
print("error contract")

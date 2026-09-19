using System.Diagnostics;
using System.Runtime.InteropServices;
using Nexa.Services.Capabilities;
using Nexa.Services.Minecraft.Launch;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Minecraft.Process;

public sealed record JvmHostEnvironment(
    string JavaExecutable,
    string WorkingDirectory,
    IReadOnlyList<string> JvmArguments,
    IReadOnlyList<string> GameArguments,
    IReadOnlyList<string> Classpath,
    string NativePath,
    string? Wrapper);

public sealed record JvmHostObservation(
    Guid SessionId,
    string InstanceId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    long LaunchDurationMilliseconds,
    long PeakWorkingSetBytes,
    long PeakPrivateBytes,
    long PeakThreadCount,
    long TotalProcessorMilliseconds,
    long HeapPeakBytes,
    long NativePeakBytes,
    long CommitPeakBytes,
    long GpuLocalPeakBytes,
    long GpuSharedPeakBytes,
    long IoReadBytes,
    long IoWriteBytes,
    string? CrashReportPath,
    string? HsErrPath,
    int? ExitCode,
    IReadOnlyList<string> StdoutTail,
    IReadOnlyList<string> StderrTail)
{
    public long CpuPeakPercent { get; init; }
    public long RuntimePhysicalP95Bytes { get; init; }
    public long RuntimeCommitP95Bytes { get; init; }
    public long RuntimeCpuP95Percent { get; init; }
}

public static class JvmHostStateContract
{
    public static readonly XsrSemanticId ObservationsKey = XsrSemanticId.Parse("observation.jvm.sessions");
    public static void DeclareState(XsrStateStoreBuilder builder) =>
        builder.Collection<JvmHostObservation, Guid>(ObservationsKey, "Nexa.Services.Minecraft.Process.JvmHost",
            static observation => observation.SessionId);
}

public interface IJvmHost
{
    JvmHostEnvironment Describe(MinecraftLaunchPlan plan);
    ValueTask<MinecraftProcessSession> StartAsync(MinecraftLaunchPlan plan, string instanceId,
        CancellationToken cancellationToken = default);
    JvmHostControlResult Suspend(MinecraftProcessSession session);
    JvmHostControlResult ResumeProcess(MinecraftProcessSession session);
    JvmHostControlResult SetPriority(MinecraftProcessSession session, ProcessPriorityClass priority);
    JvmHostControlResult SetAffinity(MinecraftProcessSession session, nint affinityMask);
}

public sealed record JvmHostControlResult(bool Succeeded, string Code, string Message);

/// <summary>
/// Formal JVM process boundary. It retains the proven Minecraft process lifecycle and adds a
/// typed environment query plus bounded observations without exposing process mechanics to the
/// launch coordinator.
/// </summary>
public sealed class JvmHostService : IJvmHost
{
    private readonly MinecraftProcessService _processes;
    private readonly XsrStateStore? _store;
    private readonly XsrStateId _observationsId;
    private readonly ResourceObservationHistory? _history;

    public JvmHostService(MinecraftProcessService processes, ResourceObservationHistory? history = null)
    {
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _store = processes.StateStore;
        _history = history;
        if (_store is not null) _observationsId = _store.Resolve(JvmHostStateContract.ObservationsKey);
    }

    public JvmHostEnvironment Describe(MinecraftLaunchPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        int mainClass = FindMainClass(plan.Arguments);
        string[] jvm = plan.Arguments.Take(mainClass).ToArray();
        string[] game = mainClass < plan.Arguments.Count ? plan.Arguments.Skip(mainClass + 1).ToArray() : [];
        return new(plan.JavaExecutablePath, plan.WorkingDirectory, jvm, game, plan.ClasspathEntries,
            plan.NativesDirectory, null);
    }

    public static IReadOnlyList<Capabilities.ICapability> DescribeCapabilities(MinecraftLaunchPlan plan,
        DateTimeOffset? timestamp = null) => JvmHostCapabilityCatalog.Describe(plan, timestamp ?? DateTimeOffset.UtcNow);

    public async ValueTask<MinecraftProcessSession> StartAsync(MinecraftLaunchPlan plan, string instanceId,
        CancellationToken cancellationToken = default)
    {
        long started = Environment.TickCount64;
        _ = Describe(plan); // validate and freeze the environment view before spawning.
        MinecraftProcessSession session = await _processes.StartAsync(plan, instanceId, cancellationToken).ConfigureAwait(false);
        long launchDuration = Math.Max(0, Environment.TickCount64 - started);
        _ = ObserveAsync(session, plan, launchDuration);
        return session;
    }

    public JvmHostControlResult Suspend(MinecraftProcessSession session) => ChangeSuspension(session, suspend: true);
    public JvmHostControlResult ResumeProcess(MinecraftProcessSession session) => ChangeSuspension(session, suspend: false);

    public JvmHostControlResult SetPriority(MinecraftProcessSession session, ProcessPriorityClass priority)
    {
        ArgumentNullException.ThrowIfNull(session);
        try { session.Process.PriorityClass = priority; return new(true, "ok", "进程优先级已更新。"); }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        { return new(false, "priority_unavailable", exception.Message); }
    }

    public JvmHostControlResult SetAffinity(MinecraftProcessSession session, nint affinityMask)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (affinityMask == 0) return new(false, "invalid_affinity", "处理器亲和掩码不能为零。");
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            return new(false, "platform_unsupported", "当前平台不支持处理器亲和性。");
        try { session.Process.ProcessorAffinity = affinityMask; return new(true, "ok", "处理器亲和性已更新。"); }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        { return new(false, "affinity_unavailable", exception.Message); }
    }

    private static JvmHostControlResult ChangeSuspension(MinecraftProcessSession session, bool suspend)
    {
        ArgumentNullException.ThrowIfNull(session);
        try
        {
            int result;
            if (OperatingSystem.IsWindows())
                result = suspend ? NtSuspendProcess(session.Process.Handle) : NtResumeProcess(session.Process.Handle);
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                result = kill(session.Process.Id, suspend ? 19 : 18);
            else return new(false, "platform_unsupported", "当前平台不支持进程暂停。");
            return result == 0 ? new(true, "ok", suspend ? "进程已暂停。" : "进程已恢复。")
                : new(false, "control_failed", "操作系统拒绝了进程控制请求。");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        { return new(false, "control_failed", exception.Message); }
    }

    private async Task ObserveAsync(MinecraftProcessSession session, MinecraftLaunchPlan plan, long launchDuration)
    {
        long peakWorking = 0, peakPrivate = 0, peakThreads = 0, cpuMs = 0, ioRead = 0, ioWrite = 0;
        Queue<long> workingSamples = new();
        Queue<long> cpuSamples = new();
        TimeSpan previousCpu = TimeSpan.Zero;
        long previousSampleTick = Environment.TickCount64;
        bool hasCpuBaseline = false;
        try
        {
            while (session.Snapshot.State is MinecraftProcessState.Created or MinecraftProcessState.Running)
            {
                try
                {
                    session.Process.Refresh();
                    peakWorking = Math.Max(peakWorking, session.Process.WorkingSet64);
                    peakPrivate = Math.Max(peakPrivate, session.Process.PrivateMemorySize64);
                    peakThreads = Math.Max(peakThreads, session.Process.Threads.Count);
                    TimeSpan currentCpu = session.Process.TotalProcessorTime;
                    cpuMs = Math.Max(cpuMs, (long)currentCpu.TotalMilliseconds);
                    long currentSampleTick = Environment.TickCount64;
                    AddSample(workingSamples, session.Process.WorkingSet64);
                    if (hasCpuBaseline)
                    {
                        long elapsed = Math.Max(1, currentSampleTick - previousSampleTick);
                        long cpuPercent = (long)Math.Clamp(
                            (currentCpu - previousCpu).TotalMilliseconds / elapsed / Environment.ProcessorCount * 100,
                            0, 100);
                        AddSample(cpuSamples, cpuPercent);
                    }
                    previousCpu = currentCpu;
                    previousSampleTick = currentSampleTick;
                    hasCpuBaseline = true;
                    if (OperatingSystem.IsWindows() && GetProcessIoCounters(session.Process.Handle, out IoCounters counters))
                    {
                        ioRead = Math.Max(ioRead, checked((long)Math.Min(counters.ReadTransferCount, long.MaxValue)));
                        ioWrite = Math.Max(ioWrite, checked((long)Math.Min(counters.WriteTransferCount, long.MaxValue)));
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
                {
                    break;
                }
                await Task.Delay(500).ConfigureAwait(false);
            }
            await session.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            // Observation is best effort and cannot change launch truth.
        }

        MinecraftProcessSnapshot snapshot = session.Snapshot;
        (string[] stdout, string[] stderr) = await session.ReadSeparatedEvidenceAsync().ConfigureAwait(false);
        DateTimeOffset evidenceFloor = snapshot.StartedAt - TimeSpan.FromSeconds(2);
        string? hsErr = FindNewestFile(plan.WorkingDirectory,
            $"hs_err_pid{session.Process.Id}.log", evidenceFloor);
        string? crashReport = FindNewestFile(Path.Combine(plan.WorkingDirectory, "crash-reports"),
            "*.txt", evidenceFloor);
        JvmHostObservation observation = new(snapshot.SessionId, snapshot.InstanceId, snapshot.StartedAt,
            snapshot.EndedAt, launchDuration, peakWorking, peakPrivate, peakThreads, cpuMs,
            0, 0, 0, 0, 0, ioRead, ioWrite, crashReport, hsErr, snapshot.ExitCode,
            stdout.TakeLast(40).ToArray(), stderr.TakeLast(40).ToArray())
        {
            CpuPeakPercent = cpuSamples.Count == 0 ? 0 : cpuSamples.Max(),
            RuntimePhysicalP95Bytes = Percentile(workingSamples.Count == 0 ? [peakWorking] : workingSamples),
            RuntimeCommitP95Bytes = 0,
            RuntimeCpuP95Percent = Percentile(cpuSamples),
        };
        Publish(observation);
        _history?.Record(new ResourceObservationSample(snapshot.InstanceId, plan.ModLoader.Kind.ToString(),
            plan.JavaMajorVersion, 0, 0, 0, 0,
            BytesToMiB(observation.RuntimePhysicalP95Bytes), BytesToMiB(observation.RuntimeCommitP95Bytes),
            0, launchDuration, snapshot.EndedAt ?? DateTimeOffset.UtcNow));
    }

    private void Publish(JvmHostObservation observation)
    {
        if (_store is null) return;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            XsrCollectionSnapshot<JvmHostObservation> current = _store.ReadCollection<JvmHostObservation>(_observationsId);
            Guid[] removals = current.Items.OrderBy(static item => item.StartedAt)
                .Take(Math.Max(0, current.Items.Count - 31)).Select(static item => item.SessionId).ToArray();
            if (_store.PublishDelta(_observationsId,
                new XsrCollectionDelta<JvmHostObservation, Guid>(current.Revision, [observation], removals)).IsApplied) return;
        }
    }

    private static int FindMainClass(IReadOnlyList<string> arguments)
    {
        for (int index = 0; index + 2 < arguments.Count; index++)
            if (arguments[index] is "-cp" or "-classpath") return index + 2;
        return arguments.Count;
    }

    private static long BytesToMiB(long bytes) => Math.Max(0, bytes / (1024 * 1024));
    private static void AddSample(Queue<long> samples, long value)
    {
        const int capacity = 4096;
        if (samples.Count == capacity) samples.Dequeue();
        samples.Enqueue(Math.Max(0, value));
    }

    private static long Percentile(IEnumerable<long> source)
    {
        long[] values = source.Where(static value => value > 0).Order().ToArray();
        return values.Length == 0 ? 0 : values[(int)Math.Ceiling(values.Length * 0.95) - 1];
    }
    private static string? FindNewestFile(string directory, string pattern, DateTimeOffset notBefore)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, pattern)
                .Where(path => File.GetLastWriteTimeUtc(path) >= notBefore.UtcDateTime)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(nint processHandle, out IoCounters counters);

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(nint processHandle);
    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(nint processHandle);
    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int processId, int signal);
}

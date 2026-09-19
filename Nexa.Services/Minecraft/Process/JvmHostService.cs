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
    int? ExitCode,
    IReadOnlyList<string> StderrTail);

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
}

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

    public JvmHostService(MinecraftProcessService processes)
    {
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _store = processes.StateStore;
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
        _ = ObserveAsync(session, launchDuration);
        return session;
    }

    private async Task ObserveAsync(MinecraftProcessSession session, long launchDuration)
    {
        long peakWorking = 0, peakPrivate = 0, peakThreads = 0, cpuMs = 0;
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
                    cpuMs = Math.Max(cpuMs, (long)session.Process.TotalProcessorTime.TotalMilliseconds);
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
        string[] evidence = await session.ReadEvidenceAsync().ConfigureAwait(false);
        JvmHostObservation observation = new(snapshot.SessionId, snapshot.InstanceId, snapshot.StartedAt,
            snapshot.EndedAt, launchDuration, peakWorking, peakPrivate, peakThreads, cpuMs, snapshot.ExitCode,
            evidence.TakeLast(40).ToArray());
        Publish(observation);
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
}

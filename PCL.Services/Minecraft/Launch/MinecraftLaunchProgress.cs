using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Services.Minecraft.Launch;

/// <summary>
/// The legacy launch stage weights, migrated unchanged: every pipeline step owns a share of
/// the total launch effort, and the reported progress is the completed weight over that
/// total. Stage tokens are stable identifiers; display strings stay on the desktop side.
/// </summary>
public static class MinecraftLaunchStages
{
    public const string GetJava = "get_java";
    public const string Login = "login";
    public const string CompleteFiles = "complete_files";
    public const string GetArguments = "get_arguments";
    public const string ExtractNatives = "extract_natives";
    public const string PreLaunch = "pre_launch";
    public const string StartProcess = "start_process";
    public const string End = "end";

    public const double GetJavaWeight = 4d;
    public const double LoginWeight = 15d;
    public const double CompleteFilesWeight = 15d;
    public const double GetArgumentsWeight = 2d;
    public const double ExtractNativesWeight = 2d;
    public const double PreLaunchWeight = 1d;
    public const double StartProcessWeight = 2d;
    public const double EndWeight = 1d;

    // The legacy table reserves one weight each for custom_command and wait_window, whose
    // features have not migrated yet. Their weight stays reserved so every migrated stage
    // reports the same overall pacing as the legacy launch.
    public const double Total = 44d;

    public static double ProgressAt(double completedWeight) =>
        Math.Clamp(completedWeight / Total, 0d, 1d);
}

/// <summary>
/// One launch pipeline report: the stage token, overall progress, whether the game is
/// running, the login method label, and the optional download speed line. This is the data
/// contract behind the launch progress overlay.
/// </summary>
public readonly record struct MinecraftLaunchStageReport(
    string Stage,
    double Progress,
    bool IsLaunched = false,
    string? Method = null,
    string? DownloadSpeed = null,
    Guid? SessionId = null);

/// <summary>
/// One atomic launch-progress truth. Scalar state keys are derived compatibility projections of
/// this value, so observers can never combine fields from different reports.
/// </summary>
public sealed record MinecraftLaunchProgressSnapshot(
    bool Active,
    string Stage,
    double Progress,
    string Method,
    string DownloadSpeed,
    bool IsLaunched,
    Guid? SessionId)
{
    public static MinecraftLaunchProgressSnapshot Empty { get; } =
        new(false, string.Empty, 0d, string.Empty, string.Empty, false, null);
}

/// <summary>
/// The launch progress state cells. The coordinator publishes one report as one coherent set
/// of cells; the renderer reads local state and the desktop controller owns display strings.
/// </summary>
public static class MinecraftLaunchProgressState
{
    public const string OwnerName = "PCL.Services.Minecraft.Launch";

    public static readonly XsrSemanticId SnapshotKey = XsrSemanticId.Parse("minecraft.launch.snapshot");
    public static readonly XsrSemanticId ActiveKey = XsrSemanticId.Parse("minecraft.launch.active");
    public static readonly XsrSemanticId StageKey = XsrSemanticId.Parse("minecraft.launch.stage");
    public static readonly XsrSemanticId ProgressKey = XsrSemanticId.Parse("minecraft.launch.progress");
    public static readonly XsrSemanticId MethodKey = XsrSemanticId.Parse("minecraft.launch.method");
    public static readonly XsrSemanticId SpeedKey = XsrSemanticId.Parse("minecraft.launch.speed");
    public static readonly XsrSemanticId LaunchedKey = XsrSemanticId.Parse("minecraft.launch.launched");

    // Java runtime acquisition approval: the pipeline pauses before any download until the
    // user decides (the legacy launcher asks before auto-downloading a runtime).
    public static readonly XsrSemanticId AcquirePendingKey = XsrSemanticId.Parse("minecraft.java.acquire.pending");
    public static readonly XsrSemanticId AcquireComponentKey = XsrSemanticId.Parse("minecraft.java.acquire.component");
    public static readonly XsrSemanticId AcquireMajorKey = XsrSemanticId.Parse("minecraft.java.acquire.major");

    public static void DeclareState(XsrStateStoreBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Cell<MinecraftLaunchProgressSnapshot>(SnapshotKey, OwnerName);
        builder.Derived(ActiveKey, OwnerName, [SnapshotKey],
            static (reader, cancellationToken) => ReadSnapshot(reader, cancellationToken).Active);
        builder.Derived(StageKey, OwnerName, [SnapshotKey],
            static (reader, cancellationToken) => ReadSnapshot(reader, cancellationToken).Stage);
        builder.Derived(ProgressKey, OwnerName, [SnapshotKey],
            static (reader, cancellationToken) => ReadSnapshot(reader, cancellationToken).Progress);
        builder.Derived(MethodKey, OwnerName, [SnapshotKey],
            static (reader, cancellationToken) => ReadSnapshot(reader, cancellationToken).Method);
        builder.Derived(SpeedKey, OwnerName, [SnapshotKey],
            static (reader, cancellationToken) => ReadSnapshot(reader, cancellationToken).DownloadSpeed);
        builder.Derived(LaunchedKey, OwnerName, [SnapshotKey],
            static (reader, cancellationToken) => ReadSnapshot(reader, cancellationToken).IsLaunched);
        builder.Cell<bool>(AcquirePendingKey, OwnerName);
        builder.Cell<string>(AcquireComponentKey, OwnerName);
        builder.Cell<int>(AcquireMajorKey, OwnerName);
    }

    private static MinecraftLaunchProgressSnapshot ReadSnapshot(
        XsrStateReader reader,
        CancellationToken cancellationToken)
    {
        XsrStateValue<MinecraftLaunchProgressSnapshot> value = reader.Read<MinecraftLaunchProgressSnapshot>(
            reader.Resolve(SnapshotKey),
            cancellationToken);
        return value.HasValue ? value.Value : MinecraftLaunchProgressSnapshot.Empty;
    }
}

/// <summary>
/// Writes launch stage reports into the shared host store. Each report publishes the whole
/// cell set so readers always see one coherent stage snapshot; publishing never throws into
/// the launch pipeline.
/// </summary>
public class MinecraftLaunchProgressPublisher(XsrStateStore store)
{
    private readonly XsrStateStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly object _gate = new();
    private readonly XsrStateId _snapshotId = store.Resolve(MinecraftLaunchProgressState.SnapshotKey);
    private readonly XsrStateId _acquirePendingId = store.Resolve(MinecraftLaunchProgressState.AcquirePendingKey);
    private readonly XsrStateId _acquireComponentId = store.Resolve(MinecraftLaunchProgressState.AcquireComponentKey);
    private readonly XsrStateId _acquireMajorId = store.Resolve(MinecraftLaunchProgressState.AcquireMajorKey);
    private MinecraftLaunchProgressSnapshot _current = MinecraftLaunchProgressSnapshot.Empty;

    public void Start() => Publish(new MinecraftLaunchStageReport(
        MinecraftLaunchStages.GetJava, 0d, IsLaunched: false, Method: null, DownloadSpeed: null));

    public virtual void Report(MinecraftLaunchStageReport report)
    {
        Publish(report);
    }

    /// <summary>Marks a Java runtime acquisition as awaiting the user's decision.</summary>
    public void RequestAcquisition(string component, int majorVersion)
    {
        try
        {
            // Publish payload before the ready flag so a UI observer never opens a decision
            // surface with a stale component or Java major from an earlier acquisition.
            _store.Publish(_acquireComponentId, component);
            _store.Publish(_acquireMajorId, majorVersion);
            _store.Publish(_acquirePendingId, true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            // Progress publication must never break the launch pipeline.
        }
    }

    /// <summary>Clears the acquisition prompt after a decision (or cancellation).</summary>
    public void ResolveAcquisition()
    {
        try
        {
            _store.Publish(_acquirePendingId, false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
        }
    }

    public void Stop()
    {
        PublishSnapshot(MinecraftLaunchProgressSnapshot.Empty);
    }

    /// <summary>
    /// Resets progress only when <paramref name="sessionId"/> is still the session represented by
    /// the current launch. Retaining the terminal ID lets Desktop correlate the process roster
    /// without leaving active/stage/launched facts stale.
    /// </summary>
    public bool Stop(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            return false;
        }

        lock (_gate)
        {
            if (_current.SessionId != sessionId)
            {
                return false;
            }

            PublishSnapshotLocked(MinecraftLaunchProgressSnapshot.Empty with { SessionId = sessionId });
            return true;
        }
    }

    private void Publish(MinecraftLaunchStageReport report)
    {
        if (string.IsNullOrEmpty(report.Stage))
        {
            return;
        }

        PublishSnapshot(new MinecraftLaunchProgressSnapshot(
            true,
            report.Stage,
            Math.Clamp(report.Progress, 0d, 1d),
            report.Method ?? string.Empty,
            report.DownloadSpeed ?? string.Empty,
            report.IsLaunched,
            report.SessionId));
    }

    private void PublishSnapshot(MinecraftLaunchProgressSnapshot snapshot)
    {
        lock (_gate)
        {
            PublishSnapshotLocked(snapshot);
        }
    }

    private void PublishSnapshotLocked(MinecraftLaunchProgressSnapshot snapshot)
    {
        try
        {
            _store.Publish(_snapshotId, snapshot);
            _current = snapshot;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            // Progress publication must never break the launch pipeline. Keep the in-memory
            // value aligned with the last successfully published snapshot.
        }
    }
}

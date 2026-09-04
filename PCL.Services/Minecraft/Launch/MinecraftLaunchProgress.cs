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
    string? DownloadSpeed = null);

/// <summary>
/// The launch progress state cells. The coordinator publishes one report as one coherent set
/// of cells; the renderer reads local state and the desktop controller owns display strings.
/// </summary>
public static class MinecraftLaunchProgressState
{
    public const string OwnerName = "PCL.Services.Minecraft.Launch";

    public static readonly XsrSemanticId ActiveKey = XsrSemanticId.Parse("minecraft.launch.active");
    public static readonly XsrSemanticId StageKey = XsrSemanticId.Parse("minecraft.launch.stage");
    public static readonly XsrSemanticId ProgressKey = XsrSemanticId.Parse("minecraft.launch.progress");
    public static readonly XsrSemanticId MethodKey = XsrSemanticId.Parse("minecraft.launch.method");
    public static readonly XsrSemanticId SpeedKey = XsrSemanticId.Parse("minecraft.launch.speed");
    public static readonly XsrSemanticId LaunchedKey = XsrSemanticId.Parse("minecraft.launch.launched");

    public static void DeclareState(XsrStateStoreBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Cell<bool>(ActiveKey, OwnerName);
        builder.Cell<string>(StageKey, OwnerName);
        builder.Cell<double>(ProgressKey, OwnerName);
        builder.Cell<string>(MethodKey, OwnerName);
        builder.Cell<string>(SpeedKey, OwnerName);
        builder.Cell<bool>(LaunchedKey, OwnerName);
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
    private readonly XsrStateId _activeId = store.Resolve(MinecraftLaunchProgressState.ActiveKey);
    private readonly XsrStateId _stageId = store.Resolve(MinecraftLaunchProgressState.StageKey);
    private readonly XsrStateId _progressId = store.Resolve(MinecraftLaunchProgressState.ProgressKey);
    private readonly XsrStateId _methodId = store.Resolve(MinecraftLaunchProgressState.MethodKey);
    private readonly XsrStateId _speedId = store.Resolve(MinecraftLaunchProgressState.SpeedKey);
    private readonly XsrStateId _launchedId = store.Resolve(MinecraftLaunchProgressState.LaunchedKey);

    public void Start() => Publish(new MinecraftLaunchStageReport(
        MinecraftLaunchStages.GetJava, 0d, IsLaunched: false, Method: null, DownloadSpeed: null));

    public virtual void Report(MinecraftLaunchStageReport report)
    {
        Publish(report);
    }

    public void Stop()
    {
        try
        {
            _store.Publish(_activeId, false);
            _store.Publish(_stageId, string.Empty);
            _store.Publish(_progressId, 0d);
            _store.Publish(_methodId, string.Empty);
            _store.Publish(_speedId, string.Empty);
            _store.Publish(_launchedId, false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            // Progress publication must never break the launch pipeline.
        }
    }

    private void Publish(MinecraftLaunchStageReport report)
    {
        if (string.IsNullOrEmpty(report.Stage))
        {
            return;
        }

        try
        {
            _store.Publish(_activeId, true);
            _store.Publish(_stageId, report.Stage);
            _store.Publish(_progressId, Math.Clamp(report.Progress, 0d, 1d));
            _store.Publish(_methodId, report.Method ?? string.Empty);
            _store.Publish(_speedId, report.DownloadSpeed ?? string.Empty);
            _store.Publish(_launchedId, report.IsLaunched);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            // Progress publication must never break the launch pipeline.
        }
    }
}

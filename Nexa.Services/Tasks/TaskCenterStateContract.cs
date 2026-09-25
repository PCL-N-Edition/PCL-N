using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Tasks;

/// <summary>State keys and declared cells for the task center. One writer: <see cref="TaskCenterService"/>.</summary>
public static class TaskCenterStateContract
{
    public static readonly XsrSemanticId EntriesKey = XsrSemanticId.Parse("tasks.center.entries");
    public static readonly XsrSemanticId SummaryKey = XsrSemanticId.Parse("tasks.center.summary");

    public static void DeclareState(XsrStateStoreBuilder builder)
    {
        builder.Cell<TaskCenterSummary>(SummaryKey, "Nexa.Services.Tasks");
        builder.Collection<TaskCenterEntry, string>(EntriesKey, "Nexa.Services.Tasks", entry => entry.TaskId);
    }
}

public enum TaskCenterEntryState { Waiting, Running, Finished, Failed, Canceled, Paused }

public sealed record TaskCenterStep(string Name, string Detail, double Progress, TaskCenterEntryState State);

/// <summary>
/// One visible task card. Terminal entries stay visible until dismissed so completion is
/// acknowledged by the user, not by a timeout (parity with the legacy task manager).
/// </summary>
public sealed record TaskCenterEntry(
    string TaskId,
    string Title,
    string Stage,
    string Detail,
    double Progress,
    int CompletedFiles,
    int TotalFiles,
    long SpeedBytesPerSecond,
    TaskCenterEntryState State,
    string? ErrorMessage = null,
    bool CanCancel = true,
    IReadOnlyList<TaskCenterStep>? Steps = null)
{
    public bool IsTerminal => State is TaskCenterEntryState.Finished or TaskCenterEntryState.Failed or TaskCenterEntryState.Canceled or TaskCenterEntryState.Paused;
};

/// <summary>Aggregated facts the bubble and the page header render; Progress is the active-task average.</summary>
public sealed record TaskCenterSummary(
    int ActiveCount,
    int VisibleCount,
    double Progress,
    long SpeedBytesPerSecond,
    int RemainingFiles);

public sealed record TaskCenterStart(string TaskId, string Title, IReadOnlyList<string> StagePlan, bool CanCancel = true);

/// <summary>Outcome of a cancel request, so the route layer can tell the states apart.</summary>
public enum TaskCenterCancelResult
{
    Canceled = 0,
    NotFound = 1,
    NotCancelable = 2,
}

public static class TaskCenterRoutes
{
    public static readonly XsrSemanticId Cancel = XsrSemanticId.Parse("tasks.center.cancel");
    public static readonly XsrSemanticId Dismiss = XsrSemanticId.Parse("tasks.center.dismiss");
    public static readonly XsrSemanticId ClearFinished = XsrSemanticId.Parse("tasks.center.clear");
}

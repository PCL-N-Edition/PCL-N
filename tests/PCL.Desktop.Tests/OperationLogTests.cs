using PCL.Desktop.Ui;
using PCL.Services.Composition;
using PCL.Services.Foundation;
using PCL.Services.Logging;
using PCL.Xsr;
using PCL.Xsr.Runtime;
using PCL.Xsr.State;

namespace PCL.Desktop.Tests;

// XSR-711: the operation log observers feed XSR telemetry into LogService through one
// composition wiring — with the logging domain excluded so the log's own state publications
// never re-enter the log.
internal static partial class Program
{
    private static readonly XsrSemanticId TestCommand = XsrSemanticId.Parse("test.command");

    private static (XsrOperationLog Log, LogService Service, XsrStateStore Store) ComposeOperationLog(
        IXsrStateObserver? primary = null)
    {
        XsrOperationLog operationLog = new();
        XsrStateStoreBuilder builder = FoundationState.CreateBuilder();
        LaunchPageState.DeclareState(builder);
        XsrStateStore store = builder.Build(
            primary is null ? operationLog.State : new XsrCompositeStateObserver(primary, operationLog.State));
        LogService log = new(store, clock: new FixedOperationTime());
        operationLog.Attach(log);
        return (operationLog, log, store);
    }

    private sealed class FixedOperationTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 1, 1, 18, 36, 42, TimeSpan.Zero);
    }

    private static IReadOnlyList<LogEntry> ReadOperationEntries(XsrStateStore store) =>
        store.ReadCollection<LogEntry>(store.Resolve(LogService.EntriesKey)).Items;

    internal static void DispatchSuccessLogsDebugTrace()
    {
        // The default level gate drops Debug traces: the verbose tier appears only when the
        // maximum level is raised.
        (XsrOperationLog log, LogService service, XsrStateStore store) = ComposeOperationLog();
        service.MaximumLevel = LogLevel.Debug;
        log.Dispatch.OnCompleted(new XsrDispatchObservation(
            XsrCorrelationId.Create(),
            XsrDispatchKind.Command,
            TestCommand,
            default,
            TimeSpan.FromMilliseconds(18.2),
            null,
            null));

        IReadOnlyList<LogEntry> entries = ReadOperationEntries(store);
        AssertEqual(1, entries.Count);
        AssertEqual(LogLevel.Debug, entries[0].Level);
        AssertEqual("Command", entries[0].Module);
        AssertTrue(entries[0].Message.Contains("test.command completed", StringComparison.Ordinal));
        AssertTrue(entries[0].Message.Contains("18.2 ms", StringComparison.Ordinal));
    }

    internal static void DispatchFailureLogsWarnWithCode()
    {
        (XsrOperationLog log, LogService service, XsrStateStore store) = ComposeOperationLog();
        XsrError error = new(XsrErrorKind.Rejected, XsrSemanticId.Parse("test.rejected"), "nope");
        log.Dispatch.OnCompleted(new XsrDispatchObservation(
            XsrCorrelationId.Create(),
            XsrDispatchKind.Query,
            TestCommand,
            default,
            TimeSpan.FromMilliseconds(3),
            error,
            "cancellation"));

        IReadOnlyList<LogEntry> entries = ReadOperationEntries(store);
        AssertEqual(1, entries.Count);
        AssertEqual(LogLevel.Warn, entries[0].Level);
        AssertEqual("Query", entries[0].Module);
        AssertTrue(entries[0].Message.Contains("test.command failed code=test.rejected", StringComparison.Ordinal));
        AssertTrue(entries[0].Message.Contains("fault=cancellation", StringComparison.Ordinal));
    }

    internal static void StateChangesLogRealTimeButQuietDomainsStaySilent()
    {
        (XsrOperationLog log, LogService service, XsrStateStore store) = ComposeOperationLog();
        service.MaximumLevel = LogLevel.RealTime;

        XsrSemanticId minecraftState = XsrSemanticId.Parse("minecraft.launch.phase");
        log.State.OnChanged(new XsrStateChange(
            default,
            minecraftState,
            XsrStateKind.Cell,
            82,
            XsrStateAvailability.Available,
            XsrStateChangeReason.ValuePublished));
        AssertTrue(ReadOperationEntries(store).Any(entry =>
            entry.Module == "State" && entry.Message.Contains("rev=82", StringComparison.Ordinal)));

        // The recursion guard: publishing the log's own state must never re-enter the log.
        int before = ReadOperationEntries(store).Count;
        XsrSemanticId loggingState = XsrSemanticId.Parse("logging.entries");
        log.State.OnChanged(new XsrStateChange(
            default,
            loggingState,
            XsrStateKind.Collection,
            9,
            XsrStateAvailability.Available,
            XsrStateChangeReason.ValuePublished));
        AssertEqual(before, ReadOperationEntries(store).Count);
    }

    internal static void CompositeStateObserverFansOutToBothObservers()
    {
        List<XsrStateChange> primarySeen = [];
        (XsrOperationLog log, LogService service, XsrStateStore store) =
            ComposeOperationLog(primary: new RecordingOperationStateObserver(primarySeen));
        service.MaximumLevel = LogLevel.RealTime;

        XsrSemanticId state = LaunchPageState.ActionLabelKey;
        store.Publish(store.Resolve(state), "启动游戏");
        AssertTrue(primarySeen.Any(change => change.SemanticId.Equals(state)));
        AssertTrue(ReadOperationEntries(store).Any(entry =>
            entry.Module == "State" && entry.Message.Contains(state.Value, StringComparison.Ordinal)));
    }

    internal static void LifecycleAndSchedulerLogAtTheirTiers()
    {
        (XsrOperationLog log, LogService service, XsrStateStore store) = ComposeOperationLog();

        // Lifecycle transitions sit at Info: low-volume milestones every bug report needs,
        // visible even under the release-default Info gate (no MaximumLevel raise here).
        // Scheduler observations stay at RealTime and only appear when the gate is raised.
        log.Lifecycle.OnPhaseChanged(new XsrLifecycleTransition(
            "SidecarSession", XsrLifecyclePhase.Running, XsrLifecyclePhase.Stopping));
        log.Scheduler.OnExecuted(new XsrScheduledObservation(
            XsrCorrelationId.Create(),
            XsrScheduledOutcome.Faulted,
            TimeSpan.FromMilliseconds(4),
            "timeout"));

        IReadOnlyList<LogEntry> entries = ReadOperationEntries(store);
        AssertTrue(entries.Any(entry =>
            entry.Level == LogLevel.Info
            && entry.Module == "Lifecycle"
            && entry.Message.Contains("SidecarSession: Running -> Stopping", StringComparison.Ordinal)));
        AssertTrue(entries.All(entry => entry.Module != "Scheduled"));

        service.MaximumLevel = LogLevel.RealTime;
        log.Scheduler.OnExecuted(new XsrScheduledObservation(
            XsrCorrelationId.Create(),
            XsrScheduledOutcome.Faulted,
            TimeSpan.FromMilliseconds(4),
            "timeout"));
        AssertTrue(ReadOperationEntries(store).Any(entry =>
            entry.Module == "Scheduled"
            && entry.Message.Contains("Faulted", StringComparison.Ordinal)
            && entry.Message.Contains("4.0 ms", StringComparison.Ordinal)));
    }

    private sealed class RecordingOperationStateObserver(List<XsrStateChange> seen) : IXsrStateObserver
    {
        public void OnChanged(XsrStateChange change) => seen.Add(change);
    }
}

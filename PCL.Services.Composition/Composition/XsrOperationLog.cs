using System.Globalization;
using PCL.Services.Logging;
using PCL.Xsr;
using PCL.Xsr.Runtime;
using PCL.Xsr.State;

namespace PCL.Services.Composition;

/// <summary>
/// Operation observers that feed XSR's own dispatch, state, event, scheduler, and lifecycle
/// telemetry into <see cref="LogService"/>, alongside explicit service breadcrumbs. Tiers
/// map onto the log level gate: dispatch failures log at Warn (always visible), user-facing
/// operations (UI intents, lifecycle transitions) at Info, command/query completions at Debug,
/// and the high-frequency state and scheduler flows at RealTime, so the verbose trace appears
/// only when the maximum level is raised. State changes under the logging, diagnostics, and
/// telemetry domains are skipped: the log's own state publications must never re-enter the log.
/// </summary>
public sealed class XsrOperationLog
{
    private LogService? _log;

    public XsrOperationLog()
    {
        Dispatch = new DispatchObserver(this);
        State = new StateObserver(this);
        Events = new EventObserver(this);
        Scheduler = new SchedulerObserver(this);
        Lifecycle = new LifecycleObserver(this);
    }

    /// <summary>Completes the late binding once LogService exists over the observed store.</summary>
    public void Attach(LogService log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Records one user-facing operation at Info (the tier users and bug reports read): the
    /// semantic command a renderer intent carried, e.g. ui.launch.primary.
    /// </summary>
    public void WriteIntent(XsrSemanticId command, XsrCorrelationId correlationId = default)
    {
        Write(LogLevel.Info, "UI", $"Intent received command={command.Value} cid={correlationId}");
    }

    public IXsrDispatchObserver Dispatch { get; private set; }

    public IXsrStateObserver State { get; private set; }

    public IXsrEventObserver Events { get; private set; }

    public IXsrSchedulerObserver Scheduler { get; private set; }

    public IXsrLifecycleObserver Lifecycle { get; private set; }

    private static bool IsQuietDomain(XsrSemanticId semanticId)
    {
        string value = semanticId.Value;
        return value.StartsWith("logging.", StringComparison.Ordinal)
            || value.StartsWith("diagnostics.", StringComparison.Ordinal)
            || value.StartsWith("telemetry.", StringComparison.Ordinal);
    }

    private void Write(LogLevel level, string module, string message)
    {
        _log?.Write(level, module, message);
    }

    private sealed class DispatchObserver(XsrOperationLog owner) : IXsrDispatchObserver
    {
        public void OnStarted(XsrDispatchStarted observation) => owner.Write(
            LogLevel.Debug,
            observation.Kind == XsrDispatchKind.Command ? "Command" : "Query",
            $"{observation.SemanticId.Value} started cid={observation.CorrelationId}");

        public void OnCompleted(XsrDispatchObservation observation)
        {
            string module = observation.Kind == XsrDispatchKind.Command ? "Command" : "Query";
            if (observation.IsSuccess)
            {
                owner.Write(
                    LogLevel.Debug,
                    module,
                    $"{observation.SemanticId.Value} completed in {Milliseconds(observation.Duration)} ms "
                    + $"cid={observation.CorrelationId}");
                return;
            }

            owner.Write(
                LogLevel.Warn,
                module,
                $"{observation.SemanticId.Value} failed code={observation.Error?.Code.Value} "
                + $"in {Milliseconds(observation.Duration)} ms cid={observation.CorrelationId}"
                + (observation.FaultType is null ? string.Empty : $" fault={observation.FaultType}"));
        }
    }

    private sealed class StateObserver(XsrOperationLog owner) : IXsrStateObserver
    {
        public void OnChanged(XsrStateChange change)
        {
            if (IsQuietDomain(change.SemanticId))
            {
                return;
            }

            owner.Write(
                LogLevel.RealTime,
                "State",
                $"{change.SemanticId.Value} rev={change.Revision} {change.Reason}");
        }
    }

    private sealed class EventObserver(XsrOperationLog owner) : IXsrEventObserver
    {
        public void OnPublished(XsrEventPublication publication)
        {
            owner.Write(
                LogLevel.Debug,
                "Event",
                $"{publication.SemanticId.Value} seq={publication.Sequence} cid={publication.CorrelationId}");
        }
    }

    private sealed class SchedulerObserver(XsrOperationLog owner) : IXsrSchedulerObserver
    {
        public void OnExecuted(XsrScheduledObservation observation)
        {
            owner.Write(
                observation.Outcome == XsrScheduledOutcome.Faulted ? LogLevel.Error : LogLevel.RealTime,
                "Scheduled",
                $"{observation.Outcome} in {Milliseconds(observation.Duration)} ms cid={observation.CorrelationId}"
                + (observation.FaultType is null ? string.Empty : $" fault={observation.FaultType}"));
        }
    }

    private sealed class LifecycleObserver(XsrOperationLog owner) : IXsrLifecycleObserver
    {
        public void OnPhaseChanged(XsrLifecycleTransition transition)
        {
            // Lifecycle transitions are low-volume and central to bug reports (which subsystem
            // hung at startup or shutdown), so they sit at Info: visible even in release builds.
            owner.Write(
                LogLevel.Info,
                "Lifecycle",
                $"{transition.Component}: {transition.From} -> {transition.To}");
        }
    }

    private static string Milliseconds(TimeSpan duration) => duration.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture);
}

/// <summary>
/// Fans one state publication out to two observers: the renderer's bridge (or any primary
/// observer attached at store build) and the operation log's state tap. The store accepts a
/// single observer, so composition chains them here.
/// </summary>
public sealed class XsrCompositeStateObserver(IXsrStateObserver? primary, IXsrStateObserver? secondary)
    : IXsrStateObserver
{
    private readonly object _gate = new();
    private readonly List<IXsrStateObserver> _late = [];

    /// <summary>
    /// Attaches one more observer after composition (for example a controller that joined the
    /// store fan-out late). Later observers see every publication from attach time onward.
    /// </summary>
    public void Add(IXsrStateObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate)
        {
            _late.Add(observer);
        }
    }

    public void OnChanged(XsrStateChange change)
    {
        primary?.OnChanged(change);
        secondary?.OnChanged(change);
        IXsrStateObserver[] late;
        lock (_gate)
        {
            late = [.. _late];
        }

        foreach (IXsrStateObserver observer in late)
        {
            observer.OnChanged(change);
        }
    }
}

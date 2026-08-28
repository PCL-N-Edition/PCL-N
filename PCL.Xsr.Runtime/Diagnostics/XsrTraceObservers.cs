using PCL.Xsr.Diagnostics;
using PCL.Xsr.State;

namespace PCL.Xsr.Runtime;

/// <summary>
/// Feeds command and query completions into one session trace, preserving correlation IDs.
/// </summary>
public sealed class XsrTraceDispatchObserver(XsrSessionTrace trace, TimeProvider? timeProvider = null) :
    IXsrDispatchObserver
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public void OnCompleted(XsrDispatchObservation observation)
    {
        string detail = observation.IsSuccess
            ? $"{observation.Kind} completed in {observation.Duration.TotalMilliseconds:F1}ms"
            : $"{observation.Kind} failed ({observation.Error?.Code.Value}) in {observation.Duration.TotalMilliseconds:F1}ms";
        if (observation.FaultType is not null)
        {
            detail += $" fault={observation.FaultType}";
        }

        trace.Record(new XsrTraceEntry(
            observation.Kind == XsrDispatchKind.Command ? XsrTraceKind.Command : XsrTraceKind.Query,
            observation.SemanticId,
            observation.CorrelationId,
            _timeProvider.GetTimestamp(),
            detail,
            observation.IsSuccess));
    }
}

/// <summary>
/// Feeds applied state changes into one session trace. State changes are correlated through
/// their semantic identity; the owning operation's correlation rides on dispatch and event
/// entries until state publication carries correlation end to end.
/// </summary>
public sealed class XsrTraceStateObserver(XsrSessionTrace trace, TimeProvider? timeProvider = null) :
    IXsrStateObserver
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public void OnChanged(XsrStateChange change)
    {
        trace.Record(new XsrTraceEntry(
            XsrTraceKind.State,
            change.SemanticId,
            default,
            _timeProvider.GetTimestamp(),
            $"{change.Reason} revision={change.Revision} availability={change.Availability}",
            true));
    }
}

/// <summary>
/// Feeds accepted event publications into one session trace, preserving correlation IDs.
/// </summary>
public sealed class XsrTraceEventObserver(XsrSessionTrace trace, TimeProvider? timeProvider = null) :
    IXsrEventObserver
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public void OnPublished(XsrEventPublication publication)
    {
        trace.Record(new XsrTraceEntry(
            XsrTraceKind.Event,
            publication.SemanticId,
            publication.CorrelationId,
            publication.Timestamp,
            $"sequence={publication.Sequence} scope={publication.ScopeId}/{publication.ScopeKey}",
            true));
    }
}

/// <summary>
/// Feeds finished scheduled work into one session trace, preserving correlation IDs.
/// </summary>
public sealed class XsrTraceSchedulerObserver(XsrSessionTrace trace, TimeProvider? timeProvider = null) :
    IXsrSchedulerObserver
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public void OnExecuted(XsrScheduledObservation observation)
    {
        string detail = $"{observation.Outcome} in {observation.Duration.TotalMilliseconds:F1}ms";
        if (observation.FaultType is not null)
        {
            detail += $" fault={observation.FaultType}";
        }

        trace.Record(new XsrTraceEntry(
            XsrTraceKind.Scheduled,
            default,
            observation.CorrelationId,
            _timeProvider.GetTimestamp(),
            detail,
            observation.Outcome != XsrScheduledOutcome.Faulted));
    }
}

/// <summary>
/// Feeds accepted lifecycle transitions into one session trace.
/// </summary>
public sealed class XsrTraceLifecycleObserver(XsrSessionTrace trace, TimeProvider? timeProvider = null) :
    IXsrLifecycleObserver
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public void OnPhaseChanged(XsrLifecycleTransition transition)
    {
        trace.Record(new XsrTraceEntry(
            XsrTraceKind.Lifecycle,
            default,
            default,
            _timeProvider.GetTimestamp(),
            $"{transition.Component}: {transition.From} -> {transition.To}",
            transition.To != XsrLifecyclePhase.Failed));
    }
}

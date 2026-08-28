namespace PCL.Xsr.Runtime;

/// <summary>
/// The lifecycle phases of one XSR component. Phases only ever move forward; a stopped or failed
/// component is terminal and a restart is a new instance.
/// </summary>
public enum XsrLifecyclePhase
{
    NotStarted = 1,
    Starting = 2,
    Running = 3,
    Stopping = 4,
    Stopped = 5,
    Failed = 6,
}

/// <summary>
/// Describes one accepted lifecycle transition.
/// </summary>
public readonly record struct XsrLifecycleTransition(
    string Component,
    XsrLifecyclePhase From,
    XsrLifecyclePhase To);

/// <summary>
/// Receives every accepted lifecycle transition. Observers never change the transition.
/// </summary>
public interface IXsrLifecycleObserver
{
    void OnPhaseChanged(XsrLifecycleTransition transition);
}

/// <summary>
/// A guarded lifecycle state machine for one component. Illegal transitions are rejected instead
/// of silently accepted, transitions are serialized under concurrency, and every accepted
/// transition is reported to the optional observer.
/// </summary>
public sealed class XsrLifecycle
{
    private readonly object _gate = new();
    private readonly IXsrLifecycleObserver? _observer;
    private XsrLifecyclePhase _phase = XsrLifecyclePhase.NotStarted;

    public XsrLifecycle(string component, IXsrLifecycleObserver? observer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        Component = component;
        _observer = observer;
    }

    public string Component { get; }

    public XsrLifecyclePhase Phase
    {
        get
        {
            lock (_gate)
            {
                return _phase;
            }
        }
    }

    /// <summary>
    /// Enters one phase, throwing when the transition is not allowed.
    /// </summary>
    public void Enter(XsrLifecyclePhase phase)
    {
        if (!TryEnter(phase))
        {
            throw new InvalidOperationException(
                $"The XSR component '{Component}' cannot transition from '{_phase}' to '{phase}'.");
        }
    }

    /// <summary>
    /// Attempts to enter one phase; returns false when the transition is not allowed.
    /// </summary>
    public bool TryEnter(XsrLifecyclePhase phase)
    {
        if (!Enum.IsDefined(phase))
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }

        XsrLifecycleTransition transition;
        lock (_gate)
        {
            if (!IsAllowed(_phase, phase))
            {
                return false;
            }

            transition = new XsrLifecycleTransition(Component, _phase, phase);
            _phase = phase;
        }

        // Observers run outside the gate so a slow observer never extends the transition lock.
        Notify(transition);
        return true;
    }

    private static bool IsAllowed(XsrLifecyclePhase from, XsrLifecyclePhase to) =>
        (from, to) switch
        {
            (XsrLifecyclePhase.NotStarted, XsrLifecyclePhase.Starting) => true,
            (XsrLifecyclePhase.Starting, XsrLifecyclePhase.Running) => true,
            (XsrLifecyclePhase.Starting, XsrLifecyclePhase.Stopping) => true,
            (XsrLifecyclePhase.Running, XsrLifecyclePhase.Stopping) => true,
            (XsrLifecyclePhase.Stopping, XsrLifecyclePhase.Stopped) => true,
            (XsrLifecyclePhase.NotStarted
                or XsrLifecyclePhase.Starting
                or XsrLifecyclePhase.Running
                or XsrLifecyclePhase.Stopping,
                XsrLifecyclePhase.Failed) => true,
            _ => false,
        };

    private void Notify(XsrLifecycleTransition transition)
    {
        if (_observer is null)
        {
            return;
        }

        try
        {
            _observer.OnPhaseChanged(transition);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            // Lifecycle progression must not be changed by a diagnostics observer failure.
        }
    }
}

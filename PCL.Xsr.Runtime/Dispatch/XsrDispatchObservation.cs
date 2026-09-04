namespace PCL.Xsr.Runtime;

/// <summary>
/// Identifies the routed operation represented by a dispatch observation.
/// </summary>
public enum XsrDispatchKind
{
    Command = 1,
    Query = 2,
}

/// <summary>Identifies an invocation before its handler runs; contains no request payload.</summary>
public readonly record struct XsrDispatchStarted(
    XsrCorrelationId CorrelationId,
    XsrDispatchKind Kind,
    XsrSemanticId SemanticId,
    XsrRuntimeId RuntimeId);

/// <summary>
/// Describes one completed route invocation without exposing its response payload.
/// </summary>
public readonly record struct XsrDispatchObservation(
    XsrCorrelationId CorrelationId,
    XsrDispatchKind Kind,
    XsrSemanticId SemanticId,
    XsrRuntimeId RuntimeId,
    TimeSpan Duration,
    XsrError? Error,
    string? FaultType)
{
    public bool IsSuccess => Error is null;
}

/// <summary>
/// Receives handler starts and every command/query completion, including detached completion.
/// </summary>
public interface IXsrDispatchObserver
{
    void OnStarted(XsrDispatchStarted observation) { }

    void OnCompleted(XsrDispatchObservation observation);
}

internal static class XsrDispatchNotifier
{
    public static void NotifyStarted(IXsrDispatchObserver observer, XsrDispatchStarted observation)
    {
        try
        {
            observer.OnStarted(observation);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            // Diagnostics must not prevent the handler from starting.
        }
    }

    public static void Notify(IXsrDispatchObserver observer, XsrDispatchObservation observation)
    {
        try
        {
            observer.OnCompleted(observation);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            // Dispatch completion must not be changed by a diagnostics observer failure.
        }
    }
}

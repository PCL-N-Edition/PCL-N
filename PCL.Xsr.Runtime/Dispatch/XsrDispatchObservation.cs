namespace PCL.Xsr.Runtime;

/// <summary>
/// Identifies the routed operation represented by a dispatch observation.
/// </summary>
public enum XsrDispatchKind
{
    Command = 1,
    Query = 2,
}

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
/// Receives every command and query completion, including detached command completion.
/// </summary>
public interface IXsrDispatchObserver
{
    void OnCompleted(XsrDispatchObservation observation);
}

internal static class XsrDispatchNotifier
{
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

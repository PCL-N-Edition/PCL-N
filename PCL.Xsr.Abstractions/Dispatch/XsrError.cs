namespace PCL.Xsr;

/// <summary>
/// Classifies an XSR error without coupling callers to transport or handler exceptions.
/// </summary>
public enum XsrErrorKind
{
    Rejected = 1,
    NotFound = 2,
    ContractMismatch = 3,
    Cancelled = 4,
    TimedOut = 5,
    Faulted = 6,
    Unavailable = 7,
    Backpressure = 8,
    Lifecycle = 9,
}

/// <summary>
/// Represents a stable XSR error contract.
/// </summary>
public sealed record XsrError
{
    public XsrError(XsrErrorKind kind, XsrSemanticId code, string message)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (!code.IsAssigned)
        {
            throw new ArgumentException("An error code must be assigned.", nameof(code));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Kind = kind;
        Code = code;
        Message = message;
    }

    public XsrErrorKind Kind { get; }

    public XsrSemanticId Code { get; }

    public string Message { get; }
}

/// <summary>
/// Creates the stable errors owned by the XSR runtime.
/// </summary>
public static class XsrRuntimeErrors
{
    public static readonly XsrSemanticId RouteNotFoundCode = XsrSemanticId.Parse("xsr.route_not_found");
    public static readonly XsrSemanticId ContractMismatchCode = XsrSemanticId.Parse("xsr.contract_mismatch");
    public static readonly XsrSemanticId CancelledCode = XsrSemanticId.Parse("xsr.cancelled");
    public static readonly XsrSemanticId TimedOutCode = XsrSemanticId.Parse("xsr.timed_out");
    public static readonly XsrSemanticId HandlerFaultedCode = XsrSemanticId.Parse("xsr.handler_faulted");
    public static readonly XsrSemanticId BackpressureCode = XsrSemanticId.Parse("xsr.backpressure");
    public static readonly XsrSemanticId NotRetainedCode = XsrSemanticId.Parse("xsr.event_not_retained");

    public static XsrError RouteNotFound() =>
        new(XsrErrorKind.NotFound, RouteNotFoundCode, "The requested XSR route is not registered.");

    public static XsrError ContractMismatch() =>
        new(XsrErrorKind.ContractMismatch, ContractMismatchCode, "The request type does not match the registered XSR contract.");

    public static XsrError Cancelled() =>
        new(XsrErrorKind.Cancelled, CancelledCode, "The XSR operation was cancelled.");

    public static XsrError TimedOut() =>
        new(XsrErrorKind.TimedOut, TimedOutCode, "The XSR operation exceeded its configured timeout.");

    public static XsrError HandlerFaulted() =>
        new(XsrErrorKind.Faulted, HandlerFaultedCode, "The XSR handler failed unexpectedly.");

    public static XsrError Backpressure() =>
        new(XsrErrorKind.Backpressure, BackpressureCode, "The XSR event scope buffer is full; publication was rejected without dropping events.");

    public static XsrError NotRetained() =>
        new(XsrErrorKind.NotFound, NotRetainedCode, "The requested XSR event sequence is no longer retained; request a fresh snapshot from state.");
}

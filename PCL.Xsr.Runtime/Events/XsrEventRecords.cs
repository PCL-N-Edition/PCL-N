namespace PCL.Xsr.Runtime;

/// <summary>
/// One retained event record inside a scope. Payloads are stored once per scope even when the
/// scope orders events of different contracts.
/// </summary>
public readonly record struct XsrEventRecord(
    long Sequence,
    XsrEventId EventId,
    XsrSemanticId SemanticId,
    XsrSemanticId ScopeId,
    string ScopeKey,
    XsrCorrelationId CorrelationId,
    long Timestamp,
    object? Payload);

/// <summary>
/// Describes one accepted publication without its payload.
/// </summary>
public readonly record struct XsrEventPublication(
    long Sequence,
    XsrEventId EventId,
    XsrSemanticId SemanticId,
    XsrSemanticId ScopeId,
    string ScopeKey,
    XsrCorrelationId CorrelationId,
    long Timestamp);

/// <summary>
/// One delivered event with its typed payload and retained record.
/// </summary>
public readonly record struct XsrEventDelivery<TEvent>(XsrEventRecord Record, TEvent Payload)
    where TEvent : notnull;

/// <summary>
/// Receives every accepted event publication. The router never lets an observer failure affect
/// publication or delivery.
/// </summary>
public interface IXsrEventObserver
{
    void OnPublished(XsrEventPublication publication);
}

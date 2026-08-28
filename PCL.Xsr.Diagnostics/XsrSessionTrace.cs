namespace PCL.Xsr.Diagnostics;

/// <summary>
/// Classifies the subsystem that produced one trace entry.
/// </summary>
public enum XsrTraceKind
{
    Command = 1,
    Query = 2,
    State = 3,
    Event = 4,
    Scheduled = 5,
    Lifecycle = 6,
}

/// <summary>
/// One neutral diagnostics entry. Trace entries never carry payloads, exceptions, or CLR type
/// names from handlers; only the fault classification the owning subsystem already publishes.
/// </summary>
public readonly record struct XsrTraceEntry(
    XsrTraceKind Kind,
    XsrSemanticId SemanticId,
    XsrCorrelationId CorrelationId,
    long Timestamp,
    string Detail,
    bool IsSuccess);

/// <summary>
/// One bounded, thread-safe trace for one runtime session. Entries are ordered oldest to
/// newest; overflow drops the oldest entries and counts them instead of growing unbounded.
/// </summary>
public sealed class XsrSessionTrace
{
    private readonly object _gate = new();
    private readonly XsrTraceEntry[] _ring;
    private long _head;
    private long _total;
    private long _dropped;

    public XsrSessionTrace(XsrSessionId sessionId, int capacity = 256)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "A trace capacity must be positive.");
        }

        SessionId = sessionId;
        _ring = new XsrTraceEntry[capacity];
    }

    public XsrSessionId SessionId { get; }

    public int Capacity => _ring.Length;

    /// <summary>
    /// Gets the number of retained entries.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return (int)Math.Min(_total, _ring.Length);
            }
        }
    }

    /// <summary>
    /// Gets how many entries were dropped by the bounded ring.
    /// </summary>
    public long DroppedCount
    {
        get
        {
            lock (_gate)
            {
                return _dropped;
            }
        }
    }

    public void Record(XsrTraceEntry entry)
    {
        lock (_gate)
        {
            if (_total >= _ring.Length)
            {
                _dropped++;
            }

            _ring[_head % _ring.Length] = entry;
            _head++;
            _total++;
        }
    }

    /// <summary>
    /// Captures the retained entries ordered oldest to newest.
    /// </summary>
    public XsrTraceEntry[] Snapshot()
    {
        lock (_gate)
        {
            int count = (int)Math.Min(_total, _ring.Length);
            XsrTraceEntry[] entries = new XsrTraceEntry[count];
            long start = Math.Max(0, _head - count);
            for (int index = 0; index < count; index++)
            {
                entries[index] = _ring[(start + index) % _ring.Length];
            }

            return entries;
        }
    }

    /// <summary>
    /// Returns the retained entries that carry one correlation ID, ordered oldest to newest.
    /// </summary>
    public XsrTraceEntry[] Find(XsrCorrelationId correlationId)
    {
        if (!correlationId.IsAssigned)
        {
            return [];
        }

        lock (_gate)
        {
            int count = (int)Math.Min(_total, _ring.Length);
            long start = Math.Max(0, _head - count);
            List<XsrTraceEntry> matches = [];
            for (int index = 0; index < count; index++)
            {
                ref readonly XsrTraceEntry entry = ref _ring[(start + index) % _ring.Length];
                if (entry.CorrelationId.Equals(correlationId))
                {
                    matches.Add(entry);
                }
            }

            return [.. matches];
        }
    }
}

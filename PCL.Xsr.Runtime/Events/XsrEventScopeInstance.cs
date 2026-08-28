namespace PCL.Xsr.Runtime;

/// <summary>
/// One bounded ordering domain. Events published into the scope share one contiguous sequence,
/// and the bounded ring retains recent records for replay without ever dropping an event that a
/// live subscriber still needs; backpressure rejects publication instead.
/// </summary>
internal sealed class XsrEventScopeInstance
{
    private readonly object _gate = new();
    private readonly XsrEventRecord[] _ring;
    private readonly List<long> _cursors = [];
    private readonly List<TaskCompletionSource> _waiters = [];
    private long _nextSequence = 1;
    private long _lowestRetained = 1;

    internal XsrEventScopeInstance(int capacity)
    {
        _ring = new XsrEventRecord[capacity];
    }

    /// <summary>
    /// Gets the next sequence that publication will assign.
    /// </summary>
    public long NextSequence
    {
        get
        {
            lock (_gate)
            {
                return _nextSequence;
            }
        }
    }

    /// <summary>
    /// Gets the number of retained records.
    /// </summary>
    public int Depth
    {
        get
        {
            lock (_gate)
            {
                return (int)(_nextSequence - _lowestRetained);
            }
        }
    }

    /// <summary>
    /// Enqueues one record with the next sequence. Returns false when the ring is full and a
    /// live subscriber still needs the oldest retained record.
    /// </summary>
    public bool TryEnqueue(
        XsrEventId eventId,
        XsrSemanticId semanticId,
        XsrSemanticId scopeId,
        string scopeKey,
        XsrCorrelationId correlationId,
        long timestamp,
        object? payload,
        out long sequence)
    {
        lock (_gate)
        {
            sequence = _nextSequence;
            if (_nextSequence - _lowestRetained >= _ring.Length)
            {
                long oldest = _lowestRetained;
                bool evictable = _cursors.Count == 0 || _cursors.Min() > oldest;
                if (!evictable)
                {
                    return false;
                }

                _lowestRetained++;
            }

            _ring[(int)((sequence - 1) % _ring.Length)] = new XsrEventRecord(
                sequence,
                eventId,
                semanticId,
                scopeId,
                scopeKey,
                correlationId,
                timestamp,
                payload);
            _nextSequence++;

            // Waiters are created with RunContinuationsAsynchronously, so waking them under the
            // gate never runs subscriber code inline.
            foreach (TaskCompletionSource waiter in _waiters)
            {
                waiter.TrySetResult();
            }

            return true;
        }
    }

    /// <summary>
    /// Registers a delivery cursor at a start sequence.
    /// </summary>
    public long AttachCursor(long requestedSequence)
    {
        lock (_gate)
        {
            long start = requestedSequence > 0 ? Math.Max(1, requestedSequence) : _nextSequence;
            _cursors.Add(start);
            return start;
        }
    }

    public void UpdateCursor(long previous, long current)
    {
        lock (_gate)
        {
            UpdateCursorLocked(previous, current);
        }
    }

    public void DetachCursor(long cursor)
    {
        lock (_gate)
        {
            _ = _cursors.Remove(cursor);
        }
    }

    /// <summary>
    /// Attempts to read the next record of one event at or after the cursor position, skipping
    /// records of sibling events in the shared scope and advancing the cursor past them. When no
    /// matching record is available yet, the waiter is registered and false is returned.
    /// </summary>
    public bool TryRead(
        ref long cursor,
        XsrEventId eventId,
        out XsrEventRecord record,
        out bool notRetained,
        TaskCompletionSource? waiter)
    {
        lock (_gate)
        {
            record = default;
            notRetained = false;

            if (cursor < _lowestRetained)
            {
                notRetained = true;
                return false;
            }

            long start = cursor;
            while (cursor < _nextSequence)
            {
                XsrEventRecord candidate = _ring[(int)((cursor - 1) % _ring.Length)];
                if (candidate.EventId.Equals(eventId))
                {
                    record = candidate;
                    cursor = candidate.Sequence + 1;
                    UpdateCursorLocked(start, cursor);
                    return true;
                }

                cursor++;
            }

            UpdateCursorLocked(start, cursor);
            if (waiter is not null && !_waiters.Contains(waiter))
            {
                _waiters.Add(waiter);
            }

            return false;
        }
    }

    public void RemoveWaiter(TaskCompletionSource waiter)
    {
        lock (_gate)
        {
            _ = _waiters.Remove(waiter);
        }
    }

    private void UpdateCursorLocked(long previous, long current)
    {
        int index = _cursors.IndexOf(previous);
        if (index >= 0)
        {
            _cursors[index] = current;
        }
    }
}

// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

public enum UiDiagnosticReaderStart : byte
{
    OldestAvailable = 0,
    NextPublished = 1
}

public sealed class UiDiagnosticEventReader
{
    private readonly UiDiagnosticJournal _journal;

    internal UiDiagnosticEventReader(UiDiagnosticJournal journal, long nextSequence)
    {
        _journal = journal;
        NextSequence = nextSequence;
    }

    public long DroppedCount { get; private set; }
    internal long NextSequence { get; set; }

    public bool TryRead(out UiDiagnosticEvent diagnosticEvent) =>
        _journal.TryRead(this, out diagnosticEvent);

    public int Drain(List<UiDiagnosticEvent> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        int count = 0;
        while (TryRead(out UiDiagnosticEvent diagnosticEvent))
        {
            destination.Add(diagnosticEvent);
            count++;
        }
        return count;
    }

    internal void AddDropped(long count) => DroppedCount = checked(DroppedCount + count);
}

/// <summary>Bounded, sequence-based, multi-reader diagnostic event journal.</summary>
public sealed class UiDiagnosticJournal
{
    private readonly object _gate = new();
    private readonly UiDiagnosticEvent[] _buffer;
    private int _head;
    private int _count;
    private long _nextSequence = 1;

    internal UiDiagnosticJournal(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _buffer = new UiDiagnosticEvent[capacity];
    }

    public int Capacity => _buffer.Length;

    public int RetainedCount
    {
        get
        {
            lock (_gate)
                return _count;
        }
    }

    public UiDiagnosticEventReader CreateReader(
        UiDiagnosticReaderStart start = UiDiagnosticReaderStart.OldestAvailable)
    {
        if (!Enum.IsDefined(start))
            throw new ArgumentOutOfRangeException(nameof(start));
        lock (_gate)
        {
            long sequence = start == UiDiagnosticReaderStart.NextPublished
                ? _nextSequence
                : FirstSequence();
            return new UiDiagnosticEventReader(this, sequence);
        }
    }

    internal void Publish(in UiDiagnosticEvent diagnosticEvent)
    {
        lock (_gate)
        {
            UiDiagnosticEvent sequenced = diagnosticEvent with { Sequence = NextSequence() };
            if (_count == _buffer.Length)
            {
                _head = (_head + 1) % _buffer.Length;
                _count--;
            }
            int index = (_head + _count) % _buffer.Length;
            _buffer[index] = sequenced;
            _count++;
        }
    }

    internal bool TryRead(UiDiagnosticEventReader reader, out UiDiagnosticEvent diagnosticEvent)
    {
        lock (_gate)
        {
            long first = FirstSequence();
            if (reader.NextSequence < first)
            {
                reader.AddDropped(first - reader.NextSequence);
                reader.NextSequence = first;
            }
            if (_count == 0 || reader.NextSequence >= _nextSequence)
            {
                diagnosticEvent = default;
                return false;
            }
            int offset = checked((int)(reader.NextSequence - first));
            if ((uint)offset >= (uint)_count)
            {
                diagnosticEvent = default;
                return false;
            }
            diagnosticEvent = _buffer[(_head + offset) % _buffer.Length];
            reader.NextSequence = checked(diagnosticEvent.Sequence + 1);
            return true;
        }
    }

    private long FirstSequence() => _count == 0 ? _nextSequence : _buffer[_head].Sequence;

    private long NextSequence()
    {
        if (_nextSequence == long.MaxValue)
            throw new InvalidOperationException("Diagnostic event sequence space is exhausted.");
        return _nextSequence++;
    }
}


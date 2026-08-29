using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Services.Logging;

/// <summary>
/// The logging capability: a bounded, ordered ring of redacted log entries published as one
/// ordered state collection, so every surface that shows logs reads local state instead of
/// reaching into a mutable shared list. There is no static global sink — services receive this
/// service through the composition root. Publication never breaks the operation being logged.
/// </summary>
public sealed class LogService
{
    public const string OwnerName = "PCL.Services.Logging";

    /// <summary>
    /// The ordered collection state key: items are <see cref="LogEntry"/>, keyed by sequence.
    /// </summary>
    public static readonly XsrSemanticId EntriesKey = XsrSemanticId.Parse("logging.entries");

    private const int MaxAppendConflicts = 8;

    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly TimeProvider _clock;
    private readonly XsrStateStore _store;
    private readonly XsrStateId _entriesId;
    private int _maximumLevel = (int)LogLevel.Info;
    private long _sequence;

    public LogService(int capacity = 2_000, IXsrStateObserver? observer = null, TimeProvider? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _capacity = capacity;
        _clock = clock ?? TimeProvider.System;

        XsrStateStoreBuilder builder = new();
        builder.Collection<LogEntry, long>(
            EntriesKey,
            OwnerName,
            static entry => entry.Sequence);
        _store = builder.Build(observer);
        _entriesId = _store.Resolve(EntriesKey);
    }

    public int Capacity => _capacity;

    public XsrStateStore StateStore => _store;

    /// <summary>
    /// The most verbose level that is recorded. The default is <see cref="LogLevel.Info"/>.
    /// Invalid values fall back to Info, mirroring the legacy gate.
    /// </summary>
    public LogLevel MaximumLevel
    {
        get => (LogLevel)Volatile.Read(ref _maximumLevel);
        set => Volatile.Write(
            ref _maximumLevel,
            (int)(Enum.IsDefined(value) ? value : LogLevel.Info));
    }

    public bool IsEnabled(LogLevel level) =>
        Enum.IsDefined(level) && (int)level <= (int)MaximumLevel;

    /// <summary>
    /// Normalizes, redacts, and records one entry when its level passes the gate. The module is
    /// trimmed or defaults to "General"; the message and exception text are redacted before
    /// storage. This method never throws into the operation being logged.
    /// </summary>
    public void Write(LogLevel level, string module, string message, string? exceptionText = null)
    {
        if (!IsEnabled(level))
        {
            return;
        }

        LogEntry entry = new(
            Sequence: Interlocked.Increment(ref _sequence),
            Timestamp: _clock.GetUtcNow(),
            Level: level,
            Module: string.IsNullOrWhiteSpace(module) ? "General" : module.Trim(),
            Message: LogRedactor.Redact(message),
            ExceptionText: string.IsNullOrWhiteSpace(exceptionText) ? null : LogRedactor.Redact(exceptionText));

        Append(entry);
    }

    /// <summary>
    /// One coherent read of the current ring, oldest first.
    /// </summary>
    public IReadOnlyList<LogEntry> GetSnapshot() => _store.ReadCollection<LogEntry>(_entriesId).Items;

    /// <summary>
    /// Empties the ring and its state collection.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            for (int attempt = 0; attempt < MaxAppendConflicts; attempt++)
            {
                XsrCollectionSnapshot<LogEntry> snapshot = _store.ReadCollection<LogEntry>(_entriesId);
                long[] removals = [.. snapshot.Items.Select(static entry => entry.Sequence)];
                XsrCollectionApplyResult result = _store.PublishDelta(
                    _entriesId,
                    new XsrCollectionDelta<LogEntry, long>(snapshot.Revision, [], removals));
                if (result.IsApplied)
                {
                    return;
                }
            }
        }
    }

    private void Append(LogEntry entry)
    {
        lock (_gate)
        {
            for (int attempt = 0; attempt < MaxAppendConflicts; attempt++)
            {
                XsrCollectionSnapshot<LogEntry> snapshot = _store.ReadCollection<LogEntry>(_entriesId);
                int overflow = snapshot.Count + 1 - _capacity;
                List<long> removals = [];
                for (int index = 0; index < overflow; index++)
                {
                    removals.Add(snapshot.Items[index].Sequence);
                }

                XsrCollectionApplyResult result = _store.PublishDelta(
                    _entriesId,
                    new XsrCollectionDelta<LogEntry, long>(snapshot.Revision, [entry], removals));
                if (result.IsApplied)
                {
                    return;
                }
            }
        }
    }
}

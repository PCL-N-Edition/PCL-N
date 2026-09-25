using System.Text;
using System.Text.Json;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Telemetry;

/// <summary>One classified event; production transport enforces a fixed field allowlist.</summary>
public sealed record TelemetryEvent(
    string Name,
    DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, string> Properties)
{
    public TelemetryLevel Level { get; init; } = TelemetryLevel.Diagnostic;
}

/// <summary>
/// Upload port for telemetry batches. Implementations return whether the batch was accepted;
/// a rejected batch stays buffered.
/// </summary>
public interface ITelemetryTransport
{
    int MaximumBatchSize => int.MaxValue;
    Task<bool> SendAsync(IReadOnlyList<TelemetryEvent> batch, CancellationToken cancellationToken = default);
}

/// <summary>
/// Separately bounded necessary and diagnostic queues. Consent gates diagnostics only.
/// </summary>
public sealed class TelemetryService : IDisposable
{
    public const string OwnerName = "Nexa.Services.Telemetry";

    /// <summary>The pending-event count state key (single integer cell).</summary>
    public static readonly XsrSemanticId PendingKey = XsrSemanticId.Parse("telemetry.pending");

    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Queue<TelemetryEvent> _events;
    private readonly Queue<TelemetryEvent> _necessary = new();
    private CancellationTokenSource? _diagnosticConsent;
    private bool _disposed;
    private readonly XsrStateStore _store;
    private readonly XsrStateId _pendingId;
    private int _consentField;
    private bool _diagnosticsRequired;
    private int _flushing;

    /// <summary>
    /// Two-phase composition, declaration phase: registers the pending-count cell into the
    /// shared host builder.
    /// </summary>
    public static void DeclareState(XsrStateStoreBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Cell<int>(PendingKey, OwnerName);
    }

    public TelemetryService(XsrStateStore store, int capacity = 500)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _events = new Queue<TelemetryEvent>(capacity);
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _pendingId = _store.Resolve(PendingKey);
        _store.Publish(_pendingId, 0, CancellationToken.None);
    }

    public XsrStateStore StateStore => _store;

    internal void RequireDiagnostics()
    {
        lock (_gate)
        {
            _diagnosticsRequired = true;
            Consent = true;
        }
    }

    /// <summary>Effective diagnostic consent; prerelease policy cannot be disabled by callers.</summary>
    public bool Consent
    {
        get => Volatile.Read(ref _consentField) != 0;
        set
        {
            CancellationTokenSource? revoked = null;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                value |= _diagnosticsRequired;
                if (Consent == value) return;
                Volatile.Write(ref _consentField, value ? 1 : 0);
                if (!value)
                {
                    revoked = _diagnosticConsent;
                    _diagnosticConsent = null;
                    _events.Clear();
                    _store.Publish(_pendingId, _necessary.Count, CancellationToken.None);
                }
                else
                {
                    _diagnosticConsent = new();
                }
            }
            if (revoked is not null)
            {
                revoked.Cancel();
                revoked.Dispose();
            }
        }
    }

    /// <summary>How many events are buffered locally right now.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _events.Count + _necessary.Count;
            }
        }
    }

    /// <summary>
    /// Records one event when consent is granted; without consent this is a no-op. The buffer
    /// is bounded: the oldest event is dropped when capacity is reached.
    /// </summary>
    public void Record(string name, IReadOnlyDictionary<string, string>? properties = null)
        => RecordCore(name, properties, TelemetryLevel.Diagnostic);

    internal void RecordNecessary(string name, IReadOnlyDictionary<string, string> properties)
    {
        if (TelemetryEventCatalog.Level(name) != TelemetryLevel.Necessary)
            throw new ArgumentException("Event is not necessary telemetry.", nameof(name));
        RecordCore(name, properties, TelemetryLevel.Necessary);
    }

    private void RecordCore(string name, IReadOnlyDictionary<string, string>? properties, TelemetryLevel level)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (level == TelemetryLevel.Diagnostic && !Consent)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed) return;
            if (level == TelemetryLevel.Diagnostic && !Consent) return;
            var queue = level == TelemetryLevel.Necessary ? _necessary : _events;
            if (queue.Count >= _capacity)
            {
                queue.Dequeue();
            }

            queue.Enqueue(new TelemetryEvent(
                name,
                DateTimeOffset.UtcNow,
                new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
                    properties is null ? new(StringComparer.Ordinal) : new Dictionary<string, string>(properties, StringComparer.Ordinal)))
            { Level = level });
            _store.Publish(_pendingId, _events.Count + _necessary.Count, CancellationToken.None);
        }
    }

    /// <summary>
    /// Sends the buffered batch through the transport. On success the buffer clears and the
    /// count is how many events were uploaded; on rejection or empty buffer nothing changes
    /// and the count is zero.
    /// </summary>
    public async Task<int> FlushAsync(ITelemetryTransport transport, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (Interlocked.Exchange(ref _flushing, 1) != 0) return 0;
        try { return await FlushCoreAsync(transport, cancellationToken).ConfigureAwait(false); }
        finally { Volatile.Write(ref _flushing, 0); }
    }

    private async Task<int> FlushCoreAsync(ITelemetryTransport transport, CancellationToken cancellationToken)
    {
        List<TelemetryEvent> batch;
        CancellationTokenSource? consent = null;
        lock (_gate)
        {
            if (_disposed || _necessary.Count + _events.Count == 0)
            {
                return 0;
            }

            batch = [.. _necessary.Concat(_events).Take(Math.Max(1, transport.MaximumBatchSize))];
            if (batch.Any(item => item.Level == TelemetryLevel.Diagnostic))
                consent = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _diagnosticConsent!.Token);
        }
        using var consentLifetime = consent;
        if (!await transport.SendAsync(batch, consent?.Token ?? cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        lock (_gate)
        {
            // Only drop the events that were actually sent: records racing the flush stay.
            foreach (var queue in new[] { _necessary, _events })
            {
                var retained = queue.Where(item => !batch.Any(sent => ReferenceEquals(item, sent))).ToArray();
                queue.Clear();
                foreach (var item in retained) queue.Enqueue(item);
            }

            _store.Publish(_pendingId, _events.Count + _necessary.Count, CancellationToken.None);
            return batch.Count;
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? consent;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            consent = _diagnosticConsent;
            _diagnosticConsent = null;
            _events.Clear();
            _necessary.Clear();
        }
        consent?.Cancel();
        consent?.Dispose();
    }

    /// <summary>Serializes one batch into the wire JSON shape.</summary>
    public static string SerializeBatch(IReadOnlyList<TelemetryEvent> batch)
    {
        using MemoryStream stream = new();
        using Utf8JsonWriter writer = new(stream);
        writer.WriteStartArray();
        foreach (TelemetryEvent @event in batch)
        {
            writer.WriteStartObject();
            writer.WriteString("name", @event.Name);
            writer.WriteString("level", @event.Level == TelemetryLevel.Necessary ? "necessary" : "diagnostic");
            writer.WriteNumber("timestamp", @event.Timestamp.ToUnixTimeMilliseconds());
            writer.WriteStartObject("properties");
            foreach ((string key, string value) in @event.Properties.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WriteString(key, value);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

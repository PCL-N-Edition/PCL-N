using System.Text;
using System.Text.Json;
using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Services.Telemetry;

/// <summary>One opt-in telemetry event with its free-form properties.</summary>
public sealed record TelemetryEvent(
    string Name,
    DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, string> Properties);

/// <summary>
/// Upload port for telemetry batches. Implementations return whether the batch was accepted;
/// a rejected batch stays buffered.
/// </summary>
public interface ITelemetryTransport
{
    Task<bool> SendAsync(IReadOnlyList<TelemetryEvent> batch, CancellationToken cancellationToken = default);
}

/// <summary>
/// The telemetry capability: strictly opt-in event collection with a bounded local buffer and
/// an explicit flush. Without consent nothing is ever recorded — the legacy
/// `TelemetryExperienceProgram` default of false is a hard rule, not a default. The pending
/// count publishes as one state cell so surfaces read it like any other fact.
/// </summary>
public sealed class TelemetryService
{
    public const string OwnerName = "PCL.Services.Telemetry";

    /// <summary>The pending-event count state key (single integer cell).</summary>
    public static readonly XsrSemanticId PendingKey = XsrSemanticId.Parse("telemetry.pending");

    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Queue<TelemetryEvent> _events;
    private readonly XsrStateStore _store;
    private readonly XsrStateId _pendingId;
    private int _consentField;

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

    /// <summary>Whether the user granted telemetry consent. Defaults to false.</summary>
    public bool Consent
    {
        get => Volatile.Read(ref _consentField) != 0;
        set => Volatile.Write(ref _consentField, value ? 1 : 0);
    }

    /// <summary>How many events are buffered locally right now.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _events.Count;
            }
        }
    }

    /// <summary>
    /// Records one event when consent is granted; without consent this is a no-op. The buffer
    /// is bounded: the oldest event is dropped when capacity is reached.
    /// </summary>
    public void Record(string name, IReadOnlyDictionary<string, string>? properties = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Consent)
        {
            return;
        }

        lock (_gate)
        {
            if (_events.Count >= _capacity)
            {
                _events.Dequeue();
            }

            _events.Enqueue(new TelemetryEvent(
                name,
                DateTimeOffset.UtcNow,
                properties ?? new Dictionary<string, string>(StringComparer.Ordinal)));
            _store.Publish(_pendingId, _events.Count, CancellationToken.None);
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
        List<TelemetryEvent> batch;
        lock (_gate)
        {
            if (!Consent || _events.Count == 0)
            {
                return 0;
            }

            batch = [.. _events];
        }

        if (!await transport.SendAsync(batch, cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        lock (_gate)
        {
            // Only drop the events that were actually sent: records racing the flush stay.
            for (int index = 0; index < batch.Count && _events.Count > 0; index++)
            {
                if (ReferenceEquals(_events.Peek(), batch[index]))
                {
                    _events.Dequeue();
                }
                else
                {
                    break;
                }
            }

            _store.Publish(_pendingId, _events.Count, CancellationToken.None);
            return batch.Count;
        }
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

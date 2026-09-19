using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Capabilities;

public static class MachineCapabilityStateContract
{
    public static readonly XsrSemanticId RevisionKey = XsrSemanticId.Parse("machine.capabilities.revision");
    public static readonly XsrSemanticId SnapshotQuery = XsrSemanticId.Parse("machine.capabilities.query");
    public static readonly XsrSemanticId RefreshCommand = XsrSemanticId.Parse("machine.capabilities.refresh");
    public static readonly XsrSemanticId PreflightQuery = XsrSemanticId.Parse("machine.capabilities.preflight.query");
    public static readonly XsrSemanticId RemediationCommand = XsrSemanticId.Parse("machine.capabilities.remediation.execute");
    public static void DeclareState(XsrStateStoreBuilder builder) => builder.Cell<long>(RevisionKey, "Nexa.Services.Capabilities");
}
public sealed record MachineCapabilityQuery(string? InstanceDirectory = null, string? InstanceId = null)
{
    public bool HasInstanceScope => !string.IsNullOrWhiteSpace(InstanceDirectory) || !string.IsNullOrWhiteSpace(InstanceId);
}
public sealed record MachineCapabilityRefresh;
public sealed record LaunchPreflightQuery(string? InstanceDirectory = null, string? InstanceId = null);
public interface IMachineCapabilityProvider
{
    string Id { get; }
    ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, MachineCapabilityQuery query,
        CancellationToken cancellationToken) => CollectAsync(timestamp, cancellationToken);
}

/// <summary>Post-collection projection that may publish a coherent family of derived values.</summary>
public interface ICapabilityProjection
{
    IReadOnlyList<ICapability> Project(IReadOnlyDictionary<string, ICapability> values, DateTimeOffset timestamp);
}

/// <summary>One off-thread, bounded collection shared by all concurrent readers.</summary>
public sealed class MachineCapabilityBroker
{
    private readonly object _gate = new();
    private readonly CapabilityRegistry _registry;
    private readonly System.Collections.ObjectModel.ReadOnlyCollection<IMachineCapabilityProvider> _providers;
    private readonly XsrStateStore _store;
    private readonly XsrStateId _revisionId;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _timeout;
    private Task<MachineCapabilitySnapshot>? _pending;
    private MachineCapabilitySnapshot? _snapshot;
    private long _revision;
    private readonly System.Collections.ObjectModel.ReadOnlyCollection<ICapabilityDerivation> _derivations;
    private readonly System.Collections.ObjectModel.ReadOnlyCollection<ICapabilityProjection> _projections;

    public MachineCapabilityBroker(CapabilityRegistry registry, IEnumerable<IMachineCapabilityProvider> providers, XsrStateStore store,
        TimeProvider? clock = null, TimeSpan? timeout = null, IEnumerable<ICapabilityDerivation>? derivations = null,
        IEnumerable<ICapabilityProjection>? projections = null)
    {
        _registry = registry; _providers = Array.AsReadOnly(providers.ToArray()); _store = store;
        _derivations = Array.AsReadOnly((derivations ?? []).ToArray());
        _projections = Array.AsReadOnly((projections ?? []).ToArray());
        if (_providers.Select(provider => provider.Id).Distinct(StringComparer.Ordinal).Count() != _providers.Count)
            throw new ArgumentException("Duplicate machine provider.", nameof(providers));
        _revisionId = store.Resolve(MachineCapabilityStateContract.RevisionKey); _clock = clock ?? TimeProvider.System;
        _timeout = timeout ?? TimeSpan.FromSeconds(3);
    }
    public Task<MachineCapabilitySnapshot> ReadAsync(bool refresh = false, CancellationToken cancellationToken = default)
        => ReadAsync(new MachineCapabilityQuery(), refresh, cancellationToken);

    public Task<MachineCapabilitySnapshot> ReadAsync(MachineCapabilityQuery query, bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        if (query.HasInstanceScope)
            return Task.Run(() => CollectAsync(query, cacheResult: false), CancellationToken.None).WaitAsync(cancellationToken);
        Task<MachineCapabilitySnapshot> task;
        lock (_gate)
        {
            if (_pending is { IsCompleted: false }) task = _pending;
            else if (!refresh && _snapshot is { } cached && _clock.GetUtcNow() - cached.Timestamp < TimeSpan.FromSeconds(30)) task = Task.FromResult(cached);
            else task = _pending = Task.Run(() => CollectAsync(query, cacheResult: true), CancellationToken.None);
        }
        return task.WaitAsync(cancellationToken);
    }
    private async Task<MachineCapabilitySnapshot> CollectAsync(MachineCapabilityQuery query, bool cacheResult)
    {
        DateTimeOffset timestamp = _clock.GetUtcNow();
        var batches = await Task.WhenAll(_providers.Select(provider => CollectProviderAsync(provider, query, timestamp))).ConfigureAwait(false);
        var values = _registry.Definitions.ToDictionary(definition => definition.Id,
            definition => definition.Unavailable(CapabilityAvailability.NotImplemented, timestamp, "尚未接入检测提供方"), StringComparer.Ordinal);
        foreach (var batch in batches) foreach (var value in batch) values[value.Id] = value;
        // Derivation pass: cross-provider Derived capabilities compute AFTER collection, in
        // registry dependency order, so a rule may read facts from any provider.
        Dictionary<string, ICapabilityDerivation> rules = [];
        foreach (ICapabilityDerivation derivation in _derivations)
        {
            if (!rules.TryAdd(derivation.Id, derivation))
                throw new InvalidOperationException("Duplicate capability derivation: " + derivation.Id);
        }

        foreach (var definition in _registry.DependencyOrder)
        {
            if (rules.TryGetValue(definition.Id, out ICapabilityDerivation? derivation))
            {
                values[definition.Id] = derivation.Evaluate(values, timestamp);
            }
        }


        foreach (ICapabilityProjection projection in _projections)
        {
            foreach (ICapability value in projection.Project(values, timestamp))
            {
                if (!_registry.ById.TryGetValue(value.Id, out ICapabilityDefinition? definition)
                    || !definition.Accepts(value))
                    throw new InvalidOperationException("Projection returned undeclared or mismatched capability: " + value.Id);
                values[value.Id] = value;
            }
        }

        foreach (var definition in _registry.DependencyOrder)
            if (values[definition.Id].Availability == CapabilityAvailability.Available && definition.Requirements.Any(id => values[id].Availability != CapabilityAvailability.Available))
                values[definition.Id] = definition.Unavailable(CapabilityAvailability.DependencyMissing, timestamp, "所需能力尚不可用");
        MachineCapabilitySnapshot snapshot;
        lock (_gate)
        {
            snapshot = new(++_revision, timestamp, values.Values);
            if (cacheResult) _snapshot = snapshot;
        }
        _store.Publish(_revisionId, snapshot.Revision);
        return snapshot;
    }
    private async Task<IReadOnlyList<ICapability>> CollectProviderAsync(IMachineCapabilityProvider provider,
        MachineCapabilityQuery query, DateTimeOffset timestamp)
    {
        var owned = _registry.Definitions.Where(definition => definition.Provider == provider.Id).ToArray();
        using var timeout = new CancellationTokenSource(_timeout);
        try
        {
            var values = await Task.Run(async () => await provider.CollectAsync(timestamp, query, timeout.Token).ConfigureAwait(false), CancellationToken.None)
                .WaitAsync(_timeout).ConfigureAwait(false);
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (var value in values)
                if (!seen.Add(value.Id) || !_registry.ById.TryGetValue(value.Id, out var definition) || definition.Provider != provider.Id || !definition.Accepts(value))
                    throw new InvalidOperationException("Provider returned undeclared, duplicate or mismatched capability.");
            return values;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            timeout.Cancel();
            string reason = error is TimeoutException or OperationCanceledException ? "检测超时" : "检测失败：" + error.GetType().Name;
            return Array.AsReadOnly(owned.Select(definition => definition.Unavailable(CapabilityAvailability.TemporarilyUnavailable, timestamp, reason)).ToArray());
        }
    }
}

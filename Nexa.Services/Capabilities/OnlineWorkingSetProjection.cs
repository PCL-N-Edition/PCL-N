namespace Nexa.Services.Capabilities;

/// <summary>Pure cache projection; cannot issue requests or probe the game during rendering.</summary>
public sealed class OnlineWorkingSetProjection(OnlineWorkingSetModelStore store, string? operatingSystem = null) : ICapabilityProjection
{
    private readonly string _os = operatingSystem ?? (OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux");
    public IReadOnlyList<ICapability> Project(IReadOnlyDictionary<string, ICapability> values, DateTimeOffset timestamp) => [];

    public IReadOnlyList<ICapability> Project(IReadOnlyDictionary<string, ICapability> values, DateTimeOffset timestamp,
        MachineCapabilityQuery query)
    {
        if (!query.HasInstanceScope || query.PlannedHeapMiB is not { } heap || query.PlannedClasspathCount is not { } count
            || query.PlannedRenderDistance is not { } render || query.PlannedLoader is not { } loader
            || store.Read(timestamp) is not { } model
            || !model.TryPredict(_os, loader, heap, count, render, timestamp, out long prediction)) return [];
        long? Read(string key) => values.TryGetValue(key, out var value)
            && value is Capability<long> { Availability: CapabilityAvailability.Available } fact ? fact.Value : null;
        if (Read("estimate.physical.system_reserve") is not { } reserve || reserve < 0
            || Read("estimate.graphics.shared_system") is not { } graphics || graphics < 0) return [];
        List<ICapability> result = [];
        string source = $"在线工作集估算校准 · {model.GeneratedAt:yyyy-MM-dd} · 权重 25%";
        foreach (var definition in new[] { ResourceEstimateCatalog.PhysicalLaunch, ResourceEstimateCatalog.PhysicalRuntime })
        {
            if (Read(definition.Id) is not { } local || local <= reserve + graphics) continue;
            double process = local - reserve - graphics;
            long adjusted = (long)Math.Ceiling(process * .75 + Math.Clamp(prediction, process * .5, process * 1.5) * .25 + reserve + graphics);
            result.Add(definition.Observe(adjusted, timestamp, source, CapabilityConfidence.Low));
            if (Read("memory.physical.available") is { } available)
            {
                var margin = ResourceEstimateCatalog.LongDefinitions[definition.Id + "_margin"];
                result.Add(margin.Observe(Math.Max(0, available / (1024 * 1024) - adjusted), timestamp, source, CapabilityConfidence.Low));
            }
        }
        return result.AsReadOnly();
    }
}

/// <summary>Owns a background refresh loop. Disposal cancels it; resources are released only after
/// the in-flight refresh exits, without blocking the GUI thread on a network request.</summary>
public sealed class OnlineWorkingSetModelSession : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly Task _run;
    private bool _disposed;
    private bool _finished;
    public OnlineWorkingSetModelSession(HttpClient http, OnlineWorkingSetModelStore store)
    {
        _run = Task.Run(async () =>
        {
            using var client = new OnlineWorkingSetModelClient(http, store: store);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
            try
            {
                do { await client.RefreshAsync(_stop.Token).ConfigureAwait(false); }
                while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            finally { lock (_gate) { _finished = true; _stop.Dispose(); } }
        });
    }
    public Task Completion => _run;
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (!_finished) _stop.Cancel();
        }
    }
}

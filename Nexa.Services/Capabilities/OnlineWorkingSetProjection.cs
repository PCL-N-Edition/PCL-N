namespace Nexa.Services.Capabilities;

/// <summary>Pure cache projection; cannot issue requests or probe the game during rendering.</summary>
public sealed class OnlineWorkingSetProjection : ICapabilityProjection
{
    public OnlineWorkingSetProjection(OnlineWorkingSetModelStore store, string? operatingSystem = null)
    {
        ArgumentNullException.ThrowIfNull(store);
    }

    public IReadOnlyList<ICapability> Project(IReadOnlyDictionary<string, ICapability> values, DateTimeOffset timestamp) => [];

    // Schema 1 cannot distinguish optimization mods from terrain generators or workload phases.
    // Aggregate validation and clamping cannot make that prediction applicable to a given pack.
    // Preserve the local estimate until a mod/workload-aware contract passes admission tests.
    public IReadOnlyList<ICapability> Project(IReadOnlyDictionary<string, ICapability> values, DateTimeOffset timestamp,
        MachineCapabilityQuery query) => [];
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

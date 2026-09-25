using System.Diagnostics;
using System.Globalization;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Minecraft.Process;
using Nexa.Xsr.State;

namespace Nexa.Services.Telemetry;

public sealed partial class LauncherTelemetrySession
{
    private readonly HashSet<string> _features = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _observedJvm = [];
    private readonly HashSet<string> _observedSamples = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _observedContexts = [];
    private readonly Dictionary<InstallLoader, long> _catalogRevisions = [];
    private long _gameCatalogRevision = -1;
    private long _resourceTimestamp;
    private double _processorMilliseconds;
    private int _diagnosticBudget = 100;
    private readonly Dictionary<string, int> _metricSamples = new(StringComparer.Ordinal);
    private readonly HashSet<string> _errorSamples = new(StringComparer.Ordinal);

    public void OnOperation(string subsystem, TimeSpan duration, bool succeeded)
    {
        if (subsystem == "HTTP") RecordMetric("network.request.ms", duration.TotalMilliseconds, succeeded ? "ok" : "failed");
    }
    private bool TryDiagnostic()
    {
        if (_disposed || !_telemetry.Consent) return false;
        lock (_gate) { if (_diagnosticBudget <= 0) return false; _diagnosticBudget--; return true; }
    }
    public void RecordMetric(string metric, double value, string result = "ok")
    {
        if (_disposed || !_telemetry.Consent || !DiagnosticTelemetry.IsMetric(metric) || !double.IsFinite(value) || value < 0 || value > 1e12) return;
        lock (_gate)
        {
            int count = _metricSamples.GetValueOrDefault(metric);
            if (count >= 2 || _diagnosticBudget <= 0) return;
            _metricSamples[metric] = count + 1;
            _diagnosticBudget--;
        }
        RecordDetails("diagnostic.metric", result, new Dictionary<string, string> { ["metric"] = metric, ["value"] = value.ToString("G", CultureInfo.InvariantCulture) });
    }
    public void RecordFeature(string semantic)
    {
        if (_disposed || !_telemetry.Consent) return;
        string feature = DiagnosticTelemetry.Category(semantic);
        var details = new Dictionary<string, string> { ["feature"] = feature };
        lock (_gate) if (_features.Add(feature)) RecordDetails("feature.used", "ok", details);
        if (TryDiagnostic()) RecordDetails("feature.invoked", "ok", details);
    }
    public void RecordError(string category, string severity, string? detail)
    {
        if (_disposed || !_telemetry.Consent) return;
        var symbols = DiagnosticTelemetry.ErrorSymbols(detail);
        lock (_gate) { if (_errorSamples.Count >= 16 || !_errorSamples.Add(category + symbols.Code + symbols.Stack)) return; }
        RecordDetails("diagnostic.error", "failed", new Dictionary<string, string>
        {
            ["category"] = DiagnosticTelemetry.Category(category),
            ["severity"] = severity,
            ["code"] = symbols.Code,
            ["stack"] = symbols.Stack
        });
    }
    public void RecordOperation(string semantic, TimeSpan duration, bool succeeded, string? fault)
    {
        if (semantic.StartsWith("telemetry.", StringComparison.Ordinal) || semantic.StartsWith("logging.", StringComparison.Ordinal)) return;
        RecordMetric($"dispatch.{DiagnosticTelemetry.Category(semantic)}.ms", duration.TotalMilliseconds, succeeded ? "ok" : "failed");
        if (!succeeded) RecordError(semantic, "error", fault);
    }
    private void SampleResources()
    {
        lock (_gate) { _diagnosticBudget = 100; _metricSamples.Clear(); _errorSamples.Clear(); }
        if (_disposed || !_telemetry.Consent) return;
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            long now = Stopwatch.GetTimestamp(); double cpu = process.TotalProcessorTime.TotalMilliseconds;
            RecordMetric("launcher.working_set.mib", process.WorkingSet64 / 1048576d);
            RecordMetric("launcher.private.mib", process.PrivateMemorySize64 / 1048576d);
            RecordMetric("launcher.managed.mib", GC.GetTotalMemory(false) / 1048576d);
            if (_resourceTimestamp != 0)
                RecordMetric("launcher.cpu.percent", Math.Clamp((cpu - _processorMilliseconds) / Stopwatch.GetElapsedTime(_resourceTimestamp, now).TotalMilliseconds / Environment.ProcessorCount * 100, 0, 100));
            _resourceTimestamp = now; _processorMilliseconds = cpu;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }
    private void ObserveDiagnostics(XsrStateChange change)
    {
        if (_disposed) return;
        if (change.SemanticId == JvmHostStateContract.SamplesKey)
        {
            var samples = _telemetry.StateStore.ReadCollection<JvmRunSample>(change.Id);
            lock (_gate)
            {
                _observedSamples.RemoveWhere(key => !samples.Items.Any(item => item.Key == key));
                foreach (var sample in samples.Items)
                    if (_observedSamples.Add(sample.Key) && _telemetry.Consent)
                        RecordDetails("diagnostic.run", sample.Ended && sample.ExitCode is not (null or 0) ? "failed" : "ok", RunDiagnosticTelemetry.Sample(sample));
            }
        }
        if (change.SemanticId == JvmHostStateContract.ContextsKey)
        {
            var contexts = _telemetry.StateStore.ReadCollection<JvmRunContext>(change.Id);
            lock (_gate)
            {
                _observedContexts.RemoveWhere(key => !contexts.Items.Any(item => item.SessionId == key));
                foreach (var context in contexts.Items)
                    if (_observedContexts.Add(context.SessionId) && _telemetry.Consent)
                        foreach (var page in RunDiagnosticTelemetry.Inventory(context)) RecordDetails("diagnostic.mods", "ok", page);
            }
        }
        if (!_telemetry.Consent) return;
        if (change.SemanticId == JvmHostStateContract.ObservationsKey)
        {
            var snapshot = _telemetry.StateStore.ReadCollection<JvmHostObservation>(change.Id);
            lock (_gate)
            {
                _observedJvm.RemoveWhere(id => !snapshot.Items.Any(item => item.SessionId == id));
                foreach (var item in snapshot.Items)
                {
                    if (item.EndedAt is null || !_observedJvm.Add(item.SessionId)) continue;
                    RecordMetric("jvm.launch.ms", item.LaunchDurationMilliseconds);
                    RecordMetric("jvm.working_set.mib", item.PeakWorkingSetBytes / 1048576d);
                    if (item.ExitCode is not (null or 0)) RecordError("launch", "error", string.Join('\n', item.StderrTail.TakeLast(30)));
                }
            }
        }
        if (change.SemanticId == InstallCatalogStateContract.StateKey && _telemetry.StateStore.ReadAppliedValue(change.Id) is InstallCatalogState catalog)
        {
            lock (_gate) foreach (var item in catalog.Catalogs)
            {
                if (item.Loading) continue;
                long old = item.Loader is { } loader ? _catalogRevisions.GetValueOrDefault(loader, -1) : _gameCatalogRevision;
                if (item.Revision <= old) continue;
                if (item.Loader is { } current) _catalogRevisions[current] = item.Revision; else _gameCatalogRevision = item.Revision;
                if (item.CacheHit is { } hit) RecordMetric("catalog.cache.hit", hit ? 1 : 0);
                if (item.InputCount is { } count) RecordMetric("catalog.input.count", count);
                if (item.NormalizeMilliseconds is { } ms) RecordMetric("catalog.normalize.ms", ms);
                RecordMetric("catalog.results.count", item.Versions.Count, item.Error is null ? "ok" : "failed");
            }
        }
    }
}

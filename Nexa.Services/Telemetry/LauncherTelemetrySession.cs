using System.Runtime.InteropServices;
using Nexa.Services.Logging;
using Nexa.Services.Minecraft.Process;
using Nexa.Services.Settings;
using Nexa.Services.Tasks;
using Nexa.Xsr.State;

namespace Nexa.Services.Telemetry;

/// <summary>Session-local tiered upload lifetime. No stable installation identifier.</summary>
public sealed partial class LauncherTelemetrySession : IDisposable, IXsrStateObserver, ILogSink, ILogOperationSink
{
    private readonly TelemetryService _telemetry;
    private readonly SettingsService _settings;
    private readonly ITelemetryTransport _transport;
    private readonly LogService _log;
    private readonly string _version;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private bool _disposed;
    private readonly Dictionary<Guid, bool> _processes = [];
    private long _processRevision = -1;
    private readonly Dictionary<string, bool> _tasks = new(StringComparer.Ordinal);
    private long _taskRevision = -1;

    public LauncherTelemetrySession(TelemetryService telemetry, SettingsService settings,
        ITelemetryTransport transport, LogService log, string version)
    {
        _telemetry = telemetry; _settings = settings; _transport = transport; _log = log; _version = version.Split('+')[0];
        if (LauncherTelemetryPolicy.IsRequired(_version)) telemetry.RequireDiagnostics();
        settings.Changed += OnSettingsChanged;
        OnSettingsChanged(0);
        if (LauncherTelemetryPolicy.IsRequired(_version)) Record("diagnostic.session", "ok");
        Record("app.started", "ok");
        _ = UploadAsync();
    }

    private void OnSettingsChanged(long revision)
    {
        lock (_gate)
        {
            if (_disposed) return;
            bool consent = LauncherTelemetryPolicy.IsRequired(_version) || _settings.GetValue<bool>("TelemetryExperienceProgram").Value;
            bool starting = !_telemetry.Consent && consent;
            _telemetry.Consent = consent;
            if (starting) { _features.Clear(); Record("diagnostic.session", "ok"); }
        }
    }

    public void Record(string name, string result) => RecordDetails(name, result, null);

    private void RecordDetails(string name, string result, IReadOnlyDictionary<string, string>? details)
    {
        if (Volatile.Read(ref _disposed) || TelemetryEventCatalog.Level(name) is not { } level) return;
        var properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["version"] = _version,
            ["os"] = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
            ["arch"] = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            ["result"] = result,
        };
        if (details is not null) foreach (var pair in details) properties.Add(pair.Key, pair.Value);
        if (level == TelemetryLevel.Necessary) _telemetry.RecordNecessary(name, properties);
        else _telemetry.Record(name, properties);
    }

    public void Write(LogEntry entry, string formattedLine)
    {
        // Symbol-only diagnostic errors; free-form log bodies remain local.
        if (entry.Module == "Telemetry") return;
        if (entry.Level == LogLevel.Error) Record("app.failure", "failed");
        if (entry.Level is LogLevel.Error or LogLevel.Warn) RecordError(entry.Module, entry.Level == LogLevel.Error ? "error" : "warning", entry.ExceptionText);
    }

    public void OnChanged(XsrStateChange change)
    {
        ObserveDiagnostics(change);
        if (change.SemanticId == TaskCenterStateContract.EntriesKey)
        {
            var tasks = _telemetry.StateStore.ReadCollection<TaskCenterEntry>(change.Id);
            lock (_gate)
            {
                if (_disposed || tasks.Revision <= _taskRevision) return;
                _taskRevision = tasks.Revision;
                var present = tasks.Items.Select(item => item.TaskId).ToHashSet(StringComparer.Ordinal);
                foreach (var id in _tasks.Keys.Where(id => !present.Contains(id)).ToArray()) _tasks.Remove(id);
                foreach (var task in tasks.Items)
                {
                    bool found = _tasks.TryGetValue(task.TaskId, out bool ended);
                    if (!found || (ended && !task.IsTerminal))
                    {
                        _tasks[task.TaskId] = false;
                        ended = false;
                        Record("task.started", "ok");
                    }
                    if (ended || !task.IsTerminal) continue;
                    _tasks[task.TaskId] = true;
                    Record("task.finished", task.State switch
                    {
                        TaskCenterEntryState.Finished => "ok",
                        TaskCenterEntryState.Canceled => "cancelled",
                        _ => "failed"
                    });
                }
            }
            return;
        }
        if (change.SemanticId != MinecraftProcessStateComposition.SessionsKey || Volatile.Read(ref _disposed)) return;
        var snapshot = _telemetry.StateStore.ReadCollection<MinecraftProcessSnapshot>(change.Id);
        lock (_gate)
        {
            if (_disposed || snapshot.Revision <= _processRevision) return;
            _processRevision = snapshot.Revision;
            var present = snapshot.Items.Select(item => item.SessionId).ToHashSet();
            foreach (var id in _processes.Keys.Where(id => !present.Contains(id)).ToArray()) _processes.Remove(id);
            foreach (var process in snapshot.Items)
            {
                if (!_processes.TryGetValue(process.SessionId, out bool ended))
                {
                    _processes.Add(process.SessionId, false);
                    // This is process creation, not a claim that a game window is visible.
                    Record("game.started", "ok");
                }
                if (ended || process.State is MinecraftProcessState.Created or MinecraftProcessState.Running) continue;
                _processes[process.SessionId] = true;
                Record("game.exited", process.State switch
                {
                    MinecraftProcessState.Cancelled => "cancelled",
                    MinecraftProcessState.Failed => "failed",
                    _ => process.ExitCode == 0 ? "ok" : "unknown",
                });
            }
        }
    }

    private async Task UploadAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            do
            {
                lock (_gate) { if (_disposed) return; }
                SampleResources();
                try
                {
                    for (int batch = 0; batch < 10; batch++)
                        if (await _telemetry.FlushAsync(_transport, _stop.Token).ConfigureAwait(false) == 0) break;
                }
                catch (OperationCanceledException) { }
                catch (Exception error) when (error is HttpRequestException or IOException)
                { _log.Debug("Telemetry", "遥测暂未发送，将在下个周期重试。"); }
            } while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _settings.Changed -= OnSettingsChanged;
            _stop.Cancel();
        }
    }
}

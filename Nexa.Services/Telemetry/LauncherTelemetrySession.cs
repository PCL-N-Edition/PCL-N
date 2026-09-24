using System.Runtime.InteropServices;
using Nexa.Services.Logging;
using Nexa.Services.Settings;

namespace Nexa.Services.Telemetry;

/// <summary>Session-local, opt-in upload lifetime. No stable installation identifier.</summary>
public sealed class LauncherTelemetrySession : IDisposable
{
    private readonly TelemetryService _telemetry;
    private readonly SettingsService _settings;
    private readonly ITelemetryTransport _transport;
    private readonly LogService _log;
    private readonly string _version;
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource _consent = new();
    private readonly object _gate = new();
    private bool _disposed;

    public LauncherTelemetrySession(TelemetryService telemetry, SettingsService settings,
        ITelemetryTransport transport, LogService log, string version)
    {
        _telemetry = telemetry; _settings = settings; _transport = transport; _log = log; _version = version.Split('+')[0];
        settings.Changed += OnSettingsChanged;
        OnSettingsChanged(0);
        _ = UploadAsync();
    }

    private void OnSettingsChanged(long revision)
    {
        lock (_gate)
        {
            if (_disposed) return;
            bool consent = LauncherTelemetryPolicy.IsRequired(_version) || _settings.GetValue<bool>("TelemetryExperienceProgram").Value;
            bool previous = _telemetry.Consent;
            _telemetry.Consent = consent;
            if (!consent) _consent.Cancel();
            else if (!previous)
            {
                _consent.Dispose(); _consent = new();
                Record("app.started", "ok");
            }
        }
    }

    public void Record(string name, string result)
    {
        if (!_telemetry.Consent) return;
        _telemetry.Record(name, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["version"] = _version,
            ["os"] = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
            ["arch"] = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            ["result"] = result,
        });
    }

    private async Task UploadAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            do
            {
                CancellationToken consent;
                lock (_gate) { if (_disposed) return; consent = _consent.Token; }
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, consent);
                try { await _telemetry.FlushAsync(_transport, linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
                catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
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
            _stop.Cancel(); _consent.Cancel();
        }
    }
}

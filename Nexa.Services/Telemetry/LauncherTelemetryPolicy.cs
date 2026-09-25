using Nexa.Services.Updates;

namespace Nexa.Services.Telemetry;

public static class LauncherTelemetryPolicy
{
    public static bool IsRequired(string? version) => LauncherBuildIdentity.Parse(version ?? "").DiagnosticsRequired;
}

using Nexa.Services.Updates;

namespace Nexa.Services.Telemetry;

public static class LauncherTelemetryPolicy
{
    public static bool IsRequired(string? version) => UpdateVersion.TryParse(version?.Split('+')[0], out var parsed)
        && parsed.Major == 2 && parsed.Stage is UpdateVersionStage.Alpha or UpdateVersionStage.Beta or UpdateVersionStage.Ci;
}

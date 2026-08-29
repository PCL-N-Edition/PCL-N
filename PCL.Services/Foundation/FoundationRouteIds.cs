using PCL.Xsr;

namespace PCL.Services.Foundation;

/// <summary>
/// Stable semantic identifiers for the Foundation command/query surface. The service assembly
/// owns these identifiers and handler contracts; the runtime composition layer only binds them
/// to routers.
/// </summary>
public static class FoundationRouteIds
{
    public static readonly XsrSemanticId SettingsSet = XsrSemanticId.Parse("settings.set");

    public static readonly XsrSemanticId SettingsGet = XsrSemanticId.Parse("settings.get");

    public static readonly XsrSemanticId TelemetryConsent = XsrSemanticId.Parse("telemetry.consent");

    public static readonly XsrSemanticId AccountUpsertProfile = XsrSemanticId.Parse("accounts.upsert-profile");
}

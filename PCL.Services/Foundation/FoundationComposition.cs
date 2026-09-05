using PCL.Services.Accounts;
using PCL.Services.Downloads;
using PCL.Services.Files;
using PCL.Services.Logging;
using PCL.Services.Minecraft.Process;
using PCL.Services.Settings;
using PCL.Services.Telemetry;
using PCL.Xsr.State;

namespace PCL.Services.Foundation;

/// <summary>
/// The two-phase host state composition. Phase one: every foundation module declares its
/// state entries into ONE shared builder, so the built store is the single host public state
/// — no per-service stores, no identifier collisions between capabilities. Phase two: the
/// built store is injected into each service constructor, which resolves its own keys and
/// becomes the single writer of its entries.
/// </summary>
public static class FoundationState
{
    /// <summary>
    /// Creates the shared builder with every foundation module's declarations. The settings
    /// schema is runtime-configured, so it is declared explicitly; pass null to compose
    /// without settings.
    /// </summary>
    public static XsrStateStoreBuilder CreateBuilder(SettingsSchema? settingsSchema = null)
    {
        XsrStateStoreBuilder builder = new();
        if (settingsSchema is not null)
        {
            SettingsService.DeclareState(builder, settingsSchema);
        }

        LogService.DeclareState(builder);
        DownloadService.DeclareState(builder);
        AccountService.DeclareState(builder);
        AccountOnboardingState.DeclareState(builder);
        AccountSkinService.DeclareState(builder);
        TelemetryService.DeclareState(builder);
        MinecraftProcessStateComposition.DeclareState(builder);
        Minecraft.Launch.MinecraftLaunchProgressState.DeclareState(builder);
        Minecraft.MinecraftLibraryService.DeclareState(builder);
        return builder;
    }
}

/// <summary>
/// The composed foundation: one shared state store plus the services built over it. The UI
/// state bridge is the store observer, so every foundation publication reaches the renderer
/// drain without extra wiring.
/// </summary>
public sealed class FoundationHost
{
    private readonly IReadOnlyList<object> _services;

    internal FoundationHost(
        XsrStateStore stateStore,
        LogService logging,
        DownloadService downloads,
        AccountService accounts,
        TelemetryService telemetry,
        SettingsService settings)
    {
        StateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        Downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        Accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        Telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _services = Array.AsReadOnly<object>([Logging, Downloads, Accounts, Telemetry, Settings]);
    }

    public XsrStateStore StateStore { get; }

    public LogService Logging { get; }

    public DownloadService Downloads { get; }

    public AccountService Accounts { get; }

    public TelemetryService Telemetry { get; }

    public SettingsService Settings { get; }

    /// <summary>Registered services in activation order (for composition diagnostics).</summary>
    public IReadOnlyList<object> Services => _services;

}

/// <summary>
/// Builds the foundation host in the locked two-phase order: declare all state, build the
/// store once, then construct every service over the shared store.
/// </summary>
public static class FoundationComposer
{
    public static FoundationHost Compose(
        ISettingsPort settingsPort,
        SettingsSchema settingsSchema,
        ILaunchProfilePort profilePort,
        IXsrStateObserver? observer = null,
        TimeProvider? clock = null,
        int logCapacity = 2_000,
        int downloadBufferSize = 128 * 1024,
        long minimumSegmentBytes = 8 * 1024 * 1024,
        int telemetryCapacity = 500,
        Action<XsrStateStoreBuilder>? declareHostState = null,
        Action<LogService>? configureLogging = null)
    {
        ArgumentNullException.ThrowIfNull(settingsPort);
        ArgumentNullException.ThrowIfNull(settingsSchema);
        ArgumentNullException.ThrowIfNull(profilePort);

        XsrStateStoreBuilder builder = FoundationState.CreateBuilder(settingsSchema);
        declareHostState?.Invoke(builder);
        XsrStateStore store = builder.Build(observer);

        var logging = new LogService(store, logCapacity, clock);
        // Sinks and observers must be attached before constructors read persisted data.
        configureLogging?.Invoke(logging);
        var downloads = new DownloadService(store, downloadBufferSize, logging, minimumSegmentBytes);
        var accounts = new AccountService(store, profilePort, logging);
        var telemetry = new TelemetryService(store, telemetryCapacity);
        var settings = new SettingsService(store, settingsSchema, settingsPort, logging);

        return new FoundationHost(store, logging, downloads, accounts, telemetry, settings);
    }
}

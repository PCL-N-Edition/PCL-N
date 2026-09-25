using Nexa.Services.Accounts;
using Nexa.Services.Capabilities;
using Nexa.Services.Downloads;
using Nexa.Services.Files;
using Nexa.Services.Logging;
using Nexa.Services.Minecraft.Process;
using Nexa.Services.Settings;
using Nexa.Services.Tasks;
using Nexa.Services.Telemetry;
using Nexa.Xsr.State;

namespace Nexa.Services.Foundation;

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
        SettingsPolicyContract.DeclareState(builder);
        MachineCapabilityStateContract.DeclareState(builder);
        DownloadService.DeclareState(builder);
        TaskCenterStateContract.DeclareState(builder);
        AccountService.DeclareState(builder);
        AccountOnboardingState.DeclareState(builder);
        AccountSkinService.DeclareState(builder);
        TelemetryService.DeclareState(builder);
        Rollouts.RolloutStateContract.DeclareState(builder);
        MinecraftProcessStateComposition.DeclareState(builder);
        Minecraft.Launch.MinecraftLaunchProgressState.DeclareState(builder);
        Minecraft.MinecraftLibraryService.DeclareState(builder);
        Minecraft.Install.InstallCatalogStateContract.DeclareState(builder);
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
        SettingsService settings,
        TaskCenterService tasks,
        string? minecraftRootDirectory = null,
        IEnumerable<IRemediationHandler>? remediationHandlers = null)
    {
        StateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        Downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        Accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        Telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        SettingsPolicy = new SettingsPolicyService(Settings);
        InputUsage = new InputUsageTracker();
        ObservationHistory = new ResourceObservationHistory();
        IRemediationHandler[] configuredRemediations = [.. remediationHandlers ?? []];
        HashSet<string> configuredRemediationIds = configuredRemediations
            .Select(static handler => handler.Id).ToHashSet(StringComparer.Ordinal);
        Remediations = new RemediationService(CoreRemediationHandlers.Create(SettingsPolicy)
            .Where(handler => !configuredRemediationIds.Contains(handler.Id))
            .Concat(configuredRemediations));
        // The full environment registry: machine facts plus the display/storage/filesystem/
        // power and java/minecraft.files namespaces. Instance-scoped providers bind to the
        // active Minecraft root so storage and file-integrity facts answer for THAT path.
        CapabilityRegistry capabilityRegistry = new CapabilityRegistry(
        [
            .. ModCatalog.Definitions(),
            .. FormFactorCatalog.Definitions.Values,
            .. InputCatalog.Definitions(),
            .. ResourceEstimateCatalog.Definitions(),
            .. PreflightCatalog.Definitions(),
            .. LaunchPolicyCatalog.Definitions(),
            .. RemediationCatalog.Definitions(),
            .. MachineDerivedRules.Definitions(),
            .. MachineHardwareCatalog.MergeInto(
                MachineInstanceCatalog.MergeInto(
                    MachineEnvironmentCatalog.MergeInto(MachineCapabilityCatalog.CreateRegistry()))).Definitions,
        ]);
        List<IMachineCapabilityProvider> capabilityProviders = [.. MachineCapabilityCatalog.CreateProviders(),
            new DisplayCapabilityProvider(),
            new StorageCapabilityProvider(minecraftRootDirectory),
            new FilesystemCapabilityProvider(minecraftRootDirectory),
            new JavaEnvironmentCapabilityProvider(javaLocator: null, minecraftRootDirectory),
            new GpuCapabilityProvider(),
            new ThermalCapabilityProvider(),
            new HardwarePowerCapabilityProvider(),
            new MinecraftEnvironmentCapabilityProvider(minecraftRootDirectory),
            new LoaderCapabilityProvider(minecraftRootDirectory),
            new ModCapabilityProvider(minecraftRootDirectory),
            new AccountCapabilityProvider(accounts),
            new FormFactorCapabilityProvider(),
            new InputCapabilityProvider(InputUsage),
            new RemediationCapabilityProvider(Remediations)];

        // The Java provider probes each runtime with one `java -version` process; the
        // registry default window (3s) times the whole java namespace out on machines with
        // several runtimes. 45s keeps the first refresh honest; the locator's process-wide
        // cache makes every later refresh instant.
        MachineCapabilities = new MachineCapabilityBroker(
            capabilityRegistry, capabilityProviders, StateStore,
            timeout: TimeSpan.FromSeconds(45),
            derivations: MachineDerivedRules.Defaults(),
            projections: [new ResourceEstimatorProjection(history: ObservationHistory), new LaunchPolicyProjection(), new PreflightProjection()]);
        ResourceEstimator = new ResourceEstimator(history: ObservationHistory);
        Preflight = new CapabilityPreflightEngine();
        Tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _services = Array.AsReadOnly<object>([Logging, Downloads, Accounts, Telemetry, Settings, SettingsPolicy, Tasks,
            InputUsage, ObservationHistory, MachineCapabilities, ResourceEstimator, Preflight, Remediations]);
    }

    public XsrStateStore StateStore { get; }

    public LogService Logging { get; }

    public DownloadService Downloads { get; }

    public AccountService Accounts { get; }

    public TelemetryService Telemetry { get; }

    public SettingsService Settings { get; }
    public SettingsPolicyService SettingsPolicy { get; }

    public TaskCenterService Tasks { get; }
    public MachineCapabilityBroker MachineCapabilities { get; }
    public InputUsageTracker InputUsage { get; }
    public ResourceEstimator ResourceEstimator { get; }
    public CapabilityPreflightEngine Preflight { get; }
    public ResourceObservationHistory ObservationHistory { get; }
    public RemediationService Remediations { get; }

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
        Action<LogService>? configureLogging = null,
        string? minecraftRootDirectory = null,
        IEnumerable<IRemediationHandler>? remediationHandlers = null)
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
        var tasks = new TaskCenterService(store);

        return new FoundationHost(store, logging, downloads, accounts, telemetry, settings, tasks,
            minecraftRootDirectory, remediationHandlers);
    }
}

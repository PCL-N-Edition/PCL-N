using PCL.Services.Foundation;
using PCL.Services.Minecraft;
using PCL.Services.Minecraft.Java;
using PCL.Services.Minecraft.Launch;
using PCL.Services.Minecraft.Process;
using PCL.Xsr.Runtime;
using PCL.Xsr.State;

namespace PCL.Services.Composition;

/// <summary>Runtime router composition for the Minecraft core capability.</summary>
public sealed class MinecraftRuntime : IDisposable
{
    private readonly IReadOnlyList<IDisposable> _ownedResources;

    public MinecraftRuntime(
        MinecraftVersionDiscovery discovery,
        MinecraftInstanceDiscovery instances,
        MinecraftProcessService processes,
        XsrCommandRouter commands,
        XsrQueryRouter queries,
        MinecraftLaunchCoordinator? launchCoordinator = null,
        IReadOnlyList<IDisposable>? ownedResources = null)
    {
        Discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        Instances = instances ?? throw new ArgumentNullException(nameof(instances));
        Processes = processes ?? throw new ArgumentNullException(nameof(processes));
        Commands = commands ?? throw new ArgumentNullException(nameof(commands));
        Queries = queries ?? throw new ArgumentNullException(nameof(queries));
        LaunchCoordinator = launchCoordinator;
        _ownedResources = ownedResources ?? [];
    }

    public MinecraftVersionDiscovery Discovery { get; }
    public MinecraftInstanceDiscovery Instances { get; }
    public MinecraftProcessService Processes { get; }
    public XsrCommandRouter Commands { get; }
    public XsrQueryRouter Queries { get; }

    /// <summary>The production product-level launch coordinator, absent in core-only tests.</summary>
    public MinecraftLaunchCoordinator? LaunchCoordinator { get; }

    public void Dispose()
    {
        foreach (IDisposable resource in _ownedResources.Reverse())
        {
            resource.Dispose();
        }
    }
}

public static class MinecraftRuntimeComposer
{
    private sealed class NullDispatchObserver : IXsrDispatchObserver
    {
        public static readonly NullDispatchObserver Instance = new();
        public void OnCompleted(XsrDispatchObservation observation) { }
    }

    public static MinecraftRuntime Compose(
        MinecraftVersionDiscovery? discovery = null,
        MinecraftProcessService? processes = null,
        IXsrDispatchObserver? observer = null,
        TimeProvider? timeProvider = null,
        XsrStateStore? hostStore = null)
    {
        MinecraftVersionDiscovery versionDiscovery = discovery ?? new MinecraftVersionDiscovery();
        MinecraftInstanceDiscovery instanceDiscovery = new(versionDiscovery: versionDiscovery);
        MinecraftProcessService processService = processes ?? new MinecraftProcessService(hostStore: hostStore);
        if (hostStore is not null && !ReferenceEquals(hostStore, processService.StateStore))
            throw new ArgumentException("The Minecraft process service must publish into the supplied host state store.", nameof(processes));
        IXsrDispatchObserver dispatchObserver = observer ?? NullDispatchObserver.Instance;
        XsrCommandRouterBuilder commandBuilder = new();
        commandBuilder.Register(MinecraftRouteIds.Launch, MinecraftCommands.CreateLaunchHandler(processService));
        commandBuilder.Register(MinecraftRouteIds.ProcessCancel, MinecraftCommands.CreateCancelProcessHandler(processService));
        XsrQueryRouterBuilder queryBuilder = new();
        queryBuilder.Register(MinecraftRouteIds.VersionsRead, MinecraftQueries.CreateVersionsHandler(versionDiscovery));
        queryBuilder.Register(MinecraftRouteIds.InstancesRead, MinecraftQueries.CreateInstancesHandler(instanceDiscovery));
        queryBuilder.Register(MinecraftRouteIds.CrashAnalyze, MinecraftQueries.CreateCrashHandler());
        return new MinecraftRuntime(
            versionDiscovery,
            instanceDiscovery,
            processService,
            commandBuilder.Build(dispatchObserver, timeProvider),
            queryBuilder.Build(dispatchObserver, timeProvider));
    }

    /// <summary>
    /// Production composition: registers the product-level start command after accounts,
    /// settings, Java resolution/acquisition, platform facts, and the shared process store are
    /// available. Desktop supplies identifiers only and cannot construct a low-level request.
    /// </summary>
    public static MinecraftRuntime Compose(
        FoundationHost host,
        string minecraftRootDirectory,
        string? javaRuntimeRootDirectory = null,
        MinecraftVersionDiscovery? discovery = null,
        MinecraftProcessService? processes = null,
        IJavaRuntimeLocator? javaLocator = null,
        IJavaRuntimeInstaller? javaInstaller = null,
        MinecraftLaunchPlatform? platform = null,
        IXsrDispatchObserver? observer = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraftRootDirectory);
        string runtimeRoot = string.IsNullOrWhiteSpace(javaRuntimeRootDirectory)
            ? Path.Combine(Path.GetFullPath(minecraftRootDirectory), "runtime")
            : Path.GetFullPath(javaRuntimeRootDirectory);

        MinecraftVersionDiscovery versionDiscovery = discovery ?? new MinecraftVersionDiscovery(host.Logging);
        MinecraftInstanceDiscovery instanceDiscovery = new(host.Logging, versionDiscovery);
        MinecraftProcessService processService = processes ?? new MinecraftProcessService(hostStore: host.StateStore, log: host.Logging);
        if (!ReferenceEquals(host.StateStore, processService.StateStore))
        {
            throw new ArgumentException(
                "The Minecraft process service must publish into the Foundation host state store.",
                nameof(processes));
        }

        List<IDisposable> owned = [];
        IJavaRuntimeLocator locator = javaLocator ?? new LocalJavaRuntimeLocator(runtimeRoot, host.Logging);
        IJavaRuntimeInstaller installer;
        if (javaInstaller is null)
        {
            HttpJavaRuntimeMetadataProvider metadata = new(host.Logging);
            JavaRuntimeInstaller concreteInstaller = new(metadata, host.Logging);
            owned.Add(metadata);
            owned.Add(concreteInstaller);
            installer = concreteInstaller;
        }
        else
        {
            installer = javaInstaller;
        }

        MinecraftLaunchExecutor executor = new(processService, host.Logging);
        MinecraftLaunchCoordinator coordinator = new(
            minecraftRootDirectory,
            runtimeRoot,
            instanceDiscovery,
            host.Accounts,
            host.Settings,
            new JavaSelectionService(locator),
            installer,
            executor,
            platform,
            host.Logging,
            new MinecraftLaunchProgressPublisher(host.StateStore));
        IXsrDispatchObserver dispatchObserver = observer ?? NullDispatchObserver.Instance;
        XsrCommandRouterBuilder commandBuilder = new();
        commandBuilder.Register(MinecraftRouteIds.Start, MinecraftCommands.CreateStartHandler(coordinator));
        commandBuilder.Register(MinecraftRouteIds.Launch, MinecraftCommands.CreateLaunchHandler(executor));
        commandBuilder.Register(MinecraftRouteIds.LaunchCancel, MinecraftCommands.CreateCancelLaunchHandler(coordinator));
        commandBuilder.Register(MinecraftRouteIds.AcquireDecide, MinecraftCommands.CreateAcquireDecideHandler(coordinator));
        commandBuilder.Register(MinecraftRouteIds.ProcessCancel, MinecraftCommands.CreateCancelProcessHandler(processService));
        XsrQueryRouterBuilder queryBuilder = new();
        queryBuilder.Register(MinecraftRouteIds.VersionsRead, MinecraftQueries.CreateVersionsHandler(versionDiscovery));
        queryBuilder.Register(MinecraftRouteIds.InstancesRead, MinecraftQueries.CreateInstancesHandler(instanceDiscovery));
        queryBuilder.Register(MinecraftRouteIds.CrashAnalyze, MinecraftQueries.CreateCrashHandler());
        return new MinecraftRuntime(
            versionDiscovery,
            instanceDiscovery,
            processService,
            commandBuilder.Build(dispatchObserver, timeProvider),
            queryBuilder.Build(dispatchObserver, timeProvider),
            coordinator,
            owned);
    }
}

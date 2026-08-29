using PCL.Services.Minecraft;
using PCL.Services.Minecraft.Process;
using PCL.Xsr.Runtime;

namespace PCL.Services.Composition;

/// <summary>Runtime router composition for the Minecraft core capability.</summary>
public sealed class MinecraftRuntime
{
    public MinecraftRuntime(
        MinecraftVersionDiscovery discovery,
        MinecraftInstanceDiscovery instances,
        MinecraftProcessService processes,
        XsrCommandRouter commands,
        XsrQueryRouter queries)
    {
        Discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        Instances = instances ?? throw new ArgumentNullException(nameof(instances));
        Processes = processes ?? throw new ArgumentNullException(nameof(processes));
        Commands = commands ?? throw new ArgumentNullException(nameof(commands));
        Queries = queries ?? throw new ArgumentNullException(nameof(queries));
    }

    public MinecraftVersionDiscovery Discovery { get; }
    public MinecraftInstanceDiscovery Instances { get; }
    public MinecraftProcessService Processes { get; }
    public XsrCommandRouter Commands { get; }
    public XsrQueryRouter Queries { get; }
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
        TimeProvider? timeProvider = null)
    {
        MinecraftVersionDiscovery versionDiscovery = discovery ?? new MinecraftVersionDiscovery();
        MinecraftInstanceDiscovery instanceDiscovery = new(versionDiscovery);
        MinecraftProcessService processService = processes ?? new MinecraftProcessService();
        IXsrDispatchObserver dispatchObserver = observer ?? NullDispatchObserver.Instance;
        XsrCommandRouterBuilder commandBuilder = new();
        commandBuilder.Register(MinecraftRouteIds.Launch, MinecraftCommands.CreateLaunchHandler(processService));
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
}

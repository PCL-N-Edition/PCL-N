using Nexa.Services.Foundation;
using Nexa.Services.Minecraft.Install;
using Nexa.Xsr.Runtime;

namespace Nexa.Services.Composition;

public sealed class InstallCatalogRuntime(InstallCatalogService service, XsrCommandRouter commands, XsrQueryRouter queries, HttpClient? ownedHttp) : IDisposable
{
    public XsrCommandRouter Commands { get; } = commands;
    public XsrQueryRouter Queries { get; } = queries;
    public void Dispose() { service.Dispose(); ownedHttp?.Dispose(); }
}
public static class InstallCatalogRuntimeComposer
{
    public static InstallCatalogRuntime Compose(FoundationHost host, IInstallCatalogSource? source = null, IXsrDispatchObserver? observer = null)
    {
        HttpClient? http = source is null ? new HttpClient() : null;
        InstallCatalogService service = new(host.StateStore, source ?? new HttpInstallCatalogSource(http!));
        XsrCommandRouterBuilder commands = new();
        commands.Register<InstallCatalogReadCommand>(InstallCatalogRoutes.Read, async (command, token) => await service.ReadAsync(command, token).ConfigureAwait(false));
        commands.Register<InstallCatalogPrefetchCommand>(InstallCatalogRoutes.Prefetch, async (command, token) => await service.PrefetchAsync(command, token).ConfigureAwait(false));
        XsrQueryRouterBuilder queries = new();
        queries.Register<InstallEligibilityQuery, InstallEligibilityResult>(InstallEligibilityContract.Query,
            (query, token) => ValueTask.FromResult(Nexa.Xsr.XsrResult.Success(service.Evaluate(query))));
        queries.Register<MinecraftInstallEditQuery, MinecraftInstallEditSnapshot>(MinecraftInstallEditContract.Query,
            async (query, token) => Nexa.Xsr.XsrResult.Success(await MinecraftInstallEditService.ReadAsync(query, token).ConfigureAwait(false)));
        queries.Register<MinecraftInstallEditPlanQuery, MinecraftInstallEditPlan>(MinecraftInstallEditPlanContract.Query,
            (query, token) => ValueTask.FromResult(Nexa.Xsr.XsrResult.Success(MinecraftInstallEditPlanner.Evaluate(query))));
        var dispatchObserver = observer ?? new Observer();
        return new(service, commands.Build(dispatchObserver), queries.Build(dispatchObserver), http);
    }
    private sealed class Observer : IXsrDispatchObserver { public void OnCompleted(XsrDispatchObservation observation) { } }
}

using Nexa.Services.Resources;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Services.Composition;

public sealed class ResourceCatalogRuntime(XsrQueryRouter queries, HttpClient? ownedHttp) : IDisposable
{
    public XsrQueryRouter Queries { get; } = queries;
    public void Dispose() => ownedHttp?.Dispose();
}

public static class ResourceCatalogRuntimeComposer
{
    public static ResourceCatalogRuntime Compose(IResourceCatalogSource? source = null, IXsrDispatchObserver? observer = null)
    {
        HttpClient? http = source is null ? new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) } : null;
        source ??= new ResourceCatalogService(http!);
        XsrQueryRouterBuilder queries = new();
        queries.Register<ResourceSearchQuery, ResourceSearchResult>(ResourceCatalogContract.Search,
            async (query, token) => XsrResult.Success(await source.SearchAsync(query, token).ConfigureAwait(false)));
        queries.Register<ResourceDetailQuery, ResourceDetail>(ResourceCatalogContract.Detail,
            async (query, token) => XsrResult.Success(await source.DetailAsync(query, token).ConfigureAwait(false)));
        return new(queries.Build(observer ?? new Observer()), http);
    }
    private sealed class Observer : IXsrDispatchObserver { public void OnCompleted(XsrDispatchObservation observation) { } }
}

using Nexa.Xsr;

namespace Nexa.Services.Resources;

public static class ResourceCatalogContract
{
    public static readonly XsrSemanticId Search = XsrSemanticId.Parse("resources.catalog.search");
    public static readonly XsrSemanticId Detail = XsrSemanticId.Parse("resources.catalog.detail");
    public const int PageSize = 20;
}

public enum ResourceKind { Mod, Modpack, ResourcePack, Shader, DataPack }
public enum ResourceOrder { Relevance, Downloads, Updated }
public sealed record ResourceSearchQuery(ResourceKind Kind = ResourceKind.Mod, string Text = "",
    string GameVersion = "", string Loader = "", ResourceOrder Order = ResourceOrder.Relevance, int Page = 0);
public sealed record ResourceProject(string Id, string Title, string Description, string Author,
    long Downloads, string Website);
public sealed record ResourceSearchResult(IReadOnlyList<ResourceProject> Projects, int Total, int Page);
public sealed record ResourceDetailQuery(string ProjectId, string GameVersion = "", string Loader = "");
public sealed record ResourceVersion(string Id, string Name, string Number, string Channel,
    IReadOnlyList<string> Games, IReadOnlyList<string> Loaders, string Published, string Website);
public sealed record ResourceDetail(ResourceProject Project, string License, IReadOnlyList<ResourceVersion> Versions);

public interface IResourceCatalogSource
{
    Task<ResourceSearchResult> SearchAsync(ResourceSearchQuery query, CancellationToken token);
    Task<ResourceDetail> DetailAsync(ResourceDetailQuery query, CancellationToken token);
}

using System.Net;
using System.Text;
using System.Text.Json;
using Nexa.Services.Resources;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask ResourceCatalogFiltersAndValidatesProviderResults()
    {
        var requests = new List<string>();
        using var http = new HttpClient(new ResourceHttp(request =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            AssertTrue(request.Headers.UserAgent.ToString().Contains("NexaCL", StringComparison.Ordinal));
            string body = request.RequestUri.AbsolutePath.EndsWith("/search", StringComparison.Ordinal)
                ? """{"hits":[{"project_id":"Valid123","title":"Sodium","description":"Renderer","author":"Author","downloads":123},{"project_id":"../evil","title":"bad"}],"total_hits":41}"""
                : request.RequestUri.AbsolutePath.EndsWith("/version", StringComparison.Ordinal)
                ? """[{"id":"V1","project_id":"Valid123","name":"One","version_number":"1","version_type":"release","game_versions":["1.21.1"],"loaders":["fabric"],"date_published":"2026-01-01"},{"id":"V2","project_id":"Other","game_versions":["1.21.1"],"loaders":["fabric"]},{"id":"V3","project_id":"Valid123","game_versions":["1.20.1"],"loaders":["fabric"]}]"""
                : """{"id":"Valid123","title":"Sodium","license":{"name":"MIT"}}""";
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        }));
        var service = new ResourceCatalogService(http);
        var search = await service.SearchAsync(new(ResourceKind.Mod, "a&b", "1.21.1", "fabric", ResourceOrder.Downloads, 1), default);
        AssertEqual(1, search.Projects.Count); AssertEqual(41, search.Total);
        AssertEqual("https://modrinth.com/project/Valid123", search.Projects[0].Website);
        AssertTrue(requests[0].Contains("offset=20", StringComparison.Ordinal));
        string query = Uri.UnescapeDataString(requests[0]);
        AssertTrue(query.Contains("versions:1.21.1", StringComparison.Ordinal));
        AssertTrue(query.Contains("categories:fabric", StringComparison.Ordinal));
        var detail = await service.DetailAsync(new("Valid123", "1.21.1", "fabric"), default);
        AssertEqual(1, detail.Versions.Count); AssertEqual("正式版", detail.Versions[0].Channel);
        AssertEqual("https://modrinth.com/project/Valid123/version/V1", detail.Versions[0].Website);
        await service.SearchAsync(new(ResourceKind.DataPack), default);
        AssertTrue(Uri.UnescapeDataString(requests[^1]).Contains("all_project_types:datapack", StringComparison.Ordinal));
        bool invalid = false;
        try { await service.DetailAsync(new("../escape"), default); } catch (ArgumentException) { invalid = true; }
        AssertTrue(invalid);
    }

    private static async ValueTask ResourceCatalogBoundsActualResponseAndHonorsCancellation()
    {
        using var http = new HttpClient(new ResourceHttp(_ =>
        {
            var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(new string(' ', 8 * 1024 * 1024 + 1))));
            content.Headers.ContentLength = 1; // A declared length cannot replace the actual-byte budget.
            return new(HttpStatusCode.OK) { Content = content };
        }));
        bool rejected = false;
        try { await new ResourceCatalogService(http).SearchAsync(new(), default); }
        catch (InvalidDataException) { rejected = true; }
        AssertTrue(rejected);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        bool canceled = false;
        try { await new ResourceCatalogService(http).SearchAsync(new(), stop.Token); }
        catch (OperationCanceledException) { canceled = true; }
        AssertTrue(canceled);
    }

    private sealed class ResourceHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }
}

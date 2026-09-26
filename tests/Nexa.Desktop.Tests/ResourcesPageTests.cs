using Nexa.Desktop.Ui;
using Nexa.Services.Composition;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Resources;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void ResourceIconsArriveWithoutRebuildingSearchOrRows()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        var icon = new TaskCompletionSource<ResourceIconResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queries = new XsrQueryRouterBuilder();
        queries.Register<ResourceSearchQuery, ResourceSearchResult>(ResourceCatalogContract.Search,
            (query, token) => ValueTask.FromResult(XsrResult.Success(new ResourceSearchResult(
                [new("WithIcon", "With icon", "", "", 1, "https://modrinth.com/project/WithIcon") { IconUrl = "https://cdn.modrinth.com/data/test/icon.png" }], 1, 0))));
        queries.Register<ResourceIconQuery, ResourceIconResult>(ResourceCatalogContract.Icon, async (query, token) => XsrResult.Success(await icon.Task));
        using var page = new ResourcesPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()), fixture.Store, _ => { });
        fixture.Shell.Stage.Navigation.Replace(page.Page);
        fixture.Shell.Renderer.ReducedMotion = true;
        var scene = fixture.Shell.Render(new(1000, 650));
        var row = FindByKey(fixture.Shell, scene, "ResourceProject.WithIcon").Entity;
        var search = FindByKey(fixture.Shell, scene, "ResourceSearch").Entity;
        var image = FindByKey(fixture.Shell, scene, "ResourceProjectIcon").Entity;
        AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiImage>(image)!.Raster is null);
        var png = Nexa.Core.Media.PngImage.TryCreate(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII="));
        icon.SetResult(new(png));
        AssertTrue(SpinWait.SpinUntil(() =>
        {
            scene = fixture.Shell.Render(new(1000, 650));
            return fixture.Shell.Tree.GetComponent<XsrUiImage>(image)!.Raster is not null;
        }, TimeSpan.FromSeconds(5)));
        AssertEqual(row, FindByKey(fixture.Shell, scene, "ResourceProject.WithIcon").Entity);
        AssertEqual(search, FindByKey(fixture.Shell, scene, "ResourceSearch").Entity);
    }
    private static void ResourcesPageUsesServiceQueriesAndPreservesSearch()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        var source = new ResourceSource();
        using var runtime = ResourceCatalogRuntimeComposer.Compose(source);
        List<Uri> opened = [];
        using var page = new ResourcesPageController(fixture.Shell, fixture.Intents, runtime.Queries, fixture.Store, opened.Add);
        var instanceQueries = new XsrQueryRouterBuilder();
        instanceQueries.Register<MinecraftInstallEditQuery, MinecraftInstallEditSnapshot>(MinecraftInstallEditContract.Query,
            (query, token) => ValueTask.FromResult(XsrResult.Success(new MinecraftInstallEditSnapshot(query.RootDirectory, query.InstanceId, "1.21.1", [], "fingerprint"))));
        page.ConfigureInstanceFilter(instanceQueries.Build(new NoopDispatchObserver()), () => new("root", "renamed-instance"));
        fixture.Controller.ResourcesPage = page.Page;
        fixture.Shell.Renderer.ReducedMotion = true;
        Emit(fixture.Intents, "ui.navigation.community");
        var scene = fixture.Shell.Render(new(1000, 650));
        AssertEqual(page.Page, fixture.Shell.Stage.Navigation.Current);
        var input = FindByKey(fixture.Shell, scene, "ResourceSearch").Entity;
        var listBounds = FindByKey(fixture.Shell, scene, "ResourceList").Rect;
        var layoutBounds = FindByKey(fixture.Shell, scene, "ResourcesLayout").Rect;
        var footerBounds = FindByKey(fixture.Shell, scene, "ResourcePagination").Rect;
        AssertTrue(listBounds.Y - layoutBounds.Y <= 90);
        AssertTrue(layoutBounds.Y + layoutBounds.Height - footerBounds.Y - footerBounds.Height <= 1);
        fixture.Shell.Renderer.SetTextInputValue(input, "Sodium");
        Emit(fixture.Intents, "ui.resources.action", page.Find("ResourceSearchButton"));
        scene = fixture.Shell.Render(new(1000, 650));
        AssertEqual("Sodium", source.Last!.Text);
        AssertEqual(input, FindByKey(fixture.Shell, scene, "ResourceSearch").Entity);
        AssertEqual("Sodium", fixture.Shell.Tree.GetComponent<XsrUiTextInput>(input)!.ReadDraft());
        var card = FindByKey(fixture.Shell, scene, "ResourceProject.Valid123");
        var body = FindByKey(fixture.Shell, scene, "ResourceProject.Valid123.Body");
        AssertTrue(body.Rect.X > card.Rect.X);
        AssertTrue(body.Rect.X + body.Rect.Width < card.Rect.X + card.Rect.Width);
        AssertTrue(card.Rect.Height <= 84);
        Emit(fixture.Intents, "ui.resources.action", page.Find("ResourceCurrentInstance"));
        scene = fixture.Shell.Render(new(1000, 650));
        AssertEqual("1.21.1", source.Last!.GameVersion);
        AssertEqual("", source.Last.Loader); // Vanilla must not default to enum value zero (Forge).
        Emit(fixture.Intents, "ui.resources.action", page.Find("ResourceDetails.Valid123"));
        scene = fixture.Shell.Render(new(1000, 650));
        AssertEqual(page.DetailPage, fixture.Shell.Stage.Navigation.Current);
        Emit(fixture.Intents, "ui.resources.action", page.Find("ResourceDownload.V1"));
        fixture.Shell.Render(new(1000, 650));
        AssertEqual("https://modrinth.com/project/Valid123/version/V1", opened.Single().AbsoluteUri);
        fixture.Shell.Stage.Navigation.Pop();
        scene = fixture.Shell.Render(new(760, 500));
        AssertEqual(input, FindByKey(fixture.Shell, scene, "ResourceSearch").Entity);
        AssertTrue(FindByKey(fixture.Shell, scene, "ResourceList").Rect.Width > 500);
        var searchBounds = FindByKey(fixture.Shell, scene, "ResourceSearch").Rect;
        AssertTrue(searchBounds.Width >= 100);
    }

    private sealed class ResourceSource : IResourceCatalogSource
    {
        internal ResourceSearchQuery? Last { get; private set; }
        private static ResourceProject Project => new("Valid123", "Sodium", "Renderer", "Author", 100, "https://modrinth.com/project/Valid123");
        public Task<ResourceSearchResult> SearchAsync(ResourceSearchQuery query, CancellationToken token)
        { Last = query; return Task.FromResult(new ResourceSearchResult([Project], 1, query.Page)); }
        public Task<ResourceDetail> DetailAsync(ResourceDetailQuery query, CancellationToken token) => Task.FromResult(new ResourceDetail(Project, "MIT",
            [new("V1", "One", "1", "正式版", ["1.21.1"], ["fabric"], "2026-01-01", "https://modrinth.com/project/Valid123/version/V1")]));
    }

    private static void ResourcesPageDiscardsSupersededSearchAndRestoresNavigation()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        var source = new DeferredResourceSource();
        using var runtime = ResourceCatalogRuntimeComposer.Compose(source);
        using var page = new ResourcesPageController(fixture.Shell, fixture.Intents, runtime.Queries, fixture.Store, _ => { });
        fixture.Controller.ResourcesPage = page.Page;
        fixture.Shell.Renderer.ReducedMotion = true;
        Emit(fixture.Intents, "ui.navigation.community");
        var scene = fixture.Shell.Render(new(1000, 650));
        var first = source.Calls[0];
        var search = FindByKey(fixture.Shell, scene, "ResourceSearch").Entity;
        fixture.Shell.Renderer.SetTextInputValue(search, "new");
        Emit(fixture.Intents, "ui.resources.action", page.Find("ResourceSearchButton"));
        fixture.Shell.Render(new(1000, 650));
        AssertTrue(first.Token.IsCancellationRequested);
        source.Calls[1].Completion.SetResult(new([new("New", "New result", "", "", 1, "https://modrinth.com/project/New")], 1, 0));
        AssertTrue(SpinWait.SpinUntil(() =>
        {
            scene = fixture.Shell.Render(new(1000, 650));
            return scene.Nodes.Any(node => node.Text == "New result");
        }, TimeSpan.FromSeconds(5)));
        first.Completion.SetResult(new([new("Old", "Old result", "", "", 1, "https://modrinth.com/project/Old")], 1, 0));
        scene = fixture.Shell.Render(new(1000, 650));
        AssertFalse(scene.Nodes.Any(node => node.Text == "Old result"));
        AssertEqual(search, FindByKey(fixture.Shell, scene, "ResourceSearch").Entity);
        Emit(fixture.Intents, "ui.resources.action", page.Find("ResourceSearchButton"));
        fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.navigation.launch");
        fixture.Shell.Render(new(1000, 650));
        AssertTrue(source.Calls[^1].Token.IsCancellationRequested);
    }
    private sealed class DeferredResourceSource : IResourceCatalogSource
    {
        internal List<(TaskCompletionSource<ResourceSearchResult> Completion, CancellationToken Token)> Calls { get; } = [];
        public Task<ResourceSearchResult> SearchAsync(ResourceSearchQuery query, CancellationToken token)
        {
            var completion = new TaskCompletionSource<ResourceSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Calls.Add((completion, token)); return completion.Task;
        }
        public Task<ResourceDetail> DetailAsync(ResourceDetailQuery query, CancellationToken token) => throw new InvalidOperationException();
    }
}

using System.Text;
using PCL.Pxml;
using PCL.Services.Accounts;
using PCL.Services.Composition;
using PCL.Services.Downloads;
using PCL.Services.Files;
using PCL.Services.Foundation;
using PCL.Services.Logging;
using PCL.Services.Settings;
using PCL.Services.Telemetry;
using PCL.UI.Next;
using PCL.Xsr;
using PCL.Xsr.Runtime;
using PCL.Xsr.State;

namespace PCL.Services.Tests;

// XSR-519: Wave 5 acceptance integration — the foundation modules compose into ONE shared
// host state store, foundation commands route through the XSR command router into services,
// publications reach the UI state bridge, and a PXML page binds across capabilities with no
// identifier collisions.
internal static partial class Program
{
    private sealed class RecordingDispatchObserver : IXsrDispatchObserver
    {
        public List<XsrDispatchObservation> Observations { get; } = [];

        public List<XsrDispatchObservation> Completed { get; } = [];

        public void OnCompleted(XsrDispatchObservation observation) => Completed.Add(observation);
    }

    private static SettingsSchema CompositionSchema() => new SettingsSchemaBuilder()
        .AddString("settings.theme", "light")
        .AddInt32("settings.download.thread", 32)
        .Build();

    private static XsrUiTree Tree { get; } = new();

    private static PxmlIrNode TextNode(string semantic, string content) => new()
    {
        Kind = PxmlIrNodeKind.Text,
        Recipe = PxmlRuntimeRecipe.Text,
        Content = content,
        Bindings =
        [
            new PxmlIrBinding(
                XsrSemanticId.Parse(semantic),
                XsrUiStateProperty.Text,
                XsrUiDirtyKinds.Paint | XsrUiDirtyKinds.State),
        ],
    };

    private static PxmlIrNode ElementNode(string semantic) => new()
    {
        Kind = PxmlIrNodeKind.StackPanel,
        Recipe = PxmlRuntimeRecipe.Element,
        Bindings =
        [
            new PxmlIrBinding(
                XsrSemanticId.Parse(semantic),
                XsrUiStateProperty.Visibility,
                XsrUiDirtyKinds.State),
        ],
    };

    internal static async ValueTask FoundationCompositionEndToEnd()
    {
        // The composition root owns both phases and returns the one host store plus its sealed
        // command/query routers. The UI bridge observes the same store used by every service.
        XsrUiTree tree = new();
        XsrUiStateBridge bridge = new(tree);
        SettingsSchema schema = CompositionSchema();
        InMemorySettingsPort settingsPort = new();
        LaunchProfileFilePort profilePort = new(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "pcl-services-tests", Guid.NewGuid().ToString("N"), "profiles.json"));
        RecordingDispatchObserver observer = new();
        FoundationHost host = FoundationComposer.Compose(settingsPort, schema, profilePort, bridge);
        FoundationRuntime runtime = FoundationRuntimeComposer.Compose(host, observer);

        AssertEqual(3, runtime.Commands.Count);
        AssertEqual(1, runtime.Queries.Count);
        AssertTrue(runtime.Commands.TryResolve(FoundationRouteIds.SettingsSet, out XsrCommandId commandId));
        AssertTrue(runtime.Commands.TryResolve(FoundationRouteIds.TelemetryConsent, out _));
        AssertTrue(runtime.Commands.TryResolve(FoundationRouteIds.AccountUpsertProfile, out _));
        AssertTrue(runtime.Queries.TryResolve(FoundationRouteIds.SettingsGet, out XsrQueryId queryId));

        // The page: one PXML text bound to settings.theme renders "light" before the command.
        PxmlHostIr ir = new(TextNode("settings.theme", "light"));
        XsrUiEntityId page = tree.Create("page-root");
        XsrUiEntityId textEntity = PxmlUiLoader.Load(ir, tree, host.StateStore, page);
        XsrUiRenderer renderer = new(tree, host.StateStore);
        renderer.SetRoot(page);
        XsrUiScene beforeScene = renderer.Render();
        string? beforeText = beforeScene.Nodes.First(static node => node.Text is not null).Text;
        AssertEqual("light", beforeText);

        // UI intent → router → service → state → bridge → drain → render.
        XsrCommandDispatch dispatch = runtime.Commands.Dispatch(
            commandId, new SettingsSetCommand("settings.theme", "dark"));
        AssertTrue(dispatch.Acceptance.IsSuccess);
        AssertTrue((await dispatch.Completion).IsSuccess);
        AssertTrue(host.Settings.GetValue<string>("settings.theme").TryGetValue(out string? theme) && theme == "dark");

        XsrResult<string> queried = await runtime.Queries.QueryAsync<SettingsGetQuery, string>(
            queryId, new SettingsGetQuery("settings.theme"));
        AssertTrue(queried.TryGetValue(out string? queriedTheme) && queriedTheme == "dark");

        // The same wire command must use the schema type rather than infer string: the I32
        // setting was the regression that the former handler rejected as a type mismatch.
        AssertTrue((await runtime.Commands.Dispatch(
            commandId, new SettingsSetCommand("settings.download.thread", "64")).Completion).IsSuccess);
        AssertTrue(host.Settings.GetValue<int>("settings.download.thread")
            .TryGetValue(out int threadCount) && threadCount == 64);
        XsrResult<string> queriedThreadCount = await runtime.Queries.QueryAsync<SettingsGetQuery, string>(
            queryId, new SettingsGetQuery("settings.download.thread"));
        AssertTrue(queriedThreadCount.TryGetValue(out string? rawThreadCount) && rawThreadCount == "64");

        AssertTrue(bridge.PendingCount > 0);
        bridge.DrainAndMark(host.StateStore);
        AssertTrue(tree.DirtyEntities().Any(entity => entity == textEntity));
        XsrUiScene afterScene = renderer.Render();
        string? afterText = afterScene.Nodes.First(static node => node.Text is not null).Text;
        AssertEqual("dark", afterText);

        // The dispatch was observed for diagnostics.
        AssertTrue(observer.Completed.Count > 0);
    }

    internal static async ValueTask FoundationDownloadsUseComposedLogging()
    {
        string directory = CreateTempDirectory();
        try
        {
            SettingsSchema schema = CompositionSchema();
            FoundationHost host = FoundationComposer.Compose(
                new InMemorySettingsPort(),
                schema,
                new LaunchProfileFilePort(Path.Combine(directory, "profiles.json")));

            DownloadTransferResult result = await host.Downloads.DownloadAsync(new DownloadRequest
            {
                Sources = ["mem://download"],
                DestinationPath = Path.Combine(directory, "payload.bin"),
                ConnectionFactory = _ => new FakeConnection(1, [0x42]),
            });

            AssertTrue(result.Success);
            AssertTrue(host.Logging.GetSnapshot().Any(entry =>
                entry.Module == DownloadService.LogModuleName
                && entry.Message.Contains("下载", StringComparison.Ordinal)));
            AssertTrue(ReferenceEquals(host.Logging.StateStore, host.Downloads.StateStore));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static async ValueTask CrossCapabilityPageHasNoStateIdCollisions()
    {
        XsrUiTree tree = new();
        XsrUiStateBridge bridge = new(tree);
        SettingsSchema schema = CompositionSchema();
        LaunchProfileFilePort profilePort = new(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "pcl-services-tests", Guid.NewGuid().ToString("N"), "profiles.json"));
        FoundationHost host = FoundationComposer.Compose(new InMemorySettingsPort(), schema, profilePort, bridge);
        FoundationRuntime runtime = FoundationRuntimeComposer.Compose(host);
        XsrStateStore store = host.StateStore;
        LogService logging = host.Logging;
        AccountService accounts = host.Accounts;
        TelemetryService telemetry = host.Telemetry;

        // Every capability's entries live in one store with distinct runtime ids.
        XsrStateId theme = store.Resolve(XsrSemanticId.Parse("settings.theme"));
        XsrStateId logEntries = store.Resolve(XsrSemanticId.Parse("logging.entries"));
        XsrStateId transfers = store.Resolve(XsrSemanticId.Parse("download.transfers"));
        XsrStateId profiles = store.Resolve(XsrSemanticId.Parse("accounts.profiles"));
        XsrStateId pending = store.Resolve(XsrSemanticId.Parse("telemetry.pending"));
        AssertTrue(theme.Value != logEntries.Value);
        AssertTrue(theme.Value != transfers.Value);
        AssertTrue(theme.Value != profiles.Value);
        AssertTrue(theme.Value != pending.Value);

        // Seed each capability so its entries carry applied values.
        logging.Write(LogLevel.Info, "x", "seed");
        AssertTrue(AccountLoginProfiles.Upsert(accounts, new LaunchProfile
        {
            Username = "Steve",
            Kind = LaunchProfileKind.Offline,
            Uuid = "uuid-offline",
        }).IsSuccess);
        telemetry.Consent = true;
        telemetry.Record("app.started");

        // One PXML page binds state from four capabilities; loading resolves every binding
        // through the shared store without identifier collisions.
        PxmlIrNode page = new()
        {
            Kind = PxmlIrNodeKind.Page,
            Recipe = PxmlRuntimeRecipe.Element,
            Children =
            [
                TextNode("settings.theme", "light"),
                ElementNode("accounts.profiles"),
                ElementNode("download.transfers"),
                ElementNode("telemetry.pending"),
                ElementNode("logging.entries"),
            ],
        };
        XsrUiEntityId root = tree.Create("cross-page");
        XsrUiEntityId loaded = PxmlUiLoader.Load(new PxmlHostIr(page), tree, store, root);

        // Clear the seeding publications so the drain observes exactly one command.
        bridge.DrainAndMark(store);
        tree.DirtyEntities().ToList().ForEach(entity => tree.ClearDirty(entity));

        // Dispatching settings.set dirties only the settings-bound entity: state identity is
        // exact, not per-store guesswork.
        XsrCommandId commandId = runtime.Commands.TryResolve(FoundationRouteIds.SettingsSet, out XsrCommandId resolved)
            ? resolved
            : throw new InvalidOperationException("settings.set route missing.");
        AssertTrue((await runtime.Commands.Dispatch(
            commandId, new SettingsSetCommand("settings.theme", "dark")).Completion).IsSuccess);

        bridge.DrainAndMark(store);
        IReadOnlyList<XsrUiEntityId> dirty = tree.DirtyEntities();

        // Only the settings-bound text child is dirty: state identity is exact, so a settings
        // publication never touches the account, download, telemetry, or log bindings.
        AssertEqual(1, dirty.Count);
        XsrUiEntityId settingsChild = tree.Children(loaded)[0];
        AssertTrue(dirty.Contains(settingsChild));
    }
}

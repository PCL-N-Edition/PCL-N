using System.Text.Json.Nodes;
using Nexa.Services.Accounts;
using Nexa.Services.Foundation;
using Nexa.Services.Minecraft;
using Nexa.Services.Settings;
using Nexa.Xsr;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask InstalledVersionKindsPreserveLegacyDistinctions()
    {
        using LibraryFixture fixture = new();
        string root = fixture.AddRoot("kinds");
        (string Id, string Type, string Library, MinecraftVersionKind Kind)[] cases =
        [
            ("1.21", "release", "", MinecraftVersionKind.Release),
            ("24w13a", "snapshot", "", MinecraftVersionKind.Snapshot),
            ("b1.7.3", "old_beta", "", MinecraftVersionKind.Old),
            ("24w14potato", "snapshot", "", MinecraftVersionKind.AprilFools),
            ("opt", "release", "optifine:OptiFine:1.20.1_HD_U_I6", MinecraftVersionKind.OptiFine),
            ("lite", "release", "com.mumfrey:liteloader:1.12.2", MinecraftVersionKind.LiteLoader),
            ("f", "release", "net.minecraftforge:forge:1.20.1-47.2.0", MinecraftVersionKind.Forge),
            ("n", "release", "net.neoforged:neoforge:20.6.119-beta", MinecraftVersionKind.NeoForge),
            ("c", "release", "com.cleanroommc:cleanroom:0.2", MinecraftVersionKind.Cleanroom),
            ("q", "release", "org.quiltmc:quilt-loader:0.27", MinecraftVersionKind.Quilt),
            ("a", "release", "net.fabricmc:fabric-loader:0.16", MinecraftVersionKind.Fabric),
            ("legacy", "release", "net.legacyfabric:intermediary:1.12.2", MinecraftVersionKind.Fabric),
            ("laby", "release", "net.labymod:client:4", MinecraftVersionKind.LabyMod),
        ];
        foreach (var item in cases)
            CreateVersionDirectory(root, item.Id, new JsonObject
            {
                ["id"] = item.Id,
                ["type"] = item.Type,
                ["libraries"] = new JsonArray(new JsonObject { ["name"] = item.Library }),
            });
        CreateVersionDirectory(root, "mixed", new JsonObject
        {
            ["id"] = "mixed",
            ["inheritsFrom"] = "a",
            ["libraries"] = new JsonArray(new JsonObject { ["name"] = "optifine:OptiFine:1.20.1" }),
        });
        CreateVersionDirectory(root, "potato-child", new JsonObject { ["id"] = "potato-child", ["inheritsFrom"] = "24w14potato" });
        IReadOnlyList<MinecraftInstanceDescriptor> instances = await new MinecraftInstanceDiscovery().DiscoverAsync(root);
        foreach (var item in cases) AssertEqual(item.Kind, instances.Single(instance => instance.Id == item.Id).Version.Kind);
        AssertEqual(MinecraftVersionKind.Fabric, instances.Single(instance => instance.Id == "mixed").Version.Kind);
        AssertEqual(MinecraftVersionKind.AprilFools, instances.Single(instance => instance.Id == "potato-child").Version.Kind);
    }

    private static async ValueTask LibraryRemembersDirectoryQualifiedSelection()
    {
        using LibraryFixture fixture = new();
        string a = fixture.AddRoot("a"), b = fixture.AddRoot("b");
        LibraryVersion(a, "same"); LibraryVersion(a, "other"); LibraryVersion(b, "same");
        using MinecraftLibraryService service = new(fixture.Host.Settings, a, new MinecraftInstanceDiscovery());
        AssertTrue((await service.RefreshAsync()).IsSuccess);
        AssertTrue(service.SelectInstance(a, "same").IsSuccess);
        AssertTrue((await service.ChangeDirectoryAsync(b, true)).IsSuccess);
        AssertEqual(b, fixture.Snapshot.RootDirectory);
        AssertEqual(Path.Combine(b, "versions", "same"), fixture.Snapshot.SelectedInstance!.DirectoryPath);
        AssertFalse(service.SelectInstance(a, "same").IsSuccess);
        AssertTrue((await service.ChangeDirectoryAsync(a, false)).IsSuccess);
        AssertEqual("same", fixture.Snapshot.SelectedInstanceId);
        AssertTrue((await service.RefreshAsync()).IsSuccess);
        AssertEqual("same", fixture.Snapshot.SelectedInstanceId);
        FoundationHost restoredHost = fixture.CreateHost();
        using MinecraftLibraryService restored = new(restoredHost.Settings, b, new MinecraftInstanceDiscovery());
        await restored.RefreshAsync();
        MinecraftLibrarySnapshot snapshot = (MinecraftLibrarySnapshot)restoredHost.StateStore.ReadAppliedValue(restoredHost.StateStore.Resolve(MinecraftLibraryService.StateKey))!;
        AssertEqual(a, snapshot.RootDirectory); AssertEqual("same", snapshot.SelectedInstanceId);
        AssertTrue((await service.ChangeDirectoryAsync(a + Path.DirectorySeparatorChar, true)).IsSuccess);
        AssertEqual(2, fixture.Snapshot.Directories.Count);
        AssertTrue((await service.ForgetDirectoryAsync(b)).IsSuccess);
        AssertTrue(File.Exists(Path.Combine(b, "versions", "same", "same.json")));
        AssertFalse((await service.ForgetDirectoryAsync(a)).IsSuccess);
    }

    private static async ValueTask LibraryRejectsStaleScansAndPreservesLiveSelection()
    {
        using LibraryFixture fixture = new();
        string a = fixture.AddRoot("a"), b = fixture.AddRoot("b");
        DeferredLibrarySource source = new();
        using MinecraftLibraryService service = new(fixture.Host.Settings, a, source);
        Task<XsrResult> first = service.RefreshAsync();
        Task<XsrResult> second = service.ChangeDirectoryAsync(b, true);
        AssertTrue(source.Tokens[0].IsCancellationRequested);
        source.Requests[1].SetResult([LibraryDescriptor(b, "same"), LibraryDescriptor(b, "chosen")]);
        AssertTrue((await second).IsSuccess);
        AssertTrue(service.SelectInstance(b, "chosen").IsSuccess);
        source.Requests[0].SetResult([LibraryDescriptor(a, "stale")]);
        AssertFalse((await first).IsSuccess);
        AssertEqual(b, fixture.Snapshot.RootDirectory); AssertEqual("chosen", fixture.Snapshot.SelectedInstanceId);
        // A loading scan publishes an empty instance list, so selection waits for it.
        Task<XsrResult> refresh = service.RefreshAsync();
        source.Requests[2].SetResult([LibraryDescriptor(b, "chosen"), LibraryDescriptor(b, "same")]);
        AssertTrue((await refresh).IsSuccess);
        AssertTrue(service.SelectInstance(b, "same").IsSuccess);
        using CancellationTokenSource cancellation = new();
        Task<XsrResult> cancelled = service.RefreshAsync(cancellation.Token);
        cancellation.Cancel(); source.Requests[3].SetCanceled(cancellation.Token);
        AssertFalse((await cancelled).IsSuccess); AssertFalse(fixture.Snapshot.IsLoading);
        AssertEqual("same", fixture.Snapshot.SelectedInstanceId);
        long beforeForget = fixture.Snapshot.Revision;
        Task<XsrResult> forget = service.ForgetDirectoryAsync(b);
        AssertEqual(beforeForget + 1, fixture.Snapshot.Revision);
        AssertTrue(fixture.Snapshot.IsLoading);
        AssertEqual(a, fixture.Snapshot.RootDirectory);
        AssertEqual("", fixture.Snapshot.SelectedInstanceId);
        source.Requests[4].SetResult([LibraryDescriptor(a, "remaining")]);
        AssertTrue((await forget).IsSuccess);
        AssertEqual(beforeForget + 2, fixture.Snapshot.Revision);
        AssertFalse(fixture.Snapshot.IsLoading);
    }

    private static async ValueTask LibraryFailedSavesAndUnavailableRootsRemainHonest()
    {
        using LibraryFixture fixture = new();
        string a = fixture.AddRoot("a"), empty = fixture.AddRoot("empty");
        LibraryVersion(a, "first"); LibraryVersion(a, "second");
        using MinecraftLibraryService service = new(fixture.Host.Settings, a, new MinecraftInstanceDiscovery());
        await service.RefreshAsync();
        string selected = fixture.Snapshot.SelectedInstanceId;
        fixture.Port.Fail = true;
        AssertFalse(service.SelectInstance(a, selected == "first" ? "second" : "first").IsSuccess);
        AssertEqual(selected, fixture.Snapshot.SelectedInstanceId);
        AssertFalse((await service.ChangeDirectoryAsync(empty, true)).IsSuccess);
        AssertEqual(a, fixture.Snapshot.RootDirectory);
        fixture.Port.Fail = false;
        AssertTrue((await service.ChangeDirectoryAsync(empty, true)).IsSuccess);
        AssertEqual(0, fixture.Snapshot.Instances.Count); AssertTrue(fixture.Snapshot.SelectedInstance is null);
        Directory.Delete(empty);
        AssertFalse((await service.RefreshAsync()).IsSuccess);
        AssertTrue(fixture.Snapshot.Error is not null); AssertFalse(fixture.Snapshot.IsLoading);
        AssertFalse((await service.ChangeDirectoryAsync("relative/path", true)).IsSuccess);
    }

    private static async ValueTask LibraryUnknownDocumentsArePreserved()
    {
        using LibraryFixture fixture = new();
        string root = fixture.AddRoot("a"); LibraryVersion(root, "game");
        const string future = "{\"SchemaVersion\":99,\"Directories\":[]}";
        AssertTrue(fixture.Host.Settings.SetValue(MinecraftLibraryService.SettingKey, future).IsSuccess);
        using MinecraftLibraryService service = new(fixture.Host.Settings, root, new MinecraftInstanceDiscovery());
        AssertFalse((await service.RefreshAsync()).IsSuccess);
        AssertEqual(future, fixture.Host.Settings.GetValue<string>(MinecraftLibraryService.SettingKey).Value);
        AssertTrue(fixture.Snapshot.SelectedInstance is null);
    }

    private static void LibraryVersion(string root, string id) => CreateVersionDirectory(root, id,
        new JsonObject { ["id"] = id, ["type"] = "release", ["mainClass"] = "example.Main" });

    private static MinecraftInstanceDescriptor LibraryDescriptor(string root, string id)
    {
        string directory = Path.Combine(root, "versions", id);
        return new(id, directory, id, new(id, directory, Path.Combine(directory, id + ".json"), null, null, "example.Main", null,
            new(id, "release", MinecraftVersionCategory.Release, null)), new());
    }

    private sealed class DeferredLibrarySource : IMinecraftInstanceSource
    {
        public List<TaskCompletionSource<IReadOnlyList<MinecraftInstanceDescriptor>>> Requests { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public async ValueTask<IReadOnlyList<MinecraftInstanceDescriptor>> DiscoverAsync(string minecraftRootDirectory, CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<IReadOnlyList<MinecraftInstanceDescriptor>> request = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add(request); Tokens.Add(cancellationToken);
            return await request.Task.ConfigureAwait(false);
        }
    }
    private sealed class LibrarySettingsPort : ISettingsPort
    {
        private readonly InMemorySettingsPort _inner = new();
        public bool Fail { get; set; }
        public IReadOnlyDictionary<string, string> Load() => _inner.Load();
        public void Save(IReadOnlyDictionary<string, string> values) { if (Fail) throw new IOException("denied"); _inner.Save(values); }
    }
    private sealed class LibraryFixture : IDisposable
    {
        private readonly string _root = CreateTempDirectory();
        public LibrarySettingsPort Port { get; } = new();
        public FoundationHost Host { get; }
        public LibraryFixture() => Host = CreateHost();
        public FoundationHost CreateHost() => FoundationComposer.Compose(Port, LauncherDefaults.CreateSchema(), new LaunchProfileFilePort(Path.Combine(_root, "profiles.json")));
        public MinecraftLibrarySnapshot Snapshot => (MinecraftLibrarySnapshot)Host.StateStore.ReadAppliedValue(Host.StateStore.Resolve(MinecraftLibraryService.StateKey))!;
        public string AddRoot(string name) { string path = Path.Combine(_root, name); Directory.CreateDirectory(path); return path; }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}

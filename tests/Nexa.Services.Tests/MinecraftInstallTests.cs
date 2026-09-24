using System.Text.Json.Nodes;
using Nexa.Services.Downloads;
using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Tasks;
using Nexa.Xsr;
using Nexa.Xsr.State;

[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Cryptographic Do Not Use",
    "CA5350:DoNotUseWeakCryptographicAlgorithms",
    Justification = "Fixture integrity facts use SHA-1 to match the Mojang metadata contract.")]

namespace Nexa.Services.Tests;

// XSR-724: the real install pipeline — version documents committed after transfer, the shared
// download planners feeding one task-center task with file-accurate progress, processor-based
// loaders outside the migrated set rejected up front, and the Installed event that grows the version library.
// Everything runs against in-memory metadata and connection fakes — no network.
internal static partial class Program
{
    private sealed class FakeMetadata : IMinecraftInstallMetadataSource
    {
        public JsonObject VanillaJson { get; set; } = [];
        public int VanillaReads;
        public JsonObject? LoaderJson { get; set; }
        public JsonObject AssetIndexJson { get; set; } = [];

        public Task<JsonObject> FetchVanillaVersionJsonAsync(string gameVersion, CancellationToken cancellationToken)
        { VanillaReads++; return Task.FromResult(VanillaJson); }

        public Task<JsonObject> FetchLoaderProfileJsonAsync(
            InstallLoader loader, string gameVersion, string build, CancellationToken cancellationToken) =>
            Task.FromResult(LoaderJson ?? []);

        public Task<JsonObject> FetchAssetIndexJsonAsync(string indexUrl, CancellationToken cancellationToken) =>
            Task.FromResult(AssetIndexJson);
    }

    private sealed class ServingConnection(byte[] payload) : IDownloadConnection
    {
        private bool _read;

        public ValueTask<DownloadConnectionInfo> StartAsync(
            long beginOffset, CancellationToken cancellationToken = default)
        {
            long remaining = beginOffset > 0 && beginOffset <= payload.Length
                ? payload.Length - beginOffset
                : payload.Length;
            return ValueTask.FromResult(new DownloadConnectionInfo(remaining, beginOffset, payload.Length - 1, false));
        }

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read)
            {
                return ValueTask.FromResult(0);
            }

            _read = true;
            payload.AsMemory(0, Math.Min(payload.Length, buffer.Length)).CopyTo(buffer);
            return ValueTask.FromResult(Math.Min(payload.Length, buffer.Length));
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class FakeCatalog(string addonFileUrl) : IInstallCatalogSource
    {
        public string? RequestedGame { get; private set; }

        public Task<IReadOnlyList<InstallCatalogVersion>> GetGamesAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<InstallCatalogVersion>>([]);

        public Task<IReadOnlyList<InstallCatalogVersion>> GetLoadersAsync(
            InstallLoader loader, string game, CancellationToken token)
        {
            RequestedGame = game;
            return Task.FromResult<IReadOnlyList<InstallCatalogVersion>>(
            [
                new InstallCatalogVersion("1.0.0", loader.ToString(), true,
                    Downloads: [new InstallDownload(loader.ToString(), $"{loader}.jar", new Uri(addonFileUrl), null, 7)]),
            ]);
        }
    }

    private sealed class InstallFixture : IDisposable
    {
        public XsrStateStore Store;
        public TaskCenterService Tasks;
        public MinecraftInstallService Install;
        public List<string> InstalledRoots = [];

        public InstallFixture(FakeMetadata metadata, IInstallCatalogSource? catalog = null, IMinecraftLoaderInstaller? loaderInstaller = null, HttpClient? http = null,
            Func<string, IDownloadConnection>? connectionFactory = null)
        {
            XsrStateStoreBuilder builder = new();
            TaskCenterStateContract.DeclareState(builder);
            DownloadService.DeclareState(builder);
            Store = builder.Build();
            Tasks = new TaskCenterService(Store);
            DownloadService downloads = new(Store);
            Install = new MinecraftInstallService(
                Tasks, downloads, catalog, http: http, metadata: metadata,
                connectionFactory: connectionFactory ?? (source => new ServingConnection(PayloadFor(source))), loaderInstaller: loaderInstaller);
            Install.Installed += root => InstalledRoots.Add(root);
        }

        public TaskCenterEntry Entry() => Store.ReadCollection<TaskCenterEntry>(
            Store.Resolve(TaskCenterStateContract.EntriesKey)).Items.Single();

        public void Dispose() => Install.Dispose();
    }

    private static string Sha1Hex(string content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(content)));

    private static JsonObject VanillaJson() => JsonNode.Parse("""
        {
          "id": "1.20.1",
          "assetIndex": {
            "id": "5",
            "url": "https://piston-meta.mojang.com/v1/packages/asset-index/5.json",
            "sha1": "__ASSET_SHA__",
            "size": 100,
            "totalSize": 100
          },
          "downloads": {
            "client": {
              "url": "https://piston-data.mojang.com/v1/objects/client/1.20.1.jar",
              "sha1": "__JAR_SHA__",
              "size": 10
            }
          },
          "libraries": [
            {
              "name": "com.example:library:1.0.0",
              "downloads": {
                "artifact": {
                  "path": "com/example/library/1.0.0/library-1.0.0.jar",
                  "url": "https://libraries.minecraft.net/com/example/library/1.0.0/library-1.0.0.jar",
                  "sha1": "__JAR_SHA__",
                  "size": 10
                }
              }
            }
          ]
        }
        """.Replace("__ASSET_SHA__", Sha1Hex("ASSET!")).Replace("__JAR_SHA__", Sha1Hex("JARCONTENT")))!.AsObject();

    private static JsonObject AssetIndexJson() => JsonNode.Parse("""
        {
          "objects": {
            "minecraft/sounds/click.ogg": {
              "hash": "__ASSET_SHA__",
              "size": 6
            }
          }
        }
        """.Replace("__ASSET_SHA__", Sha1Hex("ASSET!")))!.AsObject();

    private static byte[] PayloadFor(string url) => url.Contains("/client/") || url.Contains("library")
        ? "JARCONTENT"u8.ToArray()
        : url.Contains("resources.download") || (url.Contains("bmclapi2") && url.Contains("/assets/"))
            ? "ASSET!"u8.ToArray()
            : "MODJAR!"u8.ToArray();

    private static async ValueTask InstallKeepsGameIdentityAndSelectedAddonArtifact()
    {
        FakeMetadata metadata = new() { VanillaJson = VanillaJson(), AssetIndexJson = AssetIndexJson() };
        using InstallFixture fixture = new(metadata);
        string root = Path.Combine(Path.GetTempPath(), "nexa-install-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var artifact = new InstallDownload("Modrinth", "fabric-api.jar", new Uri("https://example.invalid/fabric-api.jar"), null, 7);
            var result = await fixture.Install.InstallAsync(new MinecraftInstallCommand(root, "1.20.1", InstallLoader.Fabric, "0.16.9",
                [new MinecraftInstallAddon(InstallLoader.FabricApi, "selected-build", [artifact])], "My Fabric"));
            AssertTrue(result.IsSuccess);
            AssertTrue(File.Exists(Path.Combine(root, "mods", "fabric-api.jar")));
            string json = await File.ReadAllTextAsync(Path.Combine(root, "versions", "My Fabric", "My Fabric.json"));
            AssertTrue(json.Contains("1.20.1", StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async ValueTask InstallRunsTheRealPipelineIntoTheVersionLibrary()
    {
        FakeMetadata metadata = new() { VanillaJson = VanillaJson(), AssetIndexJson = AssetIndexJson() };
        FakeCatalog catalog = new("https://example.invalid/fabricapi.jar");
        using InstallFixture fixture = new(metadata, catalog);
        string root = Path.Combine(Path.GetTempPath(), "nexa-install-tests", Guid.NewGuid().ToString("N"));
        try
        {
            XsrResult<MinecraftInstallResult> result = await fixture.Install.InstallAsync(
                new MinecraftInstallCommand(
                    root, "1.20.1",
                    Loader: InstallLoader.Fabric, LoaderBuild: "0.16.9",
                    Addons: [new MinecraftInstallAddon(InstallLoader.FabricApi, "1.0.0")]));
            string entryError = fixture.Entry().ErrorMessage ?? "none";
            AssertTrue(result.IsSuccess, $"install failed: {result.Error?.Code}; entry={entryError}");

            // Both version documents exist after successful transfers; the loader one carries identity.
            string vanilla = await File.ReadAllTextAsync(
                Path.Combine(root, "versions", "1.20.1", "1.20.1.json"));
            AssertTrue(vanilla.Contains("\"1.20.1\"", StringComparison.Ordinal), "vanilla json missing id");
            string loader = await File.ReadAllTextAsync(
                Path.Combine(root, "versions", "1.20.1-fabric0.16.9", "1.20.1-fabric0.16.9.json"));
            AssertTrue(loader.Contains("\"inheritsFrom\": \"1.20.1\"", StringComparison.Ordinal));

            // Client jar, library, asset, asset index, and the addon jar all landed.
            AssertTrue(File.Exists(Path.Combine(root, "versions", "1.20.1", "1.20.1.jar")), "client jar missing");
            AssertTrue(File.Exists(Path.Combine(
                root, "libraries", "com", "example", "library", "1.0.0", "library-1.0.0.jar")));
            string assetHash = Sha1Hex("ASSET!");
            AssertTrue(File.Exists(Path.Combine(root, "assets", "objects", assetHash[..2], assetHash)));
            AssertTrue(File.Exists(Path.Combine(root, "assets", "indexes", "5.json")));
            AssertTrue(File.Exists(Path.Combine(root, "mods", "FabricApi.jar")));

            AssertEqual(1, fixture.InstalledRoots.Count);
            AssertTrue(MinecraftLibraryService.PathComparer.Equals(fixture.InstalledRoots[0], root));

            // Addon resolution is game-scoped: the catalog filter saw this install's version.
            AssertEqual("1.20.1", catalog.RequestedGame);

            // The run reads as one finished task with a file-accurate count.
            TaskCenterEntry entry = fixture.Entry();
            AssertEqual(TaskCenterEntryState.Finished, entry.State);
            AssertTrue(entry.TotalFiles > 0 && entry.CompletedFiles == entry.TotalFiles,
                $"files {entry.CompletedFiles}/{entry.TotalFiles}");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            fixture.Dispose();
        }
    }

    private static async ValueTask InstallRejectsProcessorLoadersBeforeTouchingDisk()
    {
        FakeMetadata metadata = new() { VanillaJson = VanillaJson(), AssetIndexJson = AssetIndexJson() };
        using InstallFixture fixture = new(metadata);
        string root = Path.Combine(Path.GetTempPath(), "nexa-install-tests", Guid.NewGuid().ToString("N"));
        try
        {
            XsrResult<MinecraftInstallResult> result = await fixture.Install.InstallAsync(
                new MinecraftInstallCommand(root, "1.20.1", Loader: InstallLoader.FabricApi, LoaderBuild: "0.3.0"));
            AssertFalse(result.IsSuccess);
            AssertFalse(Directory.Exists(root));
            AssertEqual(0, fixture.InstalledRoots.Count);

            TaskCenterEntry entry = fixture.Entry();
            AssertEqual(TaskCenterEntryState.Failed, entry.State);
            AssertTrue(entry.ErrorMessage!.Contains("不能作为主加载器", StringComparison.Ordinal));
        }
        finally
        {
            fixture.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async ValueTask InstallCancelMarksTheTaskCanceled()
    {
        FakeMetadata metadata = new() { VanillaJson = VanillaJson(), AssetIndexJson = AssetIndexJson() };
        using InstallFixture fixture = new(metadata);
        string root = Path.Combine(Path.GetTempPath(), "nexa-install-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using CancellationTokenSource cancel = new();
            Task<XsrResult<MinecraftInstallResult>> run = fixture.Install.InstallAsync(
                new MinecraftInstallCommand(root, "1.20.1"), cancel.Token);
            cancel.Cancel();
            XsrResult<MinecraftInstallResult> result = await run;
            AssertFalse(result.IsSuccess);

            TaskCenterEntry entry = fixture.Entry();
            AssertTrue(entry.State is TaskCenterEntryState.Canceled or TaskCenterEntryState.Failed);
        }
        finally
        {
            fixture.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}

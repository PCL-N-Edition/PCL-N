using System.Net;
using Nexa.Services.Minecraft.Install;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask InstallEligibilityOwnsTransitionsAndProjection()
    {
        XsrStateStoreBuilder builder = new(); InstallCatalogStateContract.DeclareState(builder);
        using InstallCatalogService service = new(builder.Build(), new EligibilityFixtureSource());
        await service.PrefetchAsync(new("1.20.1"), default);
        InstallEligibilityResult result = service.Evaluate(new("1.20.1", [], Catalog: InstallLoader.Fabric, ToggleVersion: "0.16.0"));
        AssertTrue(result.Rejection is null);
        AssertTrue(result.Loaders.Single(item => item.Loader == InstallLoader.OptiFabric).Visible);
        AssertTrue(!result.Loaders.Single(item => item.Loader == InstallLoader.OptiFine).Visible);
        result = service.Evaluate(new("1.20.1", result.Selection, result.PrimaryLoader, InstallLoader.OptiFabric, ToggleVersion: "1.14.3"));
        AssertTrue(result.Rejection is null); AssertTrue(result.CommitError is not null);
        AssertTrue(result.Loaders.Single(item => item.Loader == InstallLoader.OptiFine).Visible);
        result = service.Evaluate(new("1.20.1", result.Selection, result.PrimaryLoader, InstallLoader.OptiFine, ToggleVersion: "I6"));
        AssertTrue(result.CommitError is null); AssertEqual(3, result.Selection.Count);
        result = service.Evaluate(new("1.20.1", result.Selection, result.PrimaryLoader, InstallLoader.OptiFabric, ToggleVersion: "1.14.3"));
        AssertEqual(1, result.Selection.Count); AssertEqual(InstallLoader.Fabric, result.Selection[0].Loader);
        result = service.Evaluate(new("1.20.1", [], Catalog: InstallLoader.Forge, ToggleVersion: "missing"));
        AssertTrue(result.Rejection is not null); AssertEqual(0, result.Selection.Count);
        result = service.Evaluate(new("1.20.1", [new(InstallLoader.Forge, "47.2.19")], InstallLoader.Forge, InstallLoader.OptiFine, ["I6"]));
        AssertTrue(result.Builds["I6"].Conflict is not null);
        AssertTrue(!result.Loaders.Single(item => item.Loader == InstallLoader.OptiFine).Visible);
        AssertTrue(!result.Loaders.Single(item => item.Loader == InstallLoader.LabyMod).Visible);
        var empty = service.Evaluate(new("", [])); AssertTrue(empty.Loaders.All(item => !item.Visible));
    }
    private sealed class EligibilityFixtureSource : IInstallCatalogSource
    {
        public Task<IReadOnlyList<InstallCatalogVersion>> GetGamesAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<InstallCatalogVersion>>([new("1.20.1", "")]);
        public Task<IReadOnlyList<InstallCatalogVersion>> GetLoadersAsync(InstallLoader loader, string game, CancellationToken token) => Task.FromResult<IReadOnlyList<InstallCatalogVersion>>(loader == InstallLoader.LabyMod ? [] : [loader switch
        {
            InstallLoader.Fabric => new("0.16.0", ""),
            InstallLoader.OptiFabric => new("1.14.3", "", FabricRequirement: ">=0.8.0"),
            InstallLoader.OptiFine => new("I6", "", ForgeRequirement: "47.2.18"),
            _ => new("47.2.19", "")
        }]);
    }
    private static async Task LiveInstallCatalogSmoke()
    {
        using HttpClient http = new(); HttpInstallCatalogSource source = new(http);
        using SemaphoreSlim gate = new(4);
        var games = await source.GetGamesAsync(default);
        Console.WriteLine($"LIVE: Minecraft {games.Count} versions");
        await Task.WhenAll(Enum.GetValues<InstallLoader>().Select(async loader =>
        {
            await gate.WaitAsync();
            try
            {
                string game = loader is InstallLoader.Cleanroom or InstallLoader.LegacyFabric or InstallLoader.LiteLoader ? "1.12.2" : "1.20.1";
                var versions = await source.GetLoadersAsync(loader, game, default);
                string sources = string.Join(",", versions.SelectMany(version => version.Downloads ?? []).Select(file => file.Source).Distinct());
                Console.WriteLine($"LIVE: {loader} {game}: {versions.Count} versions; sources={sources}; partial={versions.Any(version => version.Warning is not null)}");
            }
            catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
            { Console.WriteLine($"LIVE ERROR: {loader}: {error.GetType().Name}: {error.Message}"); }
            finally { gate.Release(); }
        }));
    }
    private static async ValueTask InstallBuildCompatibilityUsesPublishedMetadata()
    {
        var versions = HttpInstallCatalogSource.ParseOptiFineCatalog("""
            <tr><td>OptiFine_1.20.1_HD_U_I6.jar</td><td class='colForge'>Forge 47.2.18</td></tr>
            <tr><td>OptiFine_1.20.1_HD_U_I5.jar</td><td class='colForge'>N/A</td></tr>
            <tr><td>OptiFine_1.20.1_HD_U_I4.jar</td><td class='colForge'>#2847</td></tr>
            OptiFine_1.20.1_HD_U_I3.jar
            """, "1.20.1");
        AssertEqual(4, versions.Count);
        var opti = versions.Single(v => v.Id.EndsWith("I6", StringComparison.Ordinal));
        AssertEqual("47.2.18", opti.ForgeRequirement);
        AssertTrue(InstallCompatibility.MatchesForge(opti, "47.2.18"));
        AssertTrue(!InstallCompatibility.MatchesForge(opti, "47.2.19"));
        AssertTrue(InstallCompatibility.MatchesForge(versions[2], "14.23.5.2847"));
        AssertTrue(!InstallCompatibility.MatchesForge(versions[1], "47.2.18"));
        AssertTrue(!InstallCompatibility.MatchesForge(versions[3], "47.2.18"));
        Dictionary<InstallLoader, InstallCatalogVersion> selected = new() { [InstallLoader.Forge] = new("47.2.19", "") };
        AssertTrue(InstallCompatibility.BuildConflict(InstallLoader.OptiFine, opti, selected) is not null);
        selected.Clear(); selected[InstallLoader.OptiFine] = opti;
        AssertTrue(InstallCompatibility.BuildConflict(InstallLoader.Forge, new("47.2.19", ""), selected) is not null);
        selected.Clear(); selected[InstallLoader.Fabric] = new("0.16.0", "");
        AssertTrue(InstallCompatibility.BuildConflict(InstallLoader.OptiFine, opti, selected) is not null);
        selected[InstallLoader.OptiFabric] = new("1.14.3", "", FabricRequirement: ">=0.8.0");
        AssertTrue(InstallCompatibility.BuildConflict(InstallLoader.OptiFine, opti, selected) is null);
        AssertTrue(InstallCompatibility.BuildConflict(InstallLoader.Fabric, new("0.7.0", ""), selected) is not null);
        AssertTrue(InstallCompatibility.CanCombine(InstallLoader.Cleanroom, InstallLoader.OptiFine, "1.12.2"));
        selected.Clear(); selected[InstallLoader.Cleanroom] = new("0.6.9-alpha", "");
        AssertTrue(InstallCompatibility.BuildNotice(InstallLoader.OptiFine, opti, selected)!.Contains("崩溃", StringComparison.Ordinal));
        using HttpClient http = new(new OptiFabricFixtureHandler());
        var bridge = await new HttpInstallCatalogSource(http).GetLoadersAsync(InstallLoader.OptiFabric, "1.20.1", default);
        AssertEqual(1, bridge.Count); AssertEqual("1.14.3", bridge[0].Id);
        AssertEqual(">=0.8.0", bridge[0].FabricRequirement); AssertTrue(bridge[0].Warning is null);
        AssertEqual(0, (await new HttpInstallCatalogSource(http).GetLoadersAsync(InstallLoader.OptiFabric, "1.20.2", default)).Count);
    }
    private sealed class OptiFabricFixtureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            HttpContent content;
            if (request.RequestUri!.Host == "edge.forgecdn.net")
            {
                using MemoryStream memory = new();
                using (var zip = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, true))
                using (var writer = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open()))
                    writer.Write("""{"id":"optifabric","version":"1.14.3","depends":{"fabricloader":">=0.8.0","minecraft":["1.20.1"]}}""");
                content = new ByteArrayContent(memory.ToArray());
            }
            else
            {
                AssertTrue(request.RequestUri.AbsolutePath.Contains("322385", StringComparison.Ordinal));
                AssertTrue(request.RequestUri.Query.Contains("modLoaderType=4", StringComparison.Ordinal));
                content = new StringContent("""{"data":[{"id":1,"fileName":"optifabric-1.14.3.jar","downloadUrl":"https://edge.forgecdn.net/file.jar","releaseType":1}]}""");
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
    private static async ValueTask InstallCatalogParsesProviderContracts()
    {
        using HttpClient http = new(new CatalogFixtureHandler());
        HttpInstallCatalogSource source = new(http);
        AssertEqual("26.1", (await source.GetGamesAsync(default))[0].Id);
        foreach ((InstallLoader loader, string game, string expected) in new[]
        {
            (InstallLoader.FabricApi, "1.20.1", "0.92.0"), (InstallLoader.Qsl, "1.20.1", "0.92.0"),
            (InstallLoader.Forge, "1.20.1", "47.2.0"), (InstallLoader.NeoForge, "1.21.1", "21.1.7"),
            (InstallLoader.Cleanroom, "1.12.2", "0.3.1"), (InstallLoader.Fabric, "1.21.1", "0.16.0"),
            (InstallLoader.LegacyFabric, "1.12.2", "0.16.0"), (InstallLoader.Quilt, "1.20.1", "0.16.0"),
            (InstallLoader.OptiFine, "1.20.1", "1.20.1_HD_U_I6"), (InstallLoader.LiteLoader, "1.12.2", "1.12.2-SNAPSHOT"),
            (InstallLoader.LabyMod, "1.20.1", "production+4.3+abc")
        }) AssertEqual(expected, (await source.GetLoadersAsync(loader, game, default))[0].Id);
        var addons = await source.GetLoadersAsync(InstallLoader.FabricApi, "1.20.1", default);
        AssertEqual(1, addons.Count); AssertEqual(2, addons[0].Downloads!.Count);
        AssertTrue(addons[0].Warning is null);
        AssertTrue(InstallCompatibility.UnavailableReason(InstallLoader.Cleanroom, "1.20.1") is not null);
        AssertTrue(InstallCompatibility.UnavailableReason(InstallLoader.Fabric, "1.12.2") is not null);
        AssertTrue(InstallCompatibility.UnavailableReason(InstallLoader.LegacyFabric, "1.13.2") is null);
        AssertTrue(InstallCompatibility.UnavailableReason(InstallLoader.Quilt, "1.14.3") is not null);
        AssertTrue(InstallCompatibility.UnavailableReason(InstallLoader.Quilt, "1.14.4") is null);
        AssertTrue(InstallCompatibility.UnavailableReason(InstallLoader.NeoForge, "26.1") is null);
        AssertTrue(InstallCompatibility.UnavailableReason(InstallLoader.Fabric, "24w14a") is null);
        AssertTrue(!InstallCompatibility.CanCombineWithOptiFine(InstallLoader.Forge, "1.14.3"));
        AssertTrue(InstallCompatibility.CanCombineWithOptiFine(InstallLoader.Forge, "1.14.4"));
    }
    private static async ValueTask InstallAddonSourcesMergeAndIsolateFailures()
    {
        foreach (bool failModrinth in new[] { true, false })
        {
            using HttpClient http = new(new CatalogFixtureHandler(failModrinth, !failModrinth));
            HttpInstallCatalogSource source = new(http, "fixture-key");
            var versions = await source.GetLoadersAsync(InstallLoader.FabricApi, "1.20.1", default);
            AssertEqual(1, versions.Count); AssertEqual(1, versions[0].Downloads!.Count);
            AssertEqual(failModrinth ? "CurseForge" : "Modrinth", versions[0].Downloads![0].Source);
            AssertTrue(versions[0].Warning is not null);
        }
        using HttpClient failedHttp = new(new CatalogFixtureHandler(true, true));
        bool failed = false;
        try { await new HttpInstallCatalogSource(failedHttp).GetLoadersAsync(InstallLoader.Qsl, "1.20.1", default); }
        catch (IOException) { failed = true; }
        AssertTrue(failed);
    }
    private static async ValueTask InstallPrefetchRetainsSiblingResultsAndCancellation()
    {
        XsrStateStoreBuilder builder = new(); InstallCatalogStateContract.DeclareState(builder); XsrStateStore store = builder.Build();
        using HttpClient http = new(new CatalogFixtureHandler());
        using InstallCatalogService service = new(store, new HttpInstallCatalogSource(http));
        await service.PrefetchAsync(new("1.20.1"), default);
        var state = (InstallCatalogState)store.ReadAppliedValue(store.Resolve(InstallCatalogStateContract.StateKey))!;
        AssertTrue(state.Catalogs.Count >= 8);
        AssertTrue(state.Catalogs.Where(item => item.Loader is not null).All(item => !item.Loading));
        AssertEqual("47.2.0", state.Catalogs.Single(item => item.Loader == InstallLoader.Forge).Versions[0].Id);
        AssertEqual("0.16.0", state.Catalogs.Single(item => item.Loader == InstallLoader.Fabric).Versions[0].Id);
        await service.PrefetchAsync(new(""), default);
        state = (InstallCatalogState)store.ReadAppliedValue(store.Resolve(InstallCatalogStateContract.StateKey))!;
        AssertEqual(1, state.Catalogs.Count);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        await service.ReadAsync(new("1.20.6", InstallLoader.Fabric), cancellation.Token);
        state = (InstallCatalogState)store.ReadAppliedValue(store.Resolve(InstallCatalogStateContract.StateKey))!;
        AssertTrue(!state.Catalogs.Single(item => item.Loader == InstallLoader.Fabric).Loading);
        AssertTrue(state.Catalogs.Single(item => item.Loader == InstallLoader.Fabric).Error is not null);
    }
    private static async ValueTask InstallCatalogRejectsStaleResults()
    {
        XsrStateStoreBuilder builder = new(); InstallCatalogStateContract.DeclareState(builder);
        XsrStateStore store = builder.Build();
        DelayedCatalogSource source = new(); using InstallCatalogService service = new(store, source);
        Task older = service.ReadAsync(new("1.20.1", InstallLoader.Fabric), default);
        Task newer = service.ReadAsync(new("1.21.1", InstallLoader.Forge), default);
        source.Second.SetResult([new("new-loader", "Forge")]); await newer;
        source.First.SetResult([new("old-loader", "Fabric")]); await older;
        InstallCatalogSnapshot snapshot = ((InstallCatalogState)store.ReadAppliedValue(store.Resolve(InstallCatalogStateContract.StateKey))!).Catalogs[^1];
        AssertEqual("1.21.1", snapshot.GameVersion); AssertEqual("new-loader", snapshot.Versions[0].Id);
        await service.ReadAsync(new("1.20.1", InstallLoader.Cleanroom), default);
        snapshot = ((InstallCatalogState)store.ReadAppliedValue(store.Resolve(InstallCatalogStateContract.StateKey))!).Catalogs[^1];
        AssertTrue(snapshot.Unsupported is not null); AssertTrue(snapshot.Error is null); AssertTrue(!snapshot.Loading);
    }
    private sealed class DelayedCatalogSource : IInstallCatalogSource
    {
        public TaskCompletionSource<IReadOnlyList<InstallCatalogVersion>> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<InstallCatalogVersion>> Second { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<InstallCatalogVersion>> GetGamesAsync(CancellationToken token) => First.Task;
        public Task<IReadOnlyList<InstallCatalogVersion>> GetLoadersAsync(InstallLoader loader, string game, CancellationToken token) => game == "1.20.1" ? First.Task : Second.Task;
    }
    private sealed class CatalogFixtureHandler(bool failModrinth = false, bool failCurseForge = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string host = request.RequestUri!.Host;
            if (host == "mod.mcimirror.top") AssertTrue(!request.Headers.Contains("x-api-key"));
            if (failModrinth && host == "api.modrinth.com" || failCurseForge && host is "api.curseforge.com" or "mod.mcimirror.top")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            string body = host switch
            {
                "api.modrinth.com" => """[{"version_number":"0.92.0","version_type":"release","files":[{"filename":"fixture.jar","url":"https://cdn.modrinth.com/fixture.jar","hashes":{"sha1":"abc"}}]}]""",
                "mod.mcimirror.top" or "api.curseforge.com" => """{"data":[{"id":123,"fileName":"fixture.jar","downloadUrl":"https://edge.forgecdn.net/fixture.jar","releaseType":1,"hashes":[{"algo":1,"value":"abc"}],"gameVersions":["1.20.1"]}]}""",
                "piston-meta.mojang.com" => """{"versions":[{"id":"26.1","type":"release"}]}""",
                "maven.minecraftforge.net" => "<metadata><version>1.20.1-47.2.0</version><version>1.20.2-48.0.0</version></metadata>",
                "maven.neoforged.net" => "<metadata><version>21.1.7</version><version>21.2.0-beta</version></metadata>",
                "api.github.com" => """[{"tag_name":"0.3.1","draft":false,"prerelease":false}]""",
                "optifine.net" => "OptiFine_1.20.1_HD_U_I6.jar OptiFine_1.20.2_HD_U_I7.jar",
                "dl.liteloader.com" => """{"versions":{"1.12.2":{"snapshots":{"com.mumfrey:liteloader":{"latest":{"version":"1.12.2-SNAPSHOT","stream":"SNAPSHOT"}}}}}}""",
                "releases.r2.labymod.net" => """{"minecraftVersions":[{"version":"1.20.1"}],"labyModVersion":"4.3","commitReference":"abc"}""",
                _ => """[{"loader":{"version":"0.16.0","stable":true}}]"""
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}

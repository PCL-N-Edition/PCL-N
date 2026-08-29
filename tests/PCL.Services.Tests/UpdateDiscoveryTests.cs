using System.Net;
using System.Text;
using PCL.Services.Updates;

namespace PCL.Services.Tests;

// XSR-510: update discovery and transport — index fetching with the patch-index/index.json
// preference and GitHub fallback, the multi-tag hop walk, the HEAD size probe, and the
// eligibility gate running before any network. A stub handler fixture-izes the transport.
internal static partial class Program
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, HttpResponseMessage> _responses = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Requests { get; } = [];

        public void Serve(string url, string json, HttpStatusCode code = HttpStatusCode.OK)
        {
            _responses[url] = new HttpResponseMessage(code)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }

        public void ServeHead(string url, long contentLength)
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([]),
            };
            response.Content.Headers.ContentLength = contentLength;
            _responses[url] = response;
        }

        public int CountOf(string substring) => Requests.Count(request => request.Contains(substring, StringComparison.OrdinalIgnoreCase));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.Method + " " + request.RequestUri);
            if (_responses.TryGetValue(request.RequestUri!.ToString(), out HttpResponseMessage? response))
            {
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static UpdateDistributionOptions DiscoveryOptions(bool cloudflareOnly = true) => new(
        DistributionBaseUrl: "https://dist.example/v1/updates/releases",
        CloudflareOnly: cloudflareOnly,
        Owner: "example-org",
        Repo: "example-repo");

    private static UpdateDiscoveryService CreateService(StubHandler handler, bool cloudflareOnly = true) =>
        new(new HttpClient(handler), DiscoveryOptions(cloudflareOnly));

    private const string TargetIndexJson = """
        {
          "formatVersion": 2,
          "targetVersion": "1.4.12",
          "targetTag": "v1.4.12",
          "strategy": {
            "maxDirectFromVersions": 11,
            "hopInterval": 10,
            "upgradeMode": "hops",
            "selectedFromTags": ["v1.4.11"]
          },
          "variants": [
            {
              "runtimeId": "win-x64",
              "runtimeVariant": "SelfContained",
              "targetAssetName": "PCL_N_Release_win-x64_SelfContained.zip",
              "targetSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "targetSize": 80000,
              "patches": [
                {
                  "fromVersion": "1.4.11",
                  "algorithm": "hdiffpatch",
                  "fileName": "patch-1.4.11.bundle",
                  "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                  "size": 4000000,
                  "fromSha256": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
                  "fromSize": 79000
                }
              ]
            }
          ]
        }
        """;

    private const string MiddleIndexJson = """
        {
          "formatVersion": 2,
          "targetVersion": "1.4.11",
          "targetTag": "v1.4.11",
          "variants": [
            {
              "runtimeId": "win-x64",
              "runtimeVariant": "SelfContained",
              "targetAssetName": "PCL_N_Release_win-x64_SelfContained.zip",
              "targetSha256": "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
              "targetSize": 70000,
              "patches": [
                {
                  "fromVersion": "1.4.10",
                  "algorithm": "hdiffpatch",
                  "fileName": "patch-1.4.10.bundle",
                  "sha256": "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                  "size": 1000000,
                  "fromSha256": "9999999999999999999999999999999999999999999999999999999999999999",
                  "fromSize": 69000
                }
              ]
            }
          ]
        }
        """;

    internal static async ValueTask IndexFetchFollowsPreferenceAndFallbackRules()
    {
        StubHandler handler = new();
        UpdateDiscoveryService service = CreateService(handler);
        string patchIndexUrl = "https://dist.example/v1/updates/releases/v1.4.12/patch-index.json";
        string aliasUrl = "https://dist.example/v1/updates/releases/v1.4.12/index.json";

        // Alias alone is accepted when the preferred file is missing.
        handler.Serve(aliasUrl, TargetIndexJson);
        UpdatePatchIndexSource? viaAlias = await service.TryLoadPatchIndexAsync("v1.4.12");
        AssertTrue(viaAlias is not null);
        AssertEqual("1.4.12", viaAlias!.Index.TargetVersion);

        // Preferred file wins when both exist: the alias is not requested a second time.
        int requestsBefore = handler.Requests.Count;
        handler.Serve(patchIndexUrl, TargetIndexJson);
        UpdatePatchIndexSource? viaPreferred = await service.TryLoadPatchIndexAsync("v1.4.12");
        AssertTrue(viaPreferred is not null);
        IEnumerable<string> secondPass = handler.Requests.Skip(requestsBefore);
        AssertTrue(secondPass.Any(request => request.Contains("patch-index.json", StringComparison.Ordinal)));
        AssertFalse(secondPass.Any(request => request.EndsWith("/index.json", StringComparison.Ordinal)));

        // Both missing: null.
        StubHandler emptyHandler = new();
        UpdateDiscoveryService emptyService = CreateService(emptyHandler);
        AssertNull(await emptyService.TryLoadPatchIndexAsync("v1.4.12"));

        // Unusable bodies: malformed JSON, out-of-range format version, no variants.
        StubHandler unusable = new();
        UpdateDiscoveryService unusableService = CreateService(unusable);
        unusable.Serve(aliasUrl, "{ not json");
        AssertNull(await unusableService.TryLoadPatchIndexAsync("v1.4.12"));
        unusable.Serve(aliasUrl, """{"formatVersion": 4, "variants": [{"runtimeId": "win-x64"}]}""");
        AssertNull(await unusableService.TryLoadPatchIndexAsync("v1.4.12"));
        unusable.Serve(aliasUrl, """{"formatVersion": 2, "variants": []}""");
        AssertNull(await unusableService.TryLoadPatchIndexAsync("v1.4.12"));
    }

    internal static async ValueTask GithubFallbackUrlIsTriedOnlyWhenEnabled()
    {
        // Cloudflare-only: the GitHub URL is never requested.
        StubHandler cloudflare = new();
        UpdateDiscoveryService cloudflareService = CreateService(cloudflare, cloudflareOnly: true);
        await cloudflareService.TryLoadPatchIndexAsync("v1.4.12");
        AssertFalse(cloudflare.Requests.Any(request => request.Contains("github.com", StringComparison.Ordinal)));

        // With a GitHub-capable origin, both URLs are tried and the GitHub one can answer.
        StubHandler both = new();
        UpdateDiscoveryService bothService = CreateService(both, cloudflareOnly: false);
        both.Serve(
            "https://github.com/example-org/example-repo/releases/download/v1.4.12/patch-index.json",
            TargetIndexJson);
        UpdatePatchIndexSource? loaded = await bothService.TryLoadPatchIndexAsync("v1.4.12");
        AssertTrue(loaded is not null);
        AssertTrue(both.Requests.Any(request => request.Contains("github.com", StringComparison.Ordinal)));
    }

    internal static async ValueTask EligibilityGatesBeforeAnyNetwork()
    {
        StubHandler handler = new();
        UpdateDiscoveryService service = CreateService(handler);
        UpdateBuildIdentity identity = new(
            Version: "2.0.0.alpha.2",
            RuntimeId: "win-x64",
            RuntimeVariant: "SelfContained",
            Configuration: "Release");

        UpdateDiscoveryResult downgrade = await service.ResolveAsync(
            "v1.4.12", "1.4.12", UpdateChannel.Release, identity);
        AssertEqual(UpdateEligibilityDecision.Downgrade, downgrade.Decision);
        AssertNull(downgrade.Package);
        AssertEqual(0, handler.Requests.Count);

        UpdateDiscoveryResult same = await service.ResolveAsync(
            "v2.0.0.alpha.2", "2.0.0.alpha.2", UpdateChannel.Release, identity);
        AssertEqual(UpdateEligibilityDecision.SameVersion, same.Decision);
        AssertEqual(0, handler.Requests.Count);

        UpdateDiscoveryResult unrecognized = await service.ResolveAsync(
            "unknown-tag", "also-bad", UpdateChannel.Release, identity);
        AssertEqual(UpdateEligibilityDecision.Unrecognized, unrecognized.Decision);
        AssertEqual(0, handler.Requests.Count);
    }

    internal static async ValueTask BaselineAndSingleFileSkipIndexFetch()
    {
        // Before the 1.4.3 block-update baseline: full package without any block map addresses,
        // and no index fetch at all.
        StubHandler before = new();
        UpdateDiscoveryService beforeService = CreateService(before);
        UpdateBuildIdentity old = new(
            Version: "1.4.2",
            RuntimeId: "win-x64",
            RuntimeVariant: "SelfContained",
            Configuration: "Release");
        UpdateDiscoveryResult oldResult = await beforeService.ResolveAsync("v2.0.0.alpha.1", "2.0.0.alpha.1", UpdateChannel.Release, old);
        AssertTrue(oldResult.IsAllowed);
        AssertTrue(oldResult.Package!.UsesPatch == false);
        AssertNull(oldResult.Package.BlockMapUrl);
        AssertNull(oldResult.Package.BlockMapFallbackUrl);
        AssertEqual(0, before.Requests.Count);

        // Single-file layout: full package that keeps its block map, still without index fetch.
        StubHandler portable = new();
        UpdateDiscoveryService portableService = CreateService(portable);
        UpdateBuildIdentity single = new(
            Version: "2.0.0.alpha.2",
            RuntimeId: "win-x64",
            RuntimeVariant: "SelfContained",
            Configuration: "Release")
        {
            DistributionLayout = UpdateDistributionLayout.SingleFile,
        };
        UpdateDiscoveryResult portableResult = await portableService.ResolveAsync(
            "v2.0.0.alpha.3", "2.0.0.alpha.3", UpdateChannel.Release, single);
        AssertTrue(portableResult.IsAllowed);
        AssertEqual("PCL_N_Release_win-x64_SelfContained_Portable.exe", portableResult.Package!.TargetAssetName);
        AssertTrue(portableResult.Package.SupportsBlockMap);
        AssertEqual(0, portable.Requests.Count);
    }

    internal static async ValueTask MultiTagWalkLoadsPreviousIndexesUntilPathFound()
    {
        StubHandler handler = new();
        UpdateDiscoveryService service = CreateService(handler);
        handler.Serve("https://dist.example/v1/updates/releases/v1.4.12/patch-index.json", TargetIndexJson);
        handler.Serve("https://dist.example/v1/updates/releases/v1.4.11/patch-index.json", MiddleIndexJson);
        handler.ServeHead(
            "https://dist.example/v1/updates/releases/v1.4.12/PCL_N_Release_win-x64_SelfContained.zip",
            6_000_000);

        UpdateBuildIdentity identity = new(
            Version: "1.4.10",
            RuntimeId: "win-x64",
            RuntimeVariant: "SelfContained",
            Configuration: "Release");
        UpdateDiscoveryResult result = await service.ResolveAsync("v1.4.12", "1.4.12", UpdateChannel.Release, identity);

        AssertTrue(result.IsAllowed);
        AssertTrue(result.Package!.UsesPatch);
        AssertEqual(2, result.Package.PatchSteps.Count);
        AssertEqual("1.4.10", result.Package.PatchSteps[0].FromVersion);
        AssertEqual("1.4.11", result.Package.PatchSteps[0].TargetVersion);
        AssertEqual("1.4.11", result.Package.PatchSteps[1].FromVersion);
        AssertEqual("1.4.12", result.Package.PatchSteps[1].TargetVersion);
        AssertTrue(handler.Requests.Any(request => request.Contains("v1.4.11/patch-index", StringComparison.Ordinal)));
        AssertTrue(handler.Requests.Any(request => request.StartsWith("HEAD", StringComparison.Ordinal)));

        // The HEAD size (6 MB) is larger than the 5 MB chain, so the patch survives.
        AssertTrue(result.Package.PatchSteps.Sum(static step => step.Size) < 6_000_000);
    }

    internal static async ValueTask WalkStopsWhenPreviousTargetIsNotNewer()
    {
        StubHandler handler = new();
        UpdateDiscoveryService service = CreateService(handler);
        handler.Serve("https://dist.example/v1/updates/releases/v1.4.12/patch-index.json", TargetIndexJson);
        // The only selected previous tag targets 1.4.1, which is not newer than the running
        // 1.4.1 and offers no edge; the walk must stop after it.
        handler.Serve(
            "https://dist.example/v1/updates/releases/v1.4.11/patch-index.json",
            """{"formatVersion": 2, "targetVersion": "1.4.10", "variants": [{"runtimeId": "win-x64", "runtimeVariant": "SelfContained", "targetAssetName": "a.zip", "targetSha256": "aa", "patches": []}]}""");

        UpdateBuildIdentity identity = new(
            Version: "1.4.10",
            RuntimeId: "win-x64",
            RuntimeVariant: "SelfContained",
            Configuration: "Release");
        UpdateDiscoveryResult result = await service.ResolveAsync("v1.4.12", "1.4.12", UpdateChannel.Release, identity);

        AssertTrue(result.IsAllowed);
        AssertFalse(result.Package!.UsesPatch);
        AssertEqual(1, handler.CountOf("v1.4.11/patch-index"));
        AssertFalse(handler.Requests.Any(request => request.StartsWith("HEAD", StringComparison.Ordinal)));
    }

    internal static async ValueTask HeadFailureFallsBackToIndexArchiveSize()
    {
        StubHandler handler = new();
        UpdateDiscoveryService service = CreateService(handler);
        // No HEAD response registered: the probe 404s, so the planner compares the 4 MB chain
        // against the variant archive size of 90 MB and keeps the patch.
        handler.Serve("https://dist.example/v1/updates/releases/v1.4.12/patch-index.json", TargetIndexJson);
        handler.Serve("https://dist.example/v1/updates/releases/v1.4.11/patch-index.json", MiddleIndexJson);

        UpdateBuildIdentity identity = new(
            Version: "1.4.10",
            RuntimeId: "win-x64",
            RuntimeVariant: "SelfContained",
            Configuration: "Release");
        UpdateDiscoveryResult result = await service.ResolveAsync("v1.4.12", "1.4.12", UpdateChannel.Release, identity);

        AssertTrue(result.IsAllowed);
        AssertTrue(result.Package!.UsesPatch);
        AssertTrue(result.Package.PatchSteps.Sum(static step => step.Size) > 0);
    }
}

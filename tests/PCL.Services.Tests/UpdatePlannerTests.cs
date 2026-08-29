using System.Text.Json;
using PCL.Services.Updates;

namespace PCL.Services.Tests;

// XSR-509: update package planning — variant selection, cheapest patch path, the
// patch-not-worthwhile rule, asset/URL composition, and the v1-blockmap window. The planner
// is pure: fixtures stand in for fetched indexes and HEAD sizes.
internal static partial class Program
{
    private static UpdatePlannerOptions PlannerOptions(bool cloudflareOnly = true, string baseUrl = "https://dist.example/v1/updates/releases") =>
        new(baseUrl, cloudflareOnly);

    private static UpdateBuildIdentity ScatterIdentity(string version = "1.4.11") => new(
        Version: version,
        RuntimeId: "win-x64",
        RuntimeVariant: "SelfContained",
        Configuration: "Release");

    private static UpdatePatchIndexSource SampleIndex(
        string targetVersion = "1.4.12",
        string fromVersion = "1.4.11",
        string algorithm = "hdiffpatch",
        long patchSize = 4_000_000,
        long targetArchiveSize = 90_000_000,
        string runtimeId = "win-x64",
        string runtimeVariant = "SelfContained") => new(
        "v" + targetVersion,
        new UpdatePatchIndexDto
        {
            FormatVersion = 2,
            TargetVersion = targetVersion,
            TargetTag = "v" + targetVersion,
            Strategy = new UpdatePatchStrategyDto
            {
                MaxDirectFromVersions = 11,
                HopInterval = 10,
                UpgradeMode = "hops",
                SelectedFromTags = ["v1.4.1"],
            },
            Variants =
            [
                new UpdatePatchVariantDto
                {
                    RuntimeId = runtimeId,
                    RuntimeVariant = runtimeVariant,
                    Configuration = "release",
                    TargetAssetName = $"PCL_N_Release_{runtimeId}_SelfContained.zip",
                    TargetBinaryName = "PCL-N-Edition.exe",
                    TargetSha256 = new string('a', 64),
                    TargetSize = 80_000_000,
                    TargetArchiveSize = targetArchiveSize,
                    TargetFileCount = 120,
                    Patches =
                    [
                        new UpdatePatchDto
                        {
                            FromVersion = fromVersion,
                            Algorithm = algorithm,
                            FileName = @"patches\patch-" + fromVersion + ".bundle",
                            Sha256 = new string('b', 64),
                            Size = patchSize,
                            FromSha256 = new string('c', 64),
                            FromSize = 79_000_000,
                        },
                    ],
                },
            ],
        });

    internal static void PatchIndexDeserializesLegacyJson()
    {
        const string json = """
            {
              "formatVersion": 2,
              "targetVersion": "1.4.12",
              "targetTag": "v1.4.12",
              "strategy": {
                "maxDirectFromVersions": 11,
                "hopInterval": 10,
                "upgradeMode": "hops",
                "selectedFromTags": ["v1.4.1"]
              },
              "variants": [
                {
                  "runtimeId": "win-x64",
                  "runtimeVariant": "SelfContained",
                  "configuration": "release",
                  "targetAssetName": "PCL_N_Release_win-x64_SelfContained.zip",
                  "targetSha256": "aaaa",
                  "targetSize": 80000,
                  "targetArchiveSize": 90000,
                  "patches": [
                    {
                      "fromVersion": "1.4.11",
                      "algorithm": "hdiffpatch-scatter-v1",
                      "fileName": "p.bundle",
                      "sha256": "bbbb",
                      "size": 1000,
                      "fromSha256": "cccc",
                      "fromSize": 99000
                    }
                  ]
                }
              ]
            }
            """;

        UpdatePatchIndexDto? index = JsonSerializer.Deserialize(json, UpdateJsonContext.Default.UpdatePatchIndexDto);
        AssertTrue(index is not null);
        UpdatePatchIndexDto parsed = index!;
        AssertEqual(2, parsed.FormatVersion);
        AssertEqual("1.4.12", parsed.TargetVersion);
        AssertEqual("v1.4.1", parsed.Strategy!.SelectedFromTags![0]);
        UpdatePatchVariantDto variant = parsed.Variants![0];
        AssertEqual("win-x64", variant.RuntimeId);
        AssertEqual("release", variant.Configuration);
        AssertEqual("hdiffpatch-scatter-v1", variant.Patches![0].Algorithm);
        AssertEqual(1000, variant.Patches[0].Size);
    }

    internal static void BuildIdentityNormalizesVariantsAndConfigurations()
    {
        AssertEqual("NoRuntime", UpdateBuildIdentity.NormalizeRuntimeVariant("NoRuntime"));
        AssertEqual("NoRuntime", UpdateBuildIdentity.NormalizeRuntimeVariant("noruntime-with-plugin"));
        AssertEqual("SelfContained", UpdateBuildIdentity.NormalizeRuntimeVariant("SelfContained"));
        AssertEqual("SelfContained", UpdateBuildIdentity.NormalizeRuntimeVariant("with-plugin"));
        AssertEqual("SelfContained", UpdateBuildIdentity.NormalizeRuntimeVariant(null));
        AssertEqual("Release", UpdateBuildIdentity.NormalizeConfiguration(" stable "));
        AssertEqual("Beta", UpdateBuildIdentity.NormalizeConfiguration("rc"));
        AssertEqual("Beta", UpdateBuildIdentity.NormalizeConfiguration("preview"));
        AssertEqual("CI", UpdateBuildIdentity.NormalizeConfiguration("whatever"));
    }

    internal static void FullPackageCompositionMatchesLayoutAndChannel()
    {
        UpdatePackagePlanner planner = new(PlannerOptions(cloudflareOnly: true));
        UpdatePackage release = planner.PlanFull("v1.4.12", UpdateChannel.Release, ScatterIdentity());
        AssertEqual("PCL_N_Release_win-x64_SelfContained.zip", release.TargetAssetName);
        AssertEqual("https://dist.example/v1/updates/releases/v1.4.12/PCL_N_Release_win-x64_SelfContained.zip", release.FullPackageUrl);
        AssertEqual(release.FullPackageUrl + ".asc", release.FullPackageSignatureUrl);
        AssertEqual(release.FullPackageUrl + ".binary.asc", release.TargetBinarySignatureUrl);
        AssertEqual("PCL-N-Edition.exe", release.TargetBinaryName);
        AssertTrue(release.SupportsBlockMap);
        // The stem keeps the legacy rule: only the final extension is stripped.
        AssertEqual(
            "https://dist.example/v1/updates/releases/v1.4.12/PCL_N_Release_win-x64_SelfContained.blockmap.v2.json",
            release.BlockMapUrl);
        AssertNull(release.BlockMapFallbackUrl);

        // A tag at or below 1.4.7 still offers the v1 fallback map.
        UpdatePackage legacyTag = planner.PlanFull("v1.4.7", UpdateChannel.Release, ScatterIdentity());
        AssertTrue(legacyTag.BlockMapFallbackUrl is not null);

        UpdatePackage linux = planner.PlanFull("v1.4.12", UpdateChannel.Release, ScatterIdentity() with { RuntimeId = "linux-x64" });
        AssertTrue(linux.TargetAssetName.EndsWith(".tar.gz", StringComparison.Ordinal));
        AssertEqual("PCL-N-Edition", linux.TargetBinaryName);
        AssertEqual(linux.FullPackageUrl + ".binary.asc", linux.TargetBinarySignatureUrl);

        UpdatePackage portable = planner.PlanFull(
            "v1.4.12",
            UpdateChannel.Release,
            ScatterIdentity() with { DistributionLayout = UpdateDistributionLayout.SingleFile });
        AssertEqual("PCL_N_Release_win-x64_SelfContained_Portable.exe", portable.TargetAssetName);
        AssertEqual(portable.FullPackageUrl + ".asc", portable.TargetBinarySignatureUrl);

        UpdatePackage ci = planner.PlanFull("ci-latest", UpdateChannel.CI, ScatterIdentity());
        AssertEqual("PCL_N_CI_win-x64_SelfContained.zip", ci.TargetAssetName);
        AssertEqual("CI", ci.Configuration);

        UpdatePackage beta = planner.PlanFull("v2.0.0-beta.1", UpdateChannel.Beta, ScatterIdentity());
        AssertEqual("Beta", beta.Configuration);
        AssertEqual("2.0.0-beta.1", beta.TargetVersion);

        // GitHub distribution keeps signatures but never block maps.
        UpdatePackage github = new UpdatePackagePlanner(PlannerOptions(cloudflareOnly: false, baseUrl: "https://github.com/o/r/releases/download"))
            .PlanFull("v1.4.12", UpdateChannel.Release, ScatterIdentity());
        AssertFalse(github.SupportsBlockMap);
        AssertNull(github.BlockMapUrl);
        AssertEqual("https://github.com/o/r/releases/download/v1.4.12/PCL_N_Release_win-x64_SelfContained.zip", github.FullPackageUrl);
    }

    internal static void DirectPatchPathIsPlannedWithAssetUrlFallback()
    {
        UpdatePackagePlanner planner = new(PlannerOptions());
        UpdatePackage package = planner.PlanFromIndex(
            "v1.4.12",
            ScatterIdentity(),
            "1.4.11",
            [SampleIndex()])!;

        AssertTrue(package.UsesPatch);
        AssertEqual("1.4.12", package.TargetVersion);
        AssertEqual(1, package.PatchSteps.Count);
        UpdatePatchStep step = package.PatchSteps[0];
        AssertEqual("1.4.11", step.FromVersion);
        AssertEqual("1.4.12", step.TargetVersion);
        AssertEqual(4_000_000, step.Size);
        AssertEqual("hdiffpatch", step.Algorithm);
        AssertFalse(step.IsScatterBundle);
        AssertEqual(
            "https://dist.example/v1/updates/releases/v1.4.12/patch-1.4.11.bundle",
            step.DownloadUrl);
        AssertEqual(new string('a', 64), step.TargetSha256);
        AssertEqual(80_000_000, step.TargetSize);
        AssertEqual("Release", package.Configuration);
        AssertEqual("PCL-N-Edition.exe", package.TargetBinaryName);
    }

    internal static void CheaperPatchKindWinsAndUnworthwhileChainsFallBackToFull()
    {
        UpdatePackagePlanner planner = new(PlannerOptions());

        // Scatter bundle of 3 MB beats the legacy bundle of 4 MB.
        UpdatePatchIndexSource both = SampleIndex();
        both.Index.Variants![0].Patches!.Add(new UpdatePatchDto
        {
            FromVersion = "1.4.11",
            Algorithm = "hdiffpatch-scatter-v1",
            FileName = "patches/scatter-1.4.11.bundle",
            Sha256 = new string('d', 64),
            Size = 3_000_000,
            FromSha256 = new string('c', 64),
            FromSize = 79_000_000,
        });
        UpdatePackage scatterWins = planner.PlanFromIndex("v1.4.12", ScatterIdentity(), "1.4.11", [both])!;
        AssertTrue(scatterWins.PatchSteps[0].IsScatterBundle);
        AssertEqual(3_000_000, scatterWins.PatchSteps[0].Size);

        // A HEAD size smaller than the chain makes the patch pointless.
        UpdatePackage fullWins = planner.PlanFromIndex("v1.4.12", ScatterIdentity(), "1.4.11", [both], fullPackageBytes: 2_999_999)!;
        AssertFalse(fullWins.UsesPatch);
        AssertEqual(0, fullWins.PatchSteps.Count);
        AssertEqual(new string('a', 64), fullWins.TargetSha256);
        AssertEqual(80_000_000, fullWins.TargetSize);

        // Without a HEAD size the index archive size decides.
        UpdatePackage archiveWins = planner.PlanFromIndex("v1.4.12", ScatterIdentity(), "1.4.11", [SampleIndex(patchSize: 90_000_000)])!;
        AssertFalse(archiveWins.UsesPatch);
    }

    internal static void UnusableIndexesReturnNullForFullFallback()
    {
        UpdatePackagePlanner planner = new(PlannerOptions());

        AssertNull(planner.PlanFromIndex("v1.4.12", ScatterIdentity(), "1.4.11", []));

        // A different runtime id has no matching variant.
        AssertNull(planner.PlanFromIndex(
            "v1.4.12",
            ScatterIdentity() with { RuntimeId = "linux-arm64" },
            "1.4.11",
            [SampleIndex()]));
    }

    internal static void VariantMatchingNormalizesRuntimeVariants()
    {
        UpdatePackagePlanner planner = new(PlannerOptions());

        // The running build reports a legacy suffix spelling; the index says the canonical one.
        UpdateBuildIdentity legacySpelling = ScatterIdentity() with { RuntimeVariant = "NoRuntime" };
        UpdatePackage package = planner.PlanFromIndex(
            "v1.4.12",
            legacySpelling,
            "1.4.11",
            [SampleIndex(runtimeVariant: "noruntime-with-plugin")])!;

        AssertTrue(package.UsesPatch);
        AssertEqual("NoRuntime", package.RuntimeVariant);
    }

    internal static void MultiHopPathIsPlannedAcrossIndexes()
    {
        UpdatePackagePlanner planner = new(PlannerOptions());
        UpdateBuildIdentity identity = ScatterIdentity();

        // Current 1.4.1 cannot reach 1.4.12 through one index; the walker provides both
        // indexes and the graph routes 1.4.1 → 1.4.2 → 1.4.12.
        UpdatePatchIndexSource middle = new(
            "v1.4.2",
            new UpdatePatchIndexDto
            {
                FormatVersion = 2,
                TargetVersion = "1.4.2",
                TargetTag = "v1.4.2",
                Variants =
                [
                    new UpdatePatchVariantDto
                    {
                        RuntimeId = "win-x64",
                        RuntimeVariant = "SelfContained",
                        TargetAssetName = "PCL_N_Release_win-x64_SelfContained.zip",
                        TargetSha256 = new string('e', 64),
                        TargetSize = 70_000_000,
                        Patches =
                        [
                            new UpdatePatchDto
                            {
                                FromVersion = "1.4.1",
                                Algorithm = "hdiffpatch",
                                FileName = "patch-1.4.1.bundle",
                                Sha256 = new string('f', 64),
                                Size = 1_000_000,
                                FromSha256 = new string('9', 64),
                                FromSize = 69_000_000,
                            },
                        ],
                    },
                ],
            });

        // The target index's cheapest edge starts at 1.4.2, chaining with the middle hop.
        UpdatePackage package = planner.PlanFromIndex(
            "v1.4.12",
            identity,
            "1.4.1",
            [middle, SampleIndex(fromVersion: "1.4.2", patchSize: 4_000_000)])!;

        AssertTrue(package.UsesPatch);
        AssertEqual(2, package.PatchSteps.Count);
        AssertEqual("1.4.1", package.PatchSteps[0].FromVersion);
        AssertEqual("1.4.2", package.PatchSteps[0].TargetVersion);
        AssertEqual("1.4.2", package.PatchSteps[1].FromVersion);
        AssertEqual("1.4.12", package.PatchSteps[1].TargetVersion);
        AssertEqual(1_000_000 + 4_000_000, package.PatchSteps.Sum(static step => step.Size));
    }

    internal static void BlockMapWindowsAndBaselineGateTheShortcuts()
    {
        AssertTrue(UpdatePackagePlanner.EmitsV1BlockMap("v1.4.7"));
        AssertFalse(UpdatePackagePlanner.EmitsV1BlockMap("v1.4.8"));
        AssertFalse(UpdatePackagePlanner.EmitsV1BlockMap("v2.0.0.alpha.1"));
        AssertFalse(UpdatePackagePlanner.EmitsV1BlockMap("ci-latest"));
        AssertFalse(UpdatePackagePlanner.EmitsV1BlockMap("latest"));
        AssertTrue(UpdatePackagePlanner.EmitsV1BlockMap("mystery-tag"));

        AssertTrue(UpdatePackagePlanner.IsBeforeBlockUpdaterBaseline("1.4.2"));
        AssertFalse(UpdatePackagePlanner.IsBeforeBlockUpdaterBaseline("1.4.3"));
        AssertFalse(UpdatePackagePlanner.IsBeforeBlockUpdaterBaseline("2.0.0.alpha.1"));

        // Legacy normalization parity: display shapes collapse to the stable core.
        AssertEqual("1.1.8-release", UpdatePackagePlanner.NormalizeVersion(" 1.1.8 release "));
        AssertEqual("1.1.8", UpdatePackagePlanner.NormalizeVersion("v1.1.8+build.7"));
    }
}

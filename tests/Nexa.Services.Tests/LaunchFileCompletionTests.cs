using System.Text.Json.Nodes;
using Nexa.Services.Downloads;
using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Assets;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Libraries;
using Nexa.Xsr.State;

[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Cryptographic Do Not Use",
    "CA5350:DoNotUseWeakCryptographicAlgorithms",
    Justification = "Fixture integrity facts use SHA-1 to match the Mojang metadata contract.")]

namespace Nexa.Services.Tests;

// The legacy 补全文件 contract: before the JVM starts, every referenced file is verified and
// repaired — the client jar, the asset index, asset objects, and the whole inheritance chain's
// libraries. A missing library used to kill the JVM before its window appeared.
internal static partial class Program
{
    private sealed class CompletionProgressRecorder(XsrStateStore store)
        : MinecraftLaunchProgressPublisher(store)
    {
        public List<double> Reports { get; } = [];

        public override void Report(MinecraftLaunchStageReport report)
        {
            Reports.Add(report.Progress);
            base.Report(report);
        }
    }

    private sealed class CompletionFixture : IDisposable
    {
        public XsrStateStore Store;
        public DownloadService Downloads;
        public MinecraftLaunchFileCompletion Completion;
        public List<string> RequestedUrls = [];
        public CompletionProgressRecorder? Progress;
        public string Root;

        public CompletionFixture()
        {
            XsrStateStoreBuilder builder = new();
            DownloadService.DeclareState(builder);
            MinecraftLaunchProgressState.DeclareState(builder);
            Store = builder.Build();
            Downloads = new(Store);
            Progress = new CompletionProgressRecorder(Store);
            Root = Path.Combine(Path.GetTempPath(), "nexa-completion-tests", Guid.NewGuid().ToString("N"));
            Completion = new(
                Downloads,
                connectionFactory: source =>
                {
                    lock (RequestedUrls) RequestedUrls.Add(source);
                    return new ServingConnection("REPAIRED"u8.ToArray());
                });
        }

        public void Dispose()
        {
            Completion.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private static async ValueTask LaunchCompletionRepairsMissingFilesBeforeStart()
    {
        using CompletionFixture fixture = new();
        string versionsRoot = Path.Combine(fixture.Root, "versions");
        string gameDirectory = Path.Combine(versionsRoot, "1.20.1");
        Directory.CreateDirectory(gameDirectory);
        await File.WriteAllTextAsync(Path.Combine(gameDirectory, "1.20.1.json"), ("""
        {
          "id": "1.20.1",
          "assetIndex": {
            "id": "5",
            "url": "https://piston-meta.mojang.com/v1/packages/index/5.json",
            "sha1": "__INDEX_SHA__",
            "size": 90,
            "totalSize": 90
          },
          "downloads": {
            "client": {
              "url": "https://piston-data.mojang.com/v1/objects/client.jar",
              "sha1": "__JAR_SHA__",
              "size": 8
            }
          },
          "libraries": [
            {
              "name": "com.example:present:1.0.0",
              "downloads": { "artifact": {
                "path": "com/example/present/1.0.0/present-1.0.0.jar",
                "url": "https://libraries.minecraft.net/com/example/present.jar",
                "sha1": "__PRESENT_SHA__",
                "size": 13 } }
            },
            {
              "name": "com.example:missing:1.0.0",
              "downloads": { "artifact": {
                "path": "com/example/missing/1.0.0/missing-1.0.0.jar",
                "url": "https://libraries.minecraft.net/com/example/missing.jar",
                "sha1": "__MISSING_SHA__",
                "size": 8 } }
            }
          ]
        }
        """.Replace("__INDEX_SHA__", Sha1Hex("index"))
           .Replace("__JAR_SHA__", Sha1Hex("REPAIRED"))
           .Replace("__PRESENT_SHA__", Sha1Hex("ALREADY-THERE"))
           .Replace("__MISSING_SHA__", Sha1Hex("REPAIRED"))));

        // Present files stay untouched; the client jar, the missing library, the asset index,
        // and one asset object are gone.
        string presentJar = Path.Combine(
            fixture.Root, "libraries", "com", "example", "present", "1.0.0", "present-1.0.0.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(presentJar)!);
        await File.WriteAllTextAsync(presentJar, "ALREADY-THERE");
        string assetHash = Sha1Hex("ASSET-OK");
        string assetPath = Path.Combine(fixture.Root, "assets", "objects", assetHash[..2], assetHash);
        Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
        await File.WriteAllTextAsync(assetPath, "ASSET-OK");
        Directory.CreateDirectory(Path.Combine(fixture.Root, "assets", "indexes"));
        string corruptAssetPath = Path.Combine(
            fixture.Root, "assets", "objects", Sha1Hex("REPAIRED")[..2], Sha1Hex("REPAIRED"));
        Directory.CreateDirectory(Path.GetDirectoryName(corruptAssetPath)!);
        await File.WriteAllTextAsync(corruptAssetPath, "CORRUPTED-BYTES");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "assets", "indexes", "5.json"), ("""
        { "objects": {
            "minecraft/sounds/gone.ogg": {
              "hash": "__GONE_SHA__",
              "size": 8 }
        } }
        """.Replace("__GONE_SHA__", Sha1Hex("REPAIRED"))));

        MinecraftInstanceDescriptor instance = new(
            "1.20.1",
            gameDirectory,
            "1.20.1",
            new MinecraftVersionDescriptor(
                "1.20.1",
                gameDirectory,
                Path.Combine(gameDirectory, "1.20.1.json"),
                JarPath: null,
                InheritsFrom: null,
                MainClass: null,
                ReleaseTime: null,
                Classification: new MinecraftVersionClassification("1.20.1", "release", MinecraftVersionCategory.Release, null)),
            new MinecraftInstanceMetadata());
        MinecraftResolvedVersionManifests manifests = await MinecraftVersionJsonReader.ResolveAsync(
            instance, fixture.Root);

        await fixture.Completion.CompleteAsync(
            fixture.Root,
            instance,
            manifests,
            new MinecraftLaunchPlatform(
                MinecraftLibraryOperatingSystem.Win32,
                "10.0.26100",
                Is64BitArchitecture: true,
                IsArm64Architecture: false),
            method: "offline",
            progress: fixture.Progress,
            CancellationToken.None);

        // The repair reports stay inside the complete_files band and never regress: raw
        // weights would clamp past 1.0 and flash 100% mid-repair (real-device report).
        double stageCeiling = MinecraftLaunchStages.ProgressAt(
            MinecraftLaunchStages.LoginWeight + MinecraftLaunchStages.CompleteFilesWeight);
        List<double> reports = fixture.Progress!.Reports;
        AssertTrue(reports.Count > 0);
        foreach (double progress in reports)
        {
            AssertTrue(progress <= stageCeiling + 0.0001, $"progress {progress:P1} exceeded the stage ceiling");
        }

        for (int index = 1; index < reports.Count; index++)
        {
            AssertTrue(reports[index] >= reports[index - 1] - 0.0001,
                $"progress regressed from {reports[index - 1]:P1} to {reports[index]:P1}");
        }

        string missingJar = Path.Combine(
            fixture.Root, "libraries", "com", "example", "missing", "1.0.0", "missing-1.0.0.jar");
        AssertTrue(File.Exists(missingJar));
        AssertTrue(File.Exists(Path.Combine(gameDirectory, "1.20.1.jar")));
        string repairedAsset = Path.Combine(
            fixture.Root, "assets", "objects", Sha1Hex("REPAIRED")[..2],
            Sha1Hex("REPAIRED"));
        AssertTrue(File.Exists(repairedAsset));
        AssertEqual("REPAIRED", await File.ReadAllTextAsync(repairedAsset));
        // Present files are never re-downloaded.
        AssertEqual("ALREADY-THERE", await File.ReadAllTextAsync(presentJar));
        AssertEqual("ASSET-OK", await File.ReadAllTextAsync(assetPath));
        lock (fixture.RequestedUrls)
        {
            AssertFalse(fixture.RequestedUrls.Any(url => url.Contains("present.jar", StringComparison.Ordinal)));
        }
    }
}

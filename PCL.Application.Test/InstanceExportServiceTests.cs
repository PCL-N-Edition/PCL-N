// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Application.Instances;

namespace PCL.Application.Test;

[TestClass]
public sealed class InstanceExportServiceTests
{
    [TestMethod]
    public async Task ExportAsync_WritesModrinthIndexAndOverrides()
    {
        using TemporaryDirectory temporary = new();
        string gameDirectory = temporary.CreateDirectory("game");
        string instanceDirectory = temporary.CreateDirectory("instance");
        string archivePath = temporary.GetPath("example.mrpack");

        temporary.WriteFile("game/options.txt", "settings");
        temporary.WriteFile("game/config/client.toml", "enabled=true");
        temporary.WriteFile("instance/PCL/Setup.ini", "UiLauncherTheme=0");
        temporary.WriteFile("instance/instance.json", "{\"inheritsFrom\":\"1.20.1\"}");
        File.WriteAllText(archivePath, "previous archive");

        await InstanceExportService.ExportAsync(
            CreateRequest(instanceDirectory, gameDirectory, archivePath) with
            {
                PackageName = "Example Pack",
                PackageVersion = "2.4.1",
                Summary = "Example summary",
                Dependencies = new Dictionary<string, string>
                {
                    ["minecraft"] = "1.20.1",
                    ["fabric-loader"] = "0.16.10"
                },
                Rules = ["options.txt", "config/**"]
            });

        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        AssertEntry(archive, "overrides/options.txt", "settings");
        AssertEntry(archive, "overrides/config/client.toml", "enabled=true");
        AssertEntry(archive, "overrides/PCL/Setup.ini", "UiLauncherTheme=0");
        Assert.IsNull(archive.GetEntry("options.txt"));

        using JsonDocument manifest = ReadManifest(archive);
        JsonElement root = manifest.RootElement;
        Assert.AreEqual("minecraft", root.GetProperty("game").GetString());
        Assert.AreEqual(1, root.GetProperty("formatVersion").GetInt32());
        Assert.AreEqual("2.4.1", root.GetProperty("versionId").GetString());
        Assert.AreEqual("Example Pack", root.GetProperty("name").GetString());
        Assert.AreEqual("Example summary", root.GetProperty("summary").GetString());
        Assert.AreEqual(0, root.GetProperty("files").GetArrayLength());
        Assert.AreEqual("1.20.1", root.GetProperty("dependencies").GetProperty("minecraft").GetString());
        Assert.AreEqual("0.16.10", root.GetProperty("dependencies").GetProperty("fabric-loader").GetString());
        AssertNoTransactionFiles(temporary.Path, archivePath);
    }

    [TestMethod]
    public async Task ExportAsync_AppliesRulesInOrderAndDistinguishesSingleAndRecursiveWildcards()
    {
        using TemporaryDirectory temporary = new();
        string gameDirectory = temporary.CreateDirectory("game");
        string instanceDirectory = temporary.CreateDirectory("instance");
        string archivePath = temporary.GetPath("rules.mrpack");

        temporary.WriteFile("game/config/root.txt", "root");
        temporary.WriteFile("game/config/worldedit/worldedit.properties", "keep");
        temporary.WriteFile("game/config/worldedit/deeper/cache.txt", "exclude");
        temporary.WriteFile("game/single/direct.txt", "direct");
        temporary.WriteFile("game/single/nested/deep.txt", "deep");
        temporary.WriteFile("game/recursive/direct.txt", "direct");
        temporary.WriteFile("game/recursive/nested/deep.txt", "deep");
        temporary.WriteFile("game/kubejs-extra/startup_scripts/main.js", "script");
        temporary.WriteFile("game/unselected.txt", "unselected");

        await InstanceExportService.ExportAsync(
            CreateRequest(instanceDirectory, gameDirectory, archivePath) with
            {
                Rules =
                [
                    "config/**",
                    "!config/worldedit/**",
                    "config/worldedit/worldedit.properties",
                    "single/*",
                    "recursive/**",
                    "kubejs*/"
                ]
            });

        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        Assert.IsNotNull(archive.GetEntry("overrides/config/root.txt"));
        Assert.IsNotNull(archive.GetEntry("overrides/config/worldedit/worldedit.properties"));
        Assert.IsNull(archive.GetEntry("overrides/config/worldedit/deeper/cache.txt"));
        Assert.IsNotNull(archive.GetEntry("overrides/single/direct.txt"));
        Assert.IsNull(archive.GetEntry("overrides/single/nested/deep.txt"));
        Assert.IsNotNull(archive.GetEntry("overrides/recursive/direct.txt"));
        Assert.IsNotNull(archive.GetEntry("overrides/recursive/nested/deep.txt"));
        Assert.IsNotNull(archive.GetEntry("overrides/kubejs-extra/startup_scripts/main.js"));
        Assert.IsNull(archive.GetEntry("overrides/unselected.txt"));
    }

    [TestMethod]
    [DataRow("a/b", "a/**/b", true)]
    [DataRow("a/nested/b", "a/**/b", true)]
    [DataRow("file.txt", "**/file.txt", true)]
    [DataRow("nested/file.txt", "**/file.txt", true)]
    [DataRow("b.txt", "[!a].txt", true)]
    [DataRow("a.txt", "[!a].txt", false)]
    public void RuleMatches_FollowsUpstreamGlobSemantics(string path, string rule, bool expected)
    {
        Assert.AreEqual(expected, InstanceExportService.RuleMatches(path, rule));
    }

    [TestMethod]
    public async Task ExportAsync_SkipsRuntimeAndCacheDirectories()
    {
        using TemporaryDirectory temporary = new();
        string gameDirectory = temporary.CreateDirectory("game");
        string instanceDirectory = temporary.CreateDirectory("instance");
        string archivePath = temporary.GetPath("skip-directories.mrpack");

        temporary.WriteFile("game/options.txt", "settings");
        temporary.WriteFile("game/assets/indexes/1.json", "asset");
        temporary.WriteFile("game/versions/old/old.json", "version");
        temporary.WriteFile("game/libraries/example/library.jar", "library");
        temporary.WriteFile("game/structureCacheV1/cache.bin", "cache");
        temporary.WriteFile("game/.fabric/remapped.jar", "cache");
        temporary.WriteFile("game/.git/config", "git");
        temporary.WriteFile("game/avatar-cache/avatar.png", "cache");
        temporary.WriteFile("game/cosmetic-cache/cosmetic.png", "cache");
        temporary.WriteFile("game/config/.git/config", "git");
        temporary.WriteFile("game/config/structureCacheV1/cache.bin", "cache");
        temporary.WriteFile("game/config/assets/custom.json", "configuration");

        await InstanceExportService.ExportAsync(
            CreateRequest(instanceDirectory, gameDirectory, archivePath) with
            {
                Rules = ["**"]
            });

        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        Assert.IsNotNull(archive.GetEntry("overrides/options.txt"));
        Assert.IsNotNull(archive.GetEntry("overrides/config/assets/custom.json"));
        foreach (string skippedDirectory in new[]
                 {
                     "assets/",
                     "versions/",
                     "libraries/",
                     "structureCacheV1/",
                     ".fabric/",
                     ".git/",
                     "avatar-cache/",
                     "cosmetic-cache/"
                 })
        {
            Assert.IsFalse(
                archive.Entries.Any(entry => entry.FullName.StartsWith(
                    "overrides/" + skippedDirectory,
                    StringComparison.OrdinalIgnoreCase)),
                skippedDirectory);
        }
        Assert.IsNull(archive.GetEntry("overrides/config/.git/config"));
        Assert.IsNull(archive.GetEntry("overrides/config/structureCacheV1/cache.bin"));
    }

    [TestMethod]
    public async Task ExportAsync_WithNoRulesExportsNoGameFiles()
    {
        using TemporaryDirectory temporary = new();
        string gameDirectory = temporary.CreateDirectory("game");
        string instanceDirectory = temporary.CreateDirectory("instance");
        string archivePath = temporary.GetPath("empty.mrpack");
        temporary.WriteFile("game/options.txt", "must-not-be-exported");

        await InstanceExportService.ExportAsync(CreateRequest(instanceDirectory, gameDirectory, archivePath));

        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        CollectionAssert.AreEquivalent(
            new[] { "modrinth.index.json" },
            archive.Entries.Select(static entry => entry.FullName).ToArray());
        using JsonDocument manifest = ReadManifest(archive);
        Assert.AreEqual(0, manifest.RootElement.GetProperty("files").GetArrayLength());
    }

    [TestMethod]
    public async Task ExportAsync_UsesHostedResourcesUnlessBundleFilesAreRequested()
    {
        using TemporaryDirectory temporary = new();
        string gameDirectory = temporary.CreateDirectory("game");
        string instanceDirectory = temporary.CreateDirectory("instance");
        string hostedArchivePath = temporary.GetPath("hosted.mrpack");
        string bundledArchivePath = temporary.GetPath("bundled.mrpack");
        const string hostedContent = "hosted mod";

        temporary.WriteFile("game/mods/hosted.jar", hostedContent);
        temporary.WriteFile("game/mods/readme.txt", "readme");
        temporary.WriteFile("game/resourcepacks/offline.zip", "resource pack");
        temporary.WriteFile("game/config/not-hosted.jar", "configuration");

        IReadOnlyList<InstanceExportFile>? candidates = null;
        int resolverCalls = 0;
        await InstanceExportService.ExportAsync(
            CreateRequest(instanceDirectory, gameDirectory, hostedArchivePath) with
            {
                Rules = ["mods/**", "resourcepacks/**", "config/**"],
                ModrinthUploadMode = true,
                ResolveHostedFilesAsync = (files, _) =>
                {
                    resolverCalls++;
                    candidates = files;
                    IReadOnlyDictionary<string, InstanceExportHostedFile> result =
                        new Dictionary<string, InstanceExportHostedFile>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["mods/hosted.jar"] = new(
                            [
                                "https://cdn.curseforge.com/hosted.jar",
                                "https://cdn.modrinth.com/hosted.jar",
                                "HTTPS://CDN.MODRINTH.COM/hosted.jar",
                                " "
                            ]),
                            ["config/not-hosted.jar"] = new(["https://example.test/not-hosted.jar"]),
                            ["missing.jar"] = new(["https://example.test/missing.jar"])
                        };
                    return Task.FromResult(result);
                }
            });

        Assert.AreEqual(1, resolverCalls);
        Assert.IsNotNull(candidates);
        CollectionAssert.AreEquivalent(
            new[] { "mods/hosted.jar", "resourcepacks/offline.zip" },
            candidates.Select(static file => file.RelativePath).ToArray());
        InstanceExportFile hostedCandidate = candidates.Single(static file => file.RelativePath == "mods/hosted.jar");
        Assert.AreEqual(hostedContent.Length, hostedCandidate.Size);
        Assert.AreEqual(
            Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(hostedContent))),
            hostedCandidate.Sha1);
        Assert.AreEqual(
            Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes(hostedContent))),
            hostedCandidate.Sha512);
        Assert.AreNotEqual(0u, hostedCandidate.CurseForgeFingerprint);
        Assert.IsTrue(hostedCandidate.ModrinthOnly);

        using (ZipArchive archive = ZipFile.OpenRead(hostedArchivePath))
        {
            Assert.IsNull(archive.GetEntry("overrides/mods/hosted.jar"));
            Assert.IsNotNull(archive.GetEntry("overrides/mods/readme.txt"));
            Assert.IsNotNull(archive.GetEntry("overrides/resourcepacks/offline.zip"));
            Assert.IsNotNull(archive.GetEntry("overrides/config/not-hosted.jar"));

            using JsonDocument manifest = ReadManifest(archive);
            JsonElement file = manifest.RootElement.GetProperty("files").EnumerateArray().Single();
            Assert.AreEqual("mods/hosted.jar", file.GetProperty("path").GetString());
            Assert.AreEqual(hostedContent.Length, file.GetProperty("fileSize").GetInt64());
            Assert.AreEqual(hostedCandidate.Sha1, file.GetProperty("hashes").GetProperty("sha1").GetString());
            Assert.AreEqual(hostedCandidate.Sha512, file.GetProperty("hashes").GetProperty("sha512").GetString());
            string[] downloads = file.GetProperty("downloads").EnumerateArray()
                .Select(static value => value.GetString()!)
                .ToArray();
            Assert.AreEqual(2, downloads.Length);
            StringAssert.Contains(downloads[0], "modrinth.com");
        }

        await InstanceExportService.ExportAsync(
            CreateRequest(instanceDirectory, gameDirectory, bundledArchivePath) with
            {
                Rules = ["mods/**", "resourcepacks/**", "config/**"],
                IncludeBundleFiles = true,
                ResolveHostedFilesAsync = (_, _) =>
                    throw new AssertFailedException("Bundle mode must skip hosted resource lookup.")
            });

        Assert.AreEqual(1, resolverCalls);
        using (ZipArchive archive = ZipFile.OpenRead(bundledArchivePath))
        {
            Assert.IsNotNull(archive.GetEntry("overrides/mods/hosted.jar"));
            using JsonDocument manifest = ReadManifest(archive);
            Assert.AreEqual(0, manifest.RootElement.GetProperty("files").GetArrayLength());
        }
    }

    [TestMethod]
    public async Task ExportAsync_WithLauncherFilesCreatesOuterArchiveAndIncludesCustomization()
    {
        using TemporaryDirectory temporary = new();
        string gameDirectory = temporary.CreateDirectory("game");
        string instanceDirectory = temporary.CreateDirectory("instance");
        string launcherDataDirectory = temporary.CreateDirectory("launcher-data");
        string launcherPath = temporary.GetPath("PCL-N-Edition.exe");
        string archivePath = temporary.GetPath("launcher-bundle.zip");

        temporary.WriteFile("game/options.txt", "settings");
        await File.WriteAllTextAsync(launcherPath, "launcher");
        temporary.WriteFile("launcher-data/Pictures/background.png", "picture");
        temporary.WriteFile("launcher-data/Musics/theme.mp3", "music");
        temporary.WriteFile("launcher-data/Custom.xaml", "custom");
        temporary.WriteFile("launcher-data/Setup.ini", "setup");
        temporary.WriteFile("launcher-data/hints.txt", "hints");
        temporary.WriteFile("launcher-data/Logo.png", "logo");
        temporary.WriteFile("launcher-data/ignored.txt", "ignored");

        await InstanceExportService.ExportAsync(
            CreateRequest(instanceDirectory, gameDirectory, archivePath) with
            {
                Rules = ["options.txt"],
                IncludeLauncherFiles = true,
                IncludeLauncherCustom = true,
                LauncherExecutablePath = launcherPath,
                LauncherDataDirectory = launcherDataDirectory
            });

        using ZipArchive outerArchive = ZipFile.OpenRead(archivePath);
        AssertEntry(outerArchive, "PCL-N-Edition.exe", "launcher");
        AssertEntry(outerArchive, "PCL/Pictures/background.png", "picture");
        AssertEntry(outerArchive, "PCL/Musics/theme.mp3", "music");
        AssertEntry(outerArchive, "PCL/Custom.xaml", "custom");
        AssertEntry(outerArchive, "PCL/Setup.ini", "setup");
        AssertEntry(outerArchive, "PCL/hints.txt", "hints");
        AssertEntry(outerArchive, "PCL/Logo.png", "logo");
        Assert.IsNull(outerArchive.GetEntry("PCL/ignored.txt"));

        ZipArchiveEntry? innerEntry = outerArchive.GetEntry("modpack.mrpack");
        Assert.IsNotNull(innerEntry);
        using MemoryStream innerBuffer = new();
        await using (Stream innerStream = innerEntry.Open())
            await innerStream.CopyToAsync(innerBuffer);
        innerBuffer.Position = 0;
        using ZipArchive innerArchive = new(innerBuffer, ZipArchiveMode.Read);
        Assert.IsNotNull(innerArchive.GetEntry("modrinth.index.json"));
        AssertEntry(innerArchive, "overrides/options.txt", "settings");
    }

    [TestMethod]
    public async Task ExportAsync_WhenOuterArchiveFailsPreservesTargetAndRemovesTransactionFiles()
    {
        using TemporaryDirectory temporary = new();
        string gameDirectory = temporary.CreateDirectory("game");
        string instanceDirectory = temporary.CreateDirectory("instance");
        string archivePath = temporary.GetPath("existing.zip");
        string legacyTemporaryPath = archivePath + ".tmp";
        const string originalContent = "existing target";
        temporary.WriteFile("game/options.txt", "settings");
        await File.WriteAllTextAsync(archivePath, originalContent);
        await File.WriteAllTextAsync(legacyTemporaryPath, "unrelated sentinel");

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            InstanceExportService.ExportAsync(
                CreateRequest(instanceDirectory, gameDirectory, archivePath) with
                {
                    Rules = ["options.txt"],
                    IncludeLauncherFiles = true,
                    LauncherExecutablePath = temporary.GetPath("missing-launcher.exe")
                }));

        Assert.AreEqual(originalContent, await File.ReadAllTextAsync(archivePath));
        Assert.AreEqual("unrelated sentinel", await File.ReadAllTextAsync(legacyTemporaryPath));
        AssertNoTransactionFiles(temporary.Path, archivePath);
    }

    [TestMethod]
    public async Task ExportAsync_WhenPreCanceledPreservesTargetAndCreatesNoTransactionFiles()
    {
        using TemporaryDirectory temporary = new();
        string gameDirectory = temporary.CreateDirectory("game");
        string instanceDirectory = temporary.CreateDirectory("instance");
        string archivePath = temporary.GetPath("existing.mrpack");
        const string originalContent = "existing target";
        temporary.WriteFile("game/options.txt", "settings");
        File.WriteAllText(archivePath, originalContent);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            InstanceExportService.ExportAsync(
                CreateRequest(instanceDirectory, gameDirectory, archivePath) with
                {
                    Rules = ["options.txt"]
                },
                cancellation.Token));

        Assert.AreEqual(originalContent, File.ReadAllText(archivePath));
        AssertNoTransactionFiles(temporary.Path, archivePath);
    }

    private static InstanceExportRequest CreateRequest(
        string instanceDirectory,
        string gameDirectory,
        string targetArchivePath) =>
        new()
        {
            InstanceDirectory = instanceDirectory,
            GameDirectory = gameDirectory,
            TargetArchivePath = targetArchivePath
        };

    private static JsonDocument ReadManifest(ZipArchive archive)
    {
        ZipArchiveEntry? entry = archive.GetEntry("modrinth.index.json");
        Assert.IsNotNull(entry);
        using Stream stream = entry.Open();
        return JsonDocument.Parse(stream);
    }

    private static void AssertEntry(ZipArchive archive, string path, string expectedContent)
    {
        ZipArchiveEntry? entry = archive.GetEntry(path);
        Assert.IsNotNull(entry);
        using StreamReader reader = new(entry.Open());
        Assert.AreEqual(expectedContent, reader.ReadToEnd());
    }

    private static void AssertNoTransactionFiles(string directory, string archivePath)
    {
        string[] transactionFiles = Directory.GetFiles(directory, $".{Path.GetFileName(archivePath)}.*.tmp");
        Assert.AreEqual(0, transactionFiles.Length, string.Join(Environment.NewLine, transactionFiles));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "pcl-instance-export-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string GetPath(string relativePath) =>
            System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public string CreateDirectory(string relativePath)
        {
            string path = GetPath(relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        public void WriteFile(string relativePath, string content)
        {
            string path = GetPath(relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}

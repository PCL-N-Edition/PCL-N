// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.IO.Compression;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Application.Downloads;
using PCL.Application.Instances;

namespace PCL.Application.Test;

[TestClass]
public sealed class MinecraftModpackArchiveInstallerTests
{
    [TestMethod]
    public void Inspect_RecognizesModrinthManifestAndLoader()
    {
        using TemporaryDirectory temporary = new();
        string archivePath = Path.Combine(temporary.Path, "example.mrpack");
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteEntry(
                archive,
                "modrinth.index.json",
                """
                {
                  "formatVersion": 1,
                  "game": "minecraft",
                  "versionId": "2.4.1",
                  "name": "Example Pack",
                  "dependencies": {
                    "minecraft": "1.21.1",
                    "fabric-loader": "0.16.9"
                  },
                  "files": [
                    {
                      "path": "mods/example.jar",
                      "hashes": { "sha512": "00" },
                      "downloads": ["https://cdn.example/example.jar"],
                      "fileSize": 12
                    }
                  ]
                }
                """);
            WriteEntry(archive, "overrides/config/example.txt", "enabled");
        }

        MinecraftModpackInspection inspection = MinecraftModpackArchiveInstaller.Inspect(archivePath);

        Assert.AreEqual(MinecraftModpackFormat.Modrinth, inspection.Format);
        Assert.AreEqual("Example Pack", inspection.Name);
        Assert.AreEqual("2.4.1", inspection.Version);
        Assert.AreEqual("1.21.1", inspection.MinecraftVersion);
        Assert.AreEqual(MinecraftLoaderKind.Fabric, inspection.Loader?.Kind);
        Assert.AreEqual("0.16.9", inspection.Loader?.LoaderVersion);
        Assert.AreEqual(1, inspection.ResourceCount);
    }

    [TestMethod]
    public void Inspect_RecognizesCurseForgePrimaryLoaderAndRequiredFiles()
    {
        using TemporaryDirectory temporary = new();
        string archivePath = Path.Combine(temporary.Path, "curse.zip");
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteEntry(
                archive,
                "manifest.json",
                """
                {
                  "manifestType": "minecraftModpack",
                  "manifestVersion": 1,
                  "name": "Curse Pack",
                  "version": "3.0",
                  "minecraft": {
                    "version": "1.20.1",
                    "modLoaders": [
                      { "id": "fabric-0.15.0", "primary": false },
                      { "id": "forge-47.2.0", "primary": true }
                    ]
                  },
                  "files": [
                    { "projectID": 123, "fileID": 456, "required": true },
                    { "projectID": 789, "fileID": 987, "required": false }
                  ],
                  "overrides": "overrides"
                }
                """);
        }

        MinecraftModpackInspection inspection = MinecraftModpackArchiveInstaller.Inspect(archivePath);

        Assert.AreEqual(MinecraftModpackFormat.CurseForge, inspection.Format);
        Assert.AreEqual("1.20.1", inspection.MinecraftVersion);
        Assert.AreEqual(MinecraftLoaderKind.Forge, inspection.Loader?.Kind);
        Assert.AreEqual("47.2.0", inspection.Loader?.LoaderVersion);
        Assert.AreEqual(1, inspection.ResourceCount);
    }

    [TestMethod]
    public async Task InstallAsync_ImportsPclNExportIntoIsolatedUniqueInstance()
    {
        using TemporaryDirectory temporary = new();
        string archivePath = Path.Combine(temporary.Path, "export.zip");
        string minecraftRoot = Path.Combine(temporary.Path, ".minecraft");
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteEntry(
                archive,
                "Demo Pack/Demo Pack.json",
                """{"id":"Demo Pack","mainClass":"net.minecraft.client.main.Main","libraries":[]}""");
            WriteEntry(archive, "Demo Pack/Demo Pack.jar", "jar");
            WriteEntry(archive, "mods/example.jar", "mod");
            WriteEntry(archive, "config/example.cfg", "setting=true");
        }

        MinecraftModpackArchiveInstaller installer = new();
        MinecraftModpackInstallResult first = await installer.InstallAsync(
            new MinecraftModpackInstallRequest
            {
                ArchivePath = archivePath,
                MinecraftRootDirectory = minecraftRoot
            });
        MinecraftModpackInstallResult second = await installer.InstallAsync(
            new MinecraftModpackInstallRequest
            {
                ArchivePath = archivePath,
                MinecraftRootDirectory = minecraftRoot
            });

        Assert.AreEqual(MinecraftModpackFormat.PclN, first.Format);
        Assert.AreEqual("Demo Pack", first.VersionId);
        Assert.AreEqual("Demo Pack-2", second.VersionId);
        Assert.IsTrue(File.Exists(Path.Combine(first.InstanceDirectory, "Demo Pack.json")));
        Assert.IsTrue(File.Exists(Path.Combine(first.InstanceDirectory, "Demo Pack.jar")));
        Assert.IsTrue(File.Exists(Path.Combine(first.InstanceDirectory, "mods", "example.jar")));
        using (JsonDocument document = JsonDocument.Parse(
                   await File.ReadAllTextAsync(Path.Combine(first.InstanceDirectory, "Demo Pack.json"))))
        {
            Assert.AreEqual("Demo Pack", document.RootElement.GetProperty("id").GetString());
        }
        InstanceMetadata metadata = await InstanceMetadataStore.LoadAsync(first.InstanceDirectory);
        Assert.IsTrue(metadata.InstanceIsolation);
    }

    [TestMethod]
    public void Inspect_RejectsArchivePathTraversal()
    {
        using TemporaryDirectory temporary = new();
        string archivePath = Path.Combine(temporary.Path, "unsafe.zip");
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "../outside.txt", "blocked");
            WriteEntry(
                archive,
                "Safe/Safe.json",
                """{"id":"Safe","mainClass":"net.minecraft.client.main.Main"}""");
        }

        Assert.ThrowsExactly<InvalidDataException>(() => MinecraftModpackArchiveInstaller.Inspect(archivePath));
        Assert.IsFalse(MinecraftModpackArchiveInstaller.CanInstall(archivePath));
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path);
        using StreamWriter writer = new(entry.Open());
        writer.Write(content);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "pcl-modpack-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}

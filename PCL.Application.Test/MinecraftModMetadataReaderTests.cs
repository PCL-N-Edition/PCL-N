// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.IO.Compression;
using System.Text;
using PCL.Application.Downloads;

namespace PCL.Application.Test;

[TestClass]
public sealed class MinecraftModMetadataReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcln-mod-metadata-" + Guid.NewGuid().ToString("N"));

    [TestMethod]
    public void ReadsFabricDescriptorWithoutLoadingClasses()
    {
        string path = CreateArchive(
            "fabric.jar",
            "fabric.mod.json",
            """{"id":"example","name":"Example Mod","version":"1.2.3","icon":{"16":"icon-small.png","128":"assets/example/icon.png"},"depends":{"fabricloader":">=0.16","helper":"*"}}""");

        bool success = MinecraftModMetadataReader.TryRead(path, out MinecraftModMetadata? metadata);

        Assert.IsTrue(success);
        Assert.IsNotNull(metadata);
        Assert.AreEqual("example", metadata.Id);
        Assert.AreEqual("Example Mod", metadata.Name);
        Assert.AreEqual("1.2.3", metadata.Version);
        Assert.AreEqual("assets/example/icon.png", metadata.IconEntryPath);
        CollectionAssert.Contains(metadata.Dependencies.ToArray(), "helper");
    }

    [TestMethod]
    public void ReadsForgeDescriptorAndDependencies()
    {
        string path = CreateArchive(
            "forge.jar",
            "META-INF/mods.toml",
            """
            [[mods]]
            modId="exampleforge"
            version="2.0.0"
            displayName="Example Forge Mod"
            logoFile="assets/exampleforge/logo.png"
            [[dependencies.exampleforge]]
            modId="minecraft"
            [[dependencies.exampleforge]]
            modId="requiredhelper"
            """);

        Assert.IsTrue(MinecraftModMetadataReader.TryRead(path, out MinecraftModMetadata? metadata));
        Assert.IsNotNull(metadata);
        Assert.AreEqual("exampleforge", metadata.Id);
        Assert.AreEqual("forge", metadata.Loader);
        Assert.AreEqual("assets/exampleforge/logo.png", metadata.IconEntryPath);
        CollectionAssert.AreEqual(new[] { "requiredhelper" }, metadata.Dependencies.ToArray());
    }

    [TestMethod]
    public void ReadsLegacyForgeIconFromMcmodInfo()
    {
        string path = CreateArchive(
            "legacy-forge.jar",
            "mcmod.info",
            """[{"modid":"legacy","name":"Legacy Mod","version":"1.0","logoFile":"legacy-logo.png"}]""");

        Assert.IsTrue(MinecraftModMetadataReader.TryRead(path, out MinecraftModMetadata? metadata));
        Assert.IsNotNull(metadata);
        Assert.AreEqual("legacy", metadata.Id);
        Assert.AreEqual("legacy-logo.png", metadata.IconEntryPath);
    }

    [TestMethod]
    public void ReadsQuiltIconFromMetadataSizeMap()
    {
        string path = CreateArchive(
            "quilt.jar",
            "quilt.mod.json",
            """
            {
              "quilt_loader": {
                "id": "quilt-example",
                "version": "1.0.0",
                "metadata": {
                  "name": "Quilt Example",
                  "icon": {
                    "32": "assets/quilt/icon-32.png",
                    "256": "assets/quilt/icon-256.png"
                  }
                }
              }
            }
            """);

        Assert.IsTrue(MinecraftModMetadataReader.TryRead(path, out MinecraftModMetadata? metadata));
        Assert.IsNotNull(metadata);
        Assert.AreEqual("quilt-example", metadata.Id);
        Assert.AreEqual("assets/quilt/icon-256.png", metadata.IconEntryPath);
    }

    [TestMethod]
    public void ExtractsArchiveAndFolderIconsIntoReusableSafePaths()
    {
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        string archivePath = Path.Combine(_root, "icon-pack.zip");
        Directory.CreateDirectory(_root);
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            ZipArchiveEntry icon = archive.CreateEntry("assets/example/icon.png");
            using Stream stream = icon.Open();
            stream.Write(png);
        }

        string cache = Path.Combine(_root, "cache");
        string? first = MinecraftArchiveIconExtractor.TryExtract(
            archivePath,
            "assets/example/icon.png",
            cache);
        string? second = MinecraftArchiveIconExtractor.TryExtract(
            archivePath,
            "assets\\example\\icon.png",
            cache);

        Assert.IsNotNull(first);
        Assert.AreEqual(first, second);
        CollectionAssert.AreEqual(png, File.ReadAllBytes(first));
        Assert.IsNull(MinecraftArchiveIconExtractor.TryExtract(archivePath, "../pack.png", cache));

        string folderPack = Path.Combine(_root, "folder-pack");
        Directory.CreateDirectory(folderPack);
        string directIcon = Path.Combine(folderPack, "pack.png");
        File.WriteAllBytes(directIcon, png);
        Assert.AreEqual(
            directIcon,
            MinecraftArchiveIconExtractor.TryExtract(folderPack, "pack.png", cache));

        string invalidArchive = Path.Combine(_root, "invalid-icon.zip");
        using (ZipArchive archive = ZipFile.Open(invalidArchive, ZipArchiveMode.Create))
        {
            ZipArchiveEntry invalidIcon = archive.CreateEntry("pack.png");
            using Stream stream = invalidIcon.Open();
            stream.Write("not an image"u8);
        }
        Assert.IsNull(MinecraftArchiveIconExtractor.TryExtract(invalidArchive, "pack.png", cache));

        File.WriteAllText(directIcon, "not an image");
        Assert.IsNull(MinecraftArchiveIconExtractor.TryExtract(folderPack, "pack.png", cache));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string CreateArchive(string fileName, string entryName, string content)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, fileName);
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        ZipArchiveEntry entry = archive.CreateEntry(entryName);
        using StreamWriter writer = new(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
        return path;
    }
}

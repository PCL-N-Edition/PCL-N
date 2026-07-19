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
            """{"id":"example","name":"Example Mod","version":"1.2.3","depends":{"fabricloader":">=0.16","helper":"*"}}""");

        bool success = MinecraftModMetadataReader.TryRead(path, out MinecraftModMetadata? metadata);

        Assert.IsTrue(success);
        Assert.IsNotNull(metadata);
        Assert.AreEqual("example", metadata.Id);
        Assert.AreEqual("Example Mod", metadata.Name);
        Assert.AreEqual("1.2.3", metadata.Version);
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
            [[dependencies.exampleforge]]
            modId="minecraft"
            [[dependencies.exampleforge]]
            modId="requiredhelper"
            """);

        Assert.IsTrue(MinecraftModMetadataReader.TryRead(path, out MinecraftModMetadata? metadata));
        Assert.IsNotNull(metadata);
        Assert.AreEqual("exampleforge", metadata.Id);
        Assert.AreEqual("forge", metadata.Loader);
        CollectionAssert.AreEqual(new[] { "requiredhelper" }, metadata.Dependencies.ToArray());
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

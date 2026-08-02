// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.IO.Compression;
using PCL.Desktop.Hosting;
using PCL.Desktop.Paths;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class PclEmbeddedNativeRuntimeTests
{
    [TestMethod]
    public void MissingPathMapping_RestartsIntoFullOobeOnlyForNormalStartup()
    {
        Assert.IsTrue(Program.ShouldRestartIntoOobe(pathMappingExists: false, []));
        Assert.IsFalse(Program.ShouldRestartIntoOobe(pathMappingExists: true, []));
        Assert.IsFalse(Program.ShouldRestartIntoOobe(pathMappingExists: false, ["--oobe"]));
        Assert.IsFalse(Program.ShouldRestartIntoOobe(pathMappingExists: false, ["--oobe-resume"]));
        Assert.IsFalse(Program.ShouldRestartIntoOobe(pathMappingExists: false, ["--validate-assets"]));
    }

    [TestMethod]
    public void LauncherPathOverride_HasSingleLocalApplicationDataLocation()
    {
        string localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        string overridePath = LauncherPathLayout.OverrideFilePath;

        Assert.AreEqual(LauncherPathLayout.FileName, Path.GetFileName(overridePath));
        if (!string.IsNullOrWhiteSpace(localApplicationData))
        {
            Assert.AreEqual(
                Path.GetFullPath(Path.Combine(localApplicationData, "PCL-N")),
                Path.GetDirectoryName(overridePath));
        }
    }

    [TestMethod]
    public void EnsurePayloadInstalled_UsesOobeDataDirectoryAndContentHash()
    {
        string dataDirectory = CreateTemporaryDirectory();
        try
        {
            byte[] payload = CreatePayload(
                ("libSkiaSharp.dll", "skia"),
                ("libvlc/win-x64/libvlc.dll", "vlc"));

            string first = PclEmbeddedNativeRuntime.EnsurePayloadInstalled(
                payload,
                dataDirectory,
                "win-x64");
            string second = PclEmbeddedNativeRuntime.EnsurePayloadInstalled(
                payload,
                dataDirectory,
                "win-x64");

            Assert.AreEqual(first, second);
            StringAssert.StartsWith(
                first,
                Path.Combine(dataDirectory, "runtime", "native", "win-x64") +
                Path.DirectorySeparatorChar);
            Assert.IsTrue(File.Exists(Path.Combine(first, "libSkiaSharp.dll")));
            Assert.IsTrue(File.Exists(Path.Combine(first, "libvlc", "win-x64", "libvlc.dll")));
            Assert.IsTrue(File.Exists(Path.Combine(first, ".pcln-native-runtime-files")));
            Assert.IsTrue(File.Exists(Path.Combine(first, ".ready")));
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [TestMethod]
    public void EnsurePayloadInstalled_RejectsZipTraversal()
    {
        string dataDirectory = CreateTemporaryDirectory();
        try
        {
            byte[] payload = CreatePayload(("../outside.dll", "unsafe"));
            Assert.ThrowsExactly<InvalidDataException>(() =>
                PclEmbeddedNativeRuntime.EnsurePayloadInstalled(
                    payload,
                    dataDirectory,
                    "win-x64"));
            Assert.IsFalse(File.Exists(Path.Combine(dataDirectory, "outside.dll")));
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [TestMethod]
    public void GetRuntimeRoot_RejectsRidTraversal()
    {
        string dataDirectory = CreateTemporaryDirectory();
        try
        {
            Assert.ThrowsExactly<InvalidDataException>(() =>
                PclEmbeddedNativeRuntime.GetRuntimeRoot(dataDirectory, "../win-x64"));
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [TestMethod]
    public void GetNativeLibraryAliases_MatchesUnixPInvokeNames()
    {
        CollectionAssert.IsSubsetOf(
            new[] { "libSkiaSharp.so", "libSkiaSharp", "SkiaSharp" },
            PclEmbeddedNativeRuntime.GetNativeLibraryAliases("/runtime/libSkiaSharp.so"));
        CollectionAssert.IsSubsetOf(
            new[] { "libHarfBuzzSharp.so.0", "libHarfBuzzSharp", "HarfBuzzSharp" },
            PclEmbeddedNativeRuntime.GetNativeLibraryAliases("/runtime/libHarfBuzzSharp.so.0"));
    }

    private static byte[] CreatePayload(params (string Path, string Content)[] files)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string path, string content) in files)
            {
                ZipArchiveEntry entry = archive.CreateEntry(path);
                using StreamWriter writer = new(entry.Open());
                writer.Write(content);
            }
        }
        return stream.ToArray();
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "pcln-native-runtime-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

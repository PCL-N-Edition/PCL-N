// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Desktop.Features.Launching;
using PCL.Domain.Minecraft.Java;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class JavaRuntimeCatalogCacheTests
{
    [TestMethod]
    public async Task GetOrScanAsync_PrefersVerifiedCacheAcrossStoreInstances()
    {
        using TemporaryJavaRuntime runtime = TemporaryJavaRuntime.Create();
        string cachePath = Path.Combine(runtime.Root, "cache", "runtimes.json");
        using JavaRuntimeCatalogCache writer = new(cachePath);
        int scanCount = 0;

        JavaRuntimeCatalogLoadResult first = await writer.GetOrScanAsync(
            "same-environment",
            forceRefresh: false,
            _ =>
            {
                scanCount++;
                return Task.FromResult<IReadOnlyList<JavaRuntimeCandidate>>([runtime.Candidate]);
            },
            CancellationToken.None);

        using JavaRuntimeCatalogCache reader = new(cachePath);
        JavaRuntimeCatalogLoadResult second = await reader.GetOrScanAsync(
            "same-environment",
            forceRefresh: false,
            _ =>
            {
                scanCount++;
                throw new InvalidOperationException("A verified cache hit must not scan.");
            },
            CancellationToken.None);

        Assert.IsFalse(first.FromCache);
        Assert.IsTrue(second.FromCache);
        Assert.AreEqual(1, scanCount);
        Assert.AreEqual(runtime.Candidate.Installation, second.Candidates[0].Installation);
    }

    [TestMethod]
    public async Task GetOrScanAsync_ForceRefreshReplacesCache()
    {
        using TemporaryJavaRuntime runtime = TemporaryJavaRuntime.Create();
        string cachePath = Path.Combine(runtime.Root, "cache", "runtimes.json");
        using JavaRuntimeCatalogCache cache = new(cachePath);
        await cache.GetOrScanAsync(
            "same-environment",
            forceRefresh: false,
            _ => Task.FromResult<IReadOnlyList<JavaRuntimeCandidate>>([runtime.Candidate]),
            CancellationToken.None);
        int scanCount = 0;

        JavaRuntimeCatalogLoadResult refreshed = await cache.GetOrScanAsync(
            "same-environment",
            forceRefresh: true,
            _ =>
            {
                scanCount++;
                return Task.FromResult<IReadOnlyList<JavaRuntimeCandidate>>([]);
            },
            CancellationToken.None);

        Assert.IsFalse(refreshed.FromCache);
        Assert.AreEqual(1, scanCount);
        Assert.HasCount(0, refreshed.Candidates);
    }

    [TestMethod]
    public async Task GetOrScanAsync_RescansWhenCachedExecutableDisappears()
    {
        using TemporaryJavaRuntime runtime = TemporaryJavaRuntime.Create();
        string cachePath = Path.Combine(runtime.Root, "cache", "runtimes.json");
        using JavaRuntimeCatalogCache writer = new(cachePath);
        await writer.GetOrScanAsync(
            "same-environment",
            forceRefresh: false,
            _ => Task.FromResult<IReadOnlyList<JavaRuntimeCandidate>>([runtime.Candidate]),
            CancellationToken.None);
        File.Delete(runtime.Candidate.Installation.JavaExecutablePath);
        int scanCount = 0;

        using JavaRuntimeCatalogCache reader = new(cachePath);
        JavaRuntimeCatalogLoadResult result = await reader.GetOrScanAsync(
            "same-environment",
            forceRefresh: false,
            _ =>
            {
                scanCount++;
                return Task.FromResult<IReadOnlyList<JavaRuntimeCandidate>>([]);
            },
            CancellationToken.None);

        Assert.IsFalse(result.FromCache);
        Assert.AreEqual(1, scanCount);
    }

    private sealed class TemporaryJavaRuntime : IDisposable
    {
        private TemporaryJavaRuntime(string root, JavaRuntimeCandidate candidate)
        {
            Root = root;
            Candidate = candidate;
        }

        public string Root { get; }
        public JavaRuntimeCandidate Candidate { get; }

        public static TemporaryJavaRuntime Create()
        {
            string root = Path.Combine(Path.GetTempPath(), "pcl-java-cache-" + Guid.NewGuid().ToString("N"));
            string javaHome = Path.Combine(root, "jdk-21");
            string bin = Path.Combine(javaHome, "bin");
            Directory.CreateDirectory(bin);
            string executable = Path.Combine(bin, OperatingSystem.IsWindows() ? "java.exe" : "java");
            File.WriteAllText(executable, "java");
            File.WriteAllText(
                Path.Combine(javaHome, "release"),
                "JAVA_VERSION=\"21.0.5\"\nIMPLEMENTOR=\"OpenJDK\"");
            JavaInstallation installation = new(
                javaHome,
                executable,
                null,
                new Version(21, 0, 5),
                JavaBrand.OpenJDK,
                JavaArchitecture.X64,
                Is64Bit: true,
                IsJre: false);
            return new TemporaryJavaRuntime(
                root,
                new JavaRuntimeCandidate(installation, Source: JavaSource.AutoScanned));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}

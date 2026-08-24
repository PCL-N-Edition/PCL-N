// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Desktop.Hosting.PluginSidecar;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class PluginSidecarRuntimeInspectorTests
{
    [TestMethod]
    public void Inspect_AcceptsSelfContainedRuntime()
    {
        using RuntimeFixture fixture = new(selfContained: true);

        PluginSidecarRuntimeCheck result = PluginSidecarRuntimeInspector.Inspect(fixture.Executable, []);

        Assert.IsTrue(result.CanStart);
        Assert.IsFalse(result.IsFrameworkDependent);
    }

    [TestMethod]
    public void Inspect_RejectsNoRuntimeWithoutCompatibleFramework()
    {
        using RuntimeFixture fixture = new(selfContained: false);

        PluginSidecarRuntimeCheck result = PluginSidecarRuntimeInspector.Inspect(fixture.Executable, []);

        Assert.IsFalse(result.CanStart);
        Assert.IsTrue(result.IsFrameworkDependent);
        StringAssert.Contains(result.Message, "SelfContained");
    }

    [TestMethod]
    public void Inspect_AcceptsNoRuntimeWithCompatibleFramework()
    {
        using RuntimeFixture fixture = new(selfContained: false);
        string runtimeRoot = Path.Combine(fixture.Root, "dotnet");
        Directory.CreateDirectory(Path.Combine(runtimeRoot, "shared", "Microsoft.NETCore.App", "10.0.11"));

        PluginSidecarRuntimeCheck result = PluginSidecarRuntimeInspector.Inspect(
            fixture.Executable,
            [runtimeRoot]);

        Assert.IsTrue(result.CanStart);
        Assert.IsTrue(result.IsFrameworkDependent);
    }

    [TestMethod]
    public void Inspect_RejectsRuntimeVariantMarkerMismatch()
    {
        using RuntimeFixture fixture = new(selfContained: false, marker: "SelfContained");

        PluginSidecarRuntimeCheck result = PluginSidecarRuntimeInspector.Inspect(fixture.Executable, []);

        Assert.IsFalse(result.CanStart);
        StringAssert.Contains(result.Message, "发布产物已损坏");
    }

    [TestMethod]
    public void Inspect_RejectsIncompleteAppLocalHostEvenWhenSystemRuntimeExists()
    {
        using RuntimeFixture fixture = new(selfContained: false, includeLocalHost: true);
        string runtimeRoot = Path.Combine(fixture.Root, "dotnet");
        Directory.CreateDirectory(Path.Combine(runtimeRoot, "shared", "Microsoft.NETCore.App", "10.0.11"));

        PluginSidecarRuntimeCheck result = PluginSidecarRuntimeInspector.Inspect(
            fixture.Executable,
            [runtimeRoot]);

        Assert.IsFalse(result.CanStart);
        StringAssert.Contains(result.Message, "屏蔽系统已安装的运行时");
    }

    private sealed class RuntimeFixture : IDisposable
    {
        public RuntimeFixture(bool selfContained, string? marker = null, bool includeLocalHost = false)
        {
            Root = Path.Combine(Path.GetTempPath(), "pcln-sidecar-runtime-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Executable = Path.Combine(
                Root,
                OperatingSystem.IsWindows() ? "PCL.Plugin.Sidecar.exe" : "PCL.Plugin.Sidecar");
            File.WriteAllBytes(Executable, []);
            string runtimeOptions = selfContained
                ? "\"includedFrameworks\":[{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.11\"}]"
                : "\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.0\"}";
            File.WriteAllText(
                Path.Combine(Root, "PCL.Plugin.Sidecar.runtimeconfig.json"),
                "{\"runtimeOptions\":{" + runtimeOptions + "}}");
            if (marker is not null)
            {
                File.WriteAllText(
                    Path.Combine(Root, PluginSidecarRuntimeInspector.VariantMarkerFileName),
                    marker);
            }
            if (includeLocalHost)
            {
                string hostFxr = OperatingSystem.IsWindows()
                    ? "hostfxr.dll"
                    : OperatingSystem.IsMacOS()
                        ? "libhostfxr.dylib"
                        : "libhostfxr.so";
                File.WriteAllBytes(Path.Combine(Root, hostFxr), []);
            }
        }

        public string Root { get; }

        public string Executable { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}

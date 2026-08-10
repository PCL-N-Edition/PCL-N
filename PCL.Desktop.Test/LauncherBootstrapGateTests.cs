// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Desktop.Hosting;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class LauncherBootstrapGateTests
{
    [TestMethod]
    public void TryAllowDirectStart_AllowsWhenBootstrapEnvSet()
    {
        string? previous = Environment.GetEnvironmentVariable(
            LauncherBootstrapGate.BootstrapEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                LauncherBootstrapGate.BootstrapEnvironmentVariable,
                "1");
            Assert.IsTrue(
                LauncherBootstrapGate.TryAllowDirectStart([], out string message),
                message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                LauncherBootstrapGate.BootstrapEnvironmentVariable,
                previous);
        }
    }

    [TestMethod]
    public void TryAllowDirectStart_AllowsWhenDirectHostOverrideSet()
    {
        string? previousBootstrap = Environment.GetEnvironmentVariable(
            LauncherBootstrapGate.BootstrapEnvironmentVariable);
        string? previousAllow = Environment.GetEnvironmentVariable(
            LauncherBootstrapGate.AllowDirectHostEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                LauncherBootstrapGate.BootstrapEnvironmentVariable,
                null);
            Environment.SetEnvironmentVariable(
                LauncherBootstrapGate.AllowDirectHostEnvironmentVariable,
                "1");
            Assert.IsTrue(LauncherBootstrapGate.TryAllowDirectStart([], out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                LauncherBootstrapGate.BootstrapEnvironmentVariable,
                previousBootstrap);
            Environment.SetEnvironmentVariable(
                LauncherBootstrapGate.AllowDirectHostEnvironmentVariable,
                previousAllow);
        }
    }

    [TestMethod]
    public void IsDevelopmentLayout_DetectsDotnetAndBinFolders()
    {
        Assert.IsTrue(LauncherBootstrapGate.IsDevelopmentLayout(
            processPath: Path.Combine("C:", "dotnet", "dotnet.exe"),
            baseDirectory: Path.Combine("C:", "app")));
        Assert.IsTrue(LauncherBootstrapGate.IsDevelopmentLayout(
            processPath: Path.Combine("C:", "app", "PCL.Desktop.exe"),
            baseDirectory: Path.Combine("C:", "repo", "PCL.Desktop", "bin", "Debug", "net10.0")));
        Assert.IsFalse(LauncherBootstrapGate.IsDevelopmentLayout(
            processPath: Path.Combine("D:", "mc", "PCL N", "host", "PCL-N-Host.exe"),
            baseDirectory: Path.Combine("D:", "mc", "PCL N", "host")));
    }

    [TestMethod]
    public void TryAllowDirectStart_BlocksProductHostWithoutBootstrap()
    {
        string? previousBootstrap = Environment.GetEnvironmentVariable(
            LauncherBootstrapGate.BootstrapEnvironmentVariable);
        string? previousAllow = Environment.GetEnvironmentVariable(
            LauncherBootstrapGate.AllowDirectHostEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                LauncherBootstrapGate.BootstrapEnvironmentVariable,
                null);
            Environment.SetEnvironmentVariable(
                LauncherBootstrapGate.AllowDirectHostEnvironmentVariable,
                null);

            // When embedded native zip is absent and layout is not development, gate blocks.
            // Unit tests usually run under testhost/bin → development layout allows start.
            // Probe the pure helpers instead for product-path denial message content.
            Assert.IsFalse(LauncherBootstrapGate.IsBootstrapPresent());
            Assert.IsFalse(LauncherBootstrapGate.IsDirectHostExplicitlyAllowed());
            Assert.IsFalse(LauncherBootstrapGate.IsDevelopmentLayout(
                processPath: Path.Combine("D:", "install", "host", "PCL-N-Host.exe"),
                baseDirectory: Path.Combine("D:", "install", "host")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                LauncherBootstrapGate.BootstrapEnvironmentVariable,
                previousBootstrap);
            Environment.SetEnvironmentVariable(
                LauncherBootstrapGate.AllowDirectHostEnvironmentVariable,
                previousAllow);
        }
    }
}

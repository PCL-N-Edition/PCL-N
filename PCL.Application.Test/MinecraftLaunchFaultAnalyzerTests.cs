// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Launching;

namespace PCL.Application.Test;

[TestClass]
public sealed class MinecraftLaunchFaultAnalyzerTests
{
    [TestMethod]
    public void Analyze_ClassifiesJvmInitializationFailure()
    {
        MinecraftLaunchFaultReport report = MinecraftLaunchFaultAnalyzer.Analyze(
            new DllNotFoundException("Unable to load jvm.dll"),
            "JvmStarting");

        Assert.AreEqual(MinecraftLaunchFaultCode.JavaRuntimeMissing, report.Code);
        Assert.AreEqual("JVM", report.Subsystem);
        CollectionAssert.Contains(report.AllowedActions, MinecraftRepairActionKind.SelectCompatibleJava);
    }

    [TestMethod]
    public void AnalyzeText_RestrictsMissingClassToVersionRepair()
    {
        MinecraftLaunchFaultReport report = MinecraftLaunchFaultAnalyzer.AnalyzeText(
            ["java.lang.NoClassDefFoundError: com/example/RequiredLibrary"],
            "MainInvoking",
            "net.minecraft.client.main.Main");

        Assert.AreEqual(MinecraftLaunchFaultCode.ClasspathDependencyMissing, report.Code);
        CollectionAssert.Contains(report.AllowedActions, MinecraftRepairActionKind.RepairVersionFiles);
        CollectionAssert.Contains(report.AllowedActions, MinecraftRepairActionKind.ReinstallVersionAndUpdateLoader);
    }

    [TestMethod]
    public void Analyze_PreservesStructuredJvmLocation()
    {
        MinecraftLaunchFaultReport report = MinecraftLaunchFaultAnalyzer.Analyze(
            new InvalidOperationException("GLFW error: failed to create window"),
            "MinecraftClient",
            "org.lwjgl.glfw.GLFW");

        Assert.AreEqual(MinecraftLaunchFaultCode.GraphicsInitializationFailed, report.Code);
        Assert.AreEqual("Graphics", report.Subsystem);
        Assert.AreEqual("MinecraftClient", report.Stage);
        Assert.AreEqual("org.lwjgl.glfw.GLFW", report.LastClassName);
    }
}

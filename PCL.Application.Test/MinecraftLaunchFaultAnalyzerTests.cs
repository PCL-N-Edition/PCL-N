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

    [TestMethod]
    [DataRow("Open J9 is not supported by this version", MinecraftLaunchFaultCode.JavaRuntimeIncompatible)]
    [DataRow("because module java.base does not export sun.security.util", MinecraftLaunchFaultCode.JavaRuntimeIncompatible)]
    [DataRow("java.lang.ClassNotFoundException: jdk.nashorn.api.scripting.NashornScriptEngineFactory", MinecraftLaunchFaultCode.JavaRuntimeIncompatible)]
    [DataRow("Invalid maximum heap size: -Xmx4096m", MinecraftLaunchFaultCode.JavaRuntimeIncompatible)]
    [DataRow("The directories below appear to be extracted jar files. Fix this before you continue.", MinecraftLaunchFaultCode.ModConflict)]
    [DataRow("java.lang.ClassNotFoundException: org.spongepowered.asm.launch.MixinTweaker", MinecraftLaunchFaultCode.ModLoaderBootstrapFailed)]
    [DataRow("Cannot find launch target fmlclient, unable to launch", MinecraftLaunchFaultCode.ModLoaderBootstrapFailed)]
    [DataRow("The driver does not appear to support OpenGL", MinecraftLaunchFaultCode.GraphicsInitializationFailed)]
    [DataRow("Pixel format not accelerated", MinecraftLaunchFaultCode.GraphicsInitializationFailed)]
    public void AnalyzeText_RecognizesUpstream215HighConfidenceSignatures(
        string evidence,
        MinecraftLaunchFaultCode expected)
    {
        MinecraftLaunchFaultReport report = MinecraftLaunchFaultAnalyzer.AnalyzeText(
            [evidence],
            "GameProcess");

        Assert.AreEqual(expected, report.Code);
    }
}

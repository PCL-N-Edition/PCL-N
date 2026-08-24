// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Minecraft.Launch;
using PCL.Desktop.Features.Launching;
using PCL.Domain.Minecraft.Java;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class MinecraftMissingJavaPromptTests
{
    [TestMethod]
    public void CreateMissingJavaPrompt_KeepsVerifiedAlternativesAndOrdersCompatibleFirst()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcln-java-choice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string java8 = CreateExecutable(root, "java8");
            string java17 = CreateExecutable(root, "java17");
            string missing = Path.Combine(root, "missing", "java");
            JavaRequirementResolution requirement = JavaRequirementResolution.Valid(
                new JavaVersionRange(JavaVersionRange.ForMajor(17), JavaVersionRange.ForMajorMaximum(17)));

            JavaMissingRuntimePrompt prompt = MinecraftLaunchCoordinator.CreateMissingJavaPrompt(
                [
                    Candidate(java8, 8, enabled: true),
                    Candidate(java17, 17, enabled: false),
                    Candidate(missing, 21, enabled: true)
                ],
                requirement,
                JavaRuntimeAcquisitionDecision.AutoDownload("17", "java-runtime-gamma"));

            Assert.IsTrue(prompt.CanDownload);
            Assert.AreEqual("17", prompt.RequiredVersionLabel);
            Assert.HasCount(2, prompt.Alternatives);
            Assert.AreEqual(java17, prompt.Alternatives[0].JavaExecutablePath);
            Assert.IsTrue(prompt.Alternatives[0].IsCompatible);
            Assert.IsFalse(prompt.Alternatives[0].IsEnabled);
            Assert.AreEqual(java8, prompt.Alternatives[1].JavaExecutablePath);
            Assert.IsFalse(prompt.Alternatives[1].IsCompatible);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateExecutable(string root, string name)
    {
        string directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, OperatingSystem.IsWindows() ? "java.exe" : "java");
        File.WriteAllBytes(path, [0]);
        return path;
    }

    private static JavaRuntimeCandidate Candidate(string executable, int major, bool enabled)
    {
        JavaInstallation installation = new(
            Path.GetDirectoryName(executable)!,
            executable,
            null,
            new Version(major, 0, 1),
            JavaBrand.OpenJDK,
            Environment.Is64BitOperatingSystem ? JavaArchitecture.X64 : JavaArchitecture.X86,
            Environment.Is64BitOperatingSystem,
            IsJre: false);
        return new JavaRuntimeCandidate(installation, enabled, IsAvailable: true);
    }
}

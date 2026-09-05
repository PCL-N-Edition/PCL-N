using PCL.Services.Minecraft.Java;

namespace PCL.Services.Tests;

// XSR review: the Java scanner is a real service (LocalJavaRuntimeLocator drives the
// launch-time select_java stage), so its property parsing gets the same coverage as the
// rest of the pipeline — real `java -XshowSettings:properties -version` output shapes.
internal static partial class Program
{
    private static (string Home, string Properties) ComposeProbeProperties(
        string home, string versionLine, string vendor, string version, string arch, bool withJavac)
    {
        string bin = Path.Combine(home, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllBytes(Path.Combine(bin, OperatingSystem.IsWindows() ? "java.exe" : "java"), [0xCA, 0xFE]);
        if (withJavac)
        {
            File.WriteAllBytes(Path.Combine(bin, OperatingSystem.IsWindows() ? "javac.exe" : "javac"), [0xCA, 0xFE]);
        }

        string properties = $"""
            java.home = {home}
            java.runtime.name = OpenJDK Runtime Environment
            java.vendor = {vendor}
            java.version = {version}
            os.arch = {arch}
            {versionLine}
            """;
        return (home, properties);
    }

    private static void JavaProbeParsesModernPropertyOutput()
    {
        string home = Path.Combine(Path.GetTempPath(), "nexa-java-probe", Guid.NewGuid().ToString("N"));
        (string _, string properties) = ComposeProbeProperties(
            home, "openjdk version \"17.0.10\" 2024-01-16", "Eclipse Adoptium", "17.0.10", "x86_64", withJavac: true);
        try
        {
            AssertTrue(LocalJavaRuntimeLocator.TryCreateCandidate(
                Path.Combine(home, "bin", "java.exe"),
                properties,
                out JavaRuntimeCandidate? candidate));
            AssertEqual(17, candidate!.Installation.MajorVersion);
            AssertEqual(JavaArchitecture.X64, candidate.Installation.Architecture);
            AssertTrue(candidate.Installation.Is64Bit);
            AssertFalse(candidate.Installation.IsJre);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    private static void JavaProbeParsesLegacyVersionLine()
    {
        // Older JVMs print the version only as a quoted `java version "…"` line, without
        // `java.version = …` in the settings block; no javac in the home marks a JRE.
        string home = Path.Combine(Path.GetTempPath(), "nexa-java-probe", Guid.NewGuid().ToString("N"));
        (string _, string properties) = ComposeProbeProperties(
            home, "java version \"1.8.0_402\"", "Oracle Corporation", "1.8.0_402", "amd64", withJavac: false);
        try
        {
            AssertTrue(LocalJavaRuntimeLocator.TryCreateCandidate(
                Path.Combine(home, "bin", "java.exe"),
                properties,
                out JavaRuntimeCandidate? candidate));
            AssertEqual(new Version(1, 8, 0, 402), candidate!.Installation.Version);
            AssertTrue(candidate.Installation.IsJre);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    private static void JavaProbeRejectsUnrecognizedOutput()
    {
        AssertFalse(LocalJavaRuntimeLocator.TryCreateCandidate(
            "irrelevant", "this program is not a java runtime", out JavaRuntimeCandidate? candidate));
        AssertTrue(candidate is null);
    }
}

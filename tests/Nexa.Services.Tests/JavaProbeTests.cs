using Nexa.Services.Minecraft.Java;

namespace Nexa.Services.Tests;

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

    private static void JavaSearchRetainsDirectPathShimExecutable()
    {
        // Oracle javapath: a directory whose executable sits directly inside it, with no
        // bin/ child. The scan must keep it as a probe candidate instead of re-interpreting
        // it as a home and looking for bin/java that will never exist.
        string shim = Path.Combine(Path.GetTempPath(), "nexa-java-shim", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(shim);
            File.WriteAllBytes(Path.Combine(shim, OperatingSystem.IsWindows() ? "java.exe" : "java"), [0xCA, 0xFE]);

            AssertEqual(Path.GetFullPath(Path.Combine(shim, OperatingSystem.IsWindows() ? "java.exe" : "java")),
                LocalJavaRuntimeLocator.ResolveJavaExecutable(shim));
            AssertTrue(LocalJavaRuntimeLocator.ResolveJavaExecutable(Path.Combine(shim, "bin")) is null);
        }
        finally
        {
            Directory.Delete(shim, recursive: true);
        }
    }

    private static void JavaProbeRejectsUnrecognizedOutput()
    {
        AssertFalse(LocalJavaRuntimeLocator.TryCreateCandidate(
            "irrelevant", "this program is not a java runtime", out JavaRuntimeCandidate? candidate));
        AssertTrue(candidate is null);
    }

    private static void ExpandRootFindsHomesInVendorTrees()
    {
        // Legacy expand semantics: the root may be a home itself, one level of children is
        // checked, and vendor trees get a single extra level. A resolved home is not descended
        // into, and a non-java root yields nothing instead of throwing.
        string root = Path.Combine(Path.GetTempPath(), "nexa-java-expand", Guid.NewGuid().ToString("N"));
        try
        {
            string topLevelHome = CreateFakeHome(Path.Combine(root, "jdk-17"), includeJavac: false);
            string vendorHome = CreateFakeHome(
                Path.Combine(root, "Eclipse Adoptium", "jdk-17.0.10-hotspot"), includeJavac: true);
            _ = Directory.CreateDirectory(Path.Combine(root, "empty-vendor"));
            File.WriteAllText(Path.Combine(root, "notes.txt"), "not a runtime");

            List<string> homes = LocalJavaRuntimeLocator
                .ExpandRoot(root, CancellationToken.None)
                .Select(static home => Path.GetFullPath(home))
                .OrderBy(static home => home, StringComparer.OrdinalIgnoreCase)
                .ToList();

            AssertEqual(2, homes.Count);
            AssertTrue(homes.Contains(Path.GetFullPath(topLevelHome)));
            AssertTrue(homes.Contains(Path.GetFullPath(vendorHome)));

            // The root itself as a home resolves through its bin folder.
            AssertEqual(Path.GetFullPath(topLevelHome),
                Path.GetFullPath(LocalJavaRuntimeLocator.ResolveJavaHome(
                    Path.Combine(topLevelHome, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"))!));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateFakeHome(string home, bool includeJavac)
    {
        string bin = Path.Combine(home, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllBytes(Path.Combine(bin, OperatingSystem.IsWindows() ? "java.exe" : "java"), [0xCA, 0xFE]);
        if (includeJavac)
        {
            File.WriteAllBytes(Path.Combine(bin, OperatingSystem.IsWindows() ? "javac.exe" : "javac"), [0xCA, 0xFE]);
        }

        return home;
    }
}

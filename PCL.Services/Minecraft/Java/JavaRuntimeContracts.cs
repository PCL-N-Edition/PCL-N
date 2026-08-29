using System.Globalization;

namespace PCL.Services.Minecraft.Java;

public enum JavaBrand
{
    EclipseTemurin,
    Liberica,
    Zulu,
    Corretto,
    Microsoft,
    IbmSemeru,
    Oracle,
    Dragonwell,
    TencentKona,
    OpenJdk,
    GraalVmCommunity,
    JetBrains,
    Unknown,
}

public enum JavaArchitecture
{
    Unknown,
    X86,
    X64,
    Arm,
    Arm64,
}

public enum JavaSource
{
    AutoScanned,
    AutoInstalled,
    ManualAdded,
}

public sealed record JavaInstallation
{
    public JavaInstallation(
        string javaHome,
        string javaExecutablePath,
        string? windowedJavaExecutablePath,
        Version version,
        JavaBrand brand,
        JavaArchitecture architecture,
        bool is64Bit,
        bool isJre)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(javaHome);
        ArgumentException.ThrowIfNullOrWhiteSpace(javaExecutablePath);
        ArgumentNullException.ThrowIfNull(version);
        JavaHome = Path.GetFullPath(javaHome);
        JavaExecutablePath = Path.GetFullPath(javaExecutablePath);
        WindowedJavaExecutablePath = string.IsNullOrWhiteSpace(windowedJavaExecutablePath) ? null : Path.GetFullPath(windowedJavaExecutablePath);
        Version = version;
        Brand = brand;
        Architecture = architecture;
        Is64Bit = is64Bit;
        IsJre = isJre;
    }

    public string JavaHome { get; }
    public string JavaExecutablePath { get; }
    public string? WindowedJavaExecutablePath { get; }
    public Version Version { get; }
    public JavaBrand Brand { get; }
    public JavaArchitecture Architecture { get; }
    public bool Is64Bit { get; }
    public bool IsJre { get; }
    public int MajorVersion => Version.Major == 1 ? Version.Minor : Version.Major;
    public override string ToString() => $"{(IsJre ? "JRE" : "JDK")} {Version} {Brand} {(Is64Bit ? "64 Bit" : "32 Bit")} | {JavaHome}";
}

public sealed record JavaRuntimeCandidate(
    JavaInstallation Installation,
    bool IsEnabled = true,
    bool IsAvailable = true,
    JavaSource Source = JavaSource.AutoScanned);

public interface IJavaRuntimeLocator
{
    ValueTask<IReadOnlyList<JavaRuntimeCandidate>> FindAllAsync(CancellationToken cancellationToken = default);
}

public abstract record JavaPreference;
public sealed record AutoSelectJavaPreference : JavaPreference;
public sealed record ExistingJavaPreference(string JavaExecutablePath) : JavaPreference;
public sealed record UseGlobalJavaPreference : JavaPreference;
public sealed record UseRelativeJavaPreference(string RelativePath) : JavaPreference;

public readonly record struct JavaVersionRange
{
    public static JavaVersionRange Any { get; } = new(new Version(1, 7), new Version(99, 0));
    public static Version Java7Maximum { get; } = new(1, 7, 0, 999);
    public static Version Java8Maximum { get; } = new(1, 8, 0, 999);

    public JavaVersionRange(Version minimum, Version maximum)
    {
        ArgumentNullException.ThrowIfNull(minimum);
        ArgumentNullException.ThrowIfNull(maximum);
        if (minimum > maximum) throw new ArgumentException("The Java minimum cannot exceed the maximum.");
        Minimum = minimum;
        Maximum = maximum;
    }

    public Version Minimum { get; }
    public Version Maximum { get; }

    public bool Contains(Version value) => value >= Minimum && value <= Maximum;
    public static JavaVersionRange ForMajor(int major) => major switch
    {
        7 => new JavaVersionRange(new Version(1, 7), Java7Maximum),
        8 => new JavaVersionRange(new Version(1, 8), Java8Maximum),
        _ => new JavaVersionRange(new Version(major, 0), new Version(major, 999, 999, 999)),
    };

    public JavaVersionRange Intersect(JavaVersionRange other) =>
        new(Maximum < other.Maximum ? Minimum : other.Minimum, Minimum > other.Minimum ? Maximum : other.Maximum);
}

public sealed record MinecraftJavaRequirementRequest
{
    public Version? VanillaVersion { get; init; }
    public bool HasReliableVanillaVersion { get; init; }
    public DateTimeOffset? ReleaseTime { get; init; }
    public int? ManifestJavaMajorVersion { get; init; }
    public string? ManifestJavaComponent { get; init; }
    public bool HasOptiFine { get; init; }
    public bool HasForge { get; init; }
    public string? ForgeVersion { get; init; }
    public bool HasCleanroom { get; init; }
    public string? CleanroomVersion { get; init; }
    public bool HasFabric { get; init; }
    public bool HasLiteLoader { get; init; }
    public bool HasLabyMod { get; init; }
}

public enum JavaRequirementFailureReason
{
    None,
    InvalidVersionMetadata,
    ConflictingRequirements,
}

public sealed record JavaRequirementResolution
{
    public required bool Success { get; init; }
    public required JavaVersionRange Range { get; init; }
    public string? RecommendedComponent { get; init; }
    public JavaRequirementFailureReason FailureReason { get; init; }
    public string? Detail { get; init; }

    public static JavaRequirementResolution Valid(JavaVersionRange range, string? component = null) => new() { Success = true, Range = range, RecommendedComponent = component };
    public static JavaRequirementResolution Invalid(JavaRequirementFailureReason reason, string detail) => new() { Success = false, Range = JavaVersionRange.Any, FailureReason = reason, Detail = detail };
}

public static class MinecraftJavaRequirementResolver
{
    private static readonly DateTimeOffset ManifestJava21Boundary = new(2024, 4, 2, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ManifestJava25Boundary = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static JavaRequirementResolution Resolve(MinecraftJavaRequirementRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        JavaVersionRange range = JavaVersionRange.Any;
        string? component = null;

        if (request.HasCleanroom)
        {
            if (!TryParseLoaderVersion(request.CleanroomVersion, out Version? cleanroom))
                return JavaRequirementResolution.Invalid(JavaRequirementFailureReason.InvalidVersionMetadata, "Cleanroom version metadata is invalid.");
            range = range.Intersect(cleanroom!.Major >= 5 ? JavaVersionRange.ForMajor(25) : JavaVersionRange.ForMajor(21));
        }

        if (request.HasReliableVanillaVersion && request.VanillaVersion is { } version)
        {
            if (version >= new Version(20, 0, 5)) range = range.Intersect(JavaVersionRange.ForMajor(21));
            else if (version < new Version(13, 0, 0)) range = range.Intersect(JavaVersionRange.ForMajor(8));
            if (request.HasForge && version < new Version(12, 0, 0) && IsLegacyForge(request.ForgeVersion))
                range = range.Intersect(JavaVersionRange.ForMajor(7));
            if (request.HasOptiFine && version >= new Version(8, 0, 0) && version < new Version(13, 0, 0))
                range = range.Intersect(JavaVersionRange.ForMajor(8));
        }

        if (request.HasLiteLoader) range = range.Intersect(new JavaVersionRange(new Version(1, 8), JavaVersionRange.Java8Maximum));
        if (request.HasLabyMod && request.VanillaVersion is { } labyVersion && labyVersion < new Version(13, 0, 0))
            range = range.Intersect(JavaVersionRange.ForMajor(8));

        if (request.ManifestJavaMajorVersion is int manifestMajor)
        {
            if (manifestMajor < 7) return JavaRequirementResolution.Invalid(JavaRequirementFailureReason.InvalidVersionMetadata, "Manifest Java major version is below 7.");
            range = range.Intersect(JavaVersionRange.ForMajor(manifestMajor));
            component = request.ManifestJavaComponent;
        }
        else if (!request.HasReliableVanillaVersion && request.ReleaseTime is { } release && release >= ManifestJava25Boundary)
        {
            range = range.Intersect(JavaVersionRange.ForMajor(25));
            component = request.ManifestJavaComponent;
        }
        else if (!request.HasReliableVanillaVersion && request.ReleaseTime is { } snapshot && snapshot >= ManifestJava21Boundary)
        {
            range = range.Intersect(JavaVersionRange.ForMajor(21));
            component = request.ManifestJavaComponent;
        }

        if (range.Minimum > range.Maximum)
            return JavaRequirementResolution.Invalid(JavaRequirementFailureReason.ConflictingRequirements, "Minecraft metadata contains incompatible Java requirements.");
        return JavaRequirementResolution.Valid(range, component);
    }

    private static bool IsLegacyForge(string? value) => string.IsNullOrWhiteSpace(value) || value.StartsWith("9.", StringComparison.Ordinal) || value.StartsWith("10.", StringComparison.Ordinal);

    private static bool TryParseLoaderVersion(string? value, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string normalized = value.Split('-', 2)[0];
        return Version.TryParse(normalized, out version);
    }
}

public enum JavaSelectionFailureReason
{
    None,
    InvalidVersionMetadata,
    NoCompatibleRuntime,
    LocatorUnavailable,
}

public sealed record JavaSelectionResult
{
    public required bool Success { get; init; }
    public required JavaRequirementResolution Requirement { get; init; }
    public JavaRuntimeCandidate? SelectedJava { get; init; }
    public JavaSelectionFailureReason FailureReason { get; init; }
    public string? Detail { get; init; }
    public string? SuggestedDownloadComponent { get; init; }
}

public sealed class JavaSelectionService(IJavaRuntimeLocator locator)
{
    private readonly IJavaRuntimeLocator _locator = locator ?? throw new ArgumentNullException(nameof(locator));

    public async ValueTask<JavaSelectionResult> SelectAsync(MinecraftJavaRequirementRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        JavaRequirementResolution requirement = MinecraftJavaRequirementResolver.Resolve(request);
        if (!requirement.Success)
            return new JavaSelectionResult { Success = false, Requirement = requirement, FailureReason = JavaSelectionFailureReason.InvalidVersionMetadata, Detail = requirement.Detail };

        IReadOnlyList<JavaRuntimeCandidate> candidates;
        try { candidates = await _locator.FindAllAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new JavaSelectionResult { Success = false, Requirement = requirement, FailureReason = JavaSelectionFailureReason.LocatorUnavailable, Detail = exception.Message };
        }

        JavaRuntimeCandidate? selected = candidates
            .Where(candidate => candidate.IsEnabled && candidate.IsAvailable && requirement.Range.Contains(candidate.Installation.Version))
            .OrderBy(candidate => candidate.Installation.MajorVersion)
            .ThenBy(candidate => candidate.Installation.IsJre ? 1 : 0)
            .ThenBy(candidate => BrandRank(candidate.Installation.Brand))
            .ThenBy(candidate => candidate.Installation.Version)
            .ThenBy(candidate => candidate.Installation.JavaHome, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return selected is null
            ? new JavaSelectionResult { Success = false, Requirement = requirement, FailureReason = JavaSelectionFailureReason.NoCompatibleRuntime, Detail = "No enabled Java runtime satisfies the Minecraft requirement.", SuggestedDownloadComponent = requirement.RecommendedComponent }
            : new JavaSelectionResult { Success = true, Requirement = requirement, SelectedJava = selected, SuggestedDownloadComponent = requirement.RecommendedComponent };
    }

    private static int BrandRank(JavaBrand brand) => brand switch
    {
        JavaBrand.EclipseTemurin => 0,
        JavaBrand.Microsoft => 1,
        JavaBrand.Zulu => 2,
        _ => 10,
    };
}

public enum JavaAcquisitionBlockReason
{
    None,
    LegacyJava7Required,
    LegacyForgeNeedsFixerOrJava7,
    Java8Update141To320Required,
    Java8Update141OrLaterRequired,
}

public sealed record JavaRuntimeAcquisitionDecision
{
    public required bool CanAutoDownload { get; init; }
    public string? JavaVersionCode { get; init; }
    public string? DownloadComponent { get; init; }
    public JavaAcquisitionBlockReason BlockReason { get; init; }
}

public static class JavaRuntimeAcquisitionPlanner
{
    public static JavaRuntimeAcquisitionDecision Plan(JavaRequirementResolution requirement, bool hasForge = false) =>
        Plan(requirement.Range, requirement.RecommendedComponent, hasForge);

    public static JavaRuntimeAcquisitionDecision Plan(JavaVersionRange range, string? recommendedComponent = null, bool hasForge = false)
    {
        if (range.Maximum <= JavaVersionRange.Java7Maximum)
            return new JavaRuntimeAcquisitionDecision { CanAutoDownload = false, BlockReason = hasForge ? JavaAcquisitionBlockReason.LegacyForgeNeedsFixerOrJava7 : JavaAcquisitionBlockReason.LegacyJava7Required };
        if (range.Minimum >= new Version(1, 8, 0, 141) && range.Minimum <= JavaVersionRange.Java8Maximum && range.Maximum < JavaVersionRange.Java8Maximum)
            return new JavaRuntimeAcquisitionDecision { CanAutoDownload = false, BlockReason = JavaAcquisitionBlockReason.Java8Update141To320Required };
        if (range.Minimum >= new Version(1, 8, 0, 141) && range.Maximum == JavaVersionRange.Java8Maximum)
            return new JavaRuntimeAcquisitionDecision { CanAutoDownload = false, BlockReason = JavaAcquisitionBlockReason.Java8Update141OrLaterRequired };
        int major = range.Minimum.Major == 1 ? range.Minimum.Minor : range.Minimum.Major;
        return new JavaRuntimeAcquisitionDecision { CanAutoDownload = true, JavaVersionCode = major.ToString(CultureInfo.InvariantCulture), DownloadComponent = string.IsNullOrWhiteSpace(recommendedComponent) ? major.ToString(CultureInfo.InvariantCulture) : recommendedComponent };
    }
}

public static class JavaPreferenceParser
{
    public const string LegacyUseGlobalText = "使用全局设置";

    public static JavaPreference Parse(string? rawPreference, string? relativePathBaseDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(rawPreference) || string.Equals(rawPreference.Trim(), LegacyUseGlobalText, StringComparison.OrdinalIgnoreCase)) return new UseGlobalJavaPreference();
        string text = rawPreference.Trim();
        if (text.Equals("auto", StringComparison.OrdinalIgnoreCase) || text.Equals("自动选择", StringComparison.OrdinalIgnoreCase)) return new AutoSelectJavaPreference();
        if (text.StartsWith("relative:", StringComparison.OrdinalIgnoreCase)) return new UseRelativeJavaPreference(text[9..].Trim());
        return Path.IsPathRooted(text) ? new ExistingJavaPreference(Path.GetFullPath(text)) : new UseRelativeJavaPreference(text);
    }
}

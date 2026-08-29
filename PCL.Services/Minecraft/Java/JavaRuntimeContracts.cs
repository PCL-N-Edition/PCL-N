using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCL.Services.Minecraft.Java;

public enum JavaRuntimeOperatingSystem
{
    Win32,
    Linux,
    MacOs,
}

public enum JavaRuntimeArchitecture
{
    X86,
    X64,
    Arm64,
}

public readonly record struct JavaRuntimePlatform(JavaRuntimeOperatingSystem OperatingSystem, JavaRuntimeArchitecture Architecture)
{
    public string ToMojangKey() => OperatingSystem switch
    {
        JavaRuntimeOperatingSystem.Win32 => Architecture switch { JavaRuntimeArchitecture.X86 => "windows-x86", JavaRuntimeArchitecture.Arm64 => "windows-arm64", _ => "windows-x64" },
        JavaRuntimeOperatingSystem.Linux => Architecture == JavaRuntimeArchitecture.X86 ? "linux-i386" : "linux",
        JavaRuntimeOperatingSystem.MacOs => Architecture == JavaRuntimeArchitecture.Arm64 ? "mac-os-arm64" : "mac-os",
        _ => throw new ArgumentOutOfRangeException(nameof(OperatingSystem)),
    };
}

public sealed record JavaRuntimePackageDescriptor(string ComponentName, string VersionName, string ManifestUrl);
public sealed record JavaRuntimeDownloadFile(string RelativePath, string TargetPath, string Url, string Sha1, long Size, bool Executable = false);
public sealed record JavaRuntimeDownloadPlan(string ComponentName, string VersionName, string ManifestUrl, string TargetDirectory, IReadOnlyList<JavaRuntimeDownloadFile> Files);

public interface IJavaRuntimeMetadataProvider
{
    ValueTask<string> GetRuntimeIndexAsync(CancellationToken cancellationToken = default);
    ValueTask<string> GetManifestAsync(string manifestUrl, CancellationToken cancellationToken = default);
}

public static class JavaRuntimePackagePlanner
{
    private static readonly HashSet<string> IgnoredSha1 = ["12976a6c2b227cbac58969c1455444596c894656", "c80e4bab46e34d02826eab226a4441d0970f2aba", "84d2102ad171863db04e7ee22a259d1f6c5de4a5"];

    public static JavaRuntimePackageDescriptor SelectPackage(string runtimeIndexJson, JavaRuntimePlatform platform, string requestedComponent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIndexJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedComponent);
        using JsonDocument document = JsonDocument.Parse(runtimeIndexJson);
        if (!document.RootElement.TryGetProperty(platform.ToMojangKey(), out JsonElement platformElement)) throw new InvalidOperationException($"Mojang did not publish a Java runtime for {platform.ToMojangKey()}.");
        if (platformElement.TryGetProperty(requestedComponent, out JsonElement exact)) return CreateDescriptor(requestedComponent, FirstVersion(exact));
        foreach (JsonProperty component in platformElement.EnumerateObject())
        {
            try
            {
                JsonElement first = FirstVersion(component.Value);
                string version = Required(first, "version", "name");
                if (version.StartsWith(requestedComponent, StringComparison.OrdinalIgnoreCase)) return CreateDescriptor(component.Name, first);
            }
            catch (InvalidOperationException)
            {
                // Ignore malformed catalog entries while still allowing a valid sibling to be selected.
            }
        }

        throw new InvalidOperationException($"No Java runtime component matches {requestedComponent}.");
    }

    public static JavaRuntimeDownloadPlan CreateDownloadPlan(JavaRuntimePackageDescriptor packageDescriptor, string manifestJson, string runtimeRootDirectory)
    {
        ArgumentNullException.ThrowIfNull(packageDescriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRootDirectory);
        string runtimeRoot = Path.GetFullPath(runtimeRootDirectory);
        string targetDirectory = ResolveComponentDirectory(runtimeRoot, packageDescriptor.ComponentName);
        using JsonDocument document = JsonDocument.Parse(manifestJson);
        if (!document.RootElement.TryGetProperty("files", out JsonElement filesElement) || filesElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Java runtime manifest does not contain files.");
        List<JavaRuntimeDownloadFile> files = [];
        foreach (JsonProperty property in filesElement.EnumerateObject())
        {
            if (!property.Value.TryGetProperty("downloads", out JsonElement downloads) || !downloads.TryGetProperty("raw", out JsonElement raw)) continue;
            string url = Required(raw, "url");
            string sha1 = Required(raw, "sha1");
            long size = raw.GetProperty("size").GetInt64();
            if (IgnoredSha1.Contains(sha1)) continue;
            string target = ResolveContained(targetDirectory, property.Name);
            bool executable = property.Value.TryGetProperty("executable", out JsonElement executableElement) && executableElement.ValueKind == JsonValueKind.True;
            files.Add(new JavaRuntimeDownloadFile(property.Name, target, url, sha1, size, executable));
        }

        return new JavaRuntimeDownloadPlan(packageDescriptor.ComponentName, packageDescriptor.VersionName, packageDescriptor.ManifestUrl, targetDirectory, files);
    }

    private static JsonElement FirstVersion(JsonElement component) => component.ValueKind == JsonValueKind.Array && component.GetArrayLength() > 0 ? component[0] : throw new InvalidOperationException("Java runtime component has no versions.");
    private static JavaRuntimePackageDescriptor CreateDescriptor(string component, JsonElement version) => new(component, Required(version, "version", "name"), Required(version, "manifest", "url"));
    private static string Required(JsonElement element, string property, string? nested = null)
    {
        JsonElement value = nested is null ? element.GetProperty(property) : element.GetProperty(property).GetProperty(nested);
        string? text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? throw new InvalidOperationException($"Java runtime manifest field '{property}' is empty.") : text;
    }
    private static string ResolveContained(string root, string relative)
    {
        string normalized = relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized) || normalized.Split(Path.DirectorySeparatorChar).Any(static segment => segment is "" or "." or "..")) throw new InvalidOperationException("Java runtime file escapes its target directory.");
        string target = Path.GetFullPath(Path.Combine(root, normalized));
        string prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidOperationException("Java runtime file escapes its target directory.");
        return target;
    }

    private static string ResolveComponentDirectory(string runtimeRoot, string componentName)
    {
        if (string.IsNullOrWhiteSpace(componentName) || Path.IsPathRooted(componentName) || componentName.Split(['/', '\\'], StringSplitOptions.None).Any(static segment => segment is "" or "." or ".."))
            throw new InvalidOperationException("Java runtime component has an unsafe name.");
        return ResolveContained(runtimeRoot, componentName);
    }
}

public sealed class JavaRuntimeDownloadPlanService(IJavaRuntimeMetadataProvider metadataProvider)
{
    private readonly IJavaRuntimeMetadataProvider _metadataProvider = metadataProvider ?? throw new ArgumentNullException(nameof(metadataProvider));

    public async ValueTask<JavaRuntimeDownloadPlan> CreatePlanAsync(string requestedComponent, JavaRuntimePlatform platform, string runtimeRootDirectory, CancellationToken cancellationToken = default)
    {
        string index = await _metadataProvider.GetRuntimeIndexAsync(cancellationToken).ConfigureAwait(false);
        JavaRuntimePackageDescriptor packageDescriptor = JavaRuntimePackagePlanner.SelectPackage(index, platform, requestedComponent);
        string manifest = await _metadataProvider.GetManifestAsync(packageDescriptor.ManifestUrl, cancellationToken).ConfigureAwait(false);
        return JavaRuntimePackagePlanner.CreateDownloadPlan(packageDescriptor, manifest, runtimeRootDirectory);
    }

    /// <summary>Returns the launcher-scoped runtime directory without coupling Services to a platform project.</summary>
    public ValueTask<JavaRuntimeDownloadPlan> CreatePlanAsync(
        string requestedComponent,
        JavaRuntimePlatform platform,
        IJavaRuntimePathProvider pathProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pathProvider);
        return CreatePlanAsync(
            requestedComponent,
            platform,
            GetDefaultRuntimeRoot(pathProvider.ApplicationDataDirectory),
            cancellationToken);
    }

    public static string GetDefaultRuntimeRoot(string applicationDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);
        return Path.Combine(Path.GetFullPath(applicationDataDirectory), ".minecraft", "runtime");
    }
}

/// <summary>Minimal path seam owned by Services; platform adapters can implement it without a reverse reference.</summary>
public interface IJavaRuntimePathProvider
{
    string ApplicationDataDirectory { get; }
}

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

    /// <summary>
    /// Mathematical intersection: minimum = max(both minimums), maximum = min(both maximums).
    /// Returns false for a disjoint range, which callers surface as conflicting requirements
    /// instead of silently widening.
    /// </summary>
    public bool TryIntersect(JavaVersionRange other, out JavaVersionRange result)
    {
        Version minimum = Minimum > other.Minimum ? Minimum : other.Minimum;
        Version maximum = Maximum < other.Maximum ? Maximum : other.Maximum;
        if (minimum > maximum)
        {
            result = Any;
            return false;
        }

        result = new JavaVersionRange(minimum, maximum);
        return true;
    }

    public JavaVersionRange Intersect(JavaVersionRange other) =>
        TryIntersect(other, out JavaVersionRange result) ? result : throw new ArgumentException("The ranges do not intersect.");
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

    /// <summary>
    /// Normalizes a parsed Minecraft version to its true 1.x-era tuple: "1.8" becomes
    /// Version(1,8,0) instead of Version(8,0), so era gates order correctly.
    /// </summary>
    public static Version NormalizeVanilla(Version vanilla)
    {
        if (vanilla.Major == 1) return vanilla;
        return new Version(1, vanilla.Major, vanilla.Minor);
    }

    public static JavaRequirementResolution Resolve(MinecraftJavaRequirementRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        JavaVersionRange range = JavaVersionRange.Any;
        string? component = null;

        if (request.HasCleanroom)
        {
            if (!TryParseLoaderVersion(request.CleanroomVersion, out Version? cleanroom))
            {
                return JavaRequirementResolution.Invalid(JavaRequirementFailureReason.InvalidVersionMetadata, "Cleanroom version metadata is invalid.");
            }

            if (!range.TryIntersect(cleanroom!.Major >= 5 ? JavaVersionRange.ForMajor(25) : JavaVersionRange.ForMajor(21), out range))
            {
                return JavaRequirementResolution.Invalid(JavaRequirementFailureReason.ConflictingRequirements, "Overlapping Java version requirements are disjoint.");
            }
        }

        if (request.HasReliableVanillaVersion && request.VanillaVersion is { } rawVersion)
        {
            // Minecraft "1.8" parses as Version(8,0), which sorts AFTER "1.20.5" numerically.
            // Normalize to the true 1.x-era tuple before applying the gates.
            Version version = NormalizeVanilla(rawVersion);
            if (version >= new Version(1, 20, 5))
            {
                if (!range.TryIntersect(JavaVersionRange.ForMajor(21), out range))
                {
                    return JavaRequirementResolution.Invalid(JavaRequirementFailureReason.ConflictingRequirements, "Overlapping Java version requirements are disjoint.");
                }
            }
            else if (version < new Version(13, 0, 0))
            {
                if (!range.TryIntersect(JavaVersionRange.ForMajor(8), out range))
                {
                    return JavaRequirementResolution.Invalid(JavaRequirementFailureReason.ConflictingRequirements, "Overlapping Java version requirements are disjoint.");
                }
            }

            if (request.HasForge && version < new Version(12, 0, 0) && IsLegacyForge(request.ForgeVersion))
            {
                if (!range.TryIntersect(JavaVersionRange.ForMajor(7), out range))
                {
                    return JavaRequirementResolution.Invalid(JavaRequirementFailureReason.ConflictingRequirements, "Overlapping Java version requirements are disjoint.");
                }
            }

            if (request.HasOptiFine && version >= new Version(8, 0, 0) && version < new Version(13, 0, 0))
            {
                if (!range.TryIntersect(JavaVersionRange.ForMajor(8), out range))
                {
                    return JavaRequirementResolution.Invalid(JavaRequirementFailureReason.ConflictingRequirements, "Overlapping Java version requirements are disjoint.");
                }
            }
        }

        if (request.HasLiteLoader)
        {
            if (!range.TryIntersect(new JavaVersionRange(new Version(1, 8), JavaVersionRange.Java8Maximum), out range))
            {
                return JavaRequirementResolution.Invalid(JavaRequirementFailureReason.ConflictingRequirements, "Overlapping Java version requirements are disjoint.");
            }
        }

        if (request.HasLabyMod && request.VanillaVersion is { } labyVersion && labyVersion < new Version(13, 0, 0))
        {
            if (!range.TryIntersect(JavaVersionRange.ForMajor(8), out range))
            {
                return JavaRequirementResolution.Invalid(JavaRequirementFailureReason.ConflictingRequirements, "Overlapping Java version requirements are disjoint.");
            }
        }

        if (request.ManifestJavaMajorVersion is int manifestMajor)
        {
            if (manifestMajor < 7) return JavaRequirementResolution.Invalid(JavaRequirementFailureReason.InvalidVersionMetadata, "Manifest Java major version is below 7.");
            if (!range.TryIntersect(JavaVersionRange.ForMajor(manifestMajor), out range))
                return JavaRequirementResolution.Invalid(JavaRequirementFailureReason.ConflictingRequirements, "Overlapping Java version requirements are disjoint.");
            component = request.ManifestJavaComponent;
        }
        else if (!request.HasReliableVanillaVersion && request.ReleaseTime is { } release && release >= ManifestJava25Boundary)
        {
            if (!range.TryIntersect(JavaVersionRange.ForMajor(25), out range))
                return JavaRequirementResolution.Invalid(JavaRequirementFailureReason.ConflictingRequirements, "Overlapping Java version requirements are disjoint.");
            component = request.ManifestJavaComponent;
        }
        else if (!request.HasReliableVanillaVersion && request.ReleaseTime is { } snapshot && snapshot >= ManifestJava21Boundary)
        {
            if (!range.TryIntersect(JavaVersionRange.ForMajor(21), out range))
                return JavaRequirementResolution.Invalid(JavaRequirementFailureReason.ConflictingRequirements, "Overlapping Java version requirements are disjoint.");
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
        JavaPreference preference = TryParseJson(rawPreference) ?? ParseLegacy(rawPreference);
        return Normalize(preference, relativePathBaseDirectory);
    }

    private static JavaPreference? TryParseJson(string? rawPreference)
    {
        if (string.IsNullOrWhiteSpace(rawPreference) || !rawPreference.TrimStart().StartsWith('{')) return null;
        try
        {
            JsonObject? json = JsonNode.Parse(rawPreference)?.AsObject();
            string? kind = json?["kind"]?.ToString();
            if (kind is null) return null;
            return kind.ToLowerInvariant() switch
            {
                "auto" => new AutoSelectJavaPreference(),
                "global" => new UseGlobalJavaPreference(),
                "exist" => ReadString(json, "JavaExePath") is { } path ? new ExistingJavaPreference(path) : null,
                "relative" => ReadString(json, "RelativePath") is { } relativePath ? new UseRelativeJavaPreference(relativePath) : null,
                _ => null,
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static JavaPreference ParseLegacy(string? rawPreference)
    {
        string? trimmed = rawPreference?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return new AutoSelectJavaPreference();
        return string.Equals(trimmed, LegacyUseGlobalText, StringComparison.Ordinal) ? new UseGlobalJavaPreference() : new ExistingJavaPreference(trimmed);
    }

    private static JavaPreference Normalize(JavaPreference preference, string? relativePathBaseDirectory) => preference switch
    {
        ExistingJavaPreference existing when !Path.IsPathRooted(existing.JavaExecutablePath) => new UseGlobalJavaPreference(),
        UseRelativeJavaPreference relative when !IsSafeRelativePath(relative.RelativePath, relativePathBaseDirectory) => new UseGlobalJavaPreference(),
        _ => preference,
    };

    private static bool IsSafeRelativePath(string relativePath, string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || string.IsNullOrWhiteSpace(baseDirectory) || Path.IsPathRooted(relativePath)) return false;
        try
        {
            string baseFullPath = EnsureTrailingSeparator(Path.GetFullPath(baseDirectory));
            string resolvedPath = Path.GetFullPath(Path.Combine(baseFullPath, relativePath));
            return resolvedPath.StartsWith(baseFullPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static string EnsureTrailingSeparator(string directory) => directory.EndsWith(Path.DirectorySeparatorChar) || directory.EndsWith(Path.AltDirectorySeparatorChar) ? directory : directory + Path.DirectorySeparatorChar;

    private static string? ReadString(JsonObject? json, string propertyName)
    {
        string? value = json?[propertyName]?.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

namespace PCL.Services.Updates;

/// <summary>
/// How the installed launcher ships its files. Scatter installations apply per-file patch
/// bundles; single-file (portable) installations have their own signed block map and must
/// never receive a scatter patch index.
/// </summary>
public enum UpdateDistributionLayout
{
    Scatter = 0,
    SingleFile = 1,
}

/// <summary>
/// The update channel a launcher follows. Values are the legacy settings-compatible codes;
/// the former Dev channel is folded into CI.
/// </summary>
public enum UpdateChannel
{
    Release = 0,
    Beta = 1,

    /// <summary>Rolling CI builds (no binary patches are generated for CI).</summary>
    CI = 2,
}

/// <summary>
/// Identity stamped into the launcher build and used to select an update asset.
/// </summary>
public sealed record UpdateBuildIdentity(
    string Version,
    string RuntimeId,
    string RuntimeVariant,
    string Configuration)
{
    public UpdateDistributionLayout DistributionLayout { get; init; } = UpdateDistributionLayout.Scatter;

    public string NormalizedRuntimeVariant => NormalizeRuntimeVariant(RuntimeVariant);

    /// <summary>
    /// The host is always NativeAOT; the variant records whether the out-of-process plugin
    /// sidecar carries its own CoreCLR runtime. New packages drop the WithPlugin/NoPlugin
    /// suffixes, so every spelling normalizes to one of the two canonical values.
    /// </summary>
    public static string NormalizeRuntimeVariant(string? value)
    {
        string text = value?.Trim() ?? string.Empty;
        return text.StartsWith("NoRuntime", StringComparison.OrdinalIgnoreCase)
            ? "NoRuntime"
            : "SelfContained";
    }

    public static string NormalizeConfiguration(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "release" or "stable" or "final" => "Release",
        "beta" or "preview" or "rc" => "Beta",
        _ => "CI"
    };
}

/// <summary>A single HDiffPatch edge. Multi-hop updates contain more than one step.</summary>
public sealed record UpdatePatchStep(
    string FromVersion,
    string TargetVersion,
    string DownloadUrl,
    string Sha256,
    long Size,
    string FromSha256,
    long FromSize,
    string TargetSha256,
    long TargetSize,
    string Algorithm = "hdiffpatch",
    string? FromManifestSha256 = null,
    string? TargetManifestSha256 = null)
{
    public bool IsScatterBundle => string.Equals(
        Algorithm,
        "hdiffpatch-scatter-v1",
        StringComparison.OrdinalIgnoreCase);
}

/// <summary>Download and verification plan for a launcher update.</summary>
public sealed record UpdatePackage(
    string TargetVersion,
    string TargetTag,
    string FullPackageUrl,
    string TargetAssetName,
    string TargetBinaryName,
    string? TargetSha256,
    long? TargetSize,
    IReadOnlyList<UpdatePatchStep> PatchSteps,
    string RuntimeId,
    string RuntimeVariant,
    string Configuration,
    string? FullPackageSignatureUrl = null,
    string? TargetBinarySignatureUrl = null,
    string? FullPackageSha256 = null,
    long? FullPackageSize = null,
    string? BlockMapUrl = null,
    string? BlockMapSignatureUrl = null,
    string? BlockMapFallbackUrl = null,
    string? BlockMapFallbackSignatureUrl = null)
{
    public bool UsesPatch => PatchSteps.Count > 0;

    public bool SupportsBlockMap => !string.IsNullOrWhiteSpace(BlockMapUrl) &&
                                    !string.IsNullOrWhiteSpace(BlockMapSignatureUrl);
}

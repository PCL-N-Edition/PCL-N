namespace PCL.Services.Updates;

/// <summary>
/// Deployment-side inputs of update discovery: where release assets live, whether that
/// endpoint is the signed Cloudflare origin (GitHub fallbacks then never apply), and where a
/// GitHub fallback would live when it is not.
/// </summary>
public sealed record UpdateDistributionOptions(
    string DistributionBaseUrl,
    bool CloudflareOnly,
    string Owner,
    string Repo,
    string AssetNamePrefix = "PCL_N_")
{
    public string GitHubReleaseBaseUrl => $"https://github.com/{Owner}/{Repo}/releases/download";
}

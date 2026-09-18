using System.Net;
using System.Text.RegularExpressions;
namespace Nexa.Services.Minecraft.Install;

public sealed partial class HttpInstallCatalogSource
{
    public static IReadOnlyList<InstallCatalogVersion> ParseOptiFineCatalog(string html, string game)
    {
        Dictionary<string, string?> requirements = new(StringComparer.Ordinal);
        foreach (Match row in DownloadRows().Matches(html))
        {
            Match file = OptiFineFiles().Match(row.Value);
            if (!file.Success) continue;
            Match field = ForgeCell().Match(row.Value);
            string value = WebUtility.HtmlDecode(field.Groups[1].Value).Trim();
            string? requirement = value.StartsWith("Forge ", StringComparison.Ordinal) ? value[6..].Trim() : value;
            if (requirement != "N/A" && !ForgeVersion().IsMatch(requirement ?? "")) requirement = null;
            requirements[file.Groups[1].Value] = requirement;
        }
        return Array.AsReadOnly(OptiFineFiles().Matches(html).Select(match => match.Groups[1].Value)
            .Where(version => version.StartsWith(game + "_", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal)
            .Select(version => new InstallCatalogVersion(version, requirements.GetValueOrDefault(version) is { } requirement
                ? requirement == "N/A" ? "不支持 Forge" : "Forge " + requirement : "Forge 兼容性未知", Stable(version),
                ForgeRequirement: requirements.GetValueOrDefault(version))).ToArray());
    }
    [GeneratedRegex(@"<tr\b[^>]*>.*?</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 1000)]
    private static partial Regex DownloadRows();
    [GeneratedRegex("""<td\b[^>]*class=['"][^'"]*colForge[^'"]*['"][^>]*>(.*?)</td>""", RegexOptions.IgnoreCase | RegexOptions.Singleline, 1000)]
    private static partial Regex ForgeCell();
    [GeneratedRegex(@"^#?\d+(?:\.\d+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ForgeVersion();
}

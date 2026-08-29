using System.Text.RegularExpressions;

namespace PCL.Services.Logging;

/// <summary>
/// Removes common credentials from diagnostic text before it reaches any sink. The pattern set
/// is migrated from the legacy logging bridge and is part of the security behavior contract:
/// authorization headers, bearer tokens, secret assignments and arguments, and sensitive query
/// parameters are redacted wherever they appear.
/// </summary>
public static partial class LogRedactor
{
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string result = AuthorizationHeaderPattern().Replace(text, "$1<redacted>");
        result = BearerPattern().Replace(result, "$1<redacted>");
        result = SecretAssignmentPattern().Replace(result, "$1$2<redacted>");
        result = SecretArgumentPattern().Replace(result, "$1$2<redacted>");
        result = SensitiveQueryPattern().Replace(result, "$1<redacted>");
        return result;
    }

    [GeneratedRegex("(?i)(\\bAuthorization\\s*:\\s*)(?:(?:Bearer|Basic)\\s+)?[^\\s,;]+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex AuthorizationHeaderPattern();

    [GeneratedRegex("(?i)(\\bBearer\\s+)[^\\s,;]+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex BearerPattern();

    [GeneratedRegex("(?i)(\\b(?:access[_-]?token|refresh[_-]?token|password|passwd|api[_-]?key|client[_-]?secret|secret|token)\\b)(\\s*(?:=|:)\\s*)[^\\s,;&]+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SecretAssignmentPattern();

    [GeneratedRegex("(?i)(\\b(?:access[_-]?token|refresh[_-]?token|password|passwd|api[_-]?key|client[_-]?secret|secret)\\b)(\\s+)[^\\s,;&]+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SecretArgumentPattern();

    [GeneratedRegex("(?i)([?&](?:code|token|access_token|refresh_token|api_key|signature|sig)=)[^&#\\s]+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SensitiveQueryPattern();
}

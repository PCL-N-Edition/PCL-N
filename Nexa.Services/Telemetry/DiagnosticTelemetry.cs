using System.Globalization;
using System.Text.RegularExpressions;

namespace Nexa.Services.Telemetry;

/// <summary>Closed, low-cardinality diagnostic schema. Never pass free-form payloads through.</summary>
public static partial class DiagnosticTelemetry
{
    public static readonly IReadOnlyList<string> Features = Array.AsReadOnly(new[] { "launch", "install", "versions", "accounts", "settings", "tasks", "downloads", "updates", "wardrobe", "capabilities", "other" });
    public static string Category(string semantic)
    {
        foreach (string feature in Features)
        {
            string stem = feature switch { "versions" => "version", "accounts" => "account", "downloads" => "download", "updates" => "update", "tasks" => "task", "capabilities" => "capabilit", _ => feature };
            if (semantic.Contains(stem, StringComparison.OrdinalIgnoreCase)) return feature;
        }
        return "other";
    }
    public static bool IsMetric(string key) => key is "network.request.ms" or "launcher.working_set.mib" or "launcher.private.mib" or "launcher.managed.mib" or "launcher.cpu.percent"
        or "jvm.launch.ms" or "jvm.working_set.mib" or "catalog.results.count" or "catalog.normalize.ms" or "catalog.input.count" or "catalog.cache.hit" or "scheduler.duration.ms"
        || Features.Any(feature => key == $"dispatch.{feature}.ms");
    public static bool ValidExtras(TelemetryEvent item)
    {
        var p = item.Properties;
        return item.Name switch
        {
            "diagnostic.metric" => p.Count == 6 && p.TryGetValue("metric", out string? metric) && IsMetric(metric)
                && p.TryGetValue("value", out string? value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && double.IsFinite(n) && n >= 0 && n <= 1e12,
            "feature.used" or "feature.invoked" => p.Count == 5 && p.TryGetValue("feature", out string? feature) && Features.Contains(feature),
            "diagnostic.error" => p.Count == 8 && p.TryGetValue("category", out string? category) && Features.Contains(category)
                && p.TryGetValue("severity", out string? severity) && severity is "warning" or "error"
                && p.TryGetValue("code", out string? code) && CodePattern().IsMatch(code)
                && p.TryGetValue("stack", out string? stack) && stack.Length <= 2048 && stack.Split('\n').Length <= 8
                && stack.Split('\n').All(frame => frame.Length == 0 || FramePattern().IsMatch(frame)),
            _ => p.Count == 4
        };
    }
    public static (string Code, string Stack) ErrorSymbols(string? text)
    {
        text = text?[..Math.Min(text.Length, 16384)] ?? "";
        string code = ExtractCode().Match(text).Value;
        string stack = string.Join('\n', ExtractFrame().Matches(text).Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).Take(8));
        return (code.Length > 0 && code.Length <= 160 ? code : "Unknown", stack.Length <= 2048 ? stack : "");
    }
    [GeneratedRegex(@"^(?:Unknown|(?:System|Nexa|Avalonia|java|net|cpw)\.[A-Za-z0-9_.]{1,140}(?:Exception|Error))$")]
    private static partial Regex CodePattern();
    [GeneratedRegex(@"^(?:System|Nexa|Avalonia|java|net|cpw)\.[A-Za-z0-9_.+<>`]{1,220}$")]
    private static partial Regex FramePattern();
    [GeneratedRegex(@"\b(?:System|Nexa|Avalonia|java|net|cpw)\.[A-Za-z0-9_.]{1,140}(?:Exception|Error)\b")]
    private static partial Regex ExtractCode();
    [GeneratedRegex(@"(?m)^\s*(?:at|在)\s+((?:System|Nexa|Avalonia|java|net|cpw)\.[A-Za-z0-9_.+<>`]{1,220})\(")]
    private static partial Regex ExtractFrame();
}

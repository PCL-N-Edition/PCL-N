using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PCL.Services.Minecraft.Libraries;

namespace PCL.Services.Minecraft;

/// <summary>
/// The platform and feature facts used by Mojang's ordered manifest rules.
/// Keeping this context independent of a particular manifest surface lets launch arguments,
/// libraries, and future rule-bearing sections share exactly the same matching semantics.
/// </summary>
public readonly record struct MinecraftRuleContext(
    MinecraftLibraryOperatingSystem OperatingSystem,
    string OperatingSystemVersion,
    bool Is64BitArchitecture,
    bool IsArm64Architecture,
    IReadOnlyDictionary<string, bool> Features)
{
    public string OperatingSystemName => OperatingSystem switch
    {
        MinecraftLibraryOperatingSystem.Win32 => "windows",
        MinecraftLibraryOperatingSystem.Linux => "linux",
        MinecraftLibraryOperatingSystem.MacOs => "osx",
        _ => "unknown",
    };

    public string ArchitectureName => IsArm64Architecture
        ? "arm64"
        : Is64BitArchitecture ? "x86_64" : "x86";

    public static MinecraftRuleContext From(
        MinecraftLibraryOperatingSystem operatingSystem,
        string? operatingSystemVersion,
        bool is64BitArchitecture,
        bool isArm64Architecture,
        IReadOnlyDictionary<string, bool>? features = null) =>
        new(
            operatingSystem,
            operatingSystemVersion ?? string.Empty,
            is64BitArchitecture,
            isArm64Architecture,
            features ?? EmptyFeatures.Instance);

    private sealed class EmptyFeatures : IReadOnlyDictionary<string, bool>
    {
        public static readonly EmptyFeatures Instance = new();
        public IEnumerable<string> Keys => [];
        public IEnumerable<bool> Values => [];
        public int Count => 0;
        public bool this[string key] => false;
        public bool ContainsKey(string key) => false;
        public IEnumerator<KeyValuePair<string, bool>> GetEnumerator() =>
            Enumerable.Empty<KeyValuePair<string, bool>>().GetEnumerator();
        public bool TryGetValue(string key, out bool value) { value = false; return false; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

/// <summary>Evaluates Mojang manifest rules: the last matching rule wins.</summary>
public static class MinecraftRuleEvaluator
{
    public static bool IsAllowed(JsonNode? rulesNode, MinecraftRuleContext context)
    {
        if (rulesNode is not JsonArray rules || rules.Count == 0) return true;

        bool matched = false;
        bool allowed = false;
        foreach (JsonObject rule in rules.OfType<JsonObject>())
        {
            if (!Matches(rule, context)) continue;
            matched = true;
            allowed = !string.Equals(rule["action"]?.ToString(), "disallow", StringComparison.OrdinalIgnoreCase);
        }

        // A rule array with no matching rule is not an implicit allow. This is the behavior used
        // by the Mojang launcher for both argument and library rule surfaces.
        return matched && allowed;
    }

    public static bool Matches(JsonObject rule, MinecraftRuleContext context)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule["os"] is JsonObject os)
        {
            if (os["name"] is JsonNode name &&
                !string.Equals(name.ToString(), context.OperatingSystemName, StringComparison.OrdinalIgnoreCase))
                return false;

            if (os["arch"] is JsonNode arch && !ArchitectureMatches(arch.ToString(), context.ArchitectureName))
                return false;

            if (os["version"] is JsonNode version)
            {
                if (string.IsNullOrWhiteSpace(context.OperatingSystemVersion)) return false;
                try
                {
                    if (!Regex.IsMatch(
                        context.OperatingSystemVersion,
                        version.ToString(),
                        RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(250))) return false;
                }
                catch (ArgumentException)
                {
                    // A malformed third-party manifest rule is simply non-matching; it must not
                    // turn a safe resolver into an unbounded regex exception.
                    return false;
                }
                catch (RegexMatchTimeoutException)
                {
                    return false;
                }
            }
        }

        if (rule["features"] is JsonObject features)
        {
            IReadOnlyDictionary<string, bool> availableFeatures = context.Features ?? EmptyFeatures.Instance;
            foreach ((string name, JsonNode? expectedNode) in features)
            {
                bool expected = bool.TryParse(expectedNode?.ToString(), out bool parsed) && parsed;
                bool actual = availableFeatures.TryGetValue(name, out bool value) && value;
                if (actual != expected) return false;
            }
        }

        return true;
    }

    private sealed class EmptyFeatures : IReadOnlyDictionary<string, bool>
    {
        public static readonly EmptyFeatures Instance = new();
        public IEnumerable<string> Keys => [];
        public IEnumerable<bool> Values => [];
        public int Count => 0;
        public bool this[string key] => false;
        public bool ContainsKey(string key) => false;
        public bool TryGetValue(string key, out bool value) { value = false; return false; }
        public IEnumerator<KeyValuePair<string, bool>> GetEnumerator() =>
            Enumerable.Empty<KeyValuePair<string, bool>>().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static bool ArchitectureMatches(string expected, string actual)
    {
        string normalized = expected.Trim().ToLowerInvariant() switch
        {
            "amd64" or "x64" or "x86-64" => "x86_64",
            "aarch64" or "arm64-v8a" => "arm64",
            "i386" or "i486" or "i586" or "i686" or "x32" => "x86",
            _ => expected.Trim().ToLowerInvariant(),
        };
        return string.Equals(normalized, actual, StringComparison.OrdinalIgnoreCase);
    }
}

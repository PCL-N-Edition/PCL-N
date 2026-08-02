// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using PCL.Core.Logging;
using PCL.Desktop.Hosting;
using PCL.Desktop.Paths;

namespace PCL.Desktop.Telemetry;

/// <summary>One auditable place for telemetry minimisation, bucketing and redaction.</summary>
internal static class TelemetryDataPolicy
{
    private static readonly string[] ForbiddenKeyFragments =
    [
        "account", "address", "cookie", "credential", "email", "file", "host",
        "login", "mac", "machine", "password", "path", "profile", "secret", "sid",
        "token", "url", "user", "uuid"
    ];

    public static string Release => PclMetadata.Current.Version.Base;

    public static string ReleaseChannel => PclMetadata.Current.UpdateConfiguration.ToLowerInvariant();

    public static string Platform =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "macos" :
        OperatingSystem.IsLinux() ? "linux" : "other";

    public static string Architecture => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    public static string NormalizeName(string? value, string fallback = "unknown")
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        StringBuilder result = new(Math.Min(value.Length, 64));
        bool previousSeparator = false;
        foreach (char character in value.Trim().ToLowerInvariant())
        {
            bool allowed = character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-';
            char next = allowed ? character : '_';
            if (next == '_' && previousSeparator)
                continue;
            result.Append(next);
            previousSeparator = next == '_';
            if (result.Length >= 64)
                break;
        }

        string normalized = result.ToString().Trim('_', '.', '-');
        return normalized.Length == 0 ? fallback : normalized;
    }

    public static IReadOnlyDictionary<string, string> SanitizeProperties(
        IReadOnlyDictionary<string, string>? properties)
    {
        if (properties is null || properties.Count == 0)
            return EmptyProperties;

        Dictionary<string, string> safe = new(StringComparer.Ordinal);
        foreach ((string rawKey, string rawValue) in properties)
        {
            string key = NormalizeName(rawKey, string.Empty);
            if (key.Length == 0 || ForbiddenKeyFragments.Any(
                    fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string value = NormalizePropertyValue(rawValue);
            if (value.Length > 0)
                safe[key] = value;
        }

        return safe;
    }

    public static string CreateFailureFingerprint(Exception exception, string stage)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string type = exception.GetType().FullName ?? exception.GetType().Name;
        string input = type + "|" + NormalizeName(stage);
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(digest.AsSpan(0, 12)).ToLowerInvariant();
    }

    public static string RedactText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        string redacted = PortableLog.Redact(value);
        foreach (string path in EnumerateKnownPaths())
        {
            if (!string.IsNullOrWhiteSpace(path))
                redacted = redacted.Replace(path, "<local-path>", StringComparison.OrdinalIgnoreCase);
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
            redacted = redacted.Replace(home, "<user-home>", StringComparison.OrdinalIgnoreCase);

        return redacted.Length <= 4096 ? redacted : redacted[..4096] + "…";
    }

    public static IReadOnlyDictionary<string, string> CreateEnvironmentBuckets()
    {
        long memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["app_version"] = Release,
            ["release_channel"] = ReleaseChannel,
            ["platform"] = Platform,
            ["os_version"] = $"{Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}",
            ["architecture"] = Architecture,
            ["runtime_major"] = Environment.Version.Major.ToString(CultureInfo.InvariantCulture),
            ["locale"] = CultureInfo.CurrentUICulture.Name,
            ["memory_bucket"] = BucketMemory(memory),
            ["cpu_core_bucket"] = BucketCpu(Environment.ProcessorCount)
        };
    }

    private static IReadOnlyDictionary<string, string> EmptyProperties { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static string NormalizePropertyValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string redacted = RedactText(value.Trim());
        if (redacted.Contains('<') || redacted.Contains('>') ||
            redacted.Contains('/') || redacted.Contains('\\') || redacted.Contains('@'))
        {
            return NormalizeName(redacted);
        }

        return redacted.Length <= 128 ? redacted : redacted[..128];
    }

    private static IEnumerable<string> EnumerateKnownPaths()
    {
        string? processDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrWhiteSpace(processDirectory))
            yield return processDirectory;

        string? data = TryResolvePath(() => LauncherPathLayout.ResolveDataDirectory());
        if (!string.IsNullOrWhiteSpace(data))
            yield return data;

        string? cache = TryResolvePath(() => LauncherPathLayout.ResolveCacheDirectory());
        if (!string.IsNullOrWhiteSpace(cache))
            yield return cache;
    }

    private static string? TryResolvePath(Func<string> resolver)
    {
        try
        {
            return resolver();
        }
        catch
        {
            return null;
        }
    }

    private static string BucketMemory(long bytes)
    {
        long gib = bytes <= 0 ? 0 : bytes / (1024L * 1024 * 1024);
        return gib switch
        {
            <= 0 => "unknown",
            <= 4 => "0-4_gib",
            <= 8 => "5-8_gib",
            <= 16 => "9-16_gib",
            <= 32 => "17-32_gib",
            _ => "33+_gib"
        };
    }

    private static string BucketCpu(int count) => count switch
    {
        <= 0 => "unknown",
        <= 2 => "1-2",
        <= 4 => "3-4",
        <= 8 => "5-8",
        <= 16 => "9-16",
        _ => "17+"
    };
}

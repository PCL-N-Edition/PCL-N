using System.Globalization;

namespace PCL.Services.Minecraft;

/// <summary>One entry from a Minecraft version manifest catalog.</summary>
public sealed record MinecraftVersionManifestEntry
{
    public MinecraftVersionManifestEntry(string id, string type, string url, DateTimeOffset? releaseTime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        Id = id;
        Type = type;
        Url = url;
        ReleaseTime = releaseTime;
    }

    public string Id { get; }
    public string Type { get; }
    public string Url { get; }
    public DateTimeOffset? ReleaseTime { get; }
}

public enum MinecraftVersionCategory
{
    Release,
    Snapshot,
    BeforeRelease,
    AprilFools,
}

public readonly record struct MinecraftAprilFoolsDescriptor(
    string DescriptionResourceKey,
    string? TagResourceKey);

public readonly record struct MinecraftVersionClassification(
    string Id,
    string Type,
    MinecraftVersionCategory Category,
    MinecraftAprilFoolsDescriptor? AprilFoolsDescriptor);

/// <summary>
/// Compatibility classifier for Mojang catalog identifiers. It is pure and therefore can be
/// used by both the catalog transport and offline instance discovery.
/// </summary>
public static class MinecraftVersionClassifier
{
    public static MinecraftVersionClassification Classify(MinecraftVersionManifestEntry version)
    {
        ArgumentNullException.ThrowIfNull(version);

        string id = version.Id.Trim();
        string type = version.Type.Trim();
        string lower = id.ToLowerInvariant();
        MinecraftVersionCategory category = type.ToLowerInvariant() switch
        {
            "release" => MinecraftVersionCategory.Release,
            "special" => MinecraftVersionCategory.AprilFools,
            "snapshot" or "pending" => ClassifySnapshotOrPending(lower, ref type),
            _ => MinecraftVersionCategory.BeforeRelease,
        };

        if (TryMarkAprilFools(version, lower, ref id, ref type, out MinecraftAprilFoolsDescriptor? descriptor))
        {
            category = MinecraftVersionCategory.AprilFools;
        }

        return new MinecraftVersionClassification(id, type, category, descriptor);
    }

    /// <summary>Returns the historical display alias used by the launcher.</summary>
    public static string FormatVersion(string gameVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameVersion);
        string id = gameVersion.Trim().ToLowerInvariant();
        return id switch
        {
            "0.30-1" or "0.30-2" or "c0.30_01c" => "Classic_0.30",
            "in-20100206-2103" => "Indev_20100206",
            "inf-20100630-1" => "Infdev_20100630",
            "inf-20100630-2" => "Alpha_v1.0.0",
            "1.19_deep_dark_experimental_snapshot-1" => "1.19-exp1",
            "in-20100130" => "Indev_0.31_20100130",
            "b1.6-tb3" => "Beta_1.6_Test_Build_3",
            "1_14_combat-212796" => "1.14.3_-_Combat_Test",
            "1_14_combat-0" => "Combat_Test_2",
            "1_14_combat-3" => "Combat_Test_3",
            "1_15_combat-1" => "Combat_Test_4",
            "1_15_combat-6" => "Combat_Test_5",
            "1_16_combat-0" => "Combat_Test_6",
            "1_16_combat-1" => "Combat_Test_7",
            "1_16_combat-2" => "Combat_Test_7b",
            "1_16_combat-3" => "Combat_Test_7c",
            "1_16_combat-4" => "Combat_Test_8",
            "1_16_combat-5" => "Combat_Test_8b",
            "1_16_combat-6" => "Combat_Test_8c",
            _ => FormatVersionFallback(id),
        };
    }

    private static string FormatVersionFallback(string id)
    {
        if (id.StartsWith("1.0.0-rc2", StringComparison.Ordinal)) return "RC2";
        if (id.StartsWith("2.0", StringComparison.Ordinal) || id.StartsWith("2point0", StringComparison.Ordinal)) return "2.0";
        if (id.StartsWith("b1.8-pre1", StringComparison.Ordinal)) return "Beta_1.8-pre1";
        if (id.StartsWith("b1.1-", StringComparison.Ordinal)) return "Beta_1.1";
        if (id.StartsWith("a1.1.0", StringComparison.Ordinal)) return "Alpha_v1.1.0";
        if (id.StartsWith("a1.0.14", StringComparison.Ordinal)) return "Alpha_v1.0.14";
        if (id.StartsWith("a1.0.13_01", StringComparison.Ordinal)) return "Alpha_v1.0.13_01";
        if (id.StartsWith("in-20100214", StringComparison.Ordinal)) return "Indev_20100214";
        if (id.Contains("experimental-snapshot", StringComparison.Ordinal)) return id.Replace("_experimental-snapshot-", "-exp", StringComparison.Ordinal);
        if (id.StartsWith("inf-", StringComparison.Ordinal)) return "Infdev_" + id[4..];
        if (id.StartsWith("in-", StringComparison.Ordinal)) return "Indev_" + id[3..];
        if (id.StartsWith("rd-", StringComparison.Ordinal)) return "pre-Classic_" + id;
        if (id.StartsWith('b')) return "Beta_" + id[1..];
        if (id.StartsWith('a')) return "Alpha_v" + id[1..];
        return id.StartsWith('c')
            ? ("Classic_" + id[1..]).Replace("st", "SURVIVAL_TEST", StringComparison.Ordinal)
            : id;
    }

    private static MinecraftVersionCategory ClassifySnapshotOrPending(string lower, ref string type)
    {
        if (lower.StartsWith("1.", StringComparison.Ordinal) &&
            !lower.Contains("combat", StringComparison.Ordinal) &&
            !lower.Contains("rc", StringComparison.Ordinal) &&
            !lower.Contains("experimental", StringComparison.Ordinal) &&
            lower != "1.2" &&
            !lower.Contains("pre", StringComparison.Ordinal))
        {
            type = "release";
            return MinecraftVersionCategory.Release;
        }

        return MinecraftVersionCategory.Snapshot;
    }

    private static bool TryMarkAprilFools(
        MinecraftVersionManifestEntry version,
        string lower,
        ref string id,
        ref string type,
        out MinecraftAprilFoolsDescriptor? descriptor)
    {
        descriptor = lower switch
        {
            "2point0_blue" or "2point0_red" or "2point0_purple" or "2.0_blue" or "2.0_red" or "2.0_purple" or "2.0"
                => new MinecraftAprilFoolsDescriptor("Minecraft.Fool.Description.2013", GetColorTag(lower)),
            "15w14a" => new MinecraftAprilFoolsDescriptor("Minecraft.Fool.Description.2015", null),
            "1.rv-pre1" => new MinecraftAprilFoolsDescriptor("Minecraft.Fool.Description.2016", null),
            "3d shareware v1.34" => new MinecraftAprilFoolsDescriptor("Minecraft.Fool.Description.2019", null),
            "20w14infinite" or "20w14∞" => new MinecraftAprilFoolsDescriptor("Minecraft.Fool.Description.2020", null),
            "22w13oneblockatatime" => new MinecraftAprilFoolsDescriptor("Minecraft.Fool.Description.2022", null),
            "23w13a_or_b" => new MinecraftAprilFoolsDescriptor("Minecraft.Fool.Description.2023", null),
            "24w14potato" => new MinecraftAprilFoolsDescriptor("Minecraft.Fool.Description.2024", null),
            "25w14craftmine" => new MinecraftAprilFoolsDescriptor("Minecraft.Fool.Description.2025", null),
            "26w14a" => new MinecraftAprilFoolsDescriptor("Minecraft.Fool.Description.2026", null),
            _ => null,
        };

        bool isAprilFools = descriptor is not null ||
                            (!type.Equals("release", StringComparison.OrdinalIgnoreCase) &&
                             version.ReleaseTime is { } releaseTime &&
                             IsAprilFoolsDay(releaseTime));
        if (!isAprilFools)
        {
            return false;
        }

        if (lower is "2point0_blue" or "2point0_red" or "2point0_purple")
        {
            id = id.Replace("point", ".", StringComparison.OrdinalIgnoreCase);
        }
        else if (lower is "20w14infinite" or "20w14∞")
        {
            id = "20w14∞";
        }

        type = "special";
        return true;
    }

    private static string? GetColorTag(string lower) =>
        lower.EndsWith("red", StringComparison.Ordinal) ? "Minecraft.Fool.Tag.Red" :
        lower.EndsWith("blue", StringComparison.Ordinal) ? "Minecraft.Fool.Tag.Blue" :
        lower.EndsWith("purple", StringComparison.Ordinal) ? "Minecraft.Fool.Tag.Purple" : null;

    private static bool IsAprilFoolsDay(DateTimeOffset value)
    {
        DateTimeOffset adjusted = value.ToUniversalTime().AddHours(2d);
        return adjusted.Month == 4 && adjusted.Day == 1;
    }
}

/// <summary>One locally discovered version descriptor.</summary>
public sealed record MinecraftVersionDescriptor(
    string Id,
    string DirectoryPath,
    string JsonPath,
    string? JarPath,
    string? InheritsFrom,
    string? MainClass,
    DateTimeOffset? ReleaseTime,
    MinecraftVersionClassification Classification);

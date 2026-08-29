using System.Text.Json.Nodes;

namespace PCL.Services.Minecraft.ModLoaders;

public enum MinecraftModLoaderKind
{
    Vanilla,
    OptiFine,
    Forge,
    NeoForge,
    Fabric,
    Quilt,
    LiteLoader,
    Cleanroom,
    LabyMod,
    Unknown,
}

public sealed record MinecraftModLoaderDescriptor(
    MinecraftModLoaderKind Kind,
    string? Version,
    string? MainClass,
    IReadOnlyList<string> Signals);

public static class MinecraftModLoaderDetector
{
    public static MinecraftModLoaderDescriptor Detect(JsonObject versionJson)
    {
        ArgumentNullException.ThrowIfNull(versionJson);
        List<string> signals = [];
        if (versionJson["id"] is JsonNode id) signals.Add(id.ToString());
        if (versionJson["mainClass"] is JsonNode main) signals.Add(main.ToString());
        if (versionJson["libraries"] is JsonArray libraries)
        {
            foreach (JsonNode? library in libraries)
            {
                if (library?["name"] is JsonNode name) signals.Add(name.ToString());
            }
        }

        string haystack = string.Join('\n', signals);
        MinecraftModLoaderKind kind = haystack.Contains("cleanroom", StringComparison.OrdinalIgnoreCase) ? MinecraftModLoaderKind.Cleanroom :
            haystack.Contains("neoforge", StringComparison.OrdinalIgnoreCase) ? MinecraftModLoaderKind.NeoForge :
            haystack.Contains("forge", StringComparison.OrdinalIgnoreCase) ? MinecraftModLoaderKind.Forge :
            haystack.Contains("quilt", StringComparison.OrdinalIgnoreCase) ? MinecraftModLoaderKind.Quilt :
            haystack.Contains("fabric", StringComparison.OrdinalIgnoreCase) ? MinecraftModLoaderKind.Fabric :
            haystack.Contains("liteloader", StringComparison.OrdinalIgnoreCase) ? MinecraftModLoaderKind.LiteLoader :
            haystack.Contains("labymod", StringComparison.OrdinalIgnoreCase) ? MinecraftModLoaderKind.LabyMod :
            haystack.Contains("optifine", StringComparison.OrdinalIgnoreCase) ? MinecraftModLoaderKind.OptiFine :
            MinecraftModLoaderKind.Vanilla;
        string? version = ExtractVersion(haystack, kind);
        return new MinecraftModLoaderDescriptor(kind, version, versionJson["mainClass"]?.ToString(), signals);
    }

    private static string? ExtractVersion(string haystack, MinecraftModLoaderKind kind)
    {
        string marker = kind switch
        {
            MinecraftModLoaderKind.Forge => "forge-",
            MinecraftModLoaderKind.NeoForge => "neoforge-",
            MinecraftModLoaderKind.Fabric => "fabric-loader-",
            MinecraftModLoaderKind.Quilt => "quilt-loader-",
            MinecraftModLoaderKind.LiteLoader => "liteloader-",
            _ => string.Empty,
        };
        if (marker.Length == 0) return null;
        int start = haystack.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += marker.Length;
        int end = start;
        while (end < haystack.Length && (char.IsLetterOrDigit(haystack[end]) || haystack[end] is '.' or '-' or '_')) end++;
        return end == start ? null : haystack[start..end];
    }
}


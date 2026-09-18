using System.Text.Json;

namespace Nexa.Services.Minecraft;

/// <summary>Installed version family, ordered by the launcher's mixed-loader precedence.</summary>
public enum MinecraftVersionKind
{
    Release, Snapshot, Old, AprilFools, OptiFine, LiteLoader, Forge, NeoForge, Cleanroom, Quilt, Fabric, LabyMod,
}

internal static class MinecraftVersionKindReader
{
    private static readonly string[] SnapshotSignals = ["snapshot", "rc", "pre", "experimental", "combat"];
    public static MinecraftVersionKind Read(string root, string directory, string jsonPath, JsonElement manifest)
    {
        MinecraftVersionKind kind = Classify(manifest);
        HashSet<string> visited = new(MinecraftLibraryService.PathComparer) { jsonPath };
        // Reuse the installed-manifest path rules, including a parent stored beside its child.
        for (int depth = 0; depth < 64; depth++)
        {
            string reference = Text(manifest, "inheritsFrom");
            if (!MinecraftVersionPaths.IsSafeReference(reference)) break;
            string? parent = MinecraftVersionPaths.ResolveJsonPath(root, directory, reference);
            if (parent is null || !visited.Add(parent) || !MinecraftVersionPaths.TryReadDescriptor(parent, out _, out manifest)) break;
            kind = (MinecraftVersionKind)Math.Max((int)kind, (int)Classify(manifest));
            directory = Path.GetDirectoryName(parent)!;
        }
        return kind;
    }

    private static MinecraftVersionKind Classify(JsonElement manifest)
    {
        string signals = manifest.GetRawText();
        bool Has(string token) => signals.Contains(token, StringComparison.OrdinalIgnoreCase);
        if (Has("labymod")) return MinecraftVersionKind.LabyMod;
        if (Has("legacyfabric") || Has("legacy-fabric") || Has("fabric-loader")) return MinecraftVersionKind.Fabric;
        if (Has("quilt-loader")) return MinecraftVersionKind.Quilt;
        if (Has("cleanroom")) return MinecraftVersionKind.Cleanroom;
        if (Has("neoforge")) return MinecraftVersionKind.NeoForge;
        if (Has("net.minecraftforge:forge") || Has("minecraftforge")) return MinecraftVersionKind.Forge;
        if (Has("liteloader")) return MinecraftVersionKind.LiteLoader;
        if (Has("optifine")) return MinecraftVersionKind.OptiFine;
        string id = Text(manifest, "id"), inherited = Text(manifest, "inheritsFrom"), type = Text(manifest, "type");
        if (type is "fool" or "special" || IsAprilFools(id) || IsAprilFools(inherited)) return MinecraftVersionKind.AprilFools;
        if (type is "old_alpha" or "old_beta") return MinecraftVersionKind.Old;
        return type is "snapshot" or "pending" || IsSnapshot(id) || IsSnapshot(inherited)
            ? MinecraftVersionKind.Snapshot : MinecraftVersionKind.Release;
    }

    private static string Text(JsonElement manifest, string name) =>
        manifest.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    private static bool IsAprilFools(string id) => id.Length > 0 &&
        (id.StartsWith("2.0", StringComparison.OrdinalIgnoreCase) || id.StartsWith("2point0", StringComparison.OrdinalIgnoreCase) ||
        MinecraftVersionClassifier.Classify(new(id, "release", "local")).Category == MinecraftVersionCategory.AprilFools);
    private static bool IsSnapshot(string id) => id.Contains('w', StringComparison.OrdinalIgnoreCase) || id.Contains('-', StringComparison.Ordinal) ||
        SnapshotSignals.Any(token => id.Contains(token, StringComparison.OrdinalIgnoreCase));
}

using System.Text.Json.Nodes;

namespace Nexa.Services.Minecraft.Install;

internal static class AdditionalLoaderProfiles
{
    internal static JsonObject LabyMod(JsonObject profile, JsonObject manifest, JsonObject catalog, string game, string channel)
    {
        string commit = manifest["commitReference"]!.ToString();
        string sha1 = manifest["sha1"]?.ToString() ?? "";
        if (sha1.Length != 40 || !sha1.All(char.IsAsciiHexDigit)) throw new InvalidDataException("LabyMod 文件校验信息无效。");
        JsonArray libraries = profile["libraries"] as JsonArray ?? new();
        foreach (var lib in catalog["libraries"]!.AsArray().OfType<JsonObject>())
        {
            if (lib["minecraftVersion"]?.ToString() is not "all" && lib["minecraftVersion"]?.ToString() != game) continue;
            libraries.Add((JsonNode)new JsonObject
            {
                ["name"] = lib["name"]!.ToString(),
                ["downloads"] = new JsonObject { ["artifact"] = new JsonObject { ["url"] = lib["url"]!.ToString(), ["sha1"] = lib["sha1"]?.DeepClone(), ["size"] = lib["size"]?.DeepClone() } },
            });
        }
        libraries.Add((JsonNode)new JsonObject
        {
            ["name"] = "net.labymod:LabyMod:" + manifest["labyModVersion"] + "-" + commit,
            ["downloads"] = new JsonObject
            {
                ["artifact"] = new JsonObject
                {
                    ["url"] = $"https://releases.r2.labymod.net/api/v1/download/labymod4/{Uri.EscapeDataString(channel)}/{Uri.EscapeDataString(commit)}.jar",
                    // The provider's size can differ from its published JAR; its SHA-1 matches the artifact.
                    ["sha1"] = sha1,
                }
            },
        });
        if (profile["libraries"] is null) profile["libraries"] = libraries;
        JsonObject assets = new();
        foreach (var asset in manifest["assets"] as JsonObject ?? new())
            assets[asset.Key] = $"https://releases.r2.labymod.net/api/v1/download/assets/labymod4/{Uri.EscapeDataString(channel)}/{Uri.EscapeDataString(commit)}/{Uri.EscapeDataString(asset.Key)}/{Uri.EscapeDataString(asset.Value!.ToString())}.jar";
        profile["_nexaLabyAssets"] = assets;
        return profile;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Cryptographic Do Not Use", "CA5351:DoNotUseBrokenCryptographicAlgorithms", Justification = "LiteLoader publishes MD5 for its historical artifacts; verify that provider fact before recording SHA-1 for future repair.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Cryptographic Do Not Use", "CA5350:DoNotUseWeakCryptographicAlgorithms", Justification = "Minecraft manifests use SHA-1 for artifact repair.")]
    internal static async Task VerifyLiteLoaderAsync(JsonObject profile, string root, CancellationToken token)
    {
        if (profile["_nexaLiteLoaderMd5"] is not { } expected) return;
        var lib = profile["libraries"]!.AsArray().OfType<JsonObject>().Single(l => l["name"]!.ToString().StartsWith("com.mumfrey:liteloader:", StringComparison.Ordinal));
        string path = Nexa.Services.Minecraft.Libraries.MinecraftLibraryResolver.GetCoordinatePath(lib["name"]!.ToString(), root);
        await using var stream = File.OpenRead(path);
        string md5 = Convert.ToHexString(await System.Security.Cryptography.MD5.HashDataAsync(stream, token).ConfigureAwait(false));
        if (!md5.Equals(expected.ToString(), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("LiteLoader 文件校验失败。");
        stream.Position = 0;
        string sha1 = Convert.ToHexString(await System.Security.Cryptography.SHA1.HashDataAsync(stream, token).ConfigureAwait(false));
        lib["downloads"] = new JsonObject { ["artifact"] = new JsonObject { ["sha1"] = sha1, ["size"] = stream.Length } };
        profile.Remove("_nexaLiteLoaderMd5");
    }

    internal static JsonObject LiteLoader(JsonObject catalog, string game, string build)
    {
        var entry = catalog["versions"]?[game] as JsonObject ?? throw new InvalidDataException("没有此版本的 LiteLoader。");
        var channel = entry["artefacts"] as JsonObject ?? entry["snapshots"] as JsonObject;
        var latest = channel?["com.mumfrey:liteloader"]?["latest"] as JsonObject;
        if (latest?["version"]?.ToString() != build) throw new InvalidDataException("所选 LiteLoader 版本已失效。");
        JsonArray libraries = latest["libraries"]?.DeepClone() as JsonArray ?? [];
        foreach (var library in libraries.OfType<JsonObject>())
            if (library["url"] is null && library["downloads"] is null) library["url"] = library["name"]?.ToString().StartsWith("net.minecraft:", StringComparison.Ordinal) == true
                    ? "https://libraries.minecraft.net/" : "https://repo.maven.apache.org/maven2/";
        libraries.Add((JsonNode)new JsonObject { ["name"] = "com.mumfrey:liteloader:" + build, ["url"] = "https://bmclapi2.bangbang93.com/maven/" });
        return new JsonObject
        {
            ["_nexaLiteLoaderMd5"] = latest["md5"]?.DeepClone(),
            ["id"] = game + "-LiteLoader",
            ["inheritsFrom"] = game,
            ["jar"] = game,
            ["mainClass"] = "net.minecraft.launchwrapper.Launch",
            ["libraries"] = libraries,
            ["arguments"] = new JsonObject { ["game"] = new JsonArray("--tweakClass", latest["tweakClass"]?.ToString() ?? "com.mumfrey.liteloader.launch.LiteLoaderTweaker") },
        };
    }
}

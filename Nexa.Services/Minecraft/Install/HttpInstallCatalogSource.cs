using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Nexa.Services.Minecraft.Install;

/// <summary>Read-only metadata transport. It never downloads or executes an installer.</summary>
public sealed partial class HttpInstallCatalogSource(HttpClient http, string? curseForgeApiKey = null) : IInstallCatalogSource
{
    public async Task<IReadOnlyList<InstallCatalogVersion>> GetGamesAsync(CancellationToken token)
    {
        using JsonDocument json = JsonDocument.Parse(await ReadAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", token).ConfigureAwait(false));
        return Array.AsReadOnly(json.RootElement.GetProperty("versions").EnumerateArray().Select(v =>
            new InstallCatalogVersion(Text(v, "id"), Text(v, "type"), Text(v, "type") == "release")).ToArray());
    }
    public async Task<IReadOnlyList<InstallCatalogVersion>> GetLoadersAsync(InstallLoader loader, string game, CancellationToken token)
    {
        if (InstallCompatibility.UnavailableReason(loader, game) is not null) return [];
        if (loader == InstallLoader.OptiFabric) return await ReadOptiFabricAsync(game, token).ConfigureAwait(false);
        if (loader is InstallLoader.FabricApi or InstallLoader.Qsl)
            return await ReadMergedAddonAsync(loader, game, token).ConfigureAwait(false);
        if (loader is InstallLoader.Fabric or InstallLoader.LegacyFabric or InstallLoader.Quilt)
        {
            string host = loader switch { InstallLoader.Fabric => "meta.fabricmc.net/v2", InstallLoader.LegacyFabric => "meta.legacyfabric.net/v2", _ => "meta.quiltmc.org/v3" };
            string normalized = game.Replace("∞", "infinite", StringComparison.Ordinal).Replace("Combat Test 7c", "1.16_combat-3", StringComparison.Ordinal);
            using JsonDocument json = JsonDocument.Parse(await ReadAsync($"https://{host}/versions/loader/{Uri.EscapeDataString(normalized)}", token, true).ConfigureAwait(false));
            return Array.AsReadOnly(json.RootElement.EnumerateArray().Select(v => v.GetProperty("loader")).Select(v =>
                new InstallCatalogVersion(Text(v, "version"), loader.ToString(), v.TryGetProperty("stable", out JsonElement stable) ? stable.ValueKind == JsonValueKind.True : Stable(Text(v, "version")))).ToArray());
        }
        if (loader is InstallLoader.Forge or InstallLoader.NeoForge)
        {
            bool legacyNeo = loader == InstallLoader.NeoForge && game == "1.20.1";
            string url = loader == InstallLoader.Forge ? "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml"
                : legacyNeo ? "https://maven.neoforged.net/releases/net/neoforged/forge/maven-metadata.xml"
                : "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";
            string xml = await ReadAsync(url, token).ConfigureAwait(false);
            using XmlReader reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            IEnumerable<string> versions = XDocument.Load(reader).Descendants("version").Select(v => v.Value);
            versions = loader == InstallLoader.Forge || legacyNeo ? versions.Where(v => v.StartsWith(game + "-", StringComparison.Ordinal)) : versions.Where(v => NeoGame(v) == game);
            return Array.AsReadOnly(versions.Reverse().Where(v => !legacyNeo || v != "1.20.1-47.1.82")
                .Select(v => loader == InstallLoader.Forge || legacyNeo ? v[(game.Length + 1)..] : v)
                .Select(v => new InstallCatalogVersion(v, loader.ToString(), Stable(v))).ToArray());
        }
        if (loader == InstallLoader.Cleanroom)
        {
            using JsonDocument json = JsonDocument.Parse(await ReadAsync("https://api.github.com/repos/CleanroomMC/Cleanroom/releases?per_page=100", token).ConfigureAwait(false));
            return Array.AsReadOnly(json.RootElement.EnumerateArray().Where(v => !v.TryGetProperty("draft", out JsonElement draft) || draft.ValueKind != JsonValueKind.True)
                .Select(v => new InstallCatalogVersion(Text(v, "tag_name"), "Cleanroom", !v.TryGetProperty("prerelease", out JsonElement pre) || pre.ValueKind != JsonValueKind.True)).ToArray());
        }
        if (loader == InstallLoader.OptiFine)
        {
            string html = await ReadAsync("https://optifine.net/downloads", token).ConfigureAwait(false);
            return ParseOptiFineCatalog(html, game);
        }
        if (loader == InstallLoader.LiteLoader)
        {
            using JsonDocument json = JsonDocument.Parse(await ReadAsync("https://dl.liteloader.com/versions/versions.json", token).ConfigureAwait(false));
            if (!json.RootElement.GetProperty("versions").TryGetProperty(game, out JsonElement entry)) return [];
            if (!entry.TryGetProperty("artefacts", out JsonElement channel) && !entry.TryGetProperty("snapshots", out channel)) return [];
            if (!channel.TryGetProperty("com.mumfrey:liteloader", out JsonElement artifact) || !artifact.TryGetProperty("latest", out JsonElement latest)) return [];
            return [new(Text(latest, "version"), "LiteLoader", Text(latest, "stream") != "SNAPSHOT")];
        }
        if (loader == InstallLoader.LabyMod)
        {
            List<InstallCatalogVersion> versions = [];
            foreach (string channel in new[] { "production", "snapshot" })
            {
                using JsonDocument json = JsonDocument.Parse(await ReadAsync($"https://releases.r2.labymod.net/api/v1/manifest/{channel}/latest.json", token).ConfigureAwait(false));
                JsonElement root = json.RootElement;
                if (root.GetProperty("minecraftVersions").EnumerateArray().Any(v => Text(v, "version") == game || Text(v, "tag") == game))
                {
                    string version = Text(root, "labyModVersion");
                    versions.Add(new($"{channel}+{version}+{Text(root, "commitReference")}", version, channel == "production"));
                }
            }
            return versions.AsReadOnly();
        }
        throw new ArgumentOutOfRangeException(nameof(loader));
    }
    private async Task<string> ReadAsync(string url, CancellationToken token, bool emptyOnMissing = false)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Nexa/2.0");
        if (request.RequestUri!.Host == "api.curseforge.com" && !string.IsNullOrWhiteSpace(_curseForgeKey))
            request.Headers.TryAddWithoutValidation("x-api-key", _curseForgeKey);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (emptyOnMissing && response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound) return "[]";
        response.EnsureSuccessStatusCode();
        const int maxBytes = 16 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maxBytes) throw new IOException("目录响应过大。");
        await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using MemoryStream buffer = new();
        byte[] bytes = new byte[16384]; int count;
        while ((count = await stream.ReadAsync(bytes, timeout.Token).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > maxBytes) throw new IOException("目录响应过大。");
            buffer.Write(bytes, 0, count);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }
    private static string Text(JsonElement item, string key) => item.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static readonly string[] UnstableMarkers = ["alpha", "beta", "snapshot", "pre", "rc"];
    private static bool Stable(string version) => !UnstableMarkers.Any(s => version.Contains(s, StringComparison.OrdinalIgnoreCase));
    private static string NeoGame(string version)
    {
        string[] parts = version.Split('-', 2)[0].Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out int major)) return "";
        if (major == 0) return parts[1];
        return major >= 24 ? $"{major}.{parts[1]}" + (parts.Length > 2 && parts[2] != "0" ? "." + parts[2] : "")
            : $"1.{major}" + (parts[1] == "0" ? "" : "." + parts[1]);
    }
    [GeneratedRegex("OptiFine_([0-9A-Za-z_.]+)\\.jar", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex OptiFineFiles();
}

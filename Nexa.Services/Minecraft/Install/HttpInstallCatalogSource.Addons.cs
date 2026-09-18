using System.Text.Json;
namespace Nexa.Services.Minecraft.Install;

public sealed partial class HttpInstallCatalogSource
{
    private readonly string? _curseForgeKey = curseForgeApiKey
        ?? Environment.GetEnvironmentVariable("Nexa_CURSEFORGE_API_KEY") ?? Environment.GetEnvironmentVariable("CURSEFORGE_API_KEY");
    private sealed record AddonSourceResult(IReadOnlyList<InstallCatalogVersion> Versions, string? Error);
    private async Task<IReadOnlyList<InstallCatalogVersion>> ReadMergedAddonAsync(InstallLoader loader, string game, CancellationToken token)
    {
        async Task<AddonSourceResult> Read(string name, Func<Task<IReadOnlyList<InstallCatalogVersion>>> read)
        {
            try { return new(await read().ConfigureAwait(false), null); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
            { return new([], name + " 获取失败：" + error.Message); }
        }
        Task<AddonSourceResult> modrinth = Read("Modrinth", () => ReadModrinthAddonAsync(loader, game, token));
        Task<AddonSourceResult> curseForge = Read("CurseForge", () => ReadCurseForgeAddonAsync(loader, game, token));
        AddonSourceResult[] sources = await Task.WhenAll(modrinth, curseForge).ConfigureAwait(false);
        string warning = string.Join("；", sources.Where(source => source.Error is not null).Select(source => source.Error));
        List<InstallCatalogVersion> merged = [];
        foreach (InstallCatalogVersion candidate in sources.SelectMany(source => source.Versions))
        {
            InstallDownload file = candidate.Downloads![0];
            int duplicate = merged.FindIndex(existing => existing.Downloads!.Any(download =>
                !string.IsNullOrEmpty(download.Sha1) && !string.IsNullOrEmpty(file.Sha1)
                    ? string.Equals(download.Sha1, file.Sha1, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(download.FileName, file.FileName, StringComparison.OrdinalIgnoreCase)));
            if (duplicate < 0) merged.Add(candidate);
            else merged[duplicate] = merged[duplicate] with
            {
                Downloads = Array.AsReadOnly(merged[duplicate].Downloads!.Concat(candidate.Downloads!).DistinctBy(download => download.Url).ToArray()),
                Detail = "Modrinth · CurseForge"
            };
        }
        if (merged.Count == 0 && warning.Length > 0) throw new IOException(warning);
        return Array.AsReadOnly(merged.Select(version => version with { Warning = warning.Length > 0 ? warning : null }).ToArray());
    }
    private async Task<IReadOnlyList<InstallCatalogVersion>> ReadModrinthAddonAsync(InstallLoader loader, string game, CancellationToken token)
    {
        string project = loader == InstallLoader.FabricApi ? "fabric-api" : "qsl";
        string required = loader == InstallLoader.FabricApi ? "fabric" : "quilt";
        string games = Uri.EscapeDataString("[\"" + game + "\"]");
        string loaders = Uri.EscapeDataString("[\"" + required + "\"]");
        using JsonDocument json = JsonDocument.Parse(await ReadAsync($"https://api.modrinth.com/v2/project/{project}/version?game_versions={games}&loaders={loaders}", token).ConfigureAwait(false));
        List<InstallCatalogVersion> versions = [];
        foreach (JsonElement version in json.RootElement.EnumerateArray())
        {
            if (!version.TryGetProperty("files", out JsonElement files) || files.GetArrayLength() == 0) continue;
            JsonElement file = files.EnumerateArray().FirstOrDefault(file => file.TryGetProperty("primary", out JsonElement primary) && primary.ValueKind == JsonValueKind.True);
            if (file.ValueKind == JsonValueKind.Undefined) file = files[0];
            string id = Text(version, "version_number"), filename = Text(file, "filename");
            if (id.Length == 0 || filename.Length == 0 || !DownloadUri(Text(file, "url"), out Uri? url)) continue;
            string? sha1 = file.TryGetProperty("hashes", out JsonElement hashes) ? Text(hashes, "sha1") : null;
            InstallDownload download = new("Modrinth", filename, url!, sha1, Number(file, "size"));
            versions.Add(new(id, "Modrinth", Text(version, "version_type") == "release", Array.AsReadOnly(new[] { download })));
        }
        return versions.AsReadOnly();
    }
    private async Task<IReadOnlyList<InstallCatalogVersion>> ReadCurseForgeAddonAsync(InstallLoader loader, string game, CancellationToken token)
    {
        string project = loader switch { InstallLoader.FabricApi => "306612", InstallLoader.OptiFabric => "322385", _ => "634179" };
        int loaderType = loader == InstallLoader.Qsl ? 5 : 4;
        List<InstallCatalogVersion> versions = [];
        HashSet<long> seen = [];
        for (int index = 0; index < 10000; index += 50)
        {
            string path = $"/mods/{project}/files?gameVersion={Uri.EscapeDataString(game)}&modLoaderType={loaderType}&pageSize=50&index={index}";
            string? body = null;
            if (!string.IsNullOrWhiteSpace(_curseForgeKey))
            {
                try { body = await ReadAsync("https://api.curseforge.com/v1" + path, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException) { /* Legacy official-to-MCIM fallback. */ }
            }
            body ??= await ReadAsync("https://mod.mcimirror.top/curseforge/v1" + path, token).ConfigureAwait(false);
            using JsonDocument json = JsonDocument.Parse(body);
            JsonElement data = json.RootElement.GetProperty("data");
            int added = 0;
            foreach (JsonElement file in data.EnumerateArray())
            {
                if (!seen.Add(Number(file, "id"))) continue;
                added++;
                string filename = Text(file, "fileName");
                if (filename.Length == 0 || !DownloadUri(Text(file, "downloadUrl"), out Uri? url)) continue;
                // Exact game match guards against mirrors ignoring the query filter.
                if (file.TryGetProperty("gameVersions", out JsonElement games) && !games.EnumerateArray().Any(value => value.GetString() == game)) continue;
                string? sha1 = file.TryGetProperty("hashes", out JsonElement hashes)
                    ? hashes.EnumerateArray().Where(hash => Number(hash, "algo") == 1).Select(hash => Text(hash, "value")).FirstOrDefault() : null;
                InstallDownload download = new("CurseForge", filename, url!, sha1, Number(file, "fileLength"));
                string id = filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ? filename[..^4] : filename;
                versions.Add(new(id, "CurseForge", Number(file, "releaseType") == 1, Array.AsReadOnly(new[] { download })));
            }
            if (added == 0 || data.GetArrayLength() < 50) break;
            if (json.RootElement.TryGetProperty("pagination", out JsonElement pagination) && index + data.GetArrayLength() >= Number(pagination, "totalCount")) break;
        }
        return versions.AsReadOnly();
    }
    private static long Number(JsonElement item, string key) => item.TryGetProperty(key, out JsonElement value) && value.TryGetInt64(out long number) ? number : 0;
    private static bool DownloadUri(string value, out Uri? uri) => Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps;
}

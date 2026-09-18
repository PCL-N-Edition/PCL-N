using System.IO.Compression;
using System.Text.Json;
namespace Nexa.Services.Minecraft.Install;

public sealed partial class HttpInstallCatalogSource
{
    private async Task<IReadOnlyList<InstallCatalogVersion>> ReadOptiFabricAsync(string game, CancellationToken token)
    {
        var versions = await ReadCurseForgeAddonAsync(InstallLoader.OptiFabric, game, token).ConfigureAwait(false);
        using SemaphoreSlim gate = new(2);
        async Task<InstallCatalogVersion?> Read(InstallCatalogVersion version)
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                InstallDownload file = version.Downloads![0];
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                using HttpRequestMessage request = new(HttpMethod.Get, file.Url);
                request.Headers.UserAgent.ParseAdd("Nexa/2.0");
                using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                const int maxBytes = 4 * 1024 * 1024;
                if (response.Content.Headers.ContentLength > maxBytes) throw new IOException("OptiFabric 文件过大。");
                await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using MemoryStream memory = new();
                byte[] buffer = new byte[16384]; int read;
                while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    if (memory.Length + read > maxBytes) throw new IOException("OptiFabric 文件过大。");
                    memory.Write(buffer, 0, read);
                }
                memory.Position = 0;
                using ZipArchive archive = new(memory, ZipArchiveMode.Read);
                ZipArchiveEntry metadata = archive.GetEntry("fabric.mod.json") ?? throw new IOException("缺少 OptiFabric 模组元数据。");
                if (metadata.Length > 65536) throw new IOException("OptiFabric 元数据过大。");
                using Stream entry = metadata.Open();
                using JsonDocument json = await JsonDocument.ParseAsync(entry, cancellationToken: timeout.Token).ConfigureAwait(false);
                JsonElement root = json.RootElement;
                if (Text(root, "id") != "optifabric") throw new IOException("下载文件不是 OptiFabric 模组。");
                if (!root.TryGetProperty("depends", out JsonElement dependencies) && !root.TryGetProperty("requires", out dependencies))
                    throw new IOException("缺少 OptiFabric 依赖声明。");
                if (dependencies.TryGetProperty("minecraft", out JsonElement minecraft))
                {
                    bool allowed = minecraft.ValueKind == JsonValueKind.Array
                        ? minecraft.EnumerateArray().Any(item => InstallCompatibility.MatchesPredicate(game, item.GetString()))
                        : InstallCompatibility.MatchesPredicate(game, minecraft.GetString());
                    if (!allowed) return null;
                }
                string fabric = Text(dependencies, "fabricloader");
                if (fabric.Length == 0) throw new IOException("缺少 Fabric Loader 依赖声明。");
                return version with { Id = Text(root, "version"), FabricRequirement = fabric, Detail = "Fabric " + fabric };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
            { return version with { Warning = "无法验证 OptiFabric 依赖：" + error.Message }; }
            finally { gate.Release(); }
        }
        var results = await Task.WhenAll(versions.Select(Read)).ConfigureAwait(false);
        return Array.AsReadOnly(results.OfType<InstallCatalogVersion>().ToArray());
    }
}

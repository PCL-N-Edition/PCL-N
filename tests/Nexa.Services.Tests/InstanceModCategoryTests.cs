using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Minecraft.Management;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask ModCategoriesDistinguishProblemsUnknownAndVerifiedUpdates()
    {
        string root = CreateTempDirectory();
        try
        {
            void Jar(string name, string json)
            {
                using var archive = ZipFile.Open(Path.Combine(root, name), ZipArchiveMode.Create);
                using var writer = new StreamWriter(archive.CreateEntry("fabric.mod.json").Open());
                writer.Write(json);
            }
            Jar("good.jar", """{"id":"good","version":"1"}""");
            Jar("disabled.jar.disabled", """{"id":"disabled","version":"1"}""");
            Jar("bad-metadata.jar", "{");
            File.WriteAllText(Path.Combine(root, "broken.jar"), "not an archive");
            File.WriteAllText(Path.Combine(root, "notes.txt"), "not a mod");
            var source = InstanceManagementService.ReadContent(new("mods", "模组", root), default);
            var inspected = await InstanceContentMetadata.EnrichAsync(source, root, new(1024 * 1024), default);
            AssertTrue(inspected.Entries.Single(item => item.Name == "good.jar").PackageReadable == true);
            AssertTrue(inspected.Entries.Single(item => item.Name == "disabled.jar.disabled").Enabled == false);
            foreach (string name in new[] { "broken.jar", "bad-metadata.jar" })
            {
                var entry = inspected.Entries.Single(item => item.Name == name);
                AssertTrue(entry.PackageReadable == false);
                AssertTrue(entry.PackageProblem.Length > 0);
            }
            AssertTrue(inspected.Entries.Single(item => item.Name == "notes.txt").PackageReadable is null);
            var skipped = await InstanceContentMetadata.EnrichAsync(source, root, new(0), default);
            AssertTrue(skipped.Entries.All(item => item.PackageReadable is null && item.PackageProblem.Length == 0));
            string hash = Convert.ToHexString(SHA512.HashData(File.ReadAllBytes(Path.Combine(root, "good.jar")))).ToLowerInvariant();
            using var handler = new ModCategoryHttp(hash);
            using var http = new HttpClient(handler);
            var updated = await InstanceModUpdates.CheckAsync(http, inspected, root, "1.20.1", [new(InstallLoader.Fabric, "0.16")], default);
            AssertTrue(updated.Entries.Single(item => item.Name == "good.jar").UpdateAvailable == true);
            AssertEqual("2", updated.Entries.Single(item => item.Name == "good.jar").UpdateVersion);
            AssertTrue(updated.Entries.Single(item => item.Name == "disabled.jar.disabled").UpdateAvailable is null);
            handler.Mode = "incompatible";
            updated = await InstanceModUpdates.CheckAsync(http, inspected, root, "1.20.1", [new(InstallLoader.Fabric, "0.16")], default);
            AssertTrue(updated.Entries.All(item => item.UpdateAvailable is null));
            handler.Mode = "older";
            updated = await InstanceModUpdates.CheckAsync(http, inspected, root, "1.20.1", [new(InstallLoader.Fabric, "0.16")], default);
            AssertTrue(updated.Entries.Single(item => item.Name == "good.jar").UpdateAvailable == false);
            handler.Mode = "offline";
            updated = await InstanceModUpdates.CheckAsync(http, inspected, root, "1.20.1", [new(InstallLoader.Fabric, "0.16")], default);
            AssertTrue(updated.Error is not null && updated.Entries.All(item => item.UpdateAvailable is null));
            using var stop = new CancellationTokenSource(); stop.Cancel();
            try { await InstanceModUpdates.CheckAsync(http, inspected, root, "1.20.1", [new(InstallLoader.Fabric, "0.16")], stop.Token); throw new InvalidOperationException("Cancellation ignored"); }
            catch (OperationCanceledException) { }
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class ModCategoryHttp(string hash) : HttpMessageHandler
    {
        internal string Mode { get; set; } = "newer";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Mode == "offline") throw new HttpRequestException();
            bool update = request.RequestUri!.AbsolutePath.EndsWith("/update", StringComparison.Ordinal);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            AssertEqual("sha512", body["algorithm"]!.ToString());
            if (update)
            {
                AssertEqual("fabric", body["loaders"]![0]!.ToString());
                AssertEqual("1.20.1", body["game_versions"]![0]!.ToString());
            }
            string game = Mode == "incompatible" ? "1.21" : "1.20.1";
            string date = update && Mode != "older" ? "2026-02-01T00:00:00Z" : "2026-01-01T00:00:00Z";
            var result = new JsonObject
            {
                [hash] = new JsonObject
                {
                    ["id"] = update ? "new" : "old",
                    ["project_id"] = "project",
                    ["version_number"] = "2",
                    ["date_published"] = date,
                    ["game_versions"] = new JsonArray(JsonValue.Create(game)),
                    ["loaders"] = new JsonArray(JsonValue.Create("fabric")),
                    ["files"] = new JsonArray(new JsonObject { ["hashes"] = new JsonObject { ["sha512"] = update ? new string('a', 128) : hash } })
                }
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(result.ToJsonString()) };
        }
    }
}

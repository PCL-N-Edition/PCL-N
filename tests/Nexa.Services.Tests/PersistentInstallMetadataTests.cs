using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Install;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask PersistentAddonDownloadsFreezeMergedSourcesAndRejectInvalidIdentity()
    {
        string root = CreateTempDirectory();
        try
        {
            string stage = await MetadataTaskStage(root);
            var cache = new PersistentInstallMetadataSource(stage, new FakeMetadata());
            var addon = new MinecraftInstallAddon(InstallLoader.FabricApi, "0.100");
            InstallDownload[] downloads = [new("Modrinth", "api.jar", new("https://cdn.modrinth.com/api.jar"), new('A', 40), 100),
                new("CurseForge", "api.jar", new("https://edge.forgecdn.net/api.jar"), new('A', 40), 100)];
            await cache.FetchAddonDownloadsAsync("1.21.1", addon, _ => Task.FromResult<IReadOnlyList<InstallDownload>>(downloads), default);
            downloads[0] = downloads[0] with { Size = 999 };
            var reopened = new PersistentInstallMetadataSource(stage, new FakeMetadata());
            var frozen = await reopened.FetchAddonDownloadsAsync("1.21.1", addon,
                _ => throw new InvalidOperationException("Frozen component re-resolved."), default);
            AssertEqual(2, frozen.Count); AssertEqual(100L, frozen[0].Size); AssertEqual("CurseForge", frozen[1].Source);
            foreach (var invalid in new[] { downloads[0] with { FileName = "../evil.jar" }, downloads[0] with { Url = new("http://example.com/api.jar") },
                downloads[0] with { Sha1 = "bad" }, downloads[0] with { Size = -1 } })
            {
                try
                {
                    await cache.FetchAddonDownloadsAsync("1.21.1", addon with { Version = "invalid" },
                        _ => Task.FromResult<IReadOnlyList<InstallDownload>>([invalid]), default);
                    throw new InvalidOperationException("Invalid component identity persisted.");
                }
                catch (InvalidDataException) { }
            }
            // Failed resolution has no durable entry and may subsequently resolve successfully.
            var retried = await cache.FetchAddonDownloadsAsync("1.21.1", addon with { Version = "invalid" },
                _ => Task.FromResult<IReadOnlyList<InstallDownload>>([downloads[1]]), default);
            AssertEqual("CurseForge", retried.Single().Source);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<string> MetadataTaskStage(string root)
    {
        string stage = Path.Combine(root, ".nexa-modify", Guid.NewGuid().ToString("N"));
        await InstallTaskJournal.CreateAsync(stage, new(root, "1.21.1", InstanceName: "test", EditFingerprint: new('A', 64)) { InheritVanilla = false }, default);
        return stage;
    }

    private static async ValueTask PersistentInstallMetadataFreezesDocumentsAndRejectsCorruption()
    {
        string root = CreateTempDirectory();
        try
        {
            string stage = await MetadataTaskStage(root);
            var remote = new FakeMetadata { VanillaJson = new() { ["id"] = "original" }, LoaderJson = new() { ["id"] = "original-loader" }, AssetIndexJson = new() { ["id"] = "original-assets" } };
            var first = new PersistentInstallMetadataSource(stage, remote);
            (await first.FetchVanillaVersionJsonAsync("1.21.1", default))["id"] = "caller-mutated";
            await first.FetchLoaderProfileJsonAsync(InstallLoader.Fabric, "1.21.1", "0.16.0", default);
            await first.FetchAssetIndexJsonAsync("https://example.com/assets.json", default);
            var changed = new FakeMetadata { VanillaJson = new() { ["id"] = "remote-changed" } };
            var reopened = new PersistentInstallMetadataSource(stage, changed);
            AssertEqual("original", (await reopened.FetchVanillaVersionJsonAsync("1.21.1", default))["id"]!.ToString());
            AssertEqual("original-loader", (await reopened.FetchLoaderProfileJsonAsync(InstallLoader.Fabric, "1.21.1", "0.16.0", default))["id"]!.ToString());
            AssertEqual("original-assets", (await reopened.FetchAssetIndexJsonAsync("https://example.com/assets.json", default))["id"]!.ToString());
            AssertEqual(0, changed.VanillaReads);
            string folder = Path.Combine(stage, InstallTaskJournal.DirectoryName, "metadata");
            string path = Directory.EnumerateFiles(folder, "*.json").Single(file => JsonNode.Parse(File.ReadAllText(file))!["request"]!.ToString().StartsWith("vanilla:", StringComparison.Ordinal));
            string original = File.ReadAllText(path);
            var corrupt = JsonNode.Parse(original)!; corrupt["payload"]!["id"] = "corrupted";
            corrupt["sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(corrupt["payload"]!.AsObject(),
                    Nexa.Services.Minecraft.Management.RecoveryJsonContext.Default.JsonObject)));
            File.WriteAllText(path, corrupt.ToJsonString());
            try { await reopened.FetchVanillaVersionJsonAsync("1.21.1", default); throw new InvalidOperationException("Corrupt cache silently refetched."); } catch (InvalidDataException) { }
            AssertEqual(0, changed.VanillaReads);
            File.WriteAllText(path, original);
            using (var oversized = File.OpenWrite(path)) oversized.SetLength(16 * 1024 * 1024 + 1);
            try { await reopened.FetchVanillaVersionJsonAsync("1.21.1", default); throw new InvalidOperationException("Oversized cache accepted."); } catch (InvalidDataException) { }
            File.Delete(path);
            try { await reopened.FetchVanillaVersionJsonAsync("1.21.1", default); throw new InvalidOperationException("Deleted pinned metadata silently refetched."); } catch (InvalidDataException) { }
            AssertEqual(0, changed.VanillaReads);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class BlockingInstallMetadata : IMinecraftInstallMetadataSource
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Reads;
        public async Task<JsonObject> FetchVanillaVersionJsonAsync(string gameVersion, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Reads); Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken);
            return new() { ["id"] = gameVersion };
        }
        public Task<JsonObject> FetchLoaderProfileJsonAsync(InstallLoader loader, string gameVersion, string build, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<JsonObject> FetchAssetIndexJsonAsync(string indexUrl, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static async ValueTask PersistentInstallMetadataSerializesPublicationAndHonorsCancellation()
    {
        string root = CreateTempDirectory();
        try
        {
            string stage = await MetadataTaskStage(root); var remote = new BlockingInstallMetadata();
            var first = new PersistentInstallMetadataSource(stage, remote); var second = new PersistentInstallMetadataSource(stage, remote);
            Task<JsonObject> pending = first.FetchVanillaVersionJsonAsync("1.21.1", default);
            await remote.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancellation = new CancellationTokenSource();
            Task<JsonObject> waiting = second.FetchVanillaVersionJsonAsync("1.21.1", cancellation.Token);
            cancellation.Cancel();
            try { await waiting; throw new InvalidOperationException("Waiter ignored cancellation."); } catch (OperationCanceledException) { }
            remote.Release.TrySetResult(); await pending;
            await second.FetchVanillaVersionJsonAsync("1.21.1", default);
            AssertEqual(1, remote.Reads);
        }
        finally { Directory.Delete(root, true); }
    }
}

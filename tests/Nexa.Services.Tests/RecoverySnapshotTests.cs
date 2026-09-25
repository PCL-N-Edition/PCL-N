using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Management;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask RecoveryCaptureCommitsOnlyCompleteManifests()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = Path.Combine(root, "versions", "test"); Directory.CreateDirectory(instance);
            var store = new RecoverySnapshotStore(instance, instance);
            AssertTrue(await store.ReadAsync() is null);
            AssertFalse(Directory.Exists(Path.Combine(instance, "Nexa", "Recovery")));
            File.WriteAllText(Path.Combine(instance, "a.txt"), "same-content");
            File.WriteAllText(Path.Combine(instance, "b.txt"), "same-content");
            RecoverySource[] sources = [new("instance", "a.txt"), new("game", "b.txt")];
            var first = await store.CaptureAsync(sources, """{"game.jvm":"-XX:+UseG1GC"}""");
            var loaded = (await store.ReadAsync())!;
            AssertEqual(first.Revision, loaded.Revision);
            AssertEqual(2, loaded.Files.Count);
            AssertEqual(loaded.Files[0].Blob, loaded.Files[1].Blob);
            AssertTrue(loaded.SettingsDocument.Contains("UseG1GC", StringComparison.Ordinal));
            string objects = Path.Combine(instance, "Nexa", "Recovery", "objects");
            AssertEqual(1, Directory.GetFiles(objects, "*.br").Length);
            File.WriteAllText(Path.Combine(instance, "a.txt"), "changed-content");
            try { await store.CaptureAsync([sources[0], new("game", "missing.txt")], "{}"); throw new InvalidOperationException("Incomplete capture committed."); }
            catch (IOException) { }
            AssertEqual(first.Revision, (await store.ReadAsync())!.Revision);
            try
            {
                await store.CaptureAsync(sources, "{}", validatePlan: _ => throw new IOException("Simulated capture plan changed."));
                throw new InvalidOperationException("Changed file plan committed.");
            }
            catch (IOException) { }
            AssertEqual(first.Revision, (await store.ReadAsync())!.Revision);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await store.CaptureAsync(sources, "{}", cancelled.Token); throw new InvalidOperationException("Cancelled capture committed."); }
            catch (OperationCanceledException) { }
            AssertEqual(first.Revision, (await store.ReadAsync())!.Revision);
            foreach (var invalid in new[] { new RecoverySource("game", "../outside"), new("game", "Nexa/Recovery/baseline.json"), new("unknown", "a.txt") })
            {
                try { await store.CaptureAsync([invalid], "{}"); throw new InvalidOperationException("Invalid source accepted."); }
                catch (InvalidDataException) { }
            }
            try { await store.CaptureAsync([sources[0], new("game", "a.txt")], "{}"); throw new InvalidOperationException("Alias accepted."); }
            catch (InvalidDataException) { }
            var second = await store.CaptureAsync(sources, "{}");
            AssertFalse(first.Revision == second.Revision);
            AssertEqual(second.Revision, (await store.ReadAsync())!.Revision);
            string manifest = Path.Combine(instance, "Nexa", "Recovery", "baseline.json");
            var tampered = JsonNode.Parse(File.ReadAllText(manifest))!;
            tampered["files"]![0]!["path"] = "../outside";
            File.WriteAllText(manifest, tampered.ToJsonString());
            try { await store.ReadAsync(); throw new InvalidOperationException("Tampered manifest accepted."); }
            catch (InvalidDataException) { }
            AssertEqual("changed-content", File.ReadAllText(Path.Combine(instance, "a.txt")));
        }
        finally { Directory.Delete(root, true); }
    }
}

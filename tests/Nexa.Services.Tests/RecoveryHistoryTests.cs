using Nexa.Services.Minecraft.Management;
using Nexa.Services.Settings;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask RecoveryHistoryRetainsReferencesAndDefaultsToLatestOnly()
    {
        AssertEqual("false", SettingsPolicySchema.ByKey["recovery.keep-history"].DefaultValue);
        string root = CreateTempDirectory();
        try
        {
            string instance = Path.Combine(root, "versions", "test"); Directory.CreateDirectory(instance);
            string file = Path.Combine(instance, "test.json");
            var snapshots = new RecoverySnapshotStore(instance, instance);
            File.WriteAllText(file, "first");
            RecoverySource[] sources = [new("instance", "test.json")];
            var first = await snapshots.CaptureAsync(sources, "{}");
            File.WriteAllText(file, "second");
            var second = await snapshots.CaptureAsync(sources, "{}", null, retainHistory: true);
            AssertEqual(2, (await snapshots.ListAsync()).Count);
            var blobs = Path.Combine(instance, "Nexa", "Recovery", "objects");
            AssertEqual(2, Directory.GetFiles(blobs, "*.br").Length);
            var third = await snapshots.CaptureAsync(sources, "{}", null, retainHistory: true);
            AssertEqual(3, (await snapshots.ListAsync()).Count);
            AssertEqual(2, Directory.GetFiles(blobs, "*.br").Length);
            var storage = await InstanceRecoveryStorageReader.ReadAsync(instance, instance, default);
            AssertTrue(storage.Complete);
            AssertEqual(6L, storage.VersionBytes);
            AssertEqual(Directory.EnumerateFiles(Path.Combine(instance, "Nexa", "Recovery"), "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length), storage.SnapshotBytes);
            AssertEqual(3, storage.Snapshots.Count);
            AssertTrue((await snapshots.ListAsync()).Any(item => item.Revision == first.Revision));
            // A failed capture cannot delete history or advance the successful baseline.
            try { await snapshots.CaptureAsync([new("instance", "missing")], "{}"); throw new InvalidOperationException("Missing source accepted."); }
            catch (IOException) { }
            AssertEqual(third.Revision, (await snapshots.ReadAsync())!.Revision);
            AssertEqual(3, (await snapshots.ListAsync()).Count);
            var latest = await snapshots.CaptureAsync(sources, "{}");
            AssertEqual(1, (await snapshots.ListAsync()).Count);
            AssertEqual(latest.Revision, (await snapshots.ListAsync())[0].Revision);
            AssertEqual(1, Directory.GetFiles(blobs, "*.br").Length);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask RecoveryHistoryPreservesPreviousIsolationScope()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = Path.Combine(root, "versions", "test"); Directory.CreateDirectory(instance);
            File.WriteAllText(Path.Combine(root, "options.txt"), "shared");
            var shared = new RecoverySnapshotStore(instance, root);
            var old = await shared.CaptureAsync([new("game", "options.txt")], "{}");
            File.WriteAllText(Path.Combine(instance, "options.txt"), "isolated");
            var isolated = new RecoverySnapshotStore(instance, instance);
            await isolated.CaptureAsync([new("game", "options.txt")], "{}", null, retainHistory: true);
            var history = await isolated.ListAsync();
            AssertEqual(2, history.Count);
            AssertEqual(root, history.Single(item => item.Revision == old.Revision).GameDirectory);
            AssertEqual(2, Directory.GetFiles(Path.Combine(instance, "Nexa", "Recovery", "objects"), "*.br").Length);
        }
        finally { Directory.Delete(root, true); }
    }
}

using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Management;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask RecoveryPreparationVerifiesAllObjectsWithoutChangingLiveFiles()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = Path.Combine(root, "versions", "test"); Directory.CreateDirectory(instance);
            string first = Path.Combine(instance, "a.txt"), second = Path.Combine(instance, "b.txt");
            File.WriteAllText(first, "baseline"); File.WriteAllText(second, "baseline");
            var snapshots = new RecoverySnapshotStore(instance, instance);
            var baseline = await snapshots.CaptureAsync([new("instance", "a.txt"), new("instance", "b.txt")], "{\"private\":\"setting\"}");
            File.WriteAllText(first, "current"); File.Delete(second);
            var prepared = await RecoveryRestorePreparation.PrepareAsync(instance, instance, baseline.Revision);
            AssertEqual(baseline.Revision, prepared.Snapshot.Revision);
            AssertEqual("current", File.ReadAllText(first)); AssertFalse(File.Exists(second));
            AssertEqual(1, Directory.GetFiles(prepared.Directory, "*.data").Length);
            AssertEqual("baseline", File.ReadAllText(Directory.GetFiles(prepared.Directory, "*.data").Single()));
            var record = JsonNode.Parse(File.ReadAllText(Path.Combine(prepared.Directory, "prepared.json")))!;
            AssertEqual("prepared", record["phase"]!.GetValue<string>());
            AssertEqual(instance, record["instance"]!.GetValue<string>());
            AssertEqual(prepared.TransactionId.ToString("D"), record["transaction"]!.GetValue<string>());
            AssertEqual(2, record["files"]!.AsArray().Count);
            AssertEqual("setting", record["settings"]!["private"]!.GetValue<string>());
            AssertEqual(baseline.Revision, (await snapshots.ReadAsync())!.Revision);
            var reopened = await RecoveryRestorePreparation.ReadAsync(instance, instance, prepared.TransactionId);
            AssertEqual(baseline.Revision, reopened.Snapshot.Revision);
            AssertEqual(2, reopened.Snapshot.Files.Count);

            // A later successful capture must not garbage-collect objects pinned by preparation.
            await snapshots.CaptureAsync([new("instance", "a.txt")], "{}");
            AssertEqual(2, Directory.GetFiles(Path.Combine(instance, "Nexa", "Recovery", "objects"), "*.br").Length);
            File.WriteAllText(Directory.GetFiles(prepared.Directory, "*.data").Single(), "tampered");
            try
            {
                await RecoveryRestorePreparation.ReadAsync(instance, instance, prepared.TransactionId);
                throw new InvalidOperationException("Tampered staging accepted.");
            }
            catch (InvalidDataException) { }
            AssertEqual("current", File.ReadAllText(first));
            record["files"]![0]!["path"] = "../outside";
            File.WriteAllText(Path.Combine(prepared.Directory, "prepared.json"), record.ToJsonString());
            try
            {
                await RecoveryRestorePreparation.ReadAsync(instance, instance, prepared.TransactionId);
                throw new InvalidOperationException("Invalid recovery scope accepted.");
            }
            catch (InvalidDataException) { }
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask RecoveryPreparationRejectsCorruptAndStaleSnapshots()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = Path.Combine(root, "versions", "test"); Directory.CreateDirectory(instance);
            File.WriteAllText(Path.Combine(instance, "a.txt"), "baseline-a");
            File.WriteAllText(Path.Combine(instance, "b.txt"), "baseline-b");
            var snapshots = new RecoverySnapshotStore(instance, instance);
            var baseline = await snapshots.CaptureAsync([new("instance", "a.txt"), new("instance", "b.txt")], "{}");
            string recovery = Path.Combine(instance, "Nexa", "Recovery");
            try
            {
                await RecoveryRestorePreparation.PrepareAsync(instance, instance, Guid.NewGuid());
                throw new InvalidOperationException("Stale preparation succeeded.");
            }
            catch (InvalidDataException) { }
            AssertFalse(Directory.Exists(Path.Combine(recovery, "transactions")));
            // The first object stages successfully before corruption in the second is discovered.
            File.WriteAllBytes(Path.Combine(recovery, "objects", baseline.Files[1].Blob.Sha256 + ".br"), [0, 1, 2]);
            try
            {
                await RecoveryRestorePreparation.PrepareAsync(instance, instance, baseline.Revision);
                throw new InvalidOperationException("Corrupt preparation succeeded.");
            }
            catch (InvalidDataException) { }
            AssertFalse(Directory.Exists(Path.Combine(recovery, "transactions")));
            AssertEqual(baseline.Revision, (await snapshots.ReadAsync())!.Revision);
            AssertEqual("baseline-a", File.ReadAllText(Path.Combine(instance, "a.txt")));
            AssertEqual("baseline-b", File.ReadAllText(Path.Combine(instance, "b.txt")));
            using var stop = new CancellationTokenSource(); stop.Cancel();
            try
            {
                await RecoveryRestorePreparation.PrepareAsync(instance, instance, baseline.Revision, stop.Token);
                throw new InvalidOperationException("Cancelled preparation succeeded.");
            }
            catch (OperationCanceledException) { }
            AssertFalse(Directory.Exists(Path.Combine(recovery, "transactions")));
        }
        finally { Directory.Delete(root, true); }
    }
}

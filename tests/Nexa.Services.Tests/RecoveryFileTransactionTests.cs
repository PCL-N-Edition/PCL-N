using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Management;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static RecoveryBlob RecoveryTextBlob(string text) => new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))), Encoding.UTF8.GetByteCount(text));

    private static async ValueTask RecoveryFileTransactionRestoresOnlySelectedFilesAndReverses()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = Path.Combine(root, "versions", "test"); Directory.CreateDirectory(Path.Combine(instance, "mods"));
            string options = Path.Combine(instance, "options.txt"), mod = Path.Combine(instance, "mods", "old.jar"), added = Path.Combine(instance, "mods", "new.jar");
            File.WriteAllText(options, "baseline"); File.WriteAllText(mod, "old-mod");
            var store = new RecoverySnapshotStore(instance, instance);
            var baseline = await store.CaptureAsync([new("instance", "options.txt"), new("instance", "mods/old.jar")], "{}");
            var prepared = await RecoveryRestorePreparation.PrepareAsync(instance, instance, baseline.Revision);
            File.WriteAllText(options, "current"); File.Delete(mod); File.WriteAllText(added, "new-mod");
            Directory.CreateDirectory(Path.Combine(instance, "saves")); File.WriteAllText(Path.Combine(instance, "saves", "world.dat"), "world");
            RecoveryFileEdit[] edits = [new(new("instance", "options.txt"), RecoveryTextBlob("current"), RecoveryTextBlob("baseline")),
                new(new("instance", "mods/old.jar"), null, RecoveryTextBlob("old-mod")),
                new(new("instance", "mods/new.jar"), RecoveryTextBlob("new-mod"), null)];
            await RecoveryFileTransaction.ApplyAsync(prepared, edits);
            AssertEqual("baseline", File.ReadAllText(options)); AssertEqual("old-mod", File.ReadAllText(mod)); AssertFalse(File.Exists(added));
            AssertEqual("world", File.ReadAllText(Path.Combine(instance, "saves", "world.dat")));
            AssertEqual("files-applied", JsonNode.Parse(File.ReadAllText(Path.Combine(prepared.Directory, "apply.json")))!["phase"]!.GetValue<string>());
            AssertTrue(new FileInfo(Path.Combine(prepared.Directory, "apply.json")).Length < 1024);
            string planPath = Path.Combine(prepared.Directory, "file-plan.json");
            string filePlan = File.ReadAllText(planPath);
            // Reopen the durable preparation, simulating a new process handling unfinished files.
            var reopened = await RecoveryRestorePreparation.ReadAsync(instance, instance, prepared.TransactionId);
            File.WriteAllText(planPath, filePlan + " ");
            try { await RecoveryFileTransaction.RollbackAsync(reopened); throw new InvalidOperationException("Changed plan accepted."); }
            catch (InvalidDataException) { }
            AssertEqual("baseline", File.ReadAllText(options));
            File.WriteAllText(planPath, filePlan);
            await RecoveryFileTransaction.RollbackAsync(reopened);
            AssertEqual(filePlan, File.ReadAllText(planPath));
            await RecoveryFileTransaction.RollbackAsync(reopened);
            AssertEqual("current", File.ReadAllText(options)); AssertFalse(File.Exists(mod)); AssertEqual("new-mod", File.ReadAllText(added));
            var selected = await RecoveryRestorePreparation.PrepareAsync(instance, instance, baseline.Revision);
            await RecoveryFileTransaction.ApplyAsync(selected, [edits[0]]);
            AssertEqual("baseline", File.ReadAllText(options)); AssertFalse(File.Exists(mod)); AssertEqual("new-mod", File.ReadAllText(added));
            await RecoveryFileTransaction.RollbackAsync(selected);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask RecoveryFileTransactionRevertsPartialFailureAndPreservesConflicts()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = Path.Combine(root, "versions", "test"); Directory.CreateDirectory(instance);
            string a = Path.Combine(instance, "options.txt"), b = Path.Combine(instance, "optionsof.txt");
            File.WriteAllText(a, "old-a"); File.WriteAllText(b, "old-b");
            var store = new RecoverySnapshotStore(instance, instance);
            var baseline = await store.CaptureAsync([new("instance", "options.txt"), new("instance", "optionsof.txt")], "{}");
            var prepared = await RecoveryRestorePreparation.PrepareAsync(instance, instance, baseline.Revision);
            File.WriteAllText(a, "new-a"); File.WriteAllText(b, "new-b");
            RecoveryFileEdit[] edits = [new(new("instance", "options.txt"), RecoveryTextBlob("new-a"), RecoveryTextBlob("old-a")),
                new(new("instance", "optionsof.txt"), RecoveryTextBlob("new-b"), RecoveryTextBlob("old-b"))];
            File.WriteAllText(Path.Combine(prepared.Directory, RecoveryTextBlob("old-b").Sha256 + ".data"), "bad");
            try { await RecoveryFileTransaction.ApplyAsync(prepared, edits); throw new InvalidOperationException("Corrupt staged content accepted."); }
            catch (InvalidDataException) { }
            AssertEqual("new-a", File.ReadAllText(a)); AssertEqual("new-b", File.ReadAllText(b));
            AssertEqual("rolled-back", JsonNode.Parse(File.ReadAllText(Path.Combine(prepared.Directory, "apply.json")))!["phase"]!.GetValue<string>());
            var next = await RecoveryRestorePreparation.PrepareAsync(instance, instance, baseline.Revision);
            await RecoveryFileTransaction.ApplyAsync(next, edits);
            File.WriteAllText(a, "external-edit");
            try { await RecoveryFileTransaction.RollbackAsync(next); throw new InvalidOperationException("External edit overwritten."); }
            catch (IOException) { }
            AssertEqual("external-edit", File.ReadAllText(a)); AssertEqual("new-b", File.ReadAllText(b));
            AssertTrue(File.Exists(Path.Combine(next.Directory, RecoveryTextBlob("new-a").Sha256 + ".before")));
            File.WriteAllText(a, "old-a");
            await RecoveryFileTransaction.RollbackAsync(next);
            AssertEqual("new-a", File.ReadAllText(a));
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask RecoveryFileTransactionRejectsStaleAndOutOfScopeEdits()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = Path.Combine(root, "versions", "test"); Directory.CreateDirectory(instance);
            File.WriteAllText(Path.Combine(instance, "options.txt"), "baseline");
            var store = new RecoverySnapshotStore(instance, instance);
            var baseline = await store.CaptureAsync([new("instance", "options.txt")], "{}");
            var prepared = await RecoveryRestorePreparation.PrepareAsync(instance, instance, baseline.Revision);
            try { await RecoveryFileTransaction.ApplyAsync(prepared, [new(new("instance", "options.txt"), RecoveryTextBlob("stale"), RecoveryTextBlob("baseline"))]); throw new InvalidOperationException("Stale edit accepted."); }
            catch (IOException) { }
            AssertFalse(File.Exists(Path.Combine(prepared.Directory, "apply.json")));
            foreach (var source in new[] { new RecoverySource("instance", "saves/world.dat"), new("root", "versions/other/other.jar"), new("instance", "../outside") })
            {
                try { await RecoveryFileTransaction.ApplyAsync(prepared, [new(source, RecoveryTextBlob("existing"), null)]); throw new InvalidOperationException("Out-of-scope edit accepted."); }
                catch (InvalidDataException) { }
            }
            AssertEqual("baseline", File.ReadAllText(Path.Combine(instance, "options.txt")));
        }
        finally { Directory.Delete(root, true); }
    }
}

using Nexa.Services.Minecraft.Install;
using Nexa.Services.Tasks;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask InstallRecoveryDiscoveryIsolatesInvalidTasksAndHonorsDisposition()
    {
        string root = CreateTempDirectory();
        try
        {
            string ready = await MetadataTaskStage(root);
            Directory.CreateDirectory(Path.Combine(ready, "versions", "test"));
            File.WriteAllText(Path.Combine(ready, "versions", "test", "test.json"), "restored");
            await InstallPublicationJournal.PrepareAsync(root, ready, "test", ["versions/test/test.json"], new Dictionary<string, string>(), default);
            string rollback = await MetadataTaskStage(root);
            var rollbackPlan = await InstallTaskJournal.ReadAsync(root, rollback, default);
            await InstallTaskJournal.WriteStatusAsync(rollback, rollbackPlan, InstallTaskStatus.RollbackRequested, default);
            string corrupt = Path.Combine(root, ".nexa-modify", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(corrupt);
            string unknown = Path.Combine(root, ".nexa-modify", "keep-unknown"); Directory.CreateDirectory(unknown);
            var metadata = new FakeMetadata(); using var fixture = new InstallFixture(metadata);
            AssertFalse((await fixture.Install.RecoverPendingAsync(new([root, root]))).IsSuccess);
            AssertEqual("restored", File.ReadAllText(Path.Combine(root, "versions", "test", "test.json")));
            AssertEqual(InstallTaskStatus.Completed, await InstallTaskJournal.ReadStatusAsync(ready, await InstallTaskJournal.ReadAsync(root, ready, default), default));
            AssertEqual(InstallTaskStatus.RolledBack, await InstallTaskJournal.ReadStatusAsync(rollback, rollbackPlan, default));
            AssertTrue(Directory.Exists(corrupt)); AssertTrue(Directory.Exists(unknown));
            AssertEqual(0, metadata.VanillaReads);
            // An already completed task is not re-executed, even if its original scratch artifacts are gone.
            File.Delete(Path.Combine(ready, "versions", "test", "test.json"));
            AssertFalse((await fixture.Install.RecoverPendingAsync(new([root]))).IsSuccess);
            AssertEqual(0, metadata.VanillaReads);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { await fixture.Install.RecoverPendingAsync(new([root]), canceled.Token); throw new InvalidOperationException("Discovery ignored cancellation."); } catch (OperationCanceledException) { }
            AssertFalse((await fixture.Install.RecoverPendingAsync(new(Enumerable.Repeat(root, 65).ToArray()))).IsSuccess);
        }
        finally { Directory.Delete(root, true); }
    }
}

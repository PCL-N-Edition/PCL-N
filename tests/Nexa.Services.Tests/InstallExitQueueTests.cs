using Nexa.Services.Minecraft.Install;
using Nexa.Services.Tasks;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask InstallExitRejectsCorruptQueueAndAllowsRollbackRetry()
    {
        string root = CreateTempDirectory();
        try
        {
            string corrupt = Path.Combine(root, ".nexa-install-jobs", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(corrupt);
            string unknown = Path.Combine(root, ".nexa-install-jobs", "user-directory");
            Directory.CreateDirectory(unknown);
            using var fixture = new InstallFixture(new());
            AssertFalse((await fixture.Install.RecoverPendingAsync(new([root]))).IsSuccess);
            AssertFalse((await fixture.Install.StopAsync(new(false))).IsSuccess);
            AssertTrue(Directory.Exists(corrupt));
            AssertFalse((await fixture.Install.InstallAsync(new(root, "1.20.1"))).IsSuccess);
            Directory.Delete(corrupt, true);
            AssertTrue((await fixture.Install.StopAsync(new(false))).IsSuccess);
            AssertTrue(Directory.Exists(unknown));
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask InstallExitAppliesToQueuedRootsBeforeTheyStart()
    {
        foreach (bool pause in new[] { true, false })
        {
            string parent = CreateTempDirectory();
            string firstRoot = Path.Combine(parent, "first"), queuedRoot = Path.Combine(parent, "queued");
            Directory.CreateDirectory(firstRoot); Directory.CreateDirectory(queuedRoot);
            try
            {
                List<(string Root, string Stage)> plans = [];
                foreach (string root in new[] { firstRoot, queuedRoot })
                {
                    string stage = Path.Combine(root, ".nexa-install-jobs", Guid.NewGuid().ToString("N"));
                    await InstallTaskJournal.CreateAsync(stage, new(root, "1.20.1", InstanceName: "queued") { InheritVanilla = false }, default);
                    plans.Add((root, stage));
                }
                TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                using var fixture = new InstallFixture(new() { VanillaJson = VanillaJson(), AssetIndexJson = AssetIndexJson() }, connectionFactory: _ => new ExitBlockingConnection(entered));
                var recovery = fixture.Install.RecoverPendingAsync(new([firstRoot, queuedRoot]));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                AssertTrue(fixture.Store.ReadCollection<TaskCenterEntry>(fixture.Store.Resolve(TaskCenterStateContract.EntriesKey)).Items
                    .Any(item => item.TaskId.StartsWith("install-recovery:queue:", StringComparison.Ordinal) && !item.IsTerminal));
                AssertTrue((await fixture.Install.StopAsync(new(pause)).WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
                try { await recovery; } catch (OperationCanceledException) { }
                foreach (var (root, stage) in plans)
                {
                    var plan = await InstallTaskJournal.ReadAsync(root, stage, default);
                    AssertEqual(pause ? InstallTaskStatus.Pending : InstallTaskStatus.RolledBack, await InstallTaskJournal.ReadStatusAsync(stage, plan, default));
                    AssertFalse(File.Exists(Path.Combine(root, "versions", "queued", "queued.json")));
                }
                if (!pause)
                {
                    var metadata = new FakeMetadata(); using var next = new InstallFixture(metadata);
                    AssertTrue((await next.Install.RecoverPendingAsync(new([firstRoot, queuedRoot]))).IsSuccess);
                    AssertEqual(0, metadata.VanillaReads);
                }
            }
            finally { Directory.Delete(parent, true); }
        }
    }
}

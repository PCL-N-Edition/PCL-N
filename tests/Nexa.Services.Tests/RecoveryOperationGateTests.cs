using Nexa.Services.Minecraft.Management;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask RecoveryRestoreExcludesOperationsWithoutDeadlockingNestedWork()
    {
        string root = Path.GetFullPath("recovery-exclusive"), other = Path.GetFullPath("recovery-other");
        using var operation = await InstanceRecoveryOperationGate.EnterOperationAsync(root);
        var restore = InstanceRecoveryOperationGate.EnterRestoreAsync(root).AsTask();
        AssertFalse(restore.IsCompleted);
        // An installation may enter rename while its outer operation is still active.
        using var nested = await InstanceRecoveryOperationGate.EnterOperationAsync(root).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        operation.Dispose(); AssertFalse(restore.IsCompleted);
        nested.Dispose();
        using var exclusive = await restore.WaitAsync(TimeSpan.FromSeconds(5));
        AssertTrue(InstanceRecoveryOperationGate.TryCapture(root) is null);
        using var independent = await InstanceRecoveryOperationGate.EnterOperationAsync(other);
        var pending = InstanceRecoveryOperationGate.EnterOperationAsync(root).AsTask();
        var second = InstanceRecoveryOperationGate.EnterRestoreAsync(root).AsTask();
        AssertFalse(pending.IsCompleted); AssertFalse(second.IsCompleted);
        AssertFalse(exclusive.Token.IsCancellationRequested);
        exclusive.Dispose();
        using var next = await second.WaitAsync(TimeSpan.FromSeconds(5));
        AssertFalse(pending.IsCompleted);
        next.Dispose();
        using var last = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        last.Dispose();
        using var capture = InstanceRecoveryOperationGate.TryCapture(root);
        AssertTrue(capture is not null);
    }

    private static async ValueTask RecoveryRestoreCancellationReleasesWaitersAndYieldsCapture()
    {
        string root = Path.GetFullPath("recovery-exclusive-cancel");
        using var capture = InstanceRecoveryOperationGate.TryCapture(root)!;
        using var cancellation = new CancellationTokenSource();
        var restore = InstanceRecoveryOperationGate.EnterRestoreAsync(root, cancellation.Token).AsTask();
        AssertTrue(capture.Token.IsCancellationRequested); AssertFalse(restore.IsCompleted);
        cancellation.Cancel();
        try { await restore; throw new InvalidOperationException("Cancelled restore acquired lease."); }
        catch (OperationCanceledException) { }
        capture.Dispose();
        using var active = await InstanceRecoveryOperationGate.EnterRestoreAsync(root);
        using var cancelOperation = new CancellationTokenSource();
        var operation = InstanceRecoveryOperationGate.EnterOperationAsync(root, cancelOperation.Token).AsTask();
        cancelOperation.Cancel();
        try { await operation; throw new InvalidOperationException("Cancelled operation acquired lease."); }
        catch (OperationCanceledException) { }
        AssertFalse(active.Token.IsCancellationRequested);
        active.Dispose();
        using var next = InstanceRecoveryOperationGate.TryCapture(root);
        AssertTrue(next is not null);
    }

    private static async ValueTask InstallWaitsForExclusiveRecoveryBeforeWriting()
    {
        string root = CreateTempDirectory();
        try
        {
            using var fixture = new InstallFixture(new() { VanillaJson = VanillaJson(), AssetIndexJson = AssetIndexJson() });
            using var restore = await InstanceRecoveryOperationGate.EnterRestoreAsync(root);
            var installing = fixture.Install.InstallAsync(new(root, "1.20.1"));
            AssertFalse(installing.IsCompleted);
            AssertFalse(Directory.Exists(Path.Combine(root, "versions")));
            AssertFalse(restore.Token.IsCancellationRequested);
            restore.Dispose();
            AssertTrue((await installing).IsSuccess);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask RecoveryCaptureYieldsToOperationsWithoutSerializingThem()
    {
        string root = Path.GetFullPath("recovery-gate-a"), other = Path.GetFullPath("recovery-gate-b");
        using var capture = InstanceRecoveryOperationGate.TryCapture(root)!;
        AssertTrue(InstanceRecoveryOperationGate.TryCapture(root) is null);
        using var independent = InstanceRecoveryOperationGate.TryCapture(other);
        AssertTrue(independent is not null);
        var first = InstanceRecoveryOperationGate.EnterOperationAsync(root).AsTask();
        var second = InstanceRecoveryOperationGate.EnterOperationAsync(root).AsTask();
        AssertTrue(capture!.Token.IsCancellationRequested);
        AssertFalse(first.IsCompleted); AssertFalse(second.IsCompleted);
        capture.Dispose();
        using var one = await first.WaitAsync(TimeSpan.FromSeconds(5));
        using var two = await second.WaitAsync(TimeSpan.FromSeconds(5));
        AssertTrue(InstanceRecoveryOperationGate.TryCapture(root) is null);
        one.Dispose();
        AssertTrue(InstanceRecoveryOperationGate.TryCapture(root) is null);
        two.Dispose();
        using var next = InstanceRecoveryOperationGate.TryCapture(root);
        AssertTrue(next is not null);
    }

    private static async ValueTask RecoveryGateCancellationDoesNotLeakReservations()
    {
        string root = Path.GetFullPath("recovery-gate-cancel");
        using var capture = InstanceRecoveryOperationGate.TryCapture(root)!;
        using var cancellation = new CancellationTokenSource();
        var waiting = InstanceRecoveryOperationGate.EnterOperationAsync(root, cancellation.Token).AsTask();
        cancellation.Cancel();
        try { await waiting; throw new InvalidOperationException("Cancelled operation acquired capture lock."); }
        catch (OperationCanceledException) { }
        capture.Dispose();
        using var next = InstanceRecoveryOperationGate.TryCapture(root);
        AssertTrue(next is not null);
    }

    private static async ValueTask InstallCancelsSnapshotBeforeWritingFiles()
    {
        string root = CreateTempDirectory();
        try
        {
            using var fixture = new InstallFixture(new() { VanillaJson = VanillaJson(), AssetIndexJson = AssetIndexJson() });
            using var capture = InstanceRecoveryOperationGate.TryCapture(root)!;
            var installing = fixture.Install.InstallAsync(new(root, "1.20.1"));
            AssertTrue(capture.Token.IsCancellationRequested);
            AssertFalse(installing.IsCompleted);
            AssertFalse(Directory.Exists(Path.Combine(root, "versions")));
            capture.Dispose();
            AssertTrue((await installing).IsSuccess);
            using var next = InstanceRecoveryOperationGate.TryCapture(root);
            AssertTrue(next is not null);
        }
        finally { Directory.Delete(root, true); }
    }
}

using Nexa.Services.Minecraft.Management;

namespace Nexa.Services.Tests;

internal static partial class Program
{
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

using Nexa.Services.Minecraft.Management;
using Nexa.Services.Minecraft.Process;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static void RecoveryBaselineRequiresConfirmedSuccess()
    {
        var normal = new MinecraftProcessSnapshot(Guid.NewGuid(), "instance", 1, MinecraftProcessState.Exited,
            0, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow)
        { GameWindowConfirmed = true };
        AssertTrue(InstanceRecoveryEligibility.CanCapture(normal, false));
        AssertFalse(InstanceRecoveryEligibility.CanCapture(normal with { GameWindowConfirmed = false }, false));
        AssertFalse(InstanceRecoveryEligibility.CanCapture(normal, true));
        AssertFalse(InstanceRecoveryEligibility.CanCapture(normal with { State = MinecraftProcessState.Cancelled }, false));
        AssertFalse(InstanceRecoveryEligibility.CanCapture(normal with { State = MinecraftProcessState.Failed, ExitCode = 1 }, false));
        AssertFalse(InstanceRecoveryEligibility.CanCapture(normal with { State = MinecraftProcessState.Running, EndedAt = null }, false));
        AssertFalse(InstanceRecoveryEligibility.CanCapture(normal with { ExitCode = null }, false));
    }
}

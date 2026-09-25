using Nexa.Services.Minecraft.Process;

namespace Nexa.Services.Minecraft.Management;

internal static class InstanceRecoveryEligibility
{
    internal static bool CanCapture(MinecraftProcessSnapshot session, bool hasCrashEvidence) =>
        session.GameWindowConfirmed && session.State == MinecraftProcessState.Exited && session.ExitCode == 0
        && session.EndedAt is not null && !hasCrashEvidence;
}

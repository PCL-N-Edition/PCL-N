using Nexa.Services.Logging;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Process;
using Nexa.Services.Settings;
using Nexa.Xsr.State;

namespace Nexa.Services.Minecraft.Management;

/// <summary>Owns best-effort background capture after a confirmed successful game session.</summary>
public sealed class InstanceRecoveryService(SettingsPolicyService settings, XsrStateStore store, LogService? log = null)
{
    internal Task<bool> RecordSuccessfulExitAsync(MinecraftLaunchPlan plan, MinecraftProcessSnapshot session,
        bool hasCrashEvidence, CancellationToken token = default) => Task.Run(async () =>
    {
        if (!InstanceRecoveryEligibility.CanCapture(session, hasCrashEvidence)
            || plan.MinecraftRootDirectory is not { } root
            || !MinecraftLibraryService.PathComparer.Equals(session.InstanceDirectory, plan.InstanceDirectory)
            || !MinecraftLibraryService.PathComparer.Equals(session.GameDirectory, plan.GameDirectory)) return false;
        try
        {
            using var capture = InstanceRecoveryOperationGate.TryCapture(root);
            if (capture is null) return false;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, capture.Token);
            cancellation.CancelAfter(TimeSpan.FromMinutes(3));
            var stop = cancellation.Token;
            string instance = Path.GetFullPath(plan.InstanceDirectory), game = Path.GetFullPath(plan.GameDirectory);
            string manifest = Path.Combine(instance, Path.GetFileName(instance) + ".json");
            string baselineSettings = settings.CaptureRecoverySettings(instance);
            async Task Validate(CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                if (store.TryResolve(MinecraftProcessStateComposition.SessionsKey, out var id)
                    && store.ReadCollection<MinecraftProcessSnapshot>(id, cancellationToken: ct).Items.Any(item =>
                        item.State is MinecraftProcessState.Created or MinecraftProcessState.Running
                        && (MinecraftLibraryService.PathComparer.Equals(item.InstanceDirectory, instance)
                            || MinecraftLibraryService.PathComparer.Equals(item.GameDirectory, game))))
                    throw new IOException("游戏仍在使用恢复范围。");
                var metadata = await new MinecraftInstanceMetadataStore().LoadAsync(instance, ct).ConfigureAwait(false);
                string expectedGame = metadata.InstanceIsolation ? instance : Path.GetFullPath(root);
                if (!MinecraftLibraryService.PathComparer.Equals(expectedGame, game)
                    || settings.CaptureRecoverySettings(instance) != baselineSettings)
                    throw new IOException("采集期间版本设置发生变化。");
            }
            await Validate(stop).ConfigureAwait(false);
            // Verify that the artifact actually used by this launch is included, not just a guessed filename.
            var sources = await RecoveryCapturePlan.BuildAsync(root, instance, game, manifest, stop).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(plan.ClientJarPath) || !sources.Any(source =>
                MinecraftLibraryService.PathComparer.Equals(Path.GetFullPath(Path.Combine(
                    source.Area switch { "instance" => instance, "game" => game, _ => root }, source.RelativePath)), Path.GetFullPath(plan.ClientJarPath))))
                throw new InvalidDataException("恢复范围缺少本次启动使用的核心文件。");
            await new RecoverySnapshotStore(instance, game).CaptureAsync(sources, baselineSettings, async ct =>
            {
                var current = await RecoveryCapturePlan.BuildAsync(root, instance, game, manifest, ct).ConfigureAwait(false);
                if (!sources.SequenceEqual(current)) throw new IOException("采集期间恢复范围发生变化。");
                await Validate(ct).ConfigureAwait(false);
            }, stop).ConfigureAwait(false);
            log?.Info("Recovery", "已保存本次正常退出的恢复基线。");
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            log?.Warn("Recovery", $"恢复快照未完成（{error.GetType().Name}），已保留旧基线。");
            return false;
        }
    }, token);
}

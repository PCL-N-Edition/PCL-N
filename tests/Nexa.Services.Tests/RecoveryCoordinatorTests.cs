using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Settings;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask RecoveryCoordinatorCommitsAndCompensatesSettings()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = Path.Combine(root, "versions", "test"); Directory.CreateDirectory(instance);
            string file = Path.Combine(instance, "options.txt"); File.WriteAllText(file, "baseline");
            var (_, settings) = PolicyFixture();
            string settingsBaseline = settings.CaptureRecoverySettings(instance);
            var snapshot = await new RecoverySnapshotStore(instance, instance).CaptureAsync([new("instance", "options.txt")], settingsBaseline);
            File.WriteAllText(file, "current");
            AssertTrue(settings.Set(new("game.arguments", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "current"), instance)).IsSuccess);
            var plan = settings.PlanRecoverySettings(instance, settingsBaseline, ["game.arguments"]);
            var decoded = SettingsPolicyService.DecodeRecoverySettingsPlan(SettingsPolicyService.EncodeRecoverySettingsPlan(plan));
            AssertEqual(plan.Before.Single(), decoded.Before.Single()); AssertEqual(plan.After.Single(), decoded.After.Single());
            var prepared = await RecoveryRestorePreparation.PrepareAsync(instance, instance, snapshot.Revision);
            RecoveryFileEdit[] edits = [new(new("instance", "options.txt"), RecoveryTextBlob("current"), RecoveryTextBlob("baseline"))];
            int validations = 0;
            // A settings revision changes after the file phase: compensate files, preserve unrelated settings.
            try
            {
                await RecoveryTransactionCoordinator.ExecuteAsync(prepared, edits, plan, settings, _ =>
                {
                    if (++validations == 2) AssertTrue(settings.Set(new("game.width", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "1024"))).IsSuccess);
                    return Task.CompletedTask;
                });
                throw new InvalidOperationException("Stale settings committed.");
            }
            catch (IOException) { }
            AssertEqual("current", File.ReadAllText(file));
            AssertEqual("current", Effective(settings, "game.arguments", instance).Value.Value);
            AssertEqual("rolled-back", JsonNode.Parse(File.ReadAllText(Path.Combine(prepared.Directory, "transaction.json")))!["phase"]!.GetValue<string>());
            prepared = await RecoveryRestorePreparation.PrepareAsync(instance, instance, snapshot.Revision);
            plan = settings.PlanRecoverySettings(instance, settingsBaseline, ["game.arguments"]);
            await RecoveryTransactionCoordinator.ExecuteAsync(prepared, edits, plan, settings, _ => Task.CompletedTask);
            AssertEqual("baseline", File.ReadAllText(file));
            AssertFalse(settings.PreviewRecoverySettings(instance, settingsBaseline).Changes.Any(item => item.Key == "game.arguments"));
            await RecoveryTransactionCoordinator.RecoverAsync(prepared, settings, _ => Task.CompletedTask);
            AssertEqual("baseline", File.ReadAllText(file));
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask RecoveryCoordinatorReopensInterruptedSettingsCommit()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = Path.Combine(root, "versions", "test"); Directory.CreateDirectory(instance);
            string file = Path.Combine(instance, "options.txt"); File.WriteAllText(file, "baseline");
            var (_, settings) = PolicyFixture();
            string baseline = settings.CaptureRecoverySettings(instance);
            var snapshot = await new RecoverySnapshotStore(instance, instance).CaptureAsync([new("instance", "options.txt")], baseline);
            File.WriteAllText(file, "current");
            AssertTrue(settings.Set(new("game.arguments", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "current"), instance)).IsSuccess);
            var plan = settings.PlanRecoverySettings(instance, baseline, ["game.arguments"]);
            var prepared = await RecoveryRestorePreparation.PrepareAsync(instance, instance, snapshot.Revision);
            await RecoveryTransactionCoordinator.PrepareAsync(prepared, plan);
            await RecoveryFileTransaction.ApplyAsync(prepared, [new(new("instance", "options.txt"), RecoveryTextBlob("current"), RecoveryTextBlob("baseline"))]);
            string markerPath = Path.Combine(prepared.Directory, "transaction.json");
            var marker = JsonNode.Parse(File.ReadAllText(markerPath))!.AsObject();
            marker["phase"] = "settings-applying"; marker["settingsAttempted"] = true;
            File.WriteAllText(markerPath, marker.ToJsonString());
            AssertTrue(settings.ApplyRecoverySettingsPlan(plan, false).IsSuccess);
            var service = new InstanceRecoveryService(settings, new Nexa.Xsr.State.XsrStateStoreBuilder().Build());
            AssertTrue((await service.RecoverAsync(new([root]))).IsSuccess);
            AssertTrue((await service.RecoverAsync(new([root]))).IsSuccess);
            AssertFalse(Directory.Exists(prepared.Directory));
            AssertEqual("current", File.ReadAllText(file));
            AssertEqual("current", Effective(settings, "game.arguments", instance).Value.Value);
        }
        finally { Directory.Delete(root, true); }
    }
}

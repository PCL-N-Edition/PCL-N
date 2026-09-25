using System.Text.Json.Nodes;
using Nexa.Services.Settings;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static void RecoverySettingPlansRestoreOnlySelectedOverridesAndCompensate()
    {
        var (_, service) = PolicyFixture();
        string instance = Path.GetFullPath("recovery-selected-settings");
        AssertTrue(service.Set(new("game.jvm", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "old-global"))).IsSuccess);
        string baseline = service.CaptureRecoverySettings(instance);
        AssertTrue(service.Set(new("game.jvm", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "new-global"))).IsSuccess);
        AssertTrue(service.Set(new("game.arguments", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "keep-current"), instance)).IsSuccess);
        var plan = service.PlanRecoverySettings(instance, baseline, ["game.jvm"]);
        AssertEqual(1, plan.After.Count);
        AssertEqual(SettingsOverrideMode.Inherit, plan.Before[0].Value.Mode);
        AssertTrue(service.ApplyRecoverySettingsPlan(plan, reverse: false).IsSuccess);
        AssertEqual("old-global", Effective(service, "game.jvm", instance).Value.Value);
        AssertEqual("keep-current", Effective(service, "game.arguments", instance).Value.Value);
        AssertEqual("new-global", Effective(service, "game.jvm").Value.Value);
        AssertTrue(service.Set(new("game.jvm", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "later-global"))).IsSuccess);
        AssertTrue(service.ApplyRecoverySettingsPlan(plan, reverse: true).IsSuccess);
        AssertTrue(service.ApplyRecoverySettingsPlan(plan, reverse: true).IsSuccess);
        AssertEqual("later-global", Effective(service, "game.jvm", instance).Value.Value);
        AssertEqual(SettingsLayer.Global, Effective(service, "game.jvm", instance).Source);
        AssertEqual("keep-current", Effective(service, "game.arguments", instance).Value.Value);
    }

    private static void RecoverySettingPlansRejectStaleAndConflictingState()
    {
        var (_, service) = PolicyFixture();
        string instance = Path.GetFullPath("recovery-conflicting-settings");
        string baseline = service.CaptureRecoverySettings(instance);
        AssertTrue(service.Set(new("game.arguments", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "before"), instance)).IsSuccess);
        var stale = service.PlanRecoverySettings(instance, baseline, ["game.arguments"]);
        AssertTrue(service.Set(new("game.width", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "1024"))).IsSuccess);
        AssertFalse(service.ApplyRecoverySettingsPlan(stale, reverse: false).IsSuccess);
        AssertEqual("before", Effective(service, "game.arguments", instance).Value.Value);
        var plan = service.PlanRecoverySettings(instance, baseline, ["game.arguments"]);
        AssertTrue(service.ApplyRecoverySettingsPlan(plan, reverse: false).IsSuccess);
        AssertTrue(service.Set(new("game.arguments", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "external-change"), instance)).IsSuccess);
        AssertFalse(service.ApplyRecoverySettingsPlan(plan, reverse: true).IsSuccess);
        AssertEqual("external-change", Effective(service, "game.arguments", instance).Value.Value);
        foreach (var keys in new[] { new[] { "recovery.keep-history" }, ["updates.channel"], ["game.jvm", "game.jvm"] })
        {
            try { service.PlanRecoverySettings(instance, baseline, keys); throw new InvalidOperationException("Invalid recovery selection accepted."); }
            catch (InvalidDataException) { }
        }
        var other = plan with { InstanceDirectory = Path.GetFullPath("other-instance") };
        AssertFalse(service.ApplyRecoverySettingsPlan(other, reverse: true).IsSuccess);
    }

    private static void RecoverySettingsPreservePrivateArgumentsAndInheritance()
    {
        var (_, service) = PolicyFixture();
        string instance = Path.GetFullPath("recovery-settings-instance");
        string other = Path.GetFullPath("recovery-settings-other");
        AssertTrue(service.Set(new("game.jvm", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "-XX:+UseG1GC"))).IsSuccess);
        AssertTrue(service.Set(new("game.arguments", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "--demo"), instance)).IsSuccess);
        AssertTrue(service.Set(new("game.arguments", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "--other-instance"), other)).IsSuccess);
        string baseline = service.CaptureRecoverySettings(instance);
        var values = JsonNode.Parse(baseline)!["values"]!.AsObject();
        AssertEqual("-XX:+UseG1GC", values["game.jvm"]!["effective"]!["value"]!.GetValue<string>());
        AssertTrue(values["game.jvm"]!["override"] is null);
        AssertEqual("--demo", values["game.arguments"]!["override"]!["value"]!.GetValue<string>());
        AssertFalse(baseline.Contains("--other-instance", StringComparison.Ordinal));
        AssertEqual(SettingsPolicySchema.Definitions.Count(item => item.InstanceOverride && !item.Key.StartsWith("recovery.", StringComparison.Ordinal)), values.Count);
        AssertFalse(JsonNode.Parse(service.Export(new(instance)).Value!)!["values"]!.AsObject().ContainsKey("game.arguments"));
        AssertEqual(0, service.PreviewRecoverySettings(instance, baseline).Changes.Count);

        AssertTrue(service.Set(new("game.jvm", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "-XX:+UseZGC"))).IsSuccess);
        AssertTrue(service.Set(new("game.arguments", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "--changed"), instance)).IsSuccess);
        var preview = service.PreviewRecoverySettings(instance, baseline);
        AssertEqual(0, preview.Errors.Count);
        AssertEqual(2, preview.Changes.Count);
        AssertTrue(preview.Changes.All(item => item.Layer == SettingsLayer.Instance && item.InstanceId == instance));
        AssertTrue(service.SetBatch(new(preview.Changes, preview.Revision)).IsSuccess);
        AssertEqual("-XX:+UseG1GC", Effective(service, "game.jvm", instance).Value.Value!);
        AssertEqual("-XX:+UseZGC", Effective(service, "game.jvm").Value.Value!);
        AssertEqual("--demo", Effective(service, "game.arguments", instance).Value.Value!);
        AssertEqual("--other-instance", Effective(service, "game.arguments", other).Value.Value!);

        AssertTrue(service.Set(new("game.jvm", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "-XX:+UseG1GC"))).IsSuccess);
        preview = service.PreviewRecoverySettings(instance, baseline);
        AssertEqual(SettingsOverrideMode.Inherit, preview.Changes.Single().Value.Mode);
        AssertTrue(service.SetBatch(new(preview.Changes, preview.Revision)).IsSuccess);
        AssertEqual(SettingsLayer.Global, Effective(service, "game.jvm", instance).Source);
    }

    private static void RecoverySettingsRejectWrongScopeAndIncompleteBaseline()
    {
        var (_, service) = PolicyFixture();
        string instance = Path.GetFullPath("recovery-settings-instance");
        string baseline = service.CaptureRecoverySettings(instance);
        AssertTrue(service.PreviewRecoverySettings(Path.GetFullPath("other-root/recovery-settings-instance"), baseline).Errors.Count > 0);
        var document = JsonNode.Parse(baseline)!;
        document["values"]!.AsObject().Remove("game.jvm");
        var preview = service.PreviewRecoverySettings(instance, document.ToJsonString());
        AssertTrue(preview.Errors.Count > 0);
        AssertEqual(0, preview.Changes.Count);
        document = JsonNode.Parse(baseline)!;
        document["values"]!["updates.channel"] = new JsonObject();
        AssertTrue(service.PreviewRecoverySettings(instance, document.ToJsonString()).Errors.Count > 0);
        AssertTrue(service.PreviewRecoverySettings(instance, "{}").Errors.Count > 0);
    }
}

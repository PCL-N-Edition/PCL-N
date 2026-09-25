using System.Text.Json.Nodes;
using Nexa.Xsr;

namespace Nexa.Services.Settings;

internal sealed record RecoverySettingsPlan(string InstanceDirectory, long Revision,
    IReadOnlyList<SettingsMutation> Before, IReadOnlyList<SettingsMutation> After);

public sealed partial class SettingsPolicyService
{
    internal static JsonObject EncodeRecoverySettingsPlan(RecoverySettingsPlan plan)
    {
        ValidateRecoverySettingsPlan(plan);
        JsonObject values = new();
        for (int i = 0; i < plan.Before.Count; i++)
            values[plan.Before[i].Key] = new JsonObject
            {
                ["before"] = plan.Before[i].Value.Mode == SettingsOverrideMode.Inherit ? null : Encode(plan.Before[i].Value),
                ["after"] = plan.After[i].Value.Mode == SettingsOverrideMode.Inherit ? null : Encode(plan.After[i].Value)
            };
        return new JsonObject { ["version"] = 1, ["instance"] = plan.InstanceDirectory, ["revision"] = plan.Revision, ["values"] = values };
    }

    internal static RecoverySettingsPlan DecodeRecoverySettingsPlan(JsonObject document)
    {
        if (document["version"]?.GetValue<int>() != 1 || document["values"] is not JsonObject values
            || values.Count > SettingsPolicySchema.Definitions.Count) throw new InvalidDataException("恢复设置计划无效。");
        string instance = document["instance"]!.GetValue<string>();
        List<SettingsMutation> before = [], after = [];
        foreach (var item in values)
        {
            if (item.Value is not JsonObject value || !value.ContainsKey("before") || !value.ContainsKey("after"))
                throw new InvalidDataException("恢复设置计划缺少原值或目标值。");
            before.Add(new(item.Key, SettingsLayer.Instance, ReadOverride(value["before"]) ?? new(SettingsOverrideMode.Inherit), instance));
            after.Add(new(item.Key, SettingsLayer.Instance, ReadOverride(value["after"]) ?? new(SettingsOverrideMode.Inherit), instance));
        }
        var plan = new RecoverySettingsPlan(instance, document["revision"]!.GetValue<long>(), before.AsReadOnly(), after.AsReadOnly());
        ValidateRecoverySettingsPlan(plan);
        return plan;
    }

    internal RecoverySettingsPlan PlanRecoverySettings(string instanceDirectory, string baseline, IReadOnlyList<string> selectedKeys)
    {
        var selected = selectedKeys.ToHashSet(StringComparer.Ordinal);
        if (selected.Count != selectedKeys.Count || selected.Any(key => !RecoverySetting(key)))
            throw new InvalidDataException("恢复设置字段无效或重复。");
        var preview = PreviewRecoverySettings(instanceDirectory, baseline);
        if (preview.Errors.Count != 0) throw new InvalidDataException("无法准备所选启动设置。");
        var snapshot = _settings.ReadBatch();
        if (snapshot.Revision != preview.Revision) throw new IOException("恢复预览期间设置发生变化。");
        string instance = InstanceKey(instanceDirectory)!;
        var local = ReadDocument(snapshot.Values)["instances"]![instance] as JsonObject;
        var after = preview.Changes.Where(item => selected.Contains(item.Key)).ToArray();
        var before = after.Select(item => item with { Value = ReadOverride(local?[item.Key]) ?? new(SettingsOverrideMode.Inherit) }).ToArray();
        return new(instanceDirectory, snapshot.Revision, Array.AsReadOnly(before), Array.AsReadOnly(after));
    }

    internal static void ValidateRecoverySettingsPlan(RecoverySettingsPlan plan)
    {
        if (!Path.IsPathFullyQualified(plan.InstanceDirectory) || plan.Revision < 0
            || plan.Before.Count != plan.After.Count || plan.Before.Count > SettingsPolicySchema.Definitions.Count)
            throw new InvalidDataException("恢复设置计划身份无效。");
        HashSet<string> keys = new(StringComparer.Ordinal);
        for (int i = 0; i < plan.Before.Count; i++)
        {
            var before = plan.Before[i]; var after = plan.After[i];
            if (before.Key != after.Key || !keys.Add(before.Key) || !RecoverySetting(before.Key))
                throw new InvalidDataException("恢复设置计划字段无效。");
            foreach (var item in new[] { before, after })
                if (item.Layer != SettingsLayer.Instance || InstanceKey(item.InstanceId) != InstanceKey(plan.InstanceDirectory)
                    || ValidateMutation(item) is not null) throw new InvalidDataException("恢复设置计划超出实例范围。");
        }
    }

    private static bool RecoverySetting(string key) => SettingsPolicySchema.ByKey.TryGetValue(key, out var definition)
        && definition.InstanceOverride && !key.StartsWith("recovery.", StringComparison.Ordinal);

    internal XsrResult ApplyRecoverySettingsPlan(RecoverySettingsPlan plan, bool reverse)
    {
        try
        {
            ValidateRecoverySettingsPlan(plan);
            var snapshot = _settings.ReadBatch();
            if (_settings.LoadError is not null) throw new InvalidDataException("无法读取完整启动设置。");
            if (!reverse && snapshot.Revision != plan.Revision) throw new InvalidDataException("设置已变化，请重新比较。");
            var local = ReadDocument(snapshot.Values)["instances"]![InstanceKey(plan.InstanceDirectory)!] as JsonObject;
            List<SettingsMutation> changes = [];
            for (int i = 0; i < plan.Before.Count; i++)
            {
                var before = plan.Before[i]; var after = plan.After[i];
                var current = ReadOverride(local?[before.Key]) ?? new(SettingsOverrideMode.Inherit);
                if (reverse)
                {
                    if (current == before.Value) continue;
                    if (current != after.Value) throw new InvalidDataException("启动设置已被再次修改，已保留恢复记录。");
                    changes.Add(before);
                }
                else
                {
                    if (current != before.Value) throw new InvalidDataException("启动设置与恢复预览不一致。");
                    changes.Add(after);
                }
            }
            return changes.Count == 0 ? XsrResult.Success() : SetBatch(new(changes, snapshot.Revision));
        }
        catch (Exception error) when (Recoverable(error)) { return XsrResult.Failure(Invalid(error.Message)); }
    }
}

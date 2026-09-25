using System.Text.Json.Nodes;

namespace Nexa.Services.Settings;

public sealed partial class SettingsPolicyService
{
    // Local recovery only: includes private launch arguments, never a sharing/telemetry payload.
    internal string CaptureRecoverySettings(string instanceDirectory)
    {
        string instance = InstanceKey(instanceDirectory) ?? throw new InvalidDataException("实例目录不能为空。");
        var snapshot = _settings.ReadBatch();
        if (_settings.LoadError is not null) throw new InvalidDataException("无法读取完整启动设置。");
        var document = ReadDocument(snapshot.Values);
        var effective = Resolve(snapshot.Revision, snapshot.Values, document, instance);
        var local = document["instances"]![instance] as JsonObject;
        JsonObject values = new();
        foreach (var definition in SettingsPolicySchema.Definitions.Where(item => item.InstanceOverride))
        {
            var value = effective.Values.Single(item => item.Key == definition.Key);
            if (value.ValidationError is not null) throw new InvalidDataException("无法备份无效启动设置。");
            values[definition.Key] = new JsonObject
            {
                ["override"] = local?[definition.Key]?.DeepClone(),
                ["effective"] = Encode(value.Value)
            };
        }
        string result = new JsonObject { ["version"] = 1, ["instance"] = instance, ["values"] = values }.ToJsonString();
        if (result.Length > 1024 * 1024) throw new InvalidDataException("启动设置快照超过大小限制。");
        return result;
    }

    // Planning only. The recovery transaction must atomically coordinate files and settings.
    internal SettingsImportPreview PreviewRecoverySettings(string instanceDirectory, string baseline)
    {
        var snapshot = _settings.ReadBatch();
        List<SettingsMutation> changes = [];
        List<string> errors = [];
        try
        {
            string instance = InstanceKey(instanceDirectory) ?? throw new InvalidDataException("实例目录不能为空。");
            if (_settings.LoadError is not null) throw new InvalidDataException("无法读取完整启动设置。");
            if (baseline.Length > 1024 * 1024) throw new InvalidDataException("启动设置快照超过大小限制。");
            var input = JsonNode.Parse(baseline) as JsonObject;
            if (input?["version"]?.GetValue<int>() != 1 || input["instance"]?.GetValue<string>() != instance
                || input["values"] is not JsonObject values)
                throw new InvalidDataException("启动设置快照不属于当前实例。");
            var definitions = SettingsPolicySchema.Definitions.Where(item => item.InstanceOverride).ToArray();
            if (values.Count != definitions.Length || values.Any(item => !SettingsPolicySchema.ByKey.TryGetValue(item.Key, out var definition) || !definition.InstanceOverride))
                throw new InvalidDataException("启动设置快照的字段不完整或不受支持。");
            var document = ReadDocument(snapshot.Values);
            var inherited = Resolve(snapshot.Revision, snapshot.Values, document, null);
            var local = document["instances"]![instance] as JsonObject;
            foreach (var definition in definitions)
            {
                if (values[definition.Key] is not JsonObject item) throw new InvalidDataException("启动设置快照缺少字段。");
                var original = ReadOverride(item["override"]);
                var effective = ReadOverride(item["effective"]) ?? throw new InvalidDataException("启动设置快照缺少有效值。");
                if (definition.Validate(effective) is not null || original is not null && (definition.Validate(original) is not null || original != effective))
                    throw new InvalidDataException("启动设置快照包含无效值。");
                var currentInherited = inherited.Values.Single(value => value.Key == definition.Key);
                if (currentInherited.ValidationError is not null) throw new InvalidDataException("当前继承设置无效。");
                var desired = original ?? (currentInherited.Value == effective ? new(SettingsOverrideMode.Inherit) : effective);
                var current = ReadOverride(local?[definition.Key]) ?? new(SettingsOverrideMode.Inherit);
                if (current != desired) changes.Add(new(definition.Key, SettingsLayer.Instance, desired, instanceDirectory));
            }
            _ = PrepareChanges(snapshot.Values, changes);
        }
        catch (Exception error) when (Recoverable(error)) { errors.Add(error.Message); changes.Clear(); }
        return new(snapshot.Revision, changes.AsReadOnly(), errors.AsReadOnly());
    }
}

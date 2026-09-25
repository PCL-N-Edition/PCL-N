using System.Text.Json.Nodes;
using Nexa.Xsr;

namespace Nexa.Services.Settings;

internal sealed record RenameSettingsPlan(string Source, string Destination, string? Value);

public sealed partial class SettingsPolicyService
{
    internal RenameSettingsPlan PrepareRenameSettings(string source, string destination)
    {
        var snapshot = _settings.ReadBatch();
        if (_settings.LoadError is not null) throw new IOException("无法读取实例设置。");
        string from = InstanceKey(source)!, to = InstanceKey(destination)!;
        var instances = (JsonObject)ReadDocument(snapshot.Values)["instances"]!;
        if (from != to && instances[to] is not null) throw new IOException("目标名称已有实例设置。");
        return new(source, destination, instances[from]?.ToJsonString());
    }

    internal void ApplyRenameSettings(RenameSettingsPlan plan, bool reverse)
    {
        var snapshot = _settings.ReadBatch();
        if (_settings.LoadError is not null) throw new IOException("无法读取实例设置。");
        string from = InstanceKey(plan.Source)!, to = InstanceKey(plan.Destination)!;
        if (from == to) return;
        var document = ReadDocument(snapshot.Values);
        var instances = (JsonObject)document["instances"]!;
        var value = plan.Value is null ? null : JsonNode.Parse(plan.Value);
        bool before = JsonNode.DeepEquals(instances[from], value) && instances[to] is null;
        bool after = instances[from] is null && JsonNode.DeepEquals(instances[to], value);
        if (reverse ? before : after) return;
        if (!(reverse ? after : before)) throw new IOException("实例设置在改名期间发生变化，未覆盖后续修改。");
        instances.Remove(from); instances.Remove(to);
        if (value is not null) instances[reverse ? from : to] = value.DeepClone();
        var result = _settings.SetRawValues(new Dictionary<string, string> { [SettingsPolicySchema.StorageKey] = document.ToJsonString() }, snapshot.Revision);
        if (!result.IsSuccess) throw new IOException(result.Error?.Message ?? "实例设置迁移失败。");
    }
    internal XsrResult MoveInstanceSettings(string source, string destination)
    {
        try
        {
            var snapshot = _settings.ReadBatch();
            if (_settings.LoadError is { } error) return XsrResult.Failure(error);
            string from = InstanceKey(source)!, to = InstanceKey(destination)!;
            if (from == to) return XsrResult.Success();
            var document = ReadDocument(snapshot.Values);
            var instances = (JsonObject)document["instances"]!;
            if (instances[to] is not null) return XsrResult.Failure(Invalid("目标名称已有实例设置，请先处理冲突。"));
            if (instances[from] is not { } value) return XsrResult.Success();
            instances[to] = value.DeepClone(); instances.Remove(from);
            return _settings.SetRawValues(new Dictionary<string, string> { [SettingsPolicySchema.StorageKey] = document.ToJsonString() }, snapshot.Revision);
        }
        catch (Exception error) when (Recoverable(error)) { return XsrResult.Failure(Invalid(error.Message)); }
    }
}

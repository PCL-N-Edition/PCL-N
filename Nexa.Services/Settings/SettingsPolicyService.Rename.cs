using System.Text.Json.Nodes;
using Nexa.Xsr;

namespace Nexa.Services.Settings;

public sealed partial class SettingsPolicyService
{
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

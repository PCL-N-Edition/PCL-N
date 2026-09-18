using System.Text.Json.Nodes;
using Nexa.Xsr;

namespace Nexa.Services.Settings;

public sealed partial class SettingsPolicyService
{
    public XsrResult<string> Export(SettingsExportQuery query)
    {
        try
        {
            var snapshot = _settings.ReadBatch();
            var document = ReadDocument(snapshot.Values);
            string? instance = InstanceKey(query.InstanceId);
            var effective = Resolve(snapshot.Revision, snapshot.Values, document, null);
            JsonObject values = new();
            foreach (var definition in SettingsPolicySchema.Definitions.Where(item => item.Exportable))
            {
                if (instance is null)
                {
                    var item = effective.Values.Single(item => item.Key == definition.Key);
                    if (item.ValidationError is not null) return XsrResult.Failure<string>(Invalid("Cannot export invalid settings."));
                    values[definition.Key] = Encode(item.Value);
                }
                else if (definition.InstanceOverride && document["instances"]![instance] is JsonObject local && local[definition.Key] is { } value)
                    values[definition.Key] = value.DeepClone();
            }
            return XsrResult.Success(new JsonObject { ["version"] = 1, ["scope"] = instance is null ? "global" : "instance", ["values"] = values }.ToJsonString());
        }
        catch (Exception error) when (Recoverable(error)) { return XsrResult.Failure<string>(Invalid(error.Message)); }
    }

    public SettingsImportPreview PreviewImport(SettingsImportQuery query)
    {
        var snapshot = _settings.ReadBatch();
        List<string> errors = [];
        List<SettingsMutation> changes = [];
        try
        {
            if (query.Document.Length > 1024 * 1024) throw new InvalidDataException("Import exceeds 1 MiB.");
            var current = ReadDocument(snapshot.Values);
            string? instance = InstanceKey(query.InstanceId);
            var input = JsonNode.Parse(query.Document) as JsonObject;
            if (input is null || input["version"]?.GetValue<int>() != 1 || input["values"] is not JsonObject values
                || input["scope"]?.GetValue<string>() != (instance is null ? "global" : "instance"))
                throw new InvalidDataException("Unsupported import schema or scope.");
            foreach (var pair in values)
            {
                if (!SettingsPolicySchema.ByKey.TryGetValue(pair.Key, out var definition)) { errors.Add("Unknown setting: " + pair.Key); continue; }
                if (!definition.Exportable) { errors.Add("Local-only setting cannot be imported: " + pair.Key); continue; }
                SettingsOverride value;
                if (pair.Value is JsonObject node && node["mode"]?.GetValue<string>() == "Inherit")
                    value = new(SettingsOverrideMode.Inherit, node["value"]?.GetValue<string>());
                else value = ReadOverride(pair.Value) ?? throw new InvalidDataException("An import value is missing.");
                var change = new SettingsMutation(pair.Key, instance is null ? SettingsLayer.Global : SettingsLayer.Instance, value, query.InstanceId);
                string? error = ValidateMutation(change);
                if (error is not null) errors.Add(pair.Key + ": " + error);
                else
                {
                    var existing = instance is null ? ReadOverride(current["global"]![pair.Key]) : ReadOverride((current["instances"]![instance] as JsonObject)?[pair.Key]);
                    if (instance is null && definition.LegacyKey is { } key && snapshot.Values.TryGetValue(key, out var legacy))
                        existing = new(SettingsOverrideMode.Custom, legacy);
                    if (existing != value && !(existing is null && value.Mode == SettingsOverrideMode.Inherit)) changes.Add(change);
                }
            }
            // Use the same transaction validator, without saving or publishing.
            if (errors.Count == 0)
            {
                _ = PrepareChanges(snapshot.Values, changes);
            }
        }
        catch (Exception error) when (Recoverable(error)) { errors.Add(error.Message); }
        return new(snapshot.Revision, changes.AsReadOnly(), errors.AsReadOnly());
    }

    public XsrResult ApplyImport(SettingsImportCommand command)
    {
        var preview = PreviewImport(new(command.Document, command.InstanceId));
        if (preview.Revision != command.ExpectedRevision)
            return XsrResult.Failure(new(XsrErrorKind.Rejected, XsrSemanticId.Parse("settings.stale_revision"), "Settings changed; preview again."));
        if (preview.Errors.Count > 0) return XsrResult.Failure(Invalid(string.Join(" ", preview.Errors)));
        return Apply(preview.Changes, command.ExpectedRevision);
    }
}

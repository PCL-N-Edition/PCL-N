using PCL.Xsr;

namespace PCL.Services.Settings;

/// <summary>
/// The value type of one setting definition.
/// </summary>
public enum SettingValueType
{
    Bool = 1,
    I32 = 2,
    I64 = 3,
    F64 = 4,
    Text = 5,
}

/// <summary>
/// One setting definition: its stable semantic key, value type, and default value.
/// Definitions are declared through <see cref="SettingsSchemaBuilder"/> and immutable after.
/// </summary>
public sealed record SettingDefinition(
    SettingValueType ValueType,
    XsrSemanticId Key,
    string DefaultValue);

/// <summary>
/// The frozen schema of one settings family: keys, types, and defaults. The schema is the
/// data-compatibility contract — persisted values are only meaningful against it, and unknown
/// persisted keys are skipped, never invented.
/// </summary>
public sealed class SettingsSchema
{
    private readonly Dictionary<XsrSemanticId, SettingDefinition> _definitions;

    internal SettingsSchema(Dictionary<XsrSemanticId, SettingDefinition> definitions)
    {
        _definitions = definitions;
    }

    public int Count => _definitions.Count;

    public IReadOnlyCollection<SettingDefinition> Definitions => _definitions.Values;

    public SettingDefinition? TryGetDefinition(XsrSemanticId key) =>
        _definitions.TryGetValue(key, out SettingDefinition? definition) ? definition : null;
}

/// <summary>
/// Builds a settings schema. Keys must be unique; defaults must parse under their declared
/// value type with invariant-culture rules.
/// </summary>
public sealed class SettingsSchemaBuilder
{
    private readonly Dictionary<XsrSemanticId, SettingDefinition> _definitions = [];

    public SettingsSchemaBuilder AddBool(string key, bool defaultValue) =>
        Add(SettingValueType.Bool, key, defaultValue ? "true" : "false");

    public SettingsSchemaBuilder AddInt32(string key, int defaultValue) =>
        Add(SettingValueType.I32, key, defaultValue.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public SettingsSchemaBuilder AddInt64(string key, long defaultValue) =>
        Add(SettingValueType.I64, key, defaultValue.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public SettingsSchemaBuilder AddFloat64(string key, double defaultValue) =>
        Add(SettingValueType.F64, key, defaultValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

    public SettingsSchemaBuilder AddString(string key, string defaultValue)
    {
        ArgumentException.ThrowIfNullOrEmpty(defaultValue);
        if (defaultValue.Any(character => character is '\n' or '\r' or '='))
        {
            throw new ArgumentException(
                "A string setting default cannot contain line breaks or the equals sign.", nameof(defaultValue));
        }

        return Add(SettingValueType.Text, key, defaultValue);
    }

    public SettingsSchema Build()
    {
        if (_definitions.Count == 0)
        {
            throw new InvalidOperationException("A settings schema must define at least one setting.");
        }

        return new SettingsSchema(new Dictionary<XsrSemanticId, SettingDefinition>(_definitions));
    }

    private SettingsSchemaBuilder Add(SettingValueType type, string key, string defaultValue)
    {
        XsrSemanticId semantic = XsrSemanticId.Parse(key);
        if (_definitions.ContainsKey(semantic))
        {
            throw new ArgumentException($"The setting '{key}' is already defined.", nameof(key));
        }

        _definitions[semantic] = new SettingDefinition(type, semantic, defaultValue);
        return this;
    }
}

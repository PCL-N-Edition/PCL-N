using System.Globalization;

namespace PCL.Services.Settings;

/// <summary>
/// Persistence boundary of one settings family. The service owns when and what is saved; the
/// port only moves raw string entries. Failures surface as exceptions and become stable errors.
/// </summary>
public interface ISettingsPort
{
    /// <summary>
    /// Reads every persisted entry. A missing store means an empty view, not a failure.
    /// </summary>
    IReadOnlyDictionary<string, string> Load();

    /// <summary>
    /// Replaces the persisted store with exactly the given entries.
    /// </summary>
    void Save(IReadOnlyDictionary<string, string> values);
}

/// <summary>
/// Volatile port for tests and composition without disk.
/// </summary>
public sealed class InMemorySettingsPort : ISettingsPort
{
    private readonly object _gate = new();
    private Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Load()
    {
        lock (_gate)
        {
            return new Dictionary<string, string>(_values, StringComparer.Ordinal);
        }
    }

    public void Save(IReadOnlyDictionary<string, string> values)
    {
        lock (_gate)
        {
            _values = new Dictionary<string, string>(values, StringComparer.Ordinal);
        }
    }
}

/// <summary>
/// Line-based file port, kept format-compatible with the legacy `key = value` settings files.
/// Parsing skips blank lines, comments, malformed lines, and unknown keys so older or newer
/// files round-trip without loss of unrelated content. Writes are atomic within a process.
/// </summary>
public sealed class SettingsFilePort : ISettingsPort
{
    private const string Header = "# pcl-settings v1";

    private readonly string _path;

    public SettingsFilePort(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public string Path => _path;

    public IReadOnlyDictionary<string, string> Load()
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(_path))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || !trimmed.Contains('='))
            {
                continue;
            }

            int separator = trimmed.IndexOf('=');
            string key = trimmed[..separator].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            values[key] = trimmed[(separator + 1)..].Trim();
        }

        return values;
    }

    public void Save(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        string? directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = _path + ".tmp";
        using (StreamWriter writer = File.CreateText(temporary))
        {
            writer.WriteLine(Header);
            foreach ((string key, string value) in values.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
            {
                SettingValues.ValidateKey(key);
                writer.Write(key);
                writer.Write(" = ");
                writer.WriteLine(value);
            }
        }

        File.Move(temporary, _path, overwrite: true);
    }
}

/// <summary>
/// Typed-value encoders shared by the service and ports. Strings use invariant culture, `R`
/// round-trip doubles, and `true`/`false` literals; string values cannot carry line breaks,
/// control characters, or the equals sign so one line stays one entry.
/// </summary>
public static class SettingValues
{
    public static void ValidateKey(string key)
    {
        if (key.Length == 0 || key.Any(character => character is '\n' or '\r' or '=' or '#' || char.IsControl(character)))
        {
            throw new ArgumentException($"The setting key '{key}' cannot be represented in the settings line format.");
        }
    }

    public static bool IsValidString(string value) =>
        !value.Any(character => character is '\n' or '\r' or '=' || char.IsControl(character));

    public static string Encode(SettingDefinition definition, object value) => definition.ValueType switch
    {
        SettingValueType.Bool => (bool)value ? "true" : "false",
        SettingValueType.I32 => ((int)value).ToString(CultureInfo.InvariantCulture),
        SettingValueType.I64 => ((long)value).ToString(CultureInfo.InvariantCulture),
        SettingValueType.F64 => ((double)value).ToString("R", CultureInfo.InvariantCulture),
        SettingValueType.Text => EncodeText((string)value),
        _ => throw new InvalidOperationException($"The schema uses unsupported value type '{definition.ValueType}'."),
    };

    private static string EncodeText(string value) =>
        IsValidString(value)
            ? value
            : throw new ArgumentException(
                "String settings cannot contain line breaks, control characters, or the equals sign.");

    public static bool TryDecode(SettingDefinition definition, string raw, out object? value)
    {
        switch (definition.ValueType)
        {
            case SettingValueType.Bool:
                if (raw is "true" or "false")
                {
                    value = raw == "true";
                    return true;
                }

                break;
            case SettingValueType.I32:
                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedInt32))
                {
                    value = parsedInt32;
                    return true;
                }

                break;
            case SettingValueType.I64:
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedInt64))
                {
                    value = parsedInt64;
                    return true;
                }

                break;
            case SettingValueType.F64:
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedDouble)
                    && double.IsFinite(parsedDouble))
                {
                    value = parsedDouble;
                    return true;
                }

                break;
            case SettingValueType.Text:
                if (IsValidString(raw))
                {
                    value = raw;
                    return true;
                }

                break;
            default:
                throw new InvalidOperationException($"The schema uses unsupported value type '{definition.ValueType}'.");
        }

        value = null;
        return false;
    }
}

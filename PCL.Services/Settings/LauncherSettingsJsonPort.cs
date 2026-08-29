using System.Collections.Concurrent;
using System.Text.Json;
using PCL.Xsr;

namespace PCL.Services.Settings;

/// <summary>
/// Persistence port for the legacy launcher settings JSON file. The format is the launcher's
/// data-compatibility contract: a `schemaVersion`, fixed top-level fields, and three typed
/// option dictionaries. Loading keeps every valid entry, skips invalid ones, and quarantines
/// the original file next to itself (`settings.json.invalid`) when anything had to be
/// recovered or the schema is unsupported — an unsupported schema or unreadable file surfaces
/// as an <see cref="IOException"/>, which the settings service reports as a load error with
/// defaults visible. Writes are atomic: a temporary file with write-through, then a replace
/// with bounded retries. Fixed fields, unknown top-level fields, and option keys outside the
/// schema round-trip verbatim, so a save never silently rewrites history it does not know.
/// </summary>
public sealed class LauncherSettingsJsonPort : ISettingsPort
{
    public const int SupportedSchemaVersion = 1;

    private const int ReplaceAttemptCount = 5;

    private const string QuarantineSuffix = ".invalid";

    private static readonly ConcurrentDictionary<string, object> PathLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static readonly HashSet<string> OptionDictionaryNames =
        new(StringComparer.Ordinal) { "booleanOptions", "integerOptions", "textOptions" };

    private static readonly (string Name, string Json)[] FixedFieldDefaults =
    [
        ("automaticallyRepairGameIssues", "true"),
        ("colorMode", "\"System\""),
        ("lightColor", "\"CatBlue\""),
        ("darkColor", "\"CatBlue\""),
        ("downloadSource", "\"PreferOfficialWithMirrorFallback\""),
    ];

    private readonly string _path;
    private readonly SettingsSchema _schema;
    private readonly List<(string Name, string Json)> _preservedFields = [];
    private readonly List<(SettingValueType Kind, string Key, string Json)> _preservedOptions = [];

    public LauncherSettingsJsonPort(string path, SettingsSchema schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = System.IO.Path.GetFullPath(path);
        _schema = schema ?? throw new ArgumentNullException(nameof(schema));
    }

    public string Path => _path;

    public string QuarantinePath => _path + QuarantineSuffix;

    public IReadOnlyDictionary<string, string> Load()
    {
        lock (PathLocks.GetOrAdd(_path, static _ => new object()))
        {
            _preservedFields.Clear();
            _preservedOptions.Clear();
            if (!File.Exists(_path))
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            Dictionary<string, string> values = new(StringComparer.Ordinal);
            bool recovered;
            try
            {
                using JsonDocument document = JsonDocument.Parse(ReadAllTextSharing());
                recovered = ReadDocument(document.RootElement, values);
            }
            catch (Exception failure) when (failure is JsonException or InvalidDataException or IOException)
            {
                Quarantine();
                throw new IOException($"The launcher settings file '{_path}' is unreadable: {failure.Message}", failure);
            }

            if (recovered)
            {
                Quarantine();
            }

            return values;
        }
    }

    public void Save(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        lock (PathLocks.GetOrAdd(_path, static _ => new object()))
        {
            string directory = System.IO.Path.GetDirectoryName(_path)
                ?? throw new IOException($"The launcher settings path '{_path}' has no parent directory.");
            Directory.CreateDirectory(directory);
            string temporaryPath = System.IO.Path.Combine(
                directory,
                $".{System.IO.Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            bool replaced = false;
            try
            {
                using (FileStream stream = new(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 16 * 1024,
                    FileOptions.WriteThrough | FileOptions.SequentialScan))
                {
                    using Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true });
                    WriteDocument(writer, values);
                }

                ReplaceWithRetry(temporaryPath);
                replaced = true;
            }
            finally
            {
                if (!replaced)
                {
                    try
                    {
                        File.Delete(temporaryPath);
                    }
                    catch (IOException)
                    {
                        // Preserve the original save exception.
                    }
                }
            }
        }
    }

    private string ReadAllTextSharing()
    {
        using FileStream stream = new(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16 * 1024,
            FileOptions.SequentialScan);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    private bool ReadDocument(JsonElement root, Dictionary<string, string> values)
    {
        bool recovered = false;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The launcher settings file root must be an object.");
        }

        int schemaVersion = SupportedSchemaVersion;
        if (root.TryGetProperty("schemaVersion", out JsonElement versionElement))
        {
            if (!versionElement.TryGetInt32(out schemaVersion))
            {
                recovered = true;
                schemaVersion = SupportedSchemaVersion;
            }
        }

        if (schemaVersion is <= 0 or > SupportedSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported launcher settings schema: {schemaVersion}.");
        }

        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.Name != "schemaVersion" && !OptionDictionaryNames.Contains(property.Name))
            {
                _preservedFields.Add((property.Name, property.Value.GetRawText()));
            }
        }

        recovered |= ReadDictionary(root, "booleanOptions", SettingValueType.Bool, values);
        recovered |= ReadDictionary(root, "integerOptions", SettingValueType.I32, values);
        recovered |= ReadDictionary(root, "textOptions", SettingValueType.Text, values);
        return recovered;
    }

    private bool ReadDictionary(
        JsonElement root,
        string propertyName,
        SettingValueType kind,
        Dictionary<string, string> values)
    {
        bool recovered = false;
        if (!root.TryGetProperty(propertyName, out JsonElement dictionary))
        {
            return false;
        }

        if (dictionary.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        foreach (JsonProperty property in dictionary.EnumerateObject())
        {
            string? raw = property.Value.ValueKind switch
            {
                JsonValueKind.True or JsonValueKind.False when kind == SettingValueType.Bool =>
                    property.Value.GetBoolean() ? "true" : "false",
                JsonValueKind.Number when kind == SettingValueType.I32 && property.Value.TryGetInt32(out int number) =>
                    number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                JsonValueKind.String when kind == SettingValueType.Text =>
                    property.Value.GetString() ?? string.Empty,
                _ => null,
            };

            if (raw is null)
            {
                recovered = true;
                continue;
            }

            if (TryResolveSchemaKey(property.Name, out SettingDefinition? definition) && definition.ValueType == kind)
            {
                values[property.Name] = raw;
            }
            else
            {
                _preservedOptions.Add((kind, property.Name, property.Value.GetRawText()));
            }
        }

        return recovered;
    }

    private bool TryResolveSchemaKey(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SettingDefinition? definition)
    {
        if (!XsrSemanticId.TryParse(key, out XsrSemanticId semantic))
        {
            definition = null;
            return false;
        }

        definition = _schema.TryGetDefinition(semantic);
        return definition is not null;
    }

    private void WriteDocument(Utf8JsonWriter writer, IReadOnlyDictionary<string, string> values)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", SupportedSchemaVersion);
        foreach ((string name, string json) in FixedFieldDefaults)
        {
            string? preserved = _preservedFields.FirstOrDefault(entry => entry.Name == name).Json;
            WriteRawProperty(writer, name, preserved ?? json);
        }

        foreach ((string name, string json) in _preservedFields)
        {
            if (FixedFieldDefaults.Any(entry => entry.Name == name))
            {
                continue;
            }

            WriteRawProperty(writer, name, json);
        }

        writer.WriteStartObject("booleanOptions");
        WriteSchemaDictionary(writer, values, SettingValueType.Bool, WriteBooleanValue);
        WritePreservedOptions(writer, SettingValueType.Bool);
        writer.WriteEndObject();

        writer.WriteStartObject("integerOptions");
        WriteSchemaDictionary(writer, values, SettingValueType.I32, WriteInt32Value);
        WritePreservedOptions(writer, SettingValueType.I32);
        writer.WriteEndObject();

        writer.WriteStartObject("textOptions");
        WriteSchemaDictionary(writer, values, SettingValueType.Text, WriteTextValue);
        WritePreservedOptions(writer, SettingValueType.Text);
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    private static void WriteRawProperty(Utf8JsonWriter writer, string name, string json)
    {
        writer.WritePropertyName(name);
        using JsonDocument value = JsonDocument.Parse(json);
        value.RootElement.WriteTo(writer);
    }

    private void WriteSchemaDictionary(
        Utf8JsonWriter writer,
        IReadOnlyDictionary<string, string> values,
        SettingValueType kind,
        Action<Utf8JsonWriter, string, string> write)
    {
        foreach ((string key, string raw) in values.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
        {
            if (TryResolveSchemaKey(key, out SettingDefinition? definition) && definition.ValueType == kind)
            {
                write(writer, key, raw);
            }
        }
    }

    private void WritePreservedOptions(Utf8JsonWriter writer, SettingValueType kind)
    {
        foreach ((SettingValueType preservedKind, string key, string json) in _preservedOptions)
        {
            if (preservedKind == kind)
            {
                WriteRawProperty(writer, key, json);
            }
        }
    }

    private static void WriteBooleanValue(Utf8JsonWriter writer, string name, string raw) =>
        writer.WriteBoolean(name, raw == "true");

    private static void WriteInt32Value(Utf8JsonWriter writer, string name, string raw) =>
        writer.WriteNumber(name, int.Parse(raw, System.Globalization.CultureInfo.InvariantCulture));

    private static void WriteTextValue(Utf8JsonWriter writer, string name, string raw) =>
        writer.WriteString(name, raw);

    private void Quarantine()
    {
        try
        {
            File.Copy(_path, QuarantinePath, overwrite: true);
        }
        catch (IOException)
        {
            // Loading valid settings matters more than persisting the quarantine copy; the next
            // successful save replaces the quarantined content anyway.
        }
    }

    private void ReplaceWithRetry(string temporaryPath)
    {
        IOException? lastFailure = null;
        for (int attempt = 1; attempt <= ReplaceAttemptCount; attempt++)
        {
            try
            {
                File.Move(temporaryPath, _path, overwrite: true);
                return;
            }
            catch (IOException failure) when (attempt < ReplaceAttemptCount)
            {
                lastFailure = failure;
                Thread.Sleep(TimeSpan.FromMilliseconds(25 * attempt * attempt));
            }
            catch (IOException failure)
            {
                lastFailure = failure;
            }
        }

        throw new IOException(
            $"Unable to replace launcher settings file '{_path}' after {ReplaceAttemptCount} attempts.",
            lastFailure);
    }
}

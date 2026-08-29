using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Services.Settings;

/// <summary>
/// Stable error contracts of the settings capability. Codes are semantic identifiers and never
/// change meaning; new failures must add codes, not reinterpret existing ones.
/// </summary>
public static class SettingsErrors
{
    public static readonly XsrSemanticId UnknownKeyCode = XsrSemanticId.Parse("settings.unknown_key");
    public static readonly XsrSemanticId TypeMismatchCode = XsrSemanticId.Parse("settings.type_mismatch");
    public static readonly XsrSemanticId InvalidValueCode = XsrSemanticId.Parse("settings.invalid_value");
    public static readonly XsrSemanticId PersistFailedCode = XsrSemanticId.Parse("settings.persist_failed");

    public static XsrError UnknownKey(string key) =>
        new(XsrErrorKind.NotFound, UnknownKeyCode, $"The setting '{key}' is not declared by the schema.");

    public static XsrError TypeMismatch(string key, Type requested, SettingValueType declared) =>
        new(XsrErrorKind.ContractMismatch, TypeMismatchCode,
            $"The setting '{key}' is declared {DeclaredName(declared)}, not {requested.Name}.");

    public static XsrError InvalidValue(string key, string reason) =>
        new(XsrErrorKind.Rejected, InvalidValueCode, $"The value for '{key}' was rejected: {reason}");

    public static XsrError PersistFailed(string reason) =>
        new(XsrErrorKind.Unavailable, PersistFailedCode, $"The settings store could not be written: {reason}");

    private static string DeclaredName(SettingValueType type) => type switch
    {
        SettingValueType.Bool => nameof(SettingValueType.Bool),
        SettingValueType.I32 => nameof(SettingValueType.I32),
        SettingValueType.I64 => nameof(SettingValueType.I64),
        SettingValueType.F64 => nameof(SettingValueType.F64),
        SettingValueType.Text => nameof(SettingValueType.Text),
        _ => type.ToString(),
    };
}

/// <summary>
/// The settings capability: a frozen schema of typed settings, their persisted raw values, and
/// one typed state cell per setting so renderers read locally without touching persistence.
/// Writes are durable-first: a value is published to the state store only after the port saved
/// it, so Success means persisted and a failure changes nothing. A failed startup load keeps
/// schema defaults visible but marks every cell unavailable until the next successful write.
/// </summary>
public sealed class SettingsService
{
    public const string OwnerName = "PCL.Services.Settings";

    private readonly ISettingsPort _port;
    private readonly object _gate = new();
    private readonly Dictionary<XsrSemanticId, XsrStateId> _ids;

    /// <summary>
    /// Two-phase composition, declaration phase: registers one typed cell per schema
    /// definition into the shared host builder. The store is built once for the whole host,
    /// so settings state lives next to every other foundation state without identifier
    /// collisions.
    /// </summary>
    public static void DeclareState(XsrStateStoreBuilder builder, SettingsSchema schema)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(schema);
        foreach (SettingDefinition definition in schema.Definitions)
        {
            switch (definition.ValueType)
            {
                case SettingValueType.Bool:
                    builder.Cell<bool>(definition.Key, OwnerName);
                    break;
                case SettingValueType.I32:
                    builder.Cell<int>(definition.Key, OwnerName);
                    break;
                case SettingValueType.I64:
                    builder.Cell<long>(definition.Key, OwnerName);
                    break;
                case SettingValueType.F64:
                    builder.Cell<double>(definition.Key, OwnerName);
                    break;
                case SettingValueType.Text:
                    builder.Cell<string>(definition.Key, OwnerName);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"The schema uses unsupported value type '{definition.ValueType}'.");
            }
        }
    }

    public SettingsService(XsrStateStore store, SettingsSchema schema, ISettingsPort port)
    {
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _port = port ?? throw new ArgumentNullException(nameof(port));
        StateStore = store ?? throw new ArgumentNullException(nameof(store));
        _ids = [];
        foreach (SettingDefinition definition in schema.Definitions)
        {
            _ids[definition.Key] = StateStore.Resolve(definition.Key);
        }

        IReadOnlyDictionary<string, string> persisted;
        try
        {
            persisted = _port.Load();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LoadError = SettingsErrors.PersistFailed(failure.Message);
            persisted = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        foreach (SettingDefinition definition in schema.Definitions)
        {
            if (!SettingValues.TryDecode(definition, definition.DefaultValue, out object? value)
                || value is null)
            {
                throw new InvalidOperationException(
                    $"The default of '{definition.Key}' does not parse under its declared type.");
            }

            if (persisted.TryGetValue(definition.Key.Value, out string? raw))
            {
                if (SettingValues.TryDecode(definition, raw, out object? decoded) && decoded is not null)
                {
                    value = decoded;
                }
                else
                {
                    SkippedEntryCount++;
                }
            }

            Publish(definition, value);
            if (LoadError is not null)
            {
                StateStore.MarkAvailability(_ids[definition.Key], XsrStateAvailability.Unavailable);
            }
        }

        foreach (string unknown in persisted.Keys)
        {
            if (!schema.Definitions.Any(definition => definition.Key.Value == unknown))
            {
                SkippedEntryCount++;
            }
        }
    }

    public SettingsSchema Schema { get; }

    /// <summary>
    /// The typed local state of every setting, available to observers and the renderer.
    /// </summary>
    public XsrStateStore StateStore { get; }

    /// <summary>
    /// The stable error recorded when the persisted store could not be read at startup.
    /// </summary>
    public XsrError? LoadError { get; }

    /// <summary>
    /// How many persisted or stored entries were skipped as malformed or undeclared. Unknown
    /// keys are skipped, never invented into the schema.
    /// </summary>
    public int SkippedEntryCount { get; private set; }

    /// <summary>
    /// Reads one setting as one coherent typed value.
    /// </summary>
    public XsrResult<T> GetValue<T>(string key)
    {
        if (!TryDefine<T>(key, out SettingDefinition? definition))
        {
            return XsrResult.Failure<T>(SettingsErrors.UnknownKey(key));
        }

        if (!Matches<T>(definition))
        {
            return XsrResult.Failure<T>(SettingsErrors.TypeMismatch(key, typeof(T), definition.ValueType));
        }

        return XsrResult.Success(StateStore.Read<T>(_ids[definition.Key]).Value);
    }

    /// <summary>
    /// Validates, persists, then publishes one setting. On success the port holds the new value.
    /// On failure nothing changes.
    /// </summary>
    public XsrResult SetValue<T>(string key, T value)
    {
        if (!TryDefine<T>(key, out SettingDefinition? definition))
        {
            return XsrResult.Failure(SettingsErrors.UnknownKey(key));
        }

        if (value is null)
        {
            return XsrResult.Failure(SettingsErrors.InvalidValue(key, "values cannot be null."));
        }

        if (!Matches<T>(definition))
        {
            return XsrResult.Failure(SettingsErrors.TypeMismatch(key, typeof(T), definition.ValueType));
        }

        string raw;
        try
        {
            raw = SettingValues.Encode(definition, (object)value);
        }
        catch (ArgumentException failure)
        {
            return XsrResult.Failure(SettingsErrors.InvalidValue(key, failure.Message));
        }

        lock (_gate)
        {
            Dictionary<string, string> snapshot = CurrentEntries();
            snapshot[definition.Key.Value] = raw;
            XsrResult saved = Persist(snapshot, definition.Key.Value);
            if (!saved.IsSuccess)
            {
                return saved;
            }

            Publish(definition, (object)value);
            StateStore.MarkAvailability(_ids[definition.Key], XsrStateAvailability.Available);
            return XsrResult.Success();
        }
    }

    /// <summary>
    /// Restores one setting to its schema default and persists the change.
    /// </summary>
    public XsrResult ResetValue(string key)
    {
        if (!TryDefine<object?>(key, out SettingDefinition? definition))
        {
            return XsrResult.Failure(SettingsErrors.UnknownKey(key));
        }

        if (!SettingValues.TryDecode(definition, definition.DefaultValue, out object? defaultValue)
            || defaultValue is null)
        {
            return XsrResult.Failure(SettingsErrors.InvalidValue(key, "the schema default does not parse."));
        }

        return WriteBoxed(definition, defaultValue);
    }

    /// <summary>
    /// Restores every setting to its schema default and persists one atomic replacement.
    /// Durable-first: the defaults are persisted before anything is published; a save failure
    /// leaves the state untouched.
    /// </summary>
    public XsrResult ResetAll()
    {
        lock (_gate)
        {
            Dictionary<string, string> snapshot = [];
            foreach (SettingDefinition definition in Schema.Definitions)
            {
                if (!SettingValues.TryDecode(definition, definition.DefaultValue, out object? defaultValue)
                    || defaultValue is null)
                {
                    return XsrResult.Failure(SettingsErrors.InvalidValue(
                        definition.Key.Value, "the schema default does not parse."));
                }

                snapshot[definition.Key.Value] = SettingValues.Encode(definition, defaultValue);
            }

            XsrResult saved = Persist(snapshot, snapshot.Keys.FirstOrDefault() ?? string.Empty);
            if (!saved.IsSuccess)
            {
                return saved;
            }

            foreach (SettingDefinition definition in Schema.Definitions)
            {
                object value = SettingValues.TryDecode(definition, definition.DefaultValue, out object? decoded)
                    && decoded is not null
                    ? decoded
                    : throw new InvalidOperationException(
                        $"The default of '{definition.Key}' does not parse under its declared type.");
                Publish(definition, value);
                StateStore.MarkAvailability(_ids[definition.Key], XsrStateAvailability.Available);
            }

            return XsrResult.Success();
        }
    }

    private bool TryDefine<T>(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SettingDefinition? definition)
    {
        if (!XsrSemanticId.TryParse(key, out XsrSemanticId semantic)
            || (definition = Schema.TryGetDefinition(semantic)) is null)
        {
            definition = null;
            return false;
        }

        return true;
    }

    private static bool Matches<T>(SettingDefinition definition) => definition.ValueType switch
    {
        SettingValueType.Bool => typeof(T) == typeof(bool),
        SettingValueType.I32 => typeof(T) == typeof(int),
        SettingValueType.I64 => typeof(T) == typeof(long),
        SettingValueType.F64 => typeof(T) == typeof(double),
        SettingValueType.Text => typeof(T) == typeof(string),
        _ => false,
    };

    private XsrResult WriteBoxed(SettingDefinition definition, object typed)
    {
        lock (_gate)
        {
            Dictionary<string, string> snapshot = CurrentEntries();
            snapshot[definition.Key.Value] = SettingValues.Encode(definition, typed);
            XsrResult saved = Persist(snapshot, definition.Key.Value);
            if (!saved.IsSuccess)
            {
                return saved;
            }

            Publish(definition, typed);
            StateStore.MarkAvailability(_ids[definition.Key], XsrStateAvailability.Available);
            return XsrResult.Success();
        }
    }

    private XsrResult Persist(Dictionary<string, string> snapshot, string pendingKey)
    {
        try
        {
            _port.Save(snapshot);
            return XsrResult.Success();
        }
        catch (ArgumentException failure)
        {
            return XsrResult.Failure(SettingsErrors.InvalidValue(pendingKey, failure.Message));
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return XsrResult.Failure(SettingsErrors.PersistFailed(failure.Message));
        }
    }

    private Dictionary<string, string> CurrentEntries()
    {
        Dictionary<string, string> entries = [];
        foreach (SettingDefinition definition in Schema.Definitions)
        {
            entries[definition.Key.Value] = SettingValues.Encode(definition, ReadBoxed(definition));
        }

        return entries;
    }

    private object ReadBoxed(SettingDefinition definition)
    {
        XsrStateId id = _ids[definition.Key];
        return definition.ValueType switch
        {
            SettingValueType.Bool => StateStore.Read<bool>(id).Value,
            SettingValueType.I32 => StateStore.Read<int>(id).Value,
            SettingValueType.I64 => StateStore.Read<long>(id).Value,
            SettingValueType.F64 => StateStore.Read<double>(id).Value,
            SettingValueType.Text => StateStore.Read<string>(id).Value,
            _ => throw new InvalidOperationException($"The schema uses unsupported value type '{definition.ValueType}'."),
        };
    }

    private void Publish(SettingDefinition definition, object typed)
    {
        XsrStateId id = _ids[definition.Key];
        switch (definition.ValueType)
        {
            case SettingValueType.Bool:
                StateStore.Publish(id, (bool)typed);
                break;
            case SettingValueType.I32:
                StateStore.Publish(id, (int)typed);
                break;
            case SettingValueType.I64:
                StateStore.Publish(id, (long)typed);
                break;
            case SettingValueType.F64:
                StateStore.Publish(id, (double)typed);
                break;
            case SettingValueType.Text:
                StateStore.Publish(id, (string)typed);
                break;
            default:
                throw new InvalidOperationException($"The schema uses unsupported value type '{definition.ValueType}'.");
        }
    }
}

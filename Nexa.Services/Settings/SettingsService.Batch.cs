using System.Collections.ObjectModel;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Settings;

public sealed partial class SettingsService
{
    public long Revision { get; private set; }
    internal event Action<long>? Changed;
    private void NotifyChanged()
    {
        Revision++;
        Changed?.Invoke(Revision);
    }

    internal (long Revision, IReadOnlyDictionary<string, string> Values) ReadBatch()
    {
        lock (_gate) return (Revision, new ReadOnlyDictionary<string, string>(CurrentEntries()));
    }

    /// <summary>One durable transaction. Validation and revision checks precede the single save.</summary>
    public XsrResult SetRawValues(IReadOnlyDictionary<string, string> changes, long? expectedRevision = null)
    {
        ArgumentNullException.ThrowIfNull(changes);
        lock (_gate)
        {
            if (expectedRevision is { } expected && expected != Revision)
                return XsrResult.Failure(new(XsrErrorKind.Rejected, XsrSemanticId.Parse("settings.stale_revision"), "Settings changed; preview again."));
            var typed = new List<(SettingDefinition Definition, object Value)>();
            var entries = CurrentEntries();
            foreach (var (key, raw) in changes)
            {
                if (!TryDefine<object>(key, out var definition)) return XsrResult.Failure(SettingsErrors.UnknownKey(key));
                if (raw is null || !SettingValues.TryDecode(definition, raw, out var value) || value is null)
                    return XsrResult.Failure(SettingsErrors.InvalidValue(key, "Invalid value."));
                string encoded = SettingValues.Encode(definition, value);
                if (entries[key] == encoded) continue;
                entries[key] = encoded;
                typed.Add((definition, value));
            }
            if (typed.Count == 0) return XsrResult.Success();
            var saved = Persist(entries, typed[0].Definition.Key.Value);
            if (!saved.IsSuccess) return saved;
            foreach (var item in typed)
            {
                Publish(item.Definition, item.Value);
                StateStore.MarkAvailability(_ids[item.Definition.Key], XsrStateAvailability.Available);
            }
            NotifyChanged();
            return XsrResult.Success();
        }
    }
}

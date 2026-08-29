using System.Globalization;
using PCL.Services.Settings;
using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Services.Tests;

/// <summary>
/// Contract tests for the settings capability (XSR-501): typed schema behavior, durable-first
/// writes, stable error codes, data compatibility with raw `key = value` files, and state-store
/// visibility for renderers.
/// </summary>
internal static partial class Program
{
    internal const string KeyToggle = "settings.test.toggle";
    internal const string KeyCount = "settings.test.count";
    internal const string KeyBig = "settings.test.big";
    internal const string KeyRatio = "settings.test.ratio";
    internal const string KeyLabel = "settings.test.label";

    internal static SettingsSchemaBuilder TestSchema() => new SettingsSchemaBuilder()
        .AddBool(KeyToggle, defaultValue: false)
        .AddInt32(KeyCount, 3)
        .AddInt64(KeyBig, -7L)
        .AddFloat64(KeyRatio, 0.5d)
        .AddString(KeyLabel, "default");

    internal static void SchemaDefaultsAreVisibleAndAvailable()
    {
        SettingsService service = new(TestSchema().Build(), new InMemorySettingsPort());

        AssertEqual(0, service.SkippedEntryCount);
        AssertTrue(service.LoadError is null);
        AssertTrue(service.GetValue<bool>(KeyToggle).TryGetValue(out bool toggle) && !toggle);
        AssertTrue(service.GetValue<int>(KeyCount).TryGetValue(out int count) && count == 3);
        AssertTrue(service.GetValue<long>(KeyBig).TryGetValue(out long big) && big == -7L);
        AssertTrue(service.GetValue<double>(KeyRatio).TryGetValue(out double ratio) && ratio == 0.5d);
        AssertTrue(service.GetValue<string>(KeyLabel).TryGetValue(out string? label) && label == "default");

        XsrStateId id = service.StateStore.Resolve(XsrSemanticId.Parse(KeyRatio));
        XsrStateValue<double> read = service.StateStore.Read<double>(id);
        AssertTrue(read.IsAvailable);
        AssertEqual(1L, read.Revision);
    }

    internal static ValueTask SetThenGetRoundTripsEveryType()
    {
        SettingsService service = new(TestSchema().Build(), new InMemorySettingsPort());

        AssertTrue(service.SetValue(KeyToggle, true).IsSuccess);
        AssertTrue(service.SetValue(KeyCount, 42).IsSuccess);
        AssertTrue(service.SetValue(KeyBig, long.MaxValue).IsSuccess);
        AssertTrue(service.SetValue(KeyRatio, 0.125d).IsSuccess);
        AssertTrue(service.SetValue(KeyLabel, "héllo world").IsSuccess);

        AssertTrue(service.GetValue<bool>(KeyToggle).TryGetValue(out bool toggle) && toggle);
        AssertTrue(service.GetValue<int>(KeyCount).TryGetValue(out int count) && count == 42);
        AssertTrue(service.GetValue<long>(KeyBig).TryGetValue(out long big) && big == long.MaxValue);
        AssertTrue(service.GetValue<double>(KeyRatio).TryGetValue(out double ratio) && ratio == 0.125d);
        AssertTrue(service.GetValue<string>(KeyLabel).TryGetValue(out string? label) && label == "héllo world");
        return ValueTask.CompletedTask;
    }

    internal static ValueTask UnknownKeyIsRejectedStably()
    {
        SettingsService service = new(TestSchema().Build(), new InMemorySettingsPort());

        XsrResult<int> missingRead = service.GetValue<int>("settings.test.missing");
        AssertFalse(missingRead.IsSuccess);
        AssertEqual(SettingsErrors.UnknownKeyCode, missingRead.Error!.Code);
        AssertEqual(XsrErrorKind.NotFound, missingRead.Error.Kind);

        XsrResult missingWrite = service.SetValue("settings.test.missing", 1);
        AssertFalse(missingWrite.IsSuccess);
        AssertEqual(SettingsErrors.UnknownKeyCode, missingWrite.Error!.Code);

        XsrResult malformed = service.SetValue("not a semantic id", 1);
        AssertFalse(malformed.IsSuccess);
        AssertEqual(SettingsErrors.UnknownKeyCode, malformed.Error!.Code);
        return ValueTask.CompletedTask;
    }

    internal static ValueTask TypeMismatchIsRejectedStably()
    {
        SettingsService service = new(TestSchema().Build(), new InMemorySettingsPort());

        XsrResult<int> wrongRead = service.GetValue<int>(KeyToggle);
        AssertFalse(wrongRead.IsSuccess);
        AssertEqual(SettingsErrors.TypeMismatchCode, wrongRead.Error!.Code);
        AssertEqual(XsrErrorKind.ContractMismatch, wrongRead.Error.Kind);

        XsrResult wrongWrite = service.SetValue(KeyToggle, 1);
        AssertFalse(wrongWrite.IsSuccess);
        AssertEqual(SettingsErrors.TypeMismatchCode, wrongWrite.Error!.Code);
        return ValueTask.CompletedTask;
    }

    internal static ValueTask InvalidValuesAreRejectedStably()
    {
        SettingsService service = new(TestSchema().Build(), new InMemorySettingsPort());

        foreach (string bad in (string[])["line\nbreak", "a=b", "carriage\rreturn", "\u0007bell"])
        {
            XsrResult rejected = service.SetValue(KeyLabel, bad);
            AssertFalse(rejected.IsSuccess);
            AssertEqual(SettingsErrors.InvalidValueCode, rejected.Error!.Code);
            AssertEqual(XsrErrorKind.Rejected, rejected.Error.Kind);
        }

        XsrResult nullRejected = service.SetValue<string?>(KeyLabel, null);
        AssertFalse(nullRejected.IsSuccess);
        AssertEqual(SettingsErrors.InvalidValueCode, nullRejected.Error!.Code);

        AssertTrue(service.GetValue<string>(KeyLabel).TryGetValue(out string? label) && label == "default");
        return ValueTask.CompletedTask;
    }

    internal static ValueTask SetPersistsAndSurvivesRestart()
    {
        InMemorySettingsPort port = new();
        SettingsService first = new(TestSchema().Build(), port);

        AssertTrue(first.SetValue(KeyCount, 11).IsSuccess);
        AssertTrue(first.SetValue(KeyLabel, "kept").IsSuccess);
        AssertTrue(port.Load().TryGetValue(KeyCount, out string? rawCount) && rawCount == "11");
        AssertTrue(port.Load().TryGetValue(KeyLabel, out string? rawLabel) && rawLabel == "kept");

        SettingsService restarted = new(TestSchema().Build(), port);
        AssertTrue(restarted.GetValue<int>(KeyCount).TryGetValue(out int count) && count == 11);
        AssertTrue(restarted.GetValue<string>(KeyLabel).TryGetValue(out string? label) && label == "kept");
        AssertTrue(restarted.GetValue<bool>(KeyToggle).TryGetValue(out bool toggle) && !toggle);
        return ValueTask.CompletedTask;
    }

    internal static ValueTask CorruptAndUnknownPersistedEntriesAreSkipped()
    {
        InMemorySettingsPort port = new();
        port.Save(new Dictionary<string, string>
        {
            [KeyCount] = "9",
            [KeyToggle] = "definitely-not-a-bool",
            ["legacy.removed.key"] = "1",
        });

        SettingsService service = new(TestSchema().Build(), port);
        AssertEqual(2, service.SkippedEntryCount);
        AssertTrue(service.GetValue<int>(KeyCount).TryGetValue(out int count) && count == 9);
        AssertTrue(service.GetValue<bool>(KeyToggle).TryGetValue(out bool toggle) && !toggle);
        AssertTrue(service.LoadError is null);
        return ValueTask.CompletedTask;
    }

    internal static ValueTask FailedSaveReturnsStableErrorAndMutatesNothing()
    {
        ThrowingSettingsPort port = new(loadShouldThrow: false, saveShouldThrow: true);
        SettingsService service = new(TestSchema().Build(), port);

        AssertTrue(service.GetValue<int>(KeyCount).TryGetValue(out int before) && before == 3);
        XsrResult failed = service.SetValue(KeyCount, 77);
        AssertFalse(failed.IsSuccess);
        AssertEqual(SettingsErrors.PersistFailedCode, failed.Error!.Code);
        AssertEqual(XsrErrorKind.Unavailable, failed.Error.Kind);
        AssertTrue(service.GetValue<int>(KeyCount).TryGetValue(out int after) && after == 3);

        XsrStateId id = service.StateStore.Resolve(XsrSemanticId.Parse(KeyCount));
        AssertEqual(1L, service.StateStore.Read<int>(id).Revision);
        return ValueTask.CompletedTask;
    }

    internal static ValueTask FailedLoadKeepsDefaultsButMarksUnavailable()
    {
        ThrowingSettingsPort port = new(loadShouldThrow: true, saveShouldThrow: false);
        SettingsService service = new(TestSchema().Build(), port);

        AssertTrue(service.LoadError is not null);
        AssertEqual(SettingsErrors.PersistFailedCode, service.LoadError!.Code);
        AssertTrue(service.GetValue<int>(KeyCount).TryGetValue(out int count) && count == 3);

        XsrStateId id = service.StateStore.Resolve(XsrSemanticId.Parse(KeyCount));
        AssertEqual(XsrStateAvailability.Unavailable, service.StateStore.Read<int>(id).Availability);

        AssertTrue(service.SetValue(KeyCount, 5).IsSuccess);
        AssertEqual(XsrStateAvailability.Available, service.StateStore.Read<int>(id).Availability);
        AssertTrue(service.GetValue<int>(KeyCount).TryGetValue(out int updated) && updated == 5);
        return ValueTask.CompletedTask;
    }

    internal static ValueTask ResetValueAndResetAllRestoreDefaults()
    {
        InMemorySettingsPort port = new();
        SettingsService service = new(TestSchema().Build(), port);
        AssertTrue(service.SetValue(KeyCount, 100).IsSuccess);
        AssertTrue(service.SetValue(KeyLabel, "changed").IsSuccess);

        AssertTrue(service.ResetValue(KeyCount).IsSuccess);
        AssertTrue(service.GetValue<int>(KeyCount).TryGetValue(out int count) && count == 3);
        AssertTrue(service.GetValue<string>(KeyLabel).TryGetValue(out string? label) && label == "changed");
        AssertTrue(port.Load()[KeyCount] == "3" && port.Load()[KeyLabel] == "changed");

        AssertTrue(service.ResetAll().IsSuccess);
        AssertTrue(service.GetValue<string>(KeyLabel).TryGetValue(out string? reset) && reset == "default");
        AssertTrue(port.Load().All(entry => SchemaDefaults()
            .TryGetValue(entry.Key, out string? expected) && expected == entry.Value));

        static Dictionary<string, string> SchemaDefaults() => new()
        {
            [KeyToggle] = "false",
            [KeyCount] = "3",
            [KeyBig] = "-7",
            [KeyRatio] = "0.5",
            [KeyLabel] = "default",
        };
        return ValueTask.CompletedTask;
    }

    internal static ValueTask StateObserverSeesEveryAppliedChange()
    {
        RecordingObserver observer = new();
        SettingsService service = new(TestSchema().Build(), new InMemorySettingsPort(), observer);

        AssertTrue(observer.Changes.Count >= 5);
        int duringStartup = observer.Changes.Count;

        AssertTrue(service.SetValue(KeyCount, 8).IsSuccess);
        AssertEqual(duringStartup + 1, observer.Changes.Count);
        XsrStateChange change = observer.Changes[^1];
        AssertEqual(XsrSemanticId.Parse(KeyCount), change.SemanticId);
        AssertEqual(XsrStateChangeReason.ValuePublished, change.Reason);
        AssertEqual(2L, change.Revision);

        XsrStateSnapshot snapshot = service.StateStore.CaptureSnapshot();
        AssertTrue(snapshot.Entries.Any(entry => entry.SemanticId.Value == KeyCount));
        return ValueTask.CompletedTask;
    }

    internal static ValueTask FilePortRoundTripsAndSkipsMalformedLines()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pcl-services-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.ini");
        try
        {
            SettingsFilePort port = new(path);
            AssertTrue(port.Load().Count == 0);

            AssertTrue(port.Load().Count == 0);
            port.Save(new Dictionary<string, string>
            {
                [KeyCount] = "12",
                [KeyLabel] = "a b",
                [KeyToggle] = "true",
            });

            IReadOnlyDictionary<string, string> loaded = port.Load();
            AssertEqual(3, loaded.Count);
            AssertTrue(loaded[KeyCount] == "12" && loaded[KeyLabel] == "a b" && loaded[KeyToggle] == "true");

            File.WriteAllLines(path, ["# pcl-settings v1", "", "garbage-line", "=novalue", "legacy.unknown = 7", "settings.test.count = 21"]);
            IReadOnlyDictionary<string, string> repaired = port.Load();
            AssertTrue(repaired.Count == 2);
            AssertTrue(repaired[KeyCount] == "21" && repaired["legacy.unknown"] == "7");

            SettingsService service = new(TestSchema().Build(), port);
            AssertEqual(1, service.SkippedEntryCount);
            AssertTrue(service.GetValue<int>(KeyCount).TryGetValue(out int count) && count == 21);
            AssertTrue(service.GetValue<bool>(KeyToggle).TryGetValue(out bool toggle) && !toggle);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        return ValueTask.CompletedTask;
    }

    internal static ValueTask FilePortWritesSortedOrdinalEntries()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pcl-services-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.ini");
        try
        {
            SettingsFilePort port = new(path);
            port.Save(new Dictionary<string, string>
            {
                ["settings.test.zeta"] = "2",
                ["settings.test.alpha"] = "1",
                ["Settings.Test.Beta"] = "3",
            });

            string[] lines = File.ReadAllLines(path);
            AssertTrue(lines[0] == "# pcl-settings v1");
            AssertTrue(lines[^3].StartsWith("Settings.Test.Beta = ", StringComparison.Ordinal));
            AssertTrue(lines[^2].StartsWith("settings.test.alpha = ", StringComparison.Ordinal));
            AssertTrue(lines[^1].StartsWith("settings.test.zeta = ", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        return ValueTask.CompletedTask;
    }

    internal static ValueTask DoubleRoundTripsThroughFullPrecision()
    {
        InMemorySettingsPort port = new();
        SettingsService service = new(TestSchema().Build(), port);
        double original = 1d / 3d;
        AssertTrue(service.SetValue(KeyRatio, original).IsSuccess);
        AssertTrue(port.Load()[KeyRatio] == original.ToString("R", CultureInfo.InvariantCulture));

        SettingsService restarted = new(TestSchema().Build(), port);
        AssertTrue(restarted.GetValue<double>(KeyRatio).TryGetValue(out double restored));
        AssertTrue(restored.Equals(original));
        return ValueTask.CompletedTask;
    }

    private sealed class RecordingObserver : IXsrStateObserver
    {
        public List<XsrStateChange> Changes { get; } = [];

        public void OnChanged(XsrStateChange change) => Changes.Add(change);
    }

    private sealed class ThrowingSettingsPort : ISettingsPort
    {
        private readonly bool _loadShouldThrow;
        private readonly bool _saveShouldThrow;

        public ThrowingSettingsPort(bool loadShouldThrow, bool saveShouldThrow)
        {
            _loadShouldThrow = loadShouldThrow;
            _saveShouldThrow = saveShouldThrow;
        }

        public IReadOnlyDictionary<string, string> Load()
        {
            if (_loadShouldThrow)
            {
                throw new IOException("simulated load failure");
            }

            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        public void Save(IReadOnlyDictionary<string, string> values)
        {
            if (_saveShouldThrow)
            {
                throw new IOException("simulated save failure");
            }
        }
    }
}

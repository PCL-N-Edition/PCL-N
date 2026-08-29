using PCL.Services.Logging;
using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Services.Tests;

// XSR-502: logging capability contract — level gate, bounded ring, redaction, and state
// collection visibility migrated from the legacy logging bridge.
internal static partial class Program
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingLogObserver : IXsrStateObserver
    {
        public List<XsrStateChange> Changes { get; } = [];

        public void OnChanged(XsrStateChange change) => Changes.Add(change);
    }

    private sealed class ThrowingLogObserver : IXsrStateObserver
    {
        public void OnChanged(XsrStateChange change) => throw new InvalidOperationException("observer fault");
    }

    internal static void LogLevelGateDefaultsToInfoAndFallsBackStably()
    {
        LogService service = new();
        AssertTrue(service.IsEnabled(LogLevel.Error));
        AssertTrue(service.IsEnabled(LogLevel.Warn));
        AssertTrue(service.IsEnabled(LogLevel.Info));
        AssertFalse(service.IsEnabled(LogLevel.Debug));
        AssertFalse(service.IsEnabled((LogLevel)99));

        service.MaximumLevel = LogLevel.RealTime;
        AssertTrue(service.IsEnabled(LogLevel.Debug));
        AssertTrue(service.IsEnabled(LogLevel.RealTime));

        service.MaximumLevel = (LogLevel)42;
        AssertEqual(LogLevel.Info, service.MaximumLevel);

        service.Write(LogLevel.Debug, "gate", "debug before raise");
        AssertTrue(service.GetSnapshot().Count == 0);
    }

    internal static void ModulesAreNormalizedBeforeStorage()
    {
        LogService service = new();
        service.Write(LogLevel.Info, "  Launch  ", "trimmed");
        service.Write(LogLevel.Warn, "", "blank module");
        service.Write(LogLevel.Error, "   ", "whitespace module");

        IReadOnlyList<LogEntry> snapshot = service.GetSnapshot();
        AssertEqual(3, snapshot.Count);
        AssertTrue(snapshot[0].Module == "Launch");
        AssertTrue(snapshot[1].Module == "General");
        AssertTrue(snapshot[2].Module == "General");
    }

    internal static void MessagesAndExceptionsAreRedactedBeforeStorage()
    {
        LogService service = new();
        service.Write(LogLevel.Warn, "net", "login failed with password=hunter2 tonight");
        service.Write(LogLevel.Error, "net", "boom", "at login\nAuthorization: Bearer abc123");

        IReadOnlyList<LogEntry> snapshot = service.GetSnapshot();
        AssertTrue(snapshot[0].Message == "login failed with password=<redacted> tonight");
        AssertTrue(snapshot[1].ExceptionText is not null);
        string exceptionText = snapshot[1].ExceptionText!;
        AssertTrue(exceptionText.Contains("Authorization: <redacted>", StringComparison.Ordinal));
        AssertFalse(exceptionText.Contains("abc123", StringComparison.Ordinal));

        service.Write(LogLevel.Info, "x", "  ");
        AssertTrue(service.GetSnapshot()[2].ExceptionText is null);
    }

    internal static void RingEvictsOldestBeyondCapacity()
    {
        LogService service = new(capacity: 4);
        for (int index = 1; index <= 6; index++)
        {
            service.Write(LogLevel.Info, "ring", $"entry {index}");
        }

        IReadOnlyList<LogEntry> snapshot = service.GetSnapshot();
        AssertEqual(4, snapshot.Count);
        AssertEqual(3L, snapshot[0].Sequence);
        AssertEqual(6L, snapshot[3].Sequence);
        AssertTrue(snapshot.Select(static entry => entry.Message).SequenceEqual(
            ["entry 3", "entry 4", "entry 5", "entry 6"], StringComparer.Ordinal));
    }

    internal static void StateCollectionMirrorsSnapshot()
    {
        LogService service = new(capacity: 8);
        for (int index = 1; index <= 10; index++)
        {
            service.Write(LogLevel.Warn, "mirror", $"m{index}");
        }

        XsrCollectionSnapshot<LogEntry> state = service.StateStore.ReadCollection<LogEntry>(
            service.StateStore.Resolve(LogService.EntriesKey));
        AssertEqual(8, state.Count);
        AssertTrue(state.Items.SequenceEqual(service.GetSnapshot()));
        AssertTrue(state.Items.All(entry => entry.Module == "mirror"));

        service.Clear();
        XsrCollectionSnapshot<LogEntry> emptied = service.StateStore.ReadCollection<LogEntry>(
            service.StateStore.Resolve(LogService.EntriesKey));
        AssertEqual(0, emptied.Count);
    }

    internal static void ClearEmptiesRingAndState()
    {
        LogService service = new();
        service.Write(LogLevel.Info, "clear", "one");
        service.Write(LogLevel.Info, "clear", "two");
        service.Clear();
        AssertEqual(0, service.GetSnapshot().Count);

        service.Write(LogLevel.Info, "clear", "three");
        IReadOnlyList<LogEntry> afterClear = service.GetSnapshot();
        AssertEqual(1, afterClear.Count);
        AssertEqual(3L, afterClear[0].Sequence);
        AssertTrue(afterClear[0].Message == "three");
    }

    internal static void RedactorCoversLegacySecretPatterns()
    {
        AssertEqual(string.Empty, LogRedactor.Redact(null));
        AssertEqual(string.Empty, LogRedactor.Redact(string.Empty));
        AssertEqual("no secrets here", LogRedactor.Redact("no secrets here"));
        AssertEqual("Authorization: <redacted>", LogRedactor.Redact("Authorization: Bearer abc123"));
        AssertEqual("authorization: <redacted>", LogRedactor.Redact("authorization: basic user:pass"));
        AssertEqual("Bearer <redacted>", LogRedactor.Redact("Bearer xyz"));
        AssertEqual("password=<redacted>", LogRedactor.Redact("password=hunter2"));
        AssertEqual("refresh_token: <redacted>", LogRedactor.Redact("refresh_token: abc"));
        AssertEqual("api_key=<redacted>", LogRedactor.Redact("api_key=zzz"));
        AssertEqual("password <redacted>", LogRedactor.Redact("password hunter2"));
        AssertEqual("https://x/y?code=<redacted>&x=1", LogRedactor.Redact("https://x/y?code=abc&x=1"));
        AssertEqual("https://x/y?sig=<redacted>", LogRedactor.Redact("https://x/y?sig=deadbeef"));
    }

    internal static void ObserversSeeAppendsAndNeverBreakWrites()
    {
        RecordingLogObserver recorder = new();
        LogService service = new(capacity: 16, recorder);
        service.Write(LogLevel.Info, "obs", "one");
        service.Write(LogLevel.Info, "obs", "two");

        AssertTrue(recorder.Changes.Count >= 2);
        XsrStateChange last = recorder.Changes[^1];
        AssertEqual(LogService.EntriesKey, last.SemanticId);
        AssertEqual(XsrStateKind.Collection, last.Kind);
        AssertEqual(XsrStateChangeReason.CollectionDeltaApplied, last.Reason);

        LogService hostile = new(capacity: 16, new ThrowingLogObserver());
        hostile.Write(LogLevel.Error, "obs", "must not throw");
        AssertEqual(1, hostile.GetSnapshot().Count);
    }

    internal static void TimestampsComeFromTimeProvider()
    {
        DateTimeOffset now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        LogService service = new(clock: new FixedClock(now));
        service.Write(LogLevel.Info, "clock", "when");
        IReadOnlyList<LogEntry> snapshot = service.GetSnapshot();
        AssertEqual(1, snapshot.Count);
        AssertEqual(now, snapshot[0].Timestamp);
    }

    internal static void DisplayTextMatchesLegacyFormat()
    {
        DateTimeOffset local = new(
            2026, 1, 2, 10, 20, 30, 123,
            TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 1, 2, 10, 20, 30)));
        LogEntry plain = new(1, local, LogLevel.Info, "Launch", "started", null);
        AssertEqual("[10:20:30.123] [Info] [Launch] started", plain.ToDisplayText());

        LogEntry withException = new(2, local, LogLevel.Error, "Launch", "failed", "at start");
        AssertEqual($"[10:20:30.123] [Error] [Launch] failed{Environment.NewLine}at start", withException.ToDisplayText());

        LogEntry blankException = new(3, local, LogLevel.Warn, "m", "msg", "   ");
        AssertEqual("[10:20:30.123] [Warn] [m] msg", blankException.ToDisplayText());
    }

    internal static void ConcurrentWritesKeepSequenceAndOrder()
    {
        LogService service = new();
        service.MaximumLevel = LogLevel.RealTime;
        const int writers = 8;
        const int perWriter = 25;

        Parallel.For(0, writers, writer =>
        {
            for (int index = 0; index < perWriter; index++)
            {
                service.Write(LogLevel.RealTime, "par", $"w{writer} i{index}");
            }
        });

        IReadOnlyList<LogEntry> snapshot = service.GetSnapshot();
        AssertEqual(writers * perWriter, snapshot.Count);
        AssertEqual(writers * perWriter, snapshot.Select(static entry => entry.Sequence).Distinct().Count());
        long[] sequences = [.. snapshot.Select(static entry => entry.Sequence)];
        long[] ordered = [.. sequences];
        Array.Sort(ordered);
        AssertTrue(sequences.SequenceEqual(ordered));
        AssertTrue(ordered[^1] == writers * perWriter);
    }
}

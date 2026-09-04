using PCL.Services.Logging;
using PCL.Xsr.State;

namespace PCL.Services.Tests;

// XSR-711: mirrors outside the state ring — console when attached, file always, and neither
// sink may break the log operation.
internal static partial class Program
{
    private static void FileSinkAppendsAndSurvivesIoErrors()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nexa-log-sink-test", Guid.NewGuid().ToString("N"));
        FileLogSink sink = new(Path.Combine(directory, "launcher.log"));
        LogEntry entry = new(
            Sequence: 1,
            Timestamp: new DateTimeOffset(2026, 1, 1, 18, 36, 42, TimeSpan.Zero),
            Level: LogLevel.Info,
            Module: "Launch",
            Message: "hello",
            ExceptionText: null);
        sink.Write(entry, entry.ToDisplayText());
        sink.Write(entry, entry.ToDisplayText());
        sink.Dispose();

        string text = File.ReadAllText(Path.Combine(directory, "launcher.log"));
        AssertEqual(2, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        AssertTrue(text.Contains("[Info] [Launch] hello", StringComparison.Ordinal));

        // A disposed sink stops writing instead of throwing or reopening the file.
        sink.Write(entry, "after dispose");
        AssertTrue(File.ReadAllText(Path.Combine(directory, "launcher.log"))
            .Contains("after dispose", StringComparison.Ordinal) == false);
    }

    private static void ConsoleSinkDisablesInsteadOfThrowing()
    {
        ConsoleLogSink sink = new();
        LogEntry entry = new(
            Sequence: 1,
            Timestamp: DateTimeOffset.UtcNow,
            Level: LogLevel.Info,
            Module: "Test",
            Message: "console line",
            ExceptionText: null);

        // Whatever the console state is (attached, redirected, or detached GUI), the write
        // must not throw; repeated writes stay safe after the sink disables itself.
        sink.Write(entry, entry.ToDisplayText());
        sink.Write(entry, entry.ToDisplayText());
    }

    // XSR-711: the level policy — release defaults to Info (manual log points and lifecycle
    // visible), the verbose tiers record only when the maximum level is raised, and the
    // ergonomic helpers route to the documented tiers.
    private static void LevelGatePolicyHoldsAcrossTiers()
    {
        XsrStateStoreBuilder builder = new();
        LogService.DeclareState(builder);
        LogService log = new(builder.Build());
        XsrStateStore store = log.StateStore;

        // Default gate: Info records, Trace does not, and the verbose flag stays off.
        log.Info("Gate", "visible");
        log.Trace("Gate", "hidden");
        AssertFalse(log.VerboseEnabled);
        IReadOnlyList<LogEntry> entries = store.ReadCollection<LogEntry>(
            store.Resolve(LogService.EntriesKey)).Items;
        AssertEqual(1, entries.Count);
        AssertEqual(LogLevel.Info, entries[0].Level);

        // Raised gate: everything records, including loop-tier Trace.
        log.MaximumLevel = LogLevel.RealTime;
        AssertTrue(log.VerboseEnabled);
        log.Trace("Gate", "now visible");
        log.Debug("Gate", "debug visible");
        entries = store.ReadCollection<LogEntry>(store.Resolve(LogService.EntriesKey)).Items;
        AssertTrue(entries.Any(entry => entry.Message.Contains("now visible", StringComparison.Ordinal)));
        AssertTrue(entries.Any(entry => entry.Level == LogLevel.Debug));
    }
}

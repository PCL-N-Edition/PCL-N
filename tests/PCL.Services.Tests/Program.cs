namespace PCL.Services.Tests;

internal static partial class Program
{
    private static readonly (string Name, Func<ValueTask> Body)[] TestCases =
    [
        // XSR-501: settings capability contract.
        ("schema defaults are visible and available", Sync(SchemaDefaultsAreVisibleAndAvailable)),
        ("set then get round trips every type", SetThenGetRoundTripsEveryType),
        ("unknown keys are rejected stably", UnknownKeyIsRejectedStably),
        ("type mismatches are rejected stably", TypeMismatchIsRejectedStably),
        ("invalid values are rejected stably", InvalidValuesAreRejectedStably),
        ("set persists and survives restart", SetPersistsAndSurvivesRestart),
        ("corrupt and unknown persisted entries are skipped", CorruptAndUnknownPersistedEntriesAreSkipped),
        ("failed save returns a stable error and mutates nothing", FailedSaveReturnsStableErrorAndMutatesNothing),
        ("failed load keeps defaults but marks unavailable", FailedLoadKeepsDefaultsButMarksUnavailable),
        ("reset value and reset all restore defaults", ResetValueAndResetAllRestoreDefaults),
        ("the state observer sees every applied change", StateObserverSeesEveryAppliedChange),
        ("the file port round trips and skips malformed lines", FilePortRoundTripsAndSkipsMalformedLines),
        ("the file port writes sorted ordinal entries", FilePortWritesSortedOrdinalEntries),
        ("doubles round trip through full precision", DoubleRoundTripsThroughFullPrecision),
        // XSR-502: logging capability contract.
        ("log level gate defaults to info and falls back stably", Sync(LogLevelGateDefaultsToInfoAndFallsBackStably)),
        ("modules are normalized before storage", Sync(ModulesAreNormalizedBeforeStorage)),
        ("messages and exceptions are redacted before storage", Sync(MessagesAndExceptionsAreRedactedBeforeStorage)),
        ("the ring evicts oldest beyond capacity", Sync(RingEvictsOldestBeyondCapacity)),
        ("the state collection mirrors the snapshot", Sync(StateCollectionMirrorsSnapshot)),
        ("clear empties the ring and state", Sync(ClearEmptiesRingAndState)),
        ("the redactor covers the legacy secret patterns", Sync(RedactorCoversLegacySecretPatterns)),
        ("observers see appends and never break writes", Sync(ObserversSeeAppendsAndNeverBreakWrites)),
        ("timestamps come from the time provider", Sync(TimestampsComeFromTimeProvider)),
        ("display text matches the legacy format", Sync(DisplayTextMatchesLegacyFormat)),
        ("concurrent writes keep sequence and order", Sync(ConcurrentWritesKeepSequenceAndOrder)),
    ];

    private static async Task<int> Main()
    {
        foreach ((string name, Func<ValueTask> body) in TestCases)
        {
            await body().ConfigureAwait(false);
            Console.WriteLine($"PASS: {name}");
        }

        Console.WriteLine($"Services tests passed: {TestCases.Length}.");
        return 0;
    }

    private static Func<ValueTask> Sync(Action action) => () =>
    {
        action();
        return ValueTask.CompletedTask;
    };

    internal static void AssertTrue(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException("Expected true but received false.");
        }
    }

    internal static void AssertFalse(bool value) => AssertTrue(!value);

    internal static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}' but received '{actual}'.");
        }
    }
}

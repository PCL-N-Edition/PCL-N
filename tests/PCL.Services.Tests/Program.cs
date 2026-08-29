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
        ("null values are rejected and text is port-agnostic", NullValuesAreRejectedAndTextIsPortAgnostic),
        ("set persists and survives restart", SetPersistsAndSurvivesRestart),
        ("corrupt and unknown persisted entries are skipped", CorruptAndUnknownPersistedEntriesAreSkipped),
        ("failed save returns a stable error and mutates nothing", FailedSaveReturnsStableErrorAndMutatesNothing),
        ("failed load keeps defaults but marks unavailable", FailedLoadKeepsDefaultsButMarksUnavailable),
        ("reset value and reset all restore defaults", ResetValueAndResetAllRestoreDefaults),
        ("the state observer sees every applied change", StateObserverSeesEveryAppliedChange),
        ("the file port round trips and skips malformed lines", FilePortRoundTripsAndSkipsMalformedLines),
        ("the file port writes sorted ordinal entries", FilePortWritesSortedOrdinalEntries),
        ("the line port rejects unrepresentable values", LinePortRejectsUnrepresentableValues),
        // XSR-503: launcher settings file compatibility.
        ("the launcher schema matches the legacy defaults", LauncherSchemaMatchesLegacyDefaults),
        ("the json port round trips the legacy shape", JsonPortRoundTripsLegacyShape),
        ("the json port writes legacy fixed field defaults on fresh save", JsonPortWritesFreshFixedFieldDefaults),
        ("the json port quarantines an unsupported schema", JsonPortQuarantinesUnsupportedSchema),
        ("the json port recovers invalid items and quarantines", JsonPortRecoversInvalidItems),
        ("the json port treats a missing file as empty", JsonPortMissingFileIsEmpty),
        ("the json port preserves unknown fields and keys", JsonPortPreservesUnknownContent),
        ("settings over the json port persist end to end", SettingsOverJsonPortEndToEnd),
        // XSR-504: download capability contract.
        ("downloads fail over across sources", DownloadFailsOverAcrossSources),
        ("downloads fail when every source fails", DownloadFailsWhenEverySourceFails),
        ("writer temp files survive for resume", WriterTempFileSurvivesForResume),
        ("concurrent downloaders share one transfer", ConcurrentDownloadersShareOneTransfer),
        ("progress stages flow in order", ProgressStagesFlowInOrder),
        ("throwing progress handlers keep the transfer alive", ThrowingProgressHandlerKeepsTransferAlive),
        ("cancellation rejects the transfer", CancellationRejectsTheTransfer),
        ("segmented downloads assemble parallel parts", SegmentedDownloadAssemblesParallelParts),
        ("segmented falls back to single stream when unsupported", SegmentedFallsBackToSingleStreamWhenUnsupported),
        ("segmented falls back for files below the segment floor", SegmentedFallsBackForFilesBelowTheSegmentFloor),
        ("segmented range mismatches fail over to the next source", SegmentedRangeMismatchFailsOverToNextSource),
        ("segmented truncated sources fail over to the next source", SegmentedTruncatedSourceFailsOverToNextSource),
        ("segmented progress reaches completed at full length", SegmentedProgressReachesCompletedAtFullLength),
        // XSR-506: account capability contract.
        ("launch profiles round trip the legacy json shape", ProfilePortRoundTripsLegacyJsonShape),
        ("the profile port quarantines unreadable files", ProfilePortQuarantinesUnreadableFiles),
        ("profiles persist across restarts", ProfilesPersistAcrossRestarts),
        ("invalid profiles and indexes are rejected stably", InvalidProfilesAndIndexesAreRejectedStably),
        ("failed saves change nothing observable", FailedSavesChangeNothingObservable),
        // XSR-507: update block data contracts.
        ("chunk profiles match the legacy bounds", ChunkProfilesMatchLegacyBounds),
        ("chunking is deterministic and covers the file", ChunkerIsDeterministicAndCoversTheFile),
        ("block codec normalizes and detects codecs", BlockCodecNormalizesAndDetectsCodecs),
        ("block codec round trips and verifies both codecs", BlockCodecRoundTripsAndVerifiesBothCodecs),
        ("block codec detects mismatched declarations", BlockCodecDetectsMismatchedDeclaration),
        ("the local block index round trips the installed map", LocalBlockIndexRoundTripsTheInstalledMap),
        ("the local block index verifies before reusing chunks", LocalBlockIndexVerifiesBeforeReusingChunks),
        ("the local block index reads verified windows", LocalBlockIndexReadsVerifiedWindows),
        // One-way upgrade policy.
        ("the legacy one-four line crosses into two point zero", Sync(LegacyOneFourCrossesIntoTwoPointZero)),
        ("alpha beta stable ordering is monotonic", Sync(AlphaBetaStableOrderingIsMonotonic)),
        ("the same version is a no-op", Sync(SameVersionIsANoOp)),
        ("ci builds hop by commit", Sync(CiBuildsHopByCommit)),
        ("unrecognized versions are refused", Sync(UnrecognizedVersionsAreRefused)),
        ("version parsing normalizes legacy shapes", Sync(VersionParsingNormalizesLegacyShapes)),
        // XSR-509: update package planning.
        ("the patch index deserializes the legacy json", Sync(PatchIndexDeserializesLegacyJson)),
        ("build identity normalizes variants and configurations", Sync(BuildIdentityNormalizesVariantsAndConfigurations)),
        ("full package composition matches layout and channel", Sync(FullPackageCompositionMatchesLayoutAndChannel)),
        ("direct patch paths fall back to release asset urls", Sync(DirectPatchPathIsPlannedWithAssetUrlFallback)),
        ("cheaper patch kinds win and unworthwhile chains fall back", Sync(CheaperPatchKindWinsAndUnworthwhileChainsFallBackToFull)),
        ("unusable indexes return null for full fallback", Sync(UnusableIndexesReturnNullForFullFallback)),
        ("variant matching normalizes runtime variants", Sync(VariantMatchingNormalizesRuntimeVariants)),
        ("multi-hop paths are planned across indexes", Sync(MultiHopPathIsPlannedAcrossIndexes)),
        ("block map windows and baseline gate the shortcuts", Sync(BlockMapWindowsAndBaselineGateTheShortcuts)),
        // XSR-510: update discovery and transport.
        ("index fetch follows preference and fallback rules", IndexFetchFollowsPreferenceAndFallbackRules),
        ("the github fallback url is tried only when enabled", GithubFallbackUrlIsTriedOnlyWhenEnabled),
        ("eligibility gates before any network", EligibilityGatesBeforeAnyNetwork),
        ("baseline and single file skip index fetch", BaselineAndSingleFileSkipIndexFetch),
        ("the multi-tag walk loads previous indexes until a path is found", MultiTagWalkLoadsPreviousIndexesUntilPathFound),
        ("the walk stops when the previous target is not newer", WalkStopsWhenPreviousTargetIsNotNewer),
        ("head failures fall back to the index archive size", HeadFailureFallsBackToIndexArchiveSize),
        // XSR-511: signature and delta codecs.
        ("vcdiff decodes add copy and run instructions", Sync(VcdiffDecodesAddCopyAndRunInstructions)),
        ("vcdiff rejects unsupported and corrupt deltas", Sync(VcdiffRejectsUnsupportedAndCorruptDeltas)),
        ("gpg verifier accepts a genuine detached signature", GpgVerifierAcceptsGenuineDetachedSignature),
        ("gpg verifier rejects tampered foreign and unpinned keys", GpgVerifierRejectsTamperedForeignAndUnpinnedKeys),
        // XSR-512: staged install core.
        ("staged tree verification rejects mismatches", Sync(StagedTreeVerificationRejectsMismatches)),
        ("flattening collapses single package wrapper roots", Sync(FlattenSingleRootCollapsesWrapperFolders)),
        ("plan building inventories managed leftovers", Sync(BuildPlanInventoriesManagedLeftovers)),
        ("applying a plan places files and runs deletes", Sync(ApplyPlanPlacesFilesAndRunsDeletes)),
        ("unsafe paths are refused everywhere", Sync(UnsafePathsAreRefusedEverywhere)),
        // XSR-513: online account flows.
        ("the microsoft device login runs the full chain", MicrosoftDeviceLoginRunsTheFullChain),
        ("microsoft declined and expired are distinct errors", MicrosoftDeclinedAndExpiredAreDistinctErrors),
        ("microsoft refresh runs the chain without a device code", MicrosoftRefreshRunsTheChainWithoutDeviceCode),
        ("yggdrasil authenticate validate and refresh run", YggdrasilAuthenticateValidateAndRefreshRun),
        ("yggdrasil failures surface the server message", YggdrasilFailureSurfacesServerMessage),
        ("yggdrasil server normalization and jwt expiry work", Sync(YggdrasilServerNormalizationAndJwtExpiry)),
        ("login results feed the persisted roster", LoginResultsFeedThePersistedRoster),
        // XSR-514: LittleSkin OAuth and appearance services.
        ("the littleskin device flow runs to tokens", LittleSkinDeviceFlowRunsToEnd),
        ("littleskin token paths and invalid client behave", LittleSkinTokenPathsAndInvalidClient),
        ("littleskin profiles session closet and apply run", LittleSkinProfilesSessionClosetAndApply),
        ("littleskin texture upload uses the minecraft token", LittleSkinTextureUploadUsesMinecraftToken),
        ("microsoft skin upload parses the active texture", MicrosoftSkinUploadParsesActiveTexture),
        ("microsoft cape service lists and activates", MicrosoftCapeServiceListsAndActivates),
        ("transfer state mirrors active downloads", TransferStateMirrorsActiveDownloads),
        ("the file writer rejects short resume offsets", FileDownloadWriterRejectsShortResume),
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

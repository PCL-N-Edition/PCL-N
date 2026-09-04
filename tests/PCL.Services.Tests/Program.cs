namespace PCL.Services.Tests;

internal static partial class Program
{
    private static readonly (string Name, Func<ValueTask> Body)[] TestCases =
    [
        ("operation breadcrumbs retain stage source and one terminal outcome", Sync(OperationBreadcrumbsKeepStageSourceAndOneOutcome)),
        ("diagnostic redaction covers quoted secrets and device codes", Sync(DiagnosticRedactionCoversQuotedSecretsAndDeviceCodes)),
        ("foundation startup failures reach configured sinks", Sync(FoundationStartupFailuresReachConfiguredSinks)),
        ("settings diagnostics hide values and explain durable failure", Sync(SettingsDiagnosticsHideValuesAndExplainDurableFailure)),
        ("production routes and login workers emit diagnostics", ProductionRoutesAndLoginWorkersEmitDiagnostics),
        ("download commit failures identify the failed stage", DownloadCommitFailureLogsItsStage),
        ("native preparation failures log before process start", NativePreparationFailureLogsBeforeProcessStart),
        ("HTTP diagnostics exclude secrets and preserve responses", HttpDiagnosticsExcludeSecretsAndPreserveResponses),
        ("dispatch start observers cannot break handlers", DispatchStartObserversCannotBreakHandlers),
        ("profile skins resolve explicit and session textures without credentials", ProfileSkinsResolveExplicitAndSessionTextures),
        ("removed profiles reject late skin responses", RemovedProfilesRejectLateSkinResponses),
        ("profile imports validate deduplicate and persist before publication", Sync(ProfileImportIsValidatedDeduplicatedAndDurable)),
        ("legacy import never repairs or writes source data", LegacyImportNeverRepairsOrWritesSource),
        ("account authorization addresses stay on provider origins", Sync(AccountAuthorizationUrlsStayOnProviderOrigins)),
        // XSR-501: settings capability contract.
        ("schema defaults are visible and available", Sync(SchemaDefaultsAreVisibleAndAvailable)),
        ("set then get round trips every type", SetThenGetRoundTripsEveryType),
        ("raw values round trip every declared type", RawValuesRoundTripEveryDeclaredType),
        ("unknown keys are rejected stably", UnknownKeyIsRejectedStably),
        ("type mismatches are rejected stably", TypeMismatchIsRejectedStably),
        ("null values are rejected and text is port-agnostic", NullValuesAreRejectedAndTextIsPortAgnostic),
        ("set persists and survives restart", SetPersistsAndSurvivesRestart),
        ("corrupt and unknown persisted entries are skipped", CorruptAndUnknownPersistedEntriesAreSkipped),
        ("failed save returns a stable error and mutates nothing", FailedSaveReturnsStableErrorAndMutatesNothing),
        ("failed load keeps defaults but marks unavailable", FailedLoadKeepsDefaultsButMarksUnavailable),
        ("reset value and reset all restore defaults", ResetValueAndResetAllRestoreDefaults),
        ("reset all with a failed save mutates nothing", ResetAllFailedSaveMutatesNothing),
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
        ("provider id equality is case insensitive", Sync(ProviderIdEqualityIsCaseInsensitive)),
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
        // XSR-706: product launch orchestration inputs.
        ("offline identity falls back to the vanilla uuid", Sync(OfflineIdentityFallsBackToVanillaUuid)),
        ("launch coordinator builds a complete low level request", Async(LaunchCoordinatorBuildsCompleteLowLevelRequest)),
        ("launch coordinator rejects incomplete inheritance", Async(LaunchCoordinatorRejectsIncompleteInheritance)),
        ("production minecraft runtime registers product start", Sync(ProductionMinecraftRuntimeRegistersStartRoute)),
        // XSR-519: Wave 5 acceptance integration.
        ("foundation composition end to end", FoundationCompositionEndToEnd),
        ("foundation downloads use composed logging", FoundationDownloadsUseComposedLogging),
        ("file sink appends and survives io errors", Sync(FileSinkAppendsAndSurvivesIoErrors)),
        ("console sink disables instead of throwing", Sync(ConsoleSinkDisablesInsteadOfThrowing)),
        ("level gate policy holds across tiers", Sync(LevelGatePolicyHoldsAcrossTiers)),
        // XSR-712: launch progress narration.
        ("launch stage weights match the legacy table", Sync(LaunchStageWeightsMatchLegacyTable)),
        ("progress publisher writes coherent cells", Sync(ProgressPublisherWritesCoherentCells)),
        ("cancel active launch without launch returns false", Sync(CancelActiveLaunchWithoutLaunchReturnsFalse)),
        ("launch pipeline narrates stages and reaches launched", LaunchPipelineNarratesStagesAndReachesLaunchedAsync),
        ("cross capability page has no state id collisions", CrossCapabilityPageHasNoStateIdCollisions),
        // XSR-513: online account flows.
        ("the microsoft device login runs the full chain", MicrosoftDeviceLoginRunsTheFullChain),
        ("microsoft declined and expired are distinct errors", MicrosoftDeclinedAndExpiredAreDistinctErrors),
        ("microsoft refresh runs the chain without a device code", MicrosoftRefreshRunsTheChainWithoutDeviceCode),
        ("yggdrasil authenticate validate and refresh run", YggdrasilAuthenticateValidateAndRefreshRun),
        ("yggdrasil failures surface the server message", YggdrasilFailureSurfacesServerMessage),
        ("yggdrasil server normalization and jwt expiry work", Sync(YggdrasilServerNormalizationAndJwtExpiry)),
        ("login results feed the persisted roster", LoginResultsFeedThePersistedRoster),
        // XSR-516: payload extraction and patch orchestration.
        ("zip payloads extract with traversal refusal", Sync(ZipPayloadsExtractWithTraversalRefusal)),
        ("tar payloads extract with modes", TarPayloadsExtractWithModes),
        ("the hpatchz tool runs through the process port", HpatchzToolRunsThroughTheProcessPort),
        ("binary patch chains verify download and apply", BinaryPatchChainsVerifyDownloadAndApply),
        ("scatter ops produce a verified staged tree", ScatterOpsProduceAVerifiedStagedTree),
        // XSR-514: LittleSkin OAuth and appearance services.
        ("the littleskin device flow runs to tokens", LittleSkinDeviceFlowRunsToEnd),
        ("littleskin token paths and invalid client behave", LittleSkinTokenPathsAndInvalidClient),
        ("littleskin profiles session closet and apply run", LittleSkinProfilesSessionClosetAndApply),
        ("littleskin texture upload uses the minecraft token", LittleSkinTextureUploadUsesMinecraftToken),
        ("microsoft skin upload parses the active texture", MicrosoftSkinUploadParsesActiveTexture),
        ("microsoft cape service lists and activates", MicrosoftCapeServiceListsAndActivates),
        // XSR-517: Network and Telemetry families.
        ("network probes report reachability and latency", NetworkProbesReportReachabilityAndLatency),
        ("telemetry without consent records nothing", Sync(TelemetryWithoutConsentRecordsNothing)),
        ("telemetry buffers with bounded eviction", Sync(TelemetryBuffersWithBoundedEviction)),
        ("telemetry flush uploads and clears or retains", TelemetryFlushUploadsAndClearsOrRetains),
        ("telemetry batch serialization is stable", Sync(TelemetryBatchSerializationIsStable)),
        // XSR-518: helper hand-off and restart scheduling.
        ("replacement process arguments follow the helper contract", Sync(ReplacementProcessArgumentsFollowTheHelperContract)),
        ("the scheduler validates artifacts before launch", Sync(SchedulerValidatesArtifactsBeforeLaunch)),
        ("staged path helpers sanitize versions", Sync(StagedPathHelpersSanitizeVersions)),
        // XSR-606: Wave 6 acceptance hardening.
        ("conflicting java ranges are rejected", Sync(ConflictingJavaRangesAreRejected)),
        ("overlapping java ranges narrow correctly", Sync(OverlappingJavaRangesNarrowCorrectly)),
        ("minecraft java gates use normalized coordinates", Sync(MinecraftJavaGatesUseNormalizedCoordinates)),
        ("minecraft Java era matrix matches the release line", Sync(MinecraftJavaEraMatrixMatchesReleaseLine)),
        ("calendar Minecraft versions retain their scheme", Sync(MinecraftCalendarVersionsRetainTheirScheme)),
        ("manifest Java metadata is authoritative", Sync(ManifestJavaMetadataIsAuthoritative)),
        ("minecraft 1.16.5 never selects Java 7", Minecraft1165NeverSelectsJava7),
        ("minecraft 1.20.1 never selects Java 8", Minecraft1201NeverSelectsJava8),
        ("modern jvm tokens all resolve", ModernJvmTokensAllResolve),
        ("unresolved launch token fails planning", UnresolvedLaunchTokenFailsPlanning),
        ("unknown jvm token fails planning", UnknownJvmTokenFailsPlanning),
        ("vanilla launch contains client jar", VanillaLaunchContainsClientJar),
        ("inherited launch uses base client jar when provided", InheritedLaunchUsesBaseClientJarWhenProvided),
        ("inherited launch resolves base client jar automatically", InheritedLaunchResolvesBaseClientJarAutomatically),
        ("library artifact and native classifier both resolve", Sync(MinecraftLibraryArtifactAndNativeClassifierBothResolve)),
        ("system GLFW keeps the ordinary artifact", Sync(SystemGlfwKeepsOrdinaryArtifact)),
        ("system GLFW drops the native classifier", Sync(SystemGlfwDropsNativeClassifier)),
        ("arm64 native is not on classpath", Arm64NativeIsNotOnClasspath),
        ("natives are extracted before launch", NativesAreExtractedBeforeLaunch),
        ("mojang rule order matches manifest semantics", Sync(MojangRuleOrderMatchesManifestSemantics)),
        ("os version uses regex", Sync(OsVersionUsesRegex)),
        ("absent false feature matches", Sync(AbsentFalseFeatureMatches)),
        ("minecraft library rules use shared evaluator", Sync(MinecraftLibraryRulesUseSharedEvaluator)),
        ("minecraft launch route stages natives before process start", MinecraftLaunchRouteStagesNativesBeforeProcessStart),
        ("minecraft download paths reject manifest traversal", Sync(MinecraftDownloadPathsRejectManifestTraversal)),
        ("canonical corpus produces consistent launch snapshots", CanonicalCorpusProducesConsistentLaunchSnapshots),
        ("process session publishes into host store", ProcessSessionPublishesIntoHostStore),
        // XSR-515: File capability.
        ("the folder tree resolves canonical names", Sync(FolderTreeResolvesCanonicalNames)),
        ("traversal is refused on every port operation", Sync(TraversalIsRefusedOnEveryPortOperation)),
        ("text and bytes round trip atomically", TextAndBytesRoundTripAtomically),
        ("size cap and default root resolution behave", SizeCapAndDefaultRootResolutionBehave),
        ("symlink escapes are rejected", Sync(SymlinkEscapeIsRejected)),
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
        // XSR-601: Minecraft version and instance discovery.
        ("minecraft version classifier matches canonical aliases", Sync(MinecraftVersionClassifierMatchesCanonicalAliases)),
        ("minecraft version discovery uses stable safe resolution", Sync(MinecraftVersionDiscoveryUsesStableSafeResolution)),
        ("minecraft instance metadata round trips atomically", MinecraftInstanceMetadataRoundTripsAtomically),
        ("minecraft Java selection honors manifest and availability", MinecraftJavaSelectionHonorsManifestAndAvailability),
        ("minecraft assets resolve canonical object paths", Sync(MinecraftAssetsResolveCanonicalObjectPaths)),
        ("minecraft libraries and classpath honor rules", Sync(MinecraftLibrariesAndClasspathHonorRules)),
        ("minecraft ModLoader and launch plan are deterministic", Sync(MinecraftModLoaderAndLaunchPlanAreDeterministic)),
        ("minecraft crash analysis and dependency parsing are structured", Sync(MinecraftCrashAnalysisAndDependencyParsingAreStructured)),
        ("minecraft runtime composition registers routes", MinecraftRuntimeCompositionRegistersRoutes),
        ("minecraft Java runtime package planner validates manifest", MinecraftJavaRuntimePackagePlannerValidatesManifest),
        ("minecraft download planners respect existing files", Sync(MinecraftDownloadPlannersRespectExistingFiles)),
        ("minecraft Java preference parser preserves legacy semantics", Sync(MinecraftJavaPreferenceParserPreservesLegacySemantics)),
        ("minecraft libraries use ARM64 compatibility artifacts", Sync(MinecraftLibrariesUseArm64CompatibilityArtifacts)),
        ("minecraft launch plan merges inherited and modern arguments", Sync(MinecraftLaunchPlanMergesInheritedAndModernArguments)),
        ("minecraft download source planner covers official and unlisted mirrors", Sync(MinecraftDownloadSourcePlannerCoversOfficialAndUnlistedMirrors)),
        ("minecraft Java runtime installer verifies and installs", MinecraftJavaRuntimeInstallerVerifiesAndInstalls),
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

    private static Func<ValueTask> Async(Func<Task> action) => async () =>
    {
        await action().ConfigureAwait(false);
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

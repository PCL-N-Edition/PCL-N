using PCL.Desktop.Ui;
using PCL.Services.Accounts;
using PCL.Services.Composition;
using PCL.Services.Foundation;
using PCL.Services.Minecraft;
using PCL.Services.Minecraft.Launch;
using PCL.Services.Minecraft.Process;
using PCL.Services.Settings;
using PCL.UI.Next;
using PCL.Xsr;
using PCL.Xsr.Runtime;
using PCL.Xsr.State;

namespace PCL.Desktop.Tests;

internal static partial class Program
{
    public static void Main()
    {
        foreach ((string name, Action body) in TestCases)
        {
            body();
            Console.WriteLine($"PASS: {name}");
        }

        Console.WriteLine($"Desktop composition tests passed: {TestCases.Length}.");
    }

    private static readonly (string Name, Action Body)[] TestCases =
    [
        ("imported profiles remain visible when the picker reopens", ImportedProfilesRemainVisibleWhenPickerReopens),
        ("account forms share one header and create offline profiles", AccountFormsUseOneHeaderAndCreateOfflineProfiles),
        ("Microsoft onboarding reuses services and rejects late cancelled completion", MicrosoftOnboardingUsesServiceAndDiscardsLateCancellation),
        ("third-party onboarding masks passwords and uses the chosen server", ThirdPartyOnboardingMasksPasswordsAndUsesConfiguredServer),
        ("LittleSkin onboarding selects characters with separate token kinds", LittleSkinOnboardingChoosesCharacterAndKeepsTokenKindsSeparate),
        ("account failures and stale file pickers stay in the current view", AccountFailuresAndLateFilePickerStayInCurrentView),
        ("launch page replicates the legacy card layout with bound facts", LaunchPageReplicatesLegacyLayout),
        ("launch page matches legacy geometry across wide, default, and minimum windows", LaunchPageMatchesLegacyGeometry),
        ("navigation intents route between launch and placeholder pages", NavigationIntentsRouteBetweenPages),
        ("download and instance actions route to version management", DownloadAndInstanceActionsRouteToVersionManagement),
        ("launch page semantics never expose internal entity keys", LaunchPageSemanticsNeverExposeInternalKeys),
        ("instance scan publishes state without mutating the tree from its worker", InstanceScanPublishesWithoutForeignTreeMutation),
        ("an older instance scan cannot overwrite the latest generation", OlderInstanceScanCannotOverwriteLatestGeneration),
        ("launch primary dispatches the product start command", LaunchPrimaryDispatchesProductStartCommand),
        ("expand navigation never replaces the launch page", ExpandNavigationNeverReplacesTheLaunchPage),
        ("account card lists profiles and switches the selection", AccountCardListsProfilesAndSwitchesSelection),
        ("rail animation retains content and version card containment", RailAnimationRetainsContentAndCardContainment),
        ("account roster publication stays on the render thread and preserves rows", AccountRosterUpdatesAtFrameBoundary),
        ("long account rosters scroll within the card", AccountRosterScrollsWithinCard),
        ("selected profile is used by the product launch route", SelectedProfileIsUsedByLaunch),
        ("unavailable launch cannot be invoked by pointer keyboard or automation", UnavailableLaunchCannotBeInvoked),
        ("operation log dispatch success traces at debug", DispatchSuccessLogsDebugTrace),
        ("operation log dispatch failure logs warn with code", DispatchFailureLogsWarnWithCode),
        ("operation log state logs real time but quiet domains stay silent", StateChangesLogRealTimeButQuietDomainsStaySilent),
        ("operation log composite fans out to both observers", CompositeStateObserverFansOutToBothObservers),
        ("operation log lifecycle and scheduler log at their tiers", LifecycleAndSchedulerLogAtTheirTiers),
        // XSR-712: launching overlay.
        ("launch overlay shows reset facts when launch starts", LaunchOverlayShowsResetFactsWhenLaunchStarts),
        ("launch overlay narrates progress cells", LaunchOverlayNarratesProgressCells),
        ("launch overlay closes on failure", LaunchOverlayClosesOnFailure),
        ("launch overlay cancel hides overlay", LaunchOverlayCancelHidesOverlay),
        ("launch overlay prompts before the java download", LaunchOverlayPromptsBeforeJavaDownload),
        ("version subpages have independent routes and restore navigation focus", VersionSubpagesHaveIndependentRoutesAndRestoreFocus),
        ("pointer focus does not draw keyboard focus rings", PointerFocusDoesNotDrawKeyboardFocusRings),
        ("capsules occupy their presented width and remain beside the version name", CapsulesOccupyPresentedWidth),
        ("launch widgets preserve original content and page through real intents", LaunchWidgetsPreserveOriginalContent),
        ("profile presentation uses Apple hierarchy inside the experimental layout", ProfilePresentationUsesAppleHierarchy),
        ("account capsules and wardrobe navigation preserve geometry", AccountCapsulesAndWardrobeRoutePreserveGeometry),
        ("capsules ignore geometry-only hover moves", CapsulesIgnoreGeometryOnlyHoverMoves),
        ("navigation retains outgoing layers and live hit geometry", NavigationMotionHasOutgoingLayersAndLiveHitGeometry),
        ("skin route publishes media through host state into the rendered profile", SkinRoutePublishesIntoRenderedProfile),
        ("delete actions persist only the requested profile and reject stale rows", DeleteActionsPersistAndRejectStaleRows),
        ("trivia rotates every three seconds without foreign tree writes and stops on disposal", TriviaTimerPublishesOnlyStateAndStops),
        ("operational feedback uses the shared lower-left notification surface", OperationalFeedbackUsesLowerLeftNotification),
        ("notification levels keep exact lifetimes and permanent errors remain closable", NotificationLevelsKeepExactLifetimes),
        ("notifications share one lower-left surface and every level closes manually", NotificationsShareLowerLeftSurfaceAndCloseManually),
        ("notification summaries open complete one-action scrollable dialogs", NotificationSummaryOpensCompleteScrollableDialog),
        ("notification overflow remains bottom-pinned and recovers without disappearing", NotificationOverflowRecoversWithoutDisappearing),
        ("closing notifications leave stack flow before their exit settles", ClosingNotificationReflowsImmediately),
        ("notification timers request render without mutating the UI tree", NotificationTimersStayOffTheRenderTree),
        ("dialog stays inside the window traps the page and restores focus on escape", DialogStaysInsideWindowAndRestoresFocus),
    ];

    private static void LaunchPageReplicatesLegacyLayout()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        XsrUiScene scene = fixture.Shell.Render(new XsrUiSize(1280, 800));

        AssertEqual("下载游戏", FindByKey(fixture.Shell, scene, "LaunchButton").Text);
        AssertTrue(FindByKey(fixture.Shell, scene, "LaunchButton").IsClickable);
        AssertEqual("账户", FindByKey(fixture.Shell, scene, "AccountHeader").Text);
        AssertFalse(HasKey(fixture.Shell, scene, "AccountBadgeText"));
        AssertEqual("版本", FindByKey(fixture.Shell, scene, "VersionHeader").Text);
        AssertEqual("关于 PCL N Edition", FindByKey(fixture.Shell, scene, "AboutTitle").Text);

        AssertTrue(FindByKey(fixture.Shell, scene, "AccountHint").Text!.Contains("还没有账户档案", StringComparison.Ordinal));
        AssertFalse(HasKey(fixture.Shell, scene, "AccountName"));
        AssertEqual(
            ReadCell(fixture.Store, LaunchPageState.InstanceSummaryKey),
            FindByKey(fixture.Shell, scene, "VersionName").Text);

        XsrUiSceneNode header = FindByKey(fixture.Shell, scene, "AccountHeader");
        AssertEqual(18, header.VisualStyle.FontSize);
        AssertEqual(600, header.VisualStyle.FontWeight);
        XsrUiSceneNode versionName = FindByKey(fixture.Shell, scene, "VersionName");
        AssertEqual(20, versionName.VisualStyle.FontSize);
        AssertEqual(600, versionName.VisualStyle.FontWeight);
        AssertEqual(
            XsrUiTextAlignment.Center,
            FindByKey(fixture.Shell, scene, "LaunchButton").VisualStyle.TextAlignment);

        AssertEqual("未找到可启动的游戏版本", ReadCell(fixture.Store, LaunchPageState.InstanceSummaryKey));
        // Downloading a game does not require an account. Only launch requires selection.
        AssertEqual("下载游戏", ReadCell(fixture.Store, LaunchPageState.ActionLabelKey));
        AssertTrue(FindByKey(fixture.Shell, scene, "LaunchButton").IsClickable);
        AssertEqual(string.Empty, ReadCell(fixture.Store, LaunchPageState.SelectedInstanceKey));
        AssertFalse(HasKey(fixture.Shell, scene, "LaunchStatus"));
        AssertFalse(HasKey(fixture.Shell, scene, "LaunchFeedback"));
        AssertFalse(HasKey(fixture.Shell, scene, "AccountSummary"));
    }

    private static void LaunchPageMatchesLegacyGeometry()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();

        AssertLegacyGeometry(fixture.Shell, fixture.Shell.Render(new XsrUiSize(1280, 800)), contentWidth: 1176, contentHeight: 700);
        AssertLegacyGeometry(fixture.Shell, fixture.Shell.Render(new XsrUiSize(850, 500)), contentWidth: 746, contentHeight: 400);
        AssertLegacyGeometry(fixture.Shell, fixture.Shell.Render(new XsrUiSize(810, 470)), contentWidth: 706, contentHeight: 370);
    }

    private static void AssertLegacyGeometry(
        XsrUiShell shell,
        XsrUiScene scene,
        double contentWidth,
        double contentHeight)
    {
        const double contentX = 76;
        const double contentY = 76;
        const double columnGap = 16;
        const double rightCardGap = 12;
        const double versionCardHeight = 184;
        double distributableWidth = contentWidth - columnGap;
        double expectedAccountWidth = Math.Min(360, distributableWidth * 0.92 / (0.92 + 1.35));
        double expectedRightWidth = distributableWidth - expectedAccountWidth;

        XsrUiRect account = FindByKey(shell, scene, "CardAccount").Rect;
        XsrUiRect version = FindByKey(shell, scene, "CardVersion").Rect;
        XsrUiRect about = FindByKey(shell, scene, "CardAbout").Rect;
        XsrUiRect accountAdd = FindByKey(shell, scene, "AccountAdd").Rect;
        XsrUiRect accountHeader = FindByKey(shell, scene, "AccountHeaderRow").Rect;
        XsrUiRect accountContent = FindByKey(shell, scene, "AccountContent").Rect;
        XsrUiRect accountBody = FindByKey(shell, scene, "AccountBody").Rect;

        AssertRectClose(new XsrUiRect(contentX, contentY, expectedAccountWidth, contentHeight), account);
        AssertRectClose(
            new XsrUiRect(contentX + expectedAccountWidth + columnGap, contentY, expectedRightWidth, versionCardHeight),
            version);
        AssertRectClose(
            new XsrUiRect(version.X, contentY + versionCardHeight + rightCardGap, expectedRightWidth, contentHeight - versionCardHeight - rightCardGap),
            about);
        AssertClose(accountContent.X + accountContent.Width, accountAdd.X + accountAdd.Width);
        AssertClose(32, accountHeader.Height);
        AssertClose(accountContent.Y + accountContent.Height, accountBody.Y + accountBody.Height);
        AssertTrue(version.Y + version.Height <= about.Y);
    }

    private static void NavigationIntentsRouteBetweenPages()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();

        Emit(fixture.Intents, "ui.navigation.settings");
        XsrUiScene placeholder = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertTrue(placeholder.Nodes.Any(node => node.Text == "该分区将在后续单元中迁移。"));
        AssertFalse(HasKey(fixture.Shell, placeholder, "LaunchButton"));

        Emit(fixture.Intents, "ui.navigation.launch");
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        XsrUiScene launch = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertTrue(HasKey(fixture.Shell, launch, "LaunchButton"));
        AssertFalse(launch.Nodes.Any(node => node.Text == "该分区将在后续单元中迁移。"));
    }

    private static void DownloadAndInstanceActionsRouteToVersionManagement()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();

        Emit(fixture.Intents, "ui.launch.primary");
        AssertEqual(XsrSemanticId.Parse("navigation.download"), fixture.Shell.SelectedNavigationId);
        AssertTrue(fixture.Shell.Render(new XsrUiSize(1280, 800)).Nodes.Any(
            node => node.Text == "该分区将在后续单元中迁移。"));
        AssertTrue(fixture.Feedback.Snapshot().Notifications.Any(notification =>
            notification.Level == DesktopNotificationLevel.Info
            && notification.Message == "请在安装页选择或下载游戏版本。"));

        Emit(fixture.Intents, "ui.navigation.launch");
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        Emit(fixture.Intents, "ui.launch.instances");
        XsrUiScene scene = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertTrue(HasKey(fixture.Shell, scene, "VersionListPage"));
        AssertEqual("版本列表", FindByKey(fixture.Shell, scene, "TitleSubpage").Text);
        AssertFalse(HasKey(fixture.Shell, scene, "LaunchButton"));
    }

    private static void LaunchPageSemanticsNeverExposeInternalKeys()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        XsrUiScene scene = fixture.Shell.Render(new XsrUiSize(1280, 800));
        XsrUiSceneNode button = FindByKey(fixture.Shell, scene, "LaunchButton");

        AssertEqual("下载游戏", button.Text);
        AssertEqual("下载游戏", button.Label);
        AssertEqual("版本列表", FindByKey(fixture.Shell, scene, "InstanceListButton").Label);
        AssertFalse(scene.Nodes.Any(node => node.Label is "LaunchButton" or "InstanceListButton" or "VersionName" or "CardAccount"));
    }

    private static void InstanceScanPublishesWithoutForeignTreeMutation()
    {
        ControllableInstanceSource source = new();
        using LaunchPageFixture fixture = new(source, addProfile: true);
        _ = fixture.Shell.Render(new XsrUiSize(1280, 800));
        XsrUiEntityId[] dirtyBeforeWorker = [.. fixture.Shell.Tree.DirtyEntities()];

        Task.Run(() => source.Complete(0, [Instance("worker-result")])).GetAwaiter().GetResult();
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        AssertTrue(dirtyBeforeWorker.SequenceEqual(fixture.Shell.Tree.DirtyEntities()));
        AssertEqual("worker-result", ReadCell(fixture.Store, LaunchPageState.SelectedInstanceKey));

        XsrUiScene scene = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertEqual("启动游戏", FindByKey(fixture.Shell, scene, "LaunchButton").Text);
        AssertEqual("启动游戏", FindByKey(fixture.Shell, scene, "LaunchButton").Label);
    }

    private static void OlderInstanceScanCannotOverwriteLatestGeneration()
    {
        ControllableInstanceSource source = new();
        using LaunchPageFixture fixture = new(source);
        Task latest = fixture.Controller.RefreshInstancesAsync();
        AssertEqual(2, source.Count);
        AssertTrue(source.TokenAt(0).IsCancellationRequested);

        source.Complete(1, [Instance("new-result")]);
        latest.GetAwaiter().GetResult();
        source.Complete(0, [Instance("old-result")]);
        AssertTrue(SpinWait.SpinUntil(() => source.ReturnedCount == 2, TimeSpan.FromSeconds(2)));
        AssertEqual("new-result", ReadCell(fixture.Store, LaunchPageState.SelectedInstanceKey));
        AssertEqual("new-result", ReadCell(fixture.Store, LaunchPageState.InstanceSummaryKey));
    }

    private static void LaunchPrimaryDispatchesProductStartCommand()
    {
        RecordingStartRoute recording = new();
        using MinecraftRuntime runtime = CreateRecordingRuntime(recording);
        using LaunchPageFixture fixture = new(
            new ImmediateInstanceSource([Instance("playable")]),
            runtime,
            addProfile: true,
            ownsMinecraftRuntime: false);
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();

        Emit(fixture.Intents, "ui.launch.primary");
        AssertTrue(SpinWait.SpinUntil(() => recording.LastCommand is not null, TimeSpan.FromSeconds(2)));
        AssertEqual("playable", recording.LastCommand!.InstanceId);
        AssertEqual(0, recording.LastCommand.AccountIndex);
        AssertTrue(SpinWait.SpinUntil(() => fixture.Feedback.Snapshot().Notifications.Any(notification =>
                notification.Level == DesktopNotificationLevel.Info
                && notification.Message == "Minecraft 已启动。"),
            TimeSpan.FromSeconds(2)));
    }

    private static void ExpandNavigationNeverReplacesTheLaunchPage()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();

        // The rail expand/collapse toggle is shell presentation, not a destination: it must
        // never route the content host to the placeholder page.
        Emit(fixture.Intents, "ui.navigation.expand");
        XsrUiScene scene = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertTrue(HasKey(fixture.Shell, scene, "LaunchButton"));
        AssertFalse(scene.Nodes.Any(node => node.Text == "该分区将在后续单元中迁移。"));
    }

    private static void AccountCardListsProfilesAndSwitchesSelection()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]), addProfile: true);
        AssertTrue(fixture.Service.AddProfile(new LaunchProfile
        {
            Username = "Second",
            Kind = LaunchProfileKind.Microsoft,
        }).IsSuccess);

        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        XsrUiScene scene = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertEqual("Player", FindByKey(fixture.Shell, scene, "AccountName").Text);
        AssertEqual("离线账户", FindByKey(fixture.Shell, scene, "AccountKind").Text);
        AssertTrue(fixture.Shell.Renderer.Activate(FindByKey(fixture.Shell, scene, "AccountSwitch").Entity));
        scene = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertTrue(HasKey(fixture.Shell, scene, "account-row:0"));
        AssertTrue(HasKey(fixture.Shell, scene, "account-row:1"));

        // The first profile is selected by default; switching publishes the fact and updates
        // the account presentation.
        AssertEqual(0, fixture.Service.SelectedIndex);
        AssertEqual("Player", ReadCell(fixture.Store, LaunchPageState.ProfileNameKey));

        XsrUiEntityId secondRow = FindEntity(fixture.Shell, "account-row:1");
        AssertTrue(secondRow.IsAssigned);
        // Text and icon children must route real pointer input to the template's button.
        XsrUiRect name = FindByKey(fixture.Shell, scene, "ProfileName:1").Rect;
        XsrUiPoint click = new(name.X + 8, name.Y + 8);
        AssertTrue(fixture.Shell.Renderer.PointerPressed(click));
        AssertTrue(fixture.Shell.Renderer.PointerReleased(click));
        AssertTrue(SpinWait.SpinUntil(() => fixture.Service.SelectedIndex == 1, TimeSpan.FromSeconds(2)));
        scene = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertEqual(1, fixture.Service.SelectedIndex);
        AssertEqual(1, ReadCellInt(fixture.Store, AccountService.SelectedKey));
        AssertEqual("Second", ReadCell(fixture.Store, LaunchPageState.ProfileNameKey));
        AssertFalse(HasKey(fixture.Shell, scene, "AccountSummary"));

        AssertEqual("Second", FindByKey(fixture.Shell, scene, "AccountName").Text);
        AssertEqual("Microsoft 账户", FindByKey(fixture.Shell, scene, "AccountKind").Text);
        AssertFalse(HasKey(fixture.Shell, scene, "account-row:1"));
        AssertTrue(fixture.Shell.Renderer.Activate(FindByKey(fixture.Shell, scene, "AccountSwitch").Entity));
        scene = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertTrue(FindByKey(fixture.Shell, scene, "account-row:1").IsSelected);
    }

    private static int ReadCellInt(XsrStateStore store, XsrSemanticId key) =>
        (int?)store.ReadAppliedValue(store.Resolve(key)) ?? -1;

    private static MinecraftRuntime CreateRecordingRuntime(RecordingStartRoute recording)
    {
        NoopDispatchObserver observer = new();
        XsrCommandRouterBuilder commands = new();
        commands.Register<MinecraftStartCommand>(MinecraftRouteIds.Start, recording.Handle);
        commands.Register<MinecraftDecideJavaAcquisitionCommand>(MinecraftRouteIds.AcquireDecide, (command, _) =>
        {
            recording.LastDecision = command.Approve;
            return ValueTask.FromResult(XsrResult.Success());
        });
        XsrQueryRouterBuilder queries = new();
        return new MinecraftRuntime(
            new MinecraftVersionDiscovery(),
            new MinecraftInstanceDiscovery(),
            new MinecraftProcessService(),
            commands.Build(observer),
            queries.Build(observer));
    }

    private static MinecraftInstanceDescriptor Instance(string id)
    {
        MinecraftVersionClassification classification = new(
            id,
            "release",
            MinecraftVersionCategory.Release,
            null);
        MinecraftVersionDescriptor version = new(
            id,
            Path.Combine(Path.GetTempPath(), id),
            Path.Combine(Path.GetTempPath(), id, id + ".json"),
            null,
            null,
            "example.Main",
            null,
            classification);
        return new MinecraftInstanceDescriptor(id, version.DirectoryPath, id, version, new MinecraftInstanceMetadata());
    }

    private static XsrUiSceneNode FindByKey(XsrUiShell shell, XsrUiScene scene, string key) =>
        scene.Nodes.Single(node => string.Equals(shell.Tree.Name(node.Entity), key, StringComparison.Ordinal));

    private static bool HasKey(XsrUiShell shell, XsrUiScene scene, string key) =>
        scene.Nodes.Any(node => string.Equals(shell.Tree.Name(node.Entity), key, StringComparison.Ordinal));

    private static void Emit(DesktopUiIntentSink intents, string command) =>
        intents.Emit(XsrSemanticId.Parse(command), default, XsrCorrelationId.Create());

    private static void Emit(
        DesktopUiIntentSink intents,
        string command,
        XsrUiEntityId source) =>
        intents.Emit(XsrSemanticId.Parse(command), source, XsrCorrelationId.Create());

    private static XsrUiEntityId FindEntity(XsrUiShell shell, string key)
    {
        XsrUiEntityId found = default;
        shell.Tree.Walk(
            shell.Stage.Root,
            entity =>
            {
                if (string.Equals(shell.Tree.Name(entity), key, StringComparison.Ordinal))
                {
                    found = entity;
                    return false;
                }

                return true;
            });
        return found;
    }

    private static string ReadCell(XsrStateStore store, XsrSemanticId key) =>
        (string?)store.ReadAppliedValue(store.Resolve(key)) ?? string.Empty;

    private sealed class LaunchPageFixture : IDisposable
    {
        private readonly string _temporaryDirectory;
        private readonly bool _ownsMinecraftRuntime;

        public LaunchPageFixture(
            ILaunchPageInstanceSource source,
            MinecraftRuntime? minecraft = null,
            bool addProfile = false,
            bool ownsMinecraftRuntime = true,
            HttpClient? accountHttp = null,
            IMicrosoftMinecraftAuthService? microsoft = null,
            IAccountUiEffects? accountEffects = null,
            LegacyProfileImport? imports = null,
            AccountOnboardingOptions? accountOptions = null,
            bool enableSkins = false,
            TimeProvider? timeProvider = null)
        {
            _temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                "nexa-desktop-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_temporaryDirectory);
            XsrUiRuntimeContext uiRuntime = new();
            XsrCompositeStateObserver storeObservation = new(uiRuntime.StateBridge, null);
            SettingsSchema schema = LauncherDefaults.CreateSchema();
            FoundationHost host = FoundationComposer.Compose(
                new LauncherSettingsJsonPort(Path.Combine(_temporaryDirectory, "settings.json"), schema),
                schema, new LaunchProfileFilePort(Path.Combine(_temporaryDirectory, "profiles.json")),
                observer: storeObservation, declareHostState: LaunchPageState.DeclareState);
            Foundation = FoundationRuntimeComposer.Compose(host);
            Onboarding = AccountOnboardingRuntimeComposer.Compose(host, accountHttp,
                accountOptions ?? new AccountOnboardingOptions("fixture-client", null),
                imports ?? new LegacyProfileImport(() => []), microsoft: microsoft);
            Store = host.StateStore;
            Intents = new DesktopUiIntentSink();
            Shell = PxmlShellComposer.Compose(Store, uiRuntime, intentSink: Intents);
            Feedback = new DesktopFeedbackService(timeProvider);
            FeedbackPresenter = new DesktopFeedbackPresenter(Shell, Intents, Feedback, Store, timeProvider);
            Minecraft = minecraft ?? MinecraftRuntimeComposer.Compose();
            _ownsMinecraftRuntime = minecraft is null || ownsMinecraftRuntime;
            Service = host.Accounts;
            if (addProfile)
            {
                AssertTrue(Service.AddProfile(new LaunchProfile
                {
                    Username = "Player",
                    Kind = LaunchProfileKind.Offline,
                }).IsSuccess);
            }

            Controller = new LaunchPageController(
                Shell,
                Intents,
                Minecraft,
                Foundation.Commands,
                Store,
                Path.Combine(_temporaryDirectory, "minecraft"),
                Feedback, source, accountCommands: enableSkins ? Onboarding.Commands : null,
                timeProvider: timeProvider);
            AccountForm = new AccountFormController(Shell, Intents, Onboarding.Commands, Store,
                Controller.AccountBody, Feedback, accountEffects);
            storeObservation.Add(Controller.StateObserver);
            Controller.Attach();
        }

        public XsrUiShell Shell { get; }
        public string TemporaryDirectory => _temporaryDirectory;
        public FoundationRuntime Foundation { get; }
        public DesktopUiIntentSink Intents { get; }
        public XsrStateStore Store { get; }
        public MinecraftRuntime Minecraft { get; }
        public AccountService Service { get; }
        public DesktopFeedbackService Feedback { get; }
        public DesktopFeedbackPresenter FeedbackPresenter { get; }
        public LaunchPageController Controller { get; }
        public AccountOnboardingRuntime Onboarding { get; }
        public AccountFormController AccountForm { get; }

        public void Dispose()
        {
            AccountForm.Dispose();
            Onboarding.Dispose();
            Controller.Dispose();
            FeedbackPresenter.Dispose();
            Feedback.Dispose();
            if (_ownsMinecraftRuntime)
            {
                Minecraft.Dispose();
            }

            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private sealed class ImmediateInstanceSource(IReadOnlyList<MinecraftInstanceDescriptor> instances)
        : ILaunchPageInstanceSource
    {
        public ValueTask<XsrResult<IReadOnlyList<MinecraftInstanceDescriptor>>> ReadAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(XsrResult.Success(instances));
    }

    private sealed class ControllableInstanceSource : ILaunchPageInstanceSource
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource<XsrResult<IReadOnlyList<MinecraftInstanceDescriptor>>>> _requests = [];
        private readonly List<CancellationToken> _tokens = [];
        private int _returnedCount;

        public int Count { get { lock (_gate) return _requests.Count; } }
        public int ReturnedCount => Volatile.Read(ref _returnedCount);

        public async ValueTask<XsrResult<IReadOnlyList<MinecraftInstanceDescriptor>>> ReadAsync(
            CancellationToken cancellationToken)
        {
            TaskCompletionSource<XsrResult<IReadOnlyList<MinecraftInstanceDescriptor>>> request =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _requests.Add(request);
                _tokens.Add(cancellationToken);
            }

            XsrResult<IReadOnlyList<MinecraftInstanceDescriptor>> result = await request.Task.ConfigureAwait(false);
            _ = Interlocked.Increment(ref _returnedCount);
            return result;
        }

        public CancellationToken TokenAt(int index)
        {
            lock (_gate)
            {
                return _tokens[index];
            }
        }

        public void Complete(int index, IReadOnlyList<MinecraftInstanceDescriptor> instances)
        {
            TaskCompletionSource<XsrResult<IReadOnlyList<MinecraftInstanceDescriptor>>> request;
            lock (_gate)
            {
                request = _requests[index];
            }

            request.SetResult(XsrResult.Success(instances));
        }
    }

    private sealed class RecordingStartRoute
    {
        public MinecraftStartCommand? LastCommand { get; private set; }

        public XsrResult Outcome { get; set; } = XsrResult.Success();

        public bool? LastDecision { get; set; }

        public XsrStateStore? ProgressStore { get; set; }

        public string? Stage { get; set; }

        public double StageProgress { get; set; } = -1d;

        public string? Method { get; set; }

        public ValueTask<XsrResult> Handle(
            MinecraftStartCommand command,
            CancellationToken cancellationToken)
        {
            LastCommand = command;
            if (ProgressStore is not null && !string.IsNullOrWhiteSpace(Stage))
            {
                ProgressStore.Publish(ProgressStore.Resolve(MinecraftLaunchProgressState.StageKey), Stage, cancellationToken);
                if (StageProgress >= 0)
                {
                    ProgressStore.Publish(ProgressStore.Resolve(MinecraftLaunchProgressState.ProgressKey), StageProgress, cancellationToken);
                }

                ProgressStore.Publish(
                    ProgressStore.Resolve(MinecraftLaunchProgressState.MethodKey),
                    Method ?? string.Empty,
                    cancellationToken);
            }

            return ValueTask.FromResult(Outcome);
        }
    }

    private sealed class NoopDispatchObserver : IXsrDispatchObserver
    {
        public void OnCompleted(XsrDispatchObservation observation)
        {
        }
    }
}

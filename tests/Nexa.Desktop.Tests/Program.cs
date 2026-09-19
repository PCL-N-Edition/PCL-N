using Nexa.Desktop.Ui;
using Nexa.Services.Accounts;
using Nexa.Services.Composition;
using Nexa.Services.Foundation;
using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Process;
using Nexa.Services.Settings;
using Nexa.Services.Tasks;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    public static void Main(string[] args)
    {
        if (args.Contains("--window-integration-smoke"))
        {
            WindowPropertyStoreRoundTripsAppId();
            Console.WriteLine("PASS: native window property store roundtrip");
            return;
        }
        foreach ((string name, Action body) in TestCases)
        {
            body();
            Console.WriteLine($"PASS: {name}");
        }

        Console.WriteLine($"Desktop composition tests passed: {TestCases.Length}.");
    }

    private static readonly (string Name, Action Body)[] TestCases =
    [
        ("product PXML shell accepts native window metrics", ProductPxmlShellAcceptsNativeWindowMetrics),
        ("Settings page keeps the final navigation and compact layout", SettingsPageUsesFinalNavigationAndCompactLayout),
        ("Settings page saves through Services without losing draft focus", SettingsPageSavesThroughServicesAndPreservesDraftFocus),
        ("Settings developer toggle preserves scroll position and focus", SettingsDeveloperToggleKeepsPositionAndFocus),
        ("Settings argument rows support add remove and apply", SettingsArgumentRowsSupportAddRemoveAndApply),
        ("Settings platform reads and refreshes Service snapshots", SettingsPlatformReadsAndRefreshesServiceSnapshot),
        ("Settings inline choices commit in both directions", SettingsInlineSelectorCommitsBothDirections),
        ("version row actions keep selection distinct", VersionRowActionsKeepSelectionDistinct),
        ("Java choice page routes selected major and launch action tracks progress", JavaChoiceAndLaunchProgressStayInteractive),
        ("process controls address sessions and show Service crash reports", ProcessControlsProjectServiceFacts),
        ("install catalog checks bridge selection and cleanup", InstallCatalogChecksBridgeSelection),
        ("install catalog returns to selected game and loader", InstallCatalogReturnsToSelectedGameAndLoader),
        ("install catalog virtualizes and prefetches off UI thread", InstallCatalogVirtualizesAndPrefetchesOffUiThread),
        ("Windows property store roundtrips the game AUMID", WindowPropertyStoreRoundTripsAppId),
        ("version selection uses the captured directory for launch and restores each selection", VersionSelectionUsesDirectoryQualifiedLaunch),
        ("version list remains compact searchable and scrollable with distinct icons", VersionListKeepsCompactGeometryAndIcons),
        ("directory pickers cannot publish after leaving the version page", VersionDirectoryPickerDiscardsLateResult),
        ("imported profiles remain visible when the picker reopens", ImportedProfilesRemainVisibleWhenPickerReopens),
        ("account forms share one header and create offline profiles", AccountFormsUseOneHeaderAndCreateOfflineProfiles),
        ("Microsoft onboarding reuses services and rejects late cancelled completion", MicrosoftOnboardingUsesServiceAndDiscardsLateCancellation),
        ("packaged account client ids preserve LittleSkin login in CI artifacts", PackagedAccountClientIdsMergeWithRuntimeConfiguration),
        ("third-party onboarding masks passwords and uses the chosen server", ThirdPartyOnboardingMasksPasswordsAndUsesConfiguredServer),
        ("LittleSkin onboarding selects characters with separate token kinds", LittleSkinOnboardingChoosesCharacterAndKeepsTokenKindsSeparate),
        ("account failures and stale file pickers stay in the current view", AccountFailuresAndLateFilePickerStayInCurrentView),
        ("launch page replicates the legacy card layout with bound facts", LaunchPageReplicatesLegacyLayout),
        ("launch page matches legacy geometry across wide, default, and minimum windows", LaunchPageMatchesLegacyGeometry),
        ("navigation intents route between launch and placeholder pages", NavigationIntentsRouteBetweenPages),
        ("download and instance actions route to installation and version management", DownloadAndInstanceActionsRouteToInstallationAndVersionManagement),
        ("installation entry presents Java and Bedrock subpages with a horizontal catalog", InstallationEntryPresentsJavaAndBedrockSubpages),
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
        ("unsupported profile does not spin feedback", UnsupportedProfileDoesNotSpinFeedback),
        ("launched cancel button becomes back", LaunchedCancelButtonBecomesBack),
        ("version subpages have independent routes and restore navigation focus", VersionSubpagesHaveIndependentRoutesAndRestoreFocus),
        ("Install editor locks base version and resets on exit", InstallEditorLocksBaseVersionAndResetsOnExit),
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
        ("install failure leaves the idle tree clean", InstallFailureLeavesTheIdleTreeClean),
        ("staged task page renders clean between changes", StagedTaskPageRendersCleanBetweenChanges),
        ("bubbles share a vertical dock and closing bubbles release slots", BubblesShareVerticalDockAndReleaseHiddenSlots),
        ("task bubble follows the center summary and yields to the page", TaskBubbleFollowsSummaryAndYieldsToPage),
        ("task bubble shows a compact track with aggregated progress", TaskBubbleUsesCompactProgressTrack),
        ("task center page reconciles cards and routes cancel dismiss and clear", TaskCenterPageReconcilesCardsAndRoutesActions),
        ("task center page yields the bubble and reclaims it on back", TaskCenterPageYieldsBubbleAndReclaimsOnBack),
        ("operational feedback uses the shared lower-left notification surface", OperationalFeedbackUsesLowerLeftNotification),
        ("notification levels keep exact lifetimes and permanent errors remain closable", NotificationLevelsKeepExactLifetimes),
        ("notifications share one lower-left surface and every level closes manually", NotificationsShareLowerLeftSurfaceAndCloseManually),
        ("notification summaries open complete one-action scrollable dialogs", NotificationSummaryOpensCompleteScrollableDialog),
        ("notification overflow remains bottom-pinned and recovers without disappearing", NotificationOverflowRecoversWithoutDisappearing),
        ("launch overlay closes immediately on success", LaunchOverlayClosesImmediatelyOnSuccess),
        ("closing notifications leave stack flow before their exit settles", ClosingNotificationReflowsImmediately),
        ("notification timers request render without mutating the UI tree", NotificationTimersStayOffTheRenderTree),
        ("dialog stays inside the window traps the page and restores focus on escape", DialogStaysInsideWindowAndRestoresFocus),
    ];

    private static void LaunchPageReplicatesLegacyLayout()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        XsrUiScene scene = fixture.Shell.Render(new XsrUiSize(1280, 800));

        AssertEqual("下载游戏", FindByKey(fixture.Shell, scene, "LaunchButtonText").Text);
        AssertTrue(FindByKey(fixture.Shell, scene, "LaunchButton").IsClickable);
        AssertEqual("账户", FindByKey(fixture.Shell, scene, "AccountHeader").Text);
        AssertFalse(HasKey(fixture.Shell, scene, "AccountBadgeText"));
        AssertEqual("版本", FindByKey(fixture.Shell, scene, "VersionHeader").Text);
        AssertEqual("关于 Nexa", FindByKey(fixture.Shell, scene, "AboutTitle").Text);

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
        AssertTrue(placeholder.Nodes.Any(node => node.Text == "这项功能尚未迁移到 Nexa。你可以返回首页，继续选择版本和启动游戏。"));
        AssertFalse(HasKey(fixture.Shell, placeholder, "LaunchButton"));

        Emit(fixture.Intents, "ui.navigation.launch");
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        XsrUiScene launch = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertTrue(HasKey(fixture.Shell, launch, "LaunchButton"));
        AssertFalse(launch.Nodes.Any(node => node.Text == "这项功能尚未迁移到 Nexa。你可以返回首页，继续选择版本和启动游戏。"));
    }

    private static void DownloadAndInstanceActionsRouteToInstallationAndVersionManagement()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();

        Emit(fixture.Intents, "ui.launch.primary");
        AssertEqual(XsrSemanticId.Parse("navigation.download"), fixture.Shell.SelectedNavigationId);
        XsrUiScene install = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertTrue(HasKey(fixture.Shell, install, "InstallPage"));
        AssertTrue(HasKey(fixture.Shell, install, "InstallJavaChoice"));
        AssertFalse(install.Nodes.Any(node => node.Text == "这项功能尚未迁移到 Nexa。你可以返回首页，继续选择版本和启动游戏。"));
        AssertTrue(fixture.Feedback.Snapshot().Notifications.Any(notification =>
            notification.Level == DesktopNotificationLevel.Info
            && notification.Message == "请在安装页选择或下载游戏版本。"));

        Emit(fixture.Intents, "ui.navigation.launch");
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        Emit(fixture.Intents, "ui.launch.instances");
        XsrUiScene scene = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertTrue(HasKey(fixture.Shell, scene, "VersionListPage"));
        AssertEqual("选择版本", FindByKey(fixture.Shell, scene, "TitleSubpage").Text);
        AssertFalse(HasKey(fixture.Shell, scene, "LaunchButton"));
    }

    private static void InstallationEntryPresentsJavaAndBedrockSubpages()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();

        Emit(fixture.Intents, "ui.navigation.download");
        XsrUiScene root = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertTrue(HasKey(fixture.Shell, root, "InstallPage"));
        XsrUiSceneNode javaChoice = FindByKey(fixture.Shell, root, "InstallJavaChoice");
        XsrUiSceneNode bedrockChoice = FindByKey(fixture.Shell, root, "InstallBedrockChoice");
        AssertEqual("安装 Java 版 Minecraft", javaChoice.Label);
        AssertEqual("安装 Bedrock 版 Minecraft", bedrockChoice.Label);
        AssertEqual("Java", FindByKey(fixture.Shell, root, "InstallJavaTitle").Text);
        AssertEqual("Bedrock", FindByKey(fixture.Shell, root, "InstallBedrockTitle").Text);
        AssertClose(javaChoice.Rect.Width, bedrockChoice.Rect.Width);
        AssertClose(javaChoice.Rect.Height, bedrockChoice.Rect.Height);
        AssertClose(javaChoice.Rect.Y, bedrockChoice.Rect.Y);
        AssertClose(12, bedrockChoice.Rect.X - javaChoice.Rect.X - javaChoice.Rect.Width);
        AssertTrue(javaChoice.Rect.Width > 450 && javaChoice.Rect.Height > 500);
        AssertFalse(HasKey(fixture.Shell, root, "InstallTitle"));
        AssertFalse(HasKey(fixture.Shell, root, "InstallDescription"));
        AssertFalse(root.Nodes.Any(node => node.Label is "InstallJavaChoice" or "InstallBedrockChoice"));
        AssertFalse(root.Nodes.Any(node => node.Label is "Java 版渐变背景图区域" or "Bedrock 版渐变背景图区域"));

        Emit(fixture.Intents, "ui.install.java");
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        XsrUiScene java = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertTrue(HasKey(fixture.Shell, java, "JavaInstallPage"));
        AssertEqual("安装 Java 版", FindByKey(fixture.Shell, java, "TitleSubpage").Text);
        AssertFalse(HasKey(fixture.Shell, java, "JavaInstallTitle"));
        AssertFalse(HasKey(fixture.Shell, java, "JavaInstallDescription"));
        XsrUiSceneNode input = FindByKey(fixture.Shell, java, "JavaInstallVersionInput");
        XsrUiSceneNode start = FindByKey(fixture.Shell, java, "JavaInstallStart");
        AssertEqual("", input.TextInput!.Value.DisplayText);
        AssertTrue(start.Rect.X >= input.Rect.X + input.Rect.Width);
        AssertEqual(XsrUiCornerRadii.Pill(40), start.VisualStyle.CornerRadius);
        XsrUiSceneNode pager = FindByKey(fixture.Shell, java, "JavaInstallPager");
        AssertEqual(XsrUiOrientation.Horizontal, pager.Pager!.Value.Direction);
        AssertEqual(0, pager.Pager!.Value.PageIndex);
        XsrUiSceneNode rail = FindByKey(fixture.Shell, java, "JavaInstallPagerTabs");
        AssertTrue(pager.Rect.Y >= rail.Rect.Y + rail.Rect.Height);
        AssertClose(0, FindByKey(fixture.Shell, java, "JavaMinecraftTab").VisualStyle.CornerRadius);
        AssertTrue(input.Rect.Y + input.Rect.Height <= rail.Rect.Y);
        foreach (XsrUiSize size in new[] { new XsrUiSize(1024, 600), new XsrUiSize(850, 520) })
        {
            XsrUiScene compact = fixture.Shell.Render(size);
            XsrUiSceneNode compactPager = FindByKey(fixture.Shell, compact, "JavaInstallPager");
            XsrUiSceneNode compactAction = FindByKey(fixture.Shell, compact, "JavaInstallStart");
            AssertTrue(compactPager.Rect.Width > 400);
            AssertTrue(compactAction.Rect.Y + compactAction.Rect.Height <= compactPager.Rect.Y);
            AssertTrue(compactAction.Rect.Y + compactAction.Rect.Height <= size.Height);
        }
        java = fixture.Shell.Render(new XsrUiSize(1280, 800));
        pager = FindByKey(fixture.Shell, java, "JavaInstallPager");

        AssertEqual(1, pager.Pager!.Value.PageCount);
        AssertFalse(IsVisible(fixture.Shell, "JavaForgePage"));
        AssertFalse(IsVisible(fixture.Shell, "JavaFabricApiPage"));
        fixture.Shell.Renderer.ReducedMotion = true;
        AssertTrue(fixture.Shell.Renderer.Activate(FindByKey(fixture.Shell, java, "CatalogRow:game:1.20.6").Entity));
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        java = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertEqual(7, FindByKey(fixture.Shell, java, "JavaInstallPager").Pager!.Value.PageCount);
        AssertTrue(IsVisible(fixture.Shell, "JavaFabricTab"));
        AssertTrue(IsVisible(fixture.Shell, "JavaForgeTab"));
        AssertFalse(IsVisible(fixture.Shell, "JavaCleanroomTab"));
        AssertTrue(FindByKey(fixture.Shell, java, "CatalogRow:game:1.20.6").IsSelected);
        Emit(fixture.Intents, "ui.install.page.fabric");
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        java = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertTrue(fixture.Shell.Renderer.Activate(FindByKey(fixture.Shell, java, "CatalogRow:loader:fixture.2").Entity));
        java = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertEqual(3, FindByKey(fixture.Shell, java, "JavaInstallPager").Pager!.Value.PageCount);
        AssertTrue(IsVisible(fixture.Shell, "JavaFabricApiTab"));
        AssertFalse(IsVisible(fixture.Shell, "JavaForgeTab"));
        AssertFalse(IsVisible(fixture.Shell, "JavaQslTab"));
        AssertTrue(FindByKey(fixture.Shell, java, "CatalogRow:loader:fixture.2").IsSelected);
        Emit(fixture.Intents, "ui.install.addon.fabric-api");
        Emit(fixture.Intents, "ui.install.start");
        // Without the install runtime wired (fixture), the start button still refuses loudly
        // instead of silently doing nothing; the real pipeline is covered by services tests.
        AssertTrue(fixture.Feedback.Snapshot().Notifications.Any(notification =>
            notification.Level == DesktopNotificationLevel.Warn && notification.Message.Contains("无法开始安装", StringComparison.Ordinal)));
        Emit(fixture.Intents, "ui.install.loader.vanilla");
        java = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertEqual(7, FindByKey(fixture.Shell, java, "JavaInstallPager").Pager!.Value.PageCount);
        Emit(fixture.Intents, "ui.install.page.forge");
        Emit(fixture.Intents, "ui.install.loader.forge");
        java = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertEqual(3, FindByKey(fixture.Shell, java, "JavaInstallPager").Pager!.Value.PageCount);
        AssertTrue(IsVisible(fixture.Shell, "JavaOptiFineTab"));
        AssertFalse(IsVisible(fixture.Shell, "JavaFabricApiTab"));
        Emit(fixture.Intents, "ui.install.loader.vanilla");
        fixture.Shell.Render(new XsrUiSize(1280, 800));
        Emit(fixture.Intents, "ui.install.page.quilt");
        Emit(fixture.Intents, "ui.install.loader.quilt");
        java = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertEqual(2, FindByKey(fixture.Shell, java, "JavaInstallPager").Pager!.Value.PageCount);
        AssertFalse(IsVisible(fixture.Shell, "JavaQslTab"));
        AssertFalse(IsVisible(fixture.Shell, "JavaFabricApiTab"));

        Emit(fixture.Intents, "ui.page.back");
        XsrUiScene backAtRoot = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertTrue(HasKey(fixture.Shell, backAtRoot, "InstallPage"));
        Emit(fixture.Intents, "ui.install.bedrock");
        XsrUiScene bedrock = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertTrue(HasKey(fixture.Shell, bedrock, "BedrockInstallPage"));
        AssertEqual("安装 Bedrock 版", FindByKey(fixture.Shell, bedrock, "TitleSubpage").Text);
        AssertTrue(FindByKey(fixture.Shell, bedrock, "BedrockInstallDescription").Text!
            .Contains("尚未迁移", StringComparison.Ordinal));
        AssertFalse(HasKey(fixture.Shell, bedrock, "JavaInstallStart"));
    }

    private static void LaunchPageSemanticsNeverExposeInternalKeys()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        XsrUiScene scene = fixture.Shell.Render(new XsrUiSize(1280, 800));
        XsrUiSceneNode button = FindByKey(fixture.Shell, scene, "LaunchButton");

        AssertEqual("下载游戏", FindByKey(fixture.Shell, scene, "LaunchButtonText").Text);
        AssertEqual("下载游戏", button.Label);
        AssertEqual("选择版本", FindByKey(fixture.Shell, scene, "InstanceListButton").Label);
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
        AssertEqual("启动游戏", FindByKey(fixture.Shell, scene, "LaunchButtonText").Text);
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
        AssertFalse(scene.Nodes.Any(node => node.Text == "这项功能尚未迁移到 Nexa。你可以返回首页，继续选择版本和启动游戏。"));
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
        commands.Register<MinecraftSelectJavaVersionCommand>(MinecraftRouteIds.JavaVersionSelect, (command, _) =>
        { recording.LastJavaMajor = command.Major; return ValueTask.FromResult(XsrResult.Success()); });
        commands.Register<MinecraftCancelProcessCommand>(MinecraftRouteIds.ProcessCancel, (command, _) =>
        { recording.LastCancelledSession = command.SessionId; return ValueTask.FromResult(XsrResult.Success()); });
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

    private static bool IsVisible(XsrUiShell shell, string key)
    {
        XsrUiEntityId entity = FindEntity(shell, key);
        return entity.IsAssigned
            && shell.Tree.GetComponent<XsrUiElement>(entity)?.IsVisible == true;
    }

    private static string ReadCell(XsrStateStore store, XsrSemanticId key) =>
        (string?)store.ReadAppliedValue(store.Resolve(key)) ?? string.Empty;

    private sealed class FailingInstallMetadata : Nexa.Services.Minecraft.Install.IMinecraftInstallMetadataSource
    {
        public Task<System.Text.Json.Nodes.JsonObject> FetchVanillaVersionJsonAsync(
            string gameVersion, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated metadata outage");

        public Task<System.Text.Json.Nodes.JsonObject> FetchLoaderProfileJsonAsync(
            Nexa.Services.Minecraft.Install.InstallLoader loader, string gameVersion, string build,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated metadata outage");

        public Task<System.Text.Json.Nodes.JsonObject> FetchAssetIndexJsonAsync(
            string indexUrl, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated metadata outage");
    }

    private static void StagedTaskPageRendersCleanBetweenChanges()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        using DesktopTaskBubblePresenter bubble = new(fixture.Shell, fixture.Store);
        using TaskCenterRuntime taskCenter = TaskCenterRuntimeComposer.Compose(fixture.Foundation.Host);
        using TaskCenterController controller = new(
            fixture.Shell, fixture.Intents, taskCenter.Commands, fixture.Store, bubble);
        TaskCenterService tasks = fixture.Foundation.Host.Tasks;
        using ITaskCenterTask install = tasks.Begin(new TaskCenterStart(
            "install:1", "安装 Minecraft 1.20.1", ["版本信息", "游戏文件"]));
        install.Report("游戏文件", "下载中", 0.4, 2, 5, 100);
        Emit(fixture.Intents, "ui.tasks.open");
        fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertTrue(controller.IsStaged);

        // The staged page must not restyle its cards every frame: with no entry change the
        // second render returns the cached scene. Per-frame SetComponent calls dirty the
        // entity as Structure and spin the render loop (the install-start freeze class).
        XsrUiScene first = fixture.Shell.Render(new XsrUiSize(1280, 800));
        XsrUiScene second = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertTrue(ReferenceEquals(first, second));

        // A real entry change still updates the card.
        install.Report("游戏文件", "下载中", 0.8, 4, 5, 100);
        XsrUiScene updated = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertFalse(ReferenceEquals(second, updated));
    }

    private static void InstallFailureLeavesTheIdleTreeClean()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        using DesktopTaskBubblePresenter bubble = new(fixture.Shell, fixture.Store);
        using TaskCenterRuntime taskCenter = TaskCenterRuntimeComposer.Compose(fixture.Foundation.Host);
        using TaskCenterController controller = new(
            fixture.Shell, fixture.Intents, taskCenter.Commands, fixture.Store, bubble);
        using Nexa.Services.Minecraft.Install.MinecraftInstallService install = new(
            fixture.Foundation.Host.Tasks,
            fixture.Foundation.Host.Downloads,
            metadata: new FailingInstallMetadata());
        string root = Path.Combine(fixture.TemporaryDirectory, "failed-install");

        XsrResult<Nexa.Services.Minecraft.Install.MinecraftInstallResult> result = install.InstallAsync(
            new Nexa.Services.Minecraft.Install.MinecraftInstallCommand(root, "1.20.1"))
            .GetAwaiter().GetResult();
        AssertFalse(result.IsSuccess);

        // A failure must not leave a per-frame dirty writer behind: with no state changes,
        // the second render returns the cached scene (reference-equal). A spin would rebuild
        // it every frame — the class of freeze reported after install failures.
        fixture.Shell.Render(new XsrUiSize(1280, 800));
        XsrUiScene first = fixture.Shell.Render(new XsrUiSize(1280, 800));
        XsrUiScene second = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertTrue(ReferenceEquals(first, second));
    }

    private static void TaskBubbleFollowsSummaryAndYieldsToPage()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        using DesktopTaskBubblePresenter bubble = new(fixture.Shell, fixture.Store);
        fixture.Shell.Renderer.ReducedMotion = true;
        TaskCenterService tasks = fixture.Foundation.Host.Tasks;

        // No tasks: the dock slot stays empty.
        AssertFalse(HasKey(fixture.Shell, fixture.Shell.Render(new XsrUiSize(1280, 800)), "task-bubble"));

        using ITaskCenterTask install = tasks.Begin(new TaskCenterStart(
            "install:1", "安装 Minecraft 1.20.1", ["版本信息", "游戏文件"]));
        install.Report("游戏文件", "下载中", 0.4, 2, 5, 100);
        XsrUiScene scene = fixture.Shell.Render(new XsrUiSize(1280, 800));
        XsrUiSceneNode root = FindByKey(fixture.Shell, scene, "task-bubble");
        // The bubble hugs the bottom-right dock inset.
        AssertTrue(root.Rect.X >= 1280 - 18 - 48 && root.Rect.X <= 1280 - 18);
        AssertTrue(root.Rect.Y >= 800 - 18 - 48 && root.Rect.Y <= 800 - 18);
        AssertTrue(root.Label!.Contains("40%", StringComparison.Ordinal));

        // The page owns the corner while it is open.
        bubble.SetPageVisible(true);
        AssertFalse(HasKey(fixture.Shell, fixture.Shell.Render(new XsrUiSize(1280, 800)), "task-bubble"));
        bubble.SetPageVisible(false);
        AssertTrue(HasKey(fixture.Shell, fixture.Shell.Render(new XsrUiSize(1280, 800)), "task-bubble"));

        // Completion keeps the bubble visible and filled until acknowledged.
        install.Complete("安装完成");
        scene = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertTrue(Math.Abs(fixture.Shell.Tree.GetComponent<XsrUiProgress>(bubble.FillEntity)!.Target - 1d) < 0.0001);

        AssertTrue(tasks.Dismiss("install:1"));
        AssertFalse(HasKey(fixture.Shell, fixture.Shell.Render(new XsrUiSize(1280, 800)), "task-bubble"));
    }

    private static void TaskBubbleUsesCompactProgressTrack()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        using DesktopTaskBubblePresenter bubble = new(fixture.Shell, fixture.Store);
        TaskCenterService tasks = fixture.Foundation.Host.Tasks;

        using ITaskCenterTask install = tasks.Begin(new TaskCenterStart(
            "install:2", "安装", ["游戏文件"]));
        install.Report("游戏文件", "下载中", 0.5, 1, 2, 0);
        fixture.Shell.Render(new XsrUiSize(1280, 800));

        XsrUiProgress fill = fixture.Shell.Tree.GetComponent<XsrUiProgress>(bubble.FillEntity)!;
        AssertEqual(XsrUiProgressFillAnchor.Leading, fill.Anchor);
        AssertTrue(Math.Abs(fill.Target - 0.5) < 0.0001);
        AssertFalse(fixture.Shell.Render(new XsrUiSize(1280, 800)).Nodes.Any(node => node.Entity == bubble.FillEntity));
        var dock = FindByKey(fixture.Shell, fixture.Shell.Render(new(1280, 800)), "task-bubble");
        fixture.Shell.Renderer.PointerMoved(new(dock.Rect.X + 24, dock.Rect.Y + 24));
        AssertTrue(fixture.Shell.Render(new XsrUiSize(1280, 800)).Nodes.Any(node => node.Entity == bubble.FillEntity));
        fixture.Shell.Renderer.SetProgressPresentation(bubble.FillEntity, 0.5);
        var presented = fixture.Shell.Render(new XsrUiSize(1280, 800)).Nodes.Single(node => node.Entity == bubble.FillEntity);
        AssertEqual(16d, presented.Rect.Width);
        AssertEqual(3d, presented.Rect.Height);
        // The expanded caption and the bottom icon share one continuous input owner.
        var expanded = FindByKey(fixture.Shell, fixture.Shell.Render(new(1280, 800)), "task-bubble");
        var top = new XsrUiPoint(expanded.Rect.X + 24, expanded.Rect.Y + 12);
        fixture.Shell.Renderer.PointerMoved(top);
        AssertEqual(116d, FindByKey(fixture.Shell, fixture.Shell.Render(new(1280, 800)), "task-bubble").Rect.Height);
        string? emitted = null;
        fixture.Intents.IntentEmitted += (_, e) => emitted = e.Intent.Command.Value;
        AssertTrue(fixture.Shell.Renderer.PointerPressed(top));
        AssertTrue(fixture.Shell.Renderer.PointerReleased(top));
        AssertEqual("ui.tasks.open", emitted);


        // The presented fill is the renderer-owned catch-up value; with the animation clock
        // parked it must still be clamped between zero and the target.
        AssertTrue(fill.Presented >= 0d && fill.Presented <= fill.Target + 0.0001);
    }

    private static void TaskCenterPageReconcilesCardsAndRoutesActions()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        using DesktopTaskBubblePresenter bubble = new(fixture.Shell, fixture.Store);
        using TaskCenterRuntime taskCenter = TaskCenterRuntimeComposer.Compose(fixture.Foundation.Host);
        using TaskCenterController controller = new(
            fixture.Shell, fixture.Intents, taskCenter.Commands, fixture.Store, bubble);
        fixture.Shell.Renderer.ReducedMotion = true;
        TaskCenterService tasks = fixture.Foundation.Host.Tasks;

        using ITaskCenterTask install = tasks.Begin(new TaskCenterStart(
            "install:1", "安装 Minecraft 1.20.1", ["版本信息", "游戏文件"]));
        install.Report("游戏文件", "下载中", 0.5, 2, 5, 2048);
        using ITaskCenterTask skin = tasks.Begin(new TaskCenterStart(
            "download:1", "下载皮肤", ["下载文件"]));
        skin.Complete("已保存");

        Emit(fixture.Intents, "ui.tasks.open");
        XsrUiScene scene = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertTrue(controller.IsStaged);
        AssertEqual("任务中心", FindByKey(fixture.Shell, scene, "TitleSubpage").Text);
        // A clipped zero-width fill must reach the backend so its animation can start.
        AssertTrue(HasKey(fixture.Shell, scene, "TaskCardFill"));
        AssertTrue(HasKey(fixture.Shell, scene, "task-card:install:1"));
        AssertTrue(HasKey(fixture.Shell, scene, "task-card:download:1"));
        AssertTrue(scene.Nodes.Any(node => node.Text is not null && node.Text.Contains(
            "安装 Minecraft 1.20.1", StringComparison.Ordinal)));
        AssertTrue(scene.Nodes.Any(node => node.Text is not null && node.Text.Contains("游戏文件 · 下载中", StringComparison.Ordinal)));
        AssertTrue(scene.Nodes.Any(node => node.Text is not null && node.Text.Contains("50%", StringComparison.Ordinal)));
        // The header aggregates live work; the finished task keeps its card until dismissed.
        AssertTrue(FindByKey(fixture.Shell, scene, "TaskCenterSummary").Text!.Contains(
            "1 个任务进行中", StringComparison.Ordinal));

        AssertFalse(scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity).StartsWith("task-card-step:", StringComparison.Ordinal)));
        var disclosure = scene.Nodes.First(node => fixture.Shell.Tree.Name(node.Entity) == "TaskCardDetails");
        AssertTrue(fixture.Shell.Renderer.Activate(disclosure.Entity));
        scene = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertTrue(scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity).StartsWith("task-card-step:", StringComparison.Ordinal)));
        AssertTrue(fixture.Shell.Renderer.Activate(disclosure.Entity));
        fixture.Shell.Render(new XsrUiSize(1280, 800));
        // Cancel routes through the typed command to the owning token.
        XsrUiEntityId installAction = controller.CardAction("install:1");
        Emit(fixture.Intents, "ui.tasks.card.cancel", installAction);
        fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertTrue(install.CancellationToken.IsCancellationRequested);
        install.Canceled();

        // Dismiss removes a terminal card; clear-finished sweeps the rest.
        scene = fixture.Shell.Render(new XsrUiSize(1280, 800));
        Emit(fixture.Intents, "ui.tasks.card.dismiss", controller.CardAction("download:1"));
        fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertFalse(HasKey(fixture.Shell, fixture.Shell.Render(new XsrUiSize(1280, 800)), "task-card:download:1"));
        Emit(fixture.Intents, "ui.tasks.clear-finished");
        fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertFalse(HasKey(fixture.Shell, fixture.Shell.Render(new XsrUiSize(1280, 800)), "task-card:install:1"));
        AssertTrue(FindByKey(fixture.Shell, fixture.Shell.Render(new XsrUiSize(1280, 800)), "TaskCenterSummary")
            .Text!.Contains("暂无任务", StringComparison.Ordinal));
    }

    private static void TaskCenterPageYieldsBubbleAndReclaimsOnBack()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        using DesktopTaskBubblePresenter bubble = new(fixture.Shell, fixture.Store);
        using TaskCenterRuntime taskCenter = TaskCenterRuntimeComposer.Compose(fixture.Foundation.Host);
        using TaskCenterController controller = new(
            fixture.Shell, fixture.Intents, taskCenter.Commands, fixture.Store, bubble);
        fixture.Shell.Renderer.ReducedMotion = true;
        TaskCenterService tasks = fixture.Foundation.Host.Tasks;
        using ITaskCenterTask install = tasks.Begin(new TaskCenterStart("install:1", "安装", ["游戏文件"]));

        AssertTrue(HasKey(fixture.Shell, fixture.Shell.Render(new XsrUiSize(1280, 800)), "task-bubble"));
        Emit(fixture.Intents, "ui.tasks.open");
        // The page owns the corner while staged: bubble hidden, card visible.
        AssertFalse(HasKey(fixture.Shell, fixture.Shell.Render(new XsrUiSize(1280, 800)), "task-bubble"));
        AssertTrue(HasKey(fixture.Shell, fixture.Shell.Render(new XsrUiSize(1280, 800)), "task-card:install:1"));

        // The shared back affordance pops the page; the next frame reconciles staged state
        // and the bubble reclaims the corner.
        Emit(fixture.Intents, "ui.page.back");
        XsrUiScene scene = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertFalse(controller.IsStaged);
        AssertFalse(HasKey(fixture.Shell, scene, "task-card:install:1"));
        // The bubble reconciles on the frame after the controller unstaged the page (the
        // unstage happens during this controller's FramePreparing, after the bubble's).
        scene = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertTrue(HasKey(fixture.Shell, scene, "task-bubble"));
    }

    private sealed class LaunchPageFixture : IDisposable
    {
        private readonly string _temporaryDirectory;
        private IDisposable? _launchObserverSubscription;
        private readonly bool _ownsMinecraftRuntime;

        public LaunchPageFixture(
            IMinecraftInstanceSource source,
            MinecraftRuntime? minecraft = null,
            bool addProfile = false,
            bool ownsMinecraftRuntime = true,
            HttpClient? accountHttp = null,
            IMicrosoftMinecraftAuthService? microsoft = null,
            IAccountUiEffects? accountEffects = null,
            LegacyProfileImport? imports = null,
            AccountOnboardingOptions? accountOptions = null,
            bool enableSkins = false,
            TimeProvider? timeProvider = null,
            IVersionDirectoryEffects? directoryEffects = null,
            IInstallCatalogSource? installSource = null)
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

            string minecraftRoot = Path.Combine(_temporaryDirectory, "minecraft");
            Directory.CreateDirectory(minecraftRoot);
            Library = MinecraftLibraryRuntimeComposer.Compose(host, minecraftRoot, source);
            InstallCatalog = InstallCatalogRuntimeComposer.Compose(host, installSource ?? new FixtureInstallSource());
            Controller = new LaunchPageController(
                Shell,
                Intents,
                Minecraft,
                Foundation.Commands,
                Store,
                Library,
                Feedback, accountCommands: enableSkins ? Onboarding.Commands : null,
                timeProvider: timeProvider, directoryEffects: directoryEffects, installCatalogCommands: InstallCatalog.Commands, installCatalogQueries: InstallCatalog.Queries);
            AccountForm = new AccountFormController(Shell, Intents, Onboarding.Commands, Store,
                Controller.AccountBody, Feedback, accountEffects, host.Logging);
            _launchObserverSubscription = storeObservation.Subscribe(Controller.StateObserver);
            Controller.Attach();
        }

        public InstallCatalogRuntime InstallCatalog { get; }
        public XsrUiShell Shell { get; }
        public string TemporaryDirectory => _temporaryDirectory;
        public FoundationRuntime Foundation { get; }
        public DesktopUiIntentSink Intents { get; }
        public XsrStateStore Store { get; }
        public MinecraftRuntime Minecraft { get; }
        public MinecraftLibraryRuntime Library { get; }
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
            _launchObserverSubscription?.Dispose();
            Controller.Dispose();
            InstallCatalog.Dispose();
            Library.Dispose();
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
        : IMinecraftInstanceSource
    {
        public ValueTask<IReadOnlyList<MinecraftInstanceDescriptor>> DiscoverAsync(
            string minecraftRootDirectory, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(instances);
    }

    private sealed class ControllableInstanceSource : IMinecraftInstanceSource
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource<XsrResult<IReadOnlyList<MinecraftInstanceDescriptor>>>> _requests = [];
        private readonly List<CancellationToken> _tokens = [];
        private int _returnedCount;

        public int Count { get { lock (_gate) return _requests.Count; } }
        public int ReturnedCount => Volatile.Read(ref _returnedCount);

        public async ValueTask<IReadOnlyList<MinecraftInstanceDescriptor>> DiscoverAsync(
            string minecraftRootDirectory, CancellationToken cancellationToken = default)
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
            return result.Value;
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

        /// <summary>
        /// Keeps the launch pipeline in flight: success now closes the launching page
        /// immediately, so mid-launch narration tests must not complete the route.
        /// </summary>
        public bool Hang { get; set; }

        public bool? LastDecision { get; set; }
        public int? LastJavaMajor { get; set; }
        public Guid? LastCancelledSession { get; set; }

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
                try
                {
                    // The derived cells are read-only projections: the test publishes the
                    // coherent snapshot truth, exactly like the real pipeline publisher.
                    ProgressStore.Publish(
                        ProgressStore.Resolve(MinecraftLaunchProgressState.SnapshotKey),
                        new MinecraftLaunchProgressSnapshot(
                            Active: true,
                            Stage: Stage,
                            Progress: StageProgress < 0 ? 0d : StageProgress,
                            Method: Method ?? string.Empty,
                            DownloadSpeed: string.Empty,
                            IsLaunched: false,
                            SessionId: null),
                        cancellationToken);
                }
                catch (Exception exception)
                {
                    Console.WriteLine("[diag] recording handler threw: " + exception);
                    throw;
                }
            }

            if (Hang)
            {
                return new ValueTask<XsrResult>(_hang.Task);
            }

            return ValueTask.FromResult(Outcome);
        }

        private readonly TaskCompletionSource<XsrResult> _hang = new();
    }

    private sealed class NoopDispatchObserver : IXsrDispatchObserver
    {
        public void OnCompleted(XsrDispatchObservation observation)
        {
        }
    }
}

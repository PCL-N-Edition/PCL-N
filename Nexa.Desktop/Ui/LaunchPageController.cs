using System.Globalization;
using Nexa.Pxml;
using Nexa.Services.Accounts;
using Nexa.Services.Composition;
using Nexa.Services.Foundation;
using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Process;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Ui;

/// <summary>
/// The product launch page: the first vertical slice attached to the shell's content host,
/// replicating the legacy experimental launch home's information architecture — an account
/// card (profile identity / picker), a version card (版本 header, instance picker row,
/// the big accent launch button), and the community about card. It reads its facts from host
/// state cells, routes one-shot outcomes to the shared feedback service, emits the launch intent,
/// and dispatches the product-level Minecraft start command through the composed runtime routers.
/// Navigation intents route between this page and placeholders for destinations whose slices
/// have not landed yet.
/// </summary>
internal sealed partial class LaunchPageController : IDisposable
{
    private static readonly XsrSemanticId LaunchRoute = XsrSemanticId.Parse("ui.navigation.launch");
    private static readonly XsrSemanticId InstallRoute = XsrSemanticId.Parse("ui.navigation.download");

    private static readonly XsrSemanticId LaunchPrimaryCommand = XsrSemanticId.Parse("ui.launch.primary");

    private static readonly XsrSemanticId LaunchInstancesCommand = XsrSemanticId.Parse("ui.launch.instances");
    private static readonly XsrSemanticId LaunchSettingsCommand = XsrSemanticId.Parse("ui.launch.settings");
    private static readonly XsrSemanticId LaunchModifyCommand = XsrSemanticId.Parse("ui.launch.modify");
    private static readonly XsrSemanticId PageBackCommand = XsrSemanticId.Parse("ui.page.back");
    private static readonly XsrSemanticId WidgetAboutCommand = XsrSemanticId.Parse("ui.launch.widget.about");
    private static readonly XsrSemanticId WidgetTriviaCommand = XsrSemanticId.Parse("ui.launch.widget.trivia");
    private static readonly XsrSemanticId WidgetEchoCommand = XsrSemanticId.Parse("ui.launch.widget.echo");
    private static readonly XsrSemanticId WidgetHintCommand = XsrSemanticId.Parse("ui.launch.hint.refresh");

    private static readonly XsrSemanticId AccountSelectCommand = XsrSemanticId.Parse("ui.account.select");
    private static readonly XsrSemanticId AccountDeleteCommand = XsrSemanticId.Parse("ui.account.delete");
    private static readonly XsrSemanticId AccountSwitchCommand = XsrSemanticId.Parse("ui.account.switch");
    private static readonly XsrSemanticId AccountWardrobeCommand = XsrSemanticId.Parse("ui.account.wardrobe");
    private static readonly XsrSemanticId AccountDismissCommand = XsrSemanticId.Parse("ui.account.dismiss");
    private static readonly XsrSemanticId LaunchCancelCommand = XsrSemanticId.Parse("ui.launch.cancel");

    private static readonly XsrSemanticId InstallJavaCommand = XsrSemanticId.Parse("ui.install.java");
    private static readonly XsrSemanticId InstallBedrockCommand = XsrSemanticId.Parse("ui.install.bedrock");
    private static readonly XsrSemanticId InstallMinecraftPageCommand = XsrSemanticId.Parse("ui.install.page.minecraft");
    private static readonly XsrSemanticId InstallForgePageCommand = XsrSemanticId.Parse("ui.install.page.forge");
    private static readonly XsrSemanticId InstallCleanroomPageCommand = XsrSemanticId.Parse("ui.install.page.cleanroom");
    private static readonly XsrSemanticId InstallNeoForgePageCommand = XsrSemanticId.Parse("ui.install.page.neoforge");
    private static readonly XsrSemanticId InstallFabricPageCommand = XsrSemanticId.Parse("ui.install.page.fabric");
    private static readonly XsrSemanticId InstallLegacyFabricPageCommand = XsrSemanticId.Parse("ui.install.page.legacy-fabric");
    private static readonly XsrSemanticId InstallFabricApiPageCommand = XsrSemanticId.Parse("ui.install.page.fabric-api");
    private static readonly XsrSemanticId InstallQuiltPageCommand = XsrSemanticId.Parse("ui.install.page.quilt");
    private static readonly XsrSemanticId InstallQslPageCommand = XsrSemanticId.Parse("ui.install.page.qsl");
    private static readonly XsrSemanticId InstallLabyModPageCommand = XsrSemanticId.Parse("ui.install.page.labymod");
    private static readonly XsrSemanticId InstallOptiFinePageCommand = XsrSemanticId.Parse("ui.install.page.optifine");
    private static readonly XsrSemanticId InstallLiteLoaderPageCommand = XsrSemanticId.Parse("ui.install.page.liteloader");
    private static readonly XsrSemanticId InstallStartCommand = XsrSemanticId.Parse("ui.install.start");
    private static readonly XsrSemanticId InstallVersion1211Command = XsrSemanticId.Parse("ui.install.version.1.21.1");
    private static readonly XsrSemanticId InstallVersion1206Command = XsrSemanticId.Parse("ui.install.version.1.20.6");
    private static readonly XsrSemanticId InstallVersion1201Command = XsrSemanticId.Parse("ui.install.version.1.20.1");
    private static readonly XsrSemanticId InstallLoaderFabricCommand = XsrSemanticId.Parse("ui.install.loader.fabric");
    // No visible button emits this any more, but the catalog flow keeps it as the explicit
    // "back to vanilla" selection reset (version picks and tests still drive it).
    private static readonly XsrSemanticId InstallLoaderVanillaCommand = XsrSemanticId.Parse("ui.install.loader.vanilla");
    private static readonly XsrSemanticId InstallLoaderForgeCommand = XsrSemanticId.Parse("ui.install.loader.forge");
    private static readonly XsrSemanticId InstallLoaderNeoForgeCommand = XsrSemanticId.Parse("ui.install.loader.neoforge");
    private static readonly XsrSemanticId InstallLoaderQuiltCommand = XsrSemanticId.Parse("ui.install.loader.quilt");
    private static readonly XsrSemanticId InstallLoaderOptiFineCommand = XsrSemanticId.Parse("ui.install.loader.optifine");
    private static readonly XsrSemanticId InstallLoaderCleanroomCommand = XsrSemanticId.Parse("ui.install.loader.cleanroom");
    private static readonly XsrSemanticId InstallLoaderLiteLoaderCommand = XsrSemanticId.Parse("ui.install.loader.liteloader");
    private static readonly XsrSemanticId InstallLoaderLegacyFabricCommand = XsrSemanticId.Parse("ui.install.loader.legacy-fabric");
    private static readonly XsrSemanticId InstallLoaderLabyModCommand = XsrSemanticId.Parse("ui.install.loader.labymod");
    private static readonly XsrSemanticId InstallFabricApiAddonCommand = XsrSemanticId.Parse("ui.install.addon.fabric-api");
    private static readonly XsrSemanticId InstallQslAddonCommand = XsrSemanticId.Parse("ui.install.addon.qsl");

    private static readonly XsrSemanticId DownloadNavigationId = XsrSemanticId.Parse("navigation.download");

    // The legacy experimental launch-home palette (light theme).
    private static readonly XsrUiColor CardBackground = new(255, 255, 255, 241);
    private static readonly XsrUiColor CardBorder = new(224, 234, 253);
    private static readonly XsrUiColor PickerBackground = new(238, 242, 247);
    private static readonly XsrUiColor PrimaryText = new(52, 61, 74);
    private static readonly XsrUiColor SecondaryText = new(122, 138, 153);
    private static readonly XsrUiColor BadgeBackground = new(224, 234, 253);
    private static readonly XsrUiColor BadgeText = new(11, 91, 203);
    private static readonly XsrUiColor LaunchButtonBackground = new(11, 91, 203);
    private static readonly XsrUiColor LaunchButtonHover = new(19, 112, 243);
    private static readonly XsrUiColor ProfileSecondaryText = new(96, 108, 124);
    private static readonly XsrUiColor ProfileSurface = new(244, 246, 250);
    private static readonly XsrUiColor LaunchProgressTrack = new(224, 234, 253);
    private static readonly XsrUiColor LaunchProgressFill = new(11, 91, 203);
    private static readonly XsrUiColor InstallJavaTint = new(229, 239, 255);
    private static readonly XsrUiColor InstallJavaAccent = new(28, 97, 210);
    private static readonly XsrUiColor InstallJavaHover = new(214, 231, 255);
    private static readonly XsrUiColor InstallBedrockTint = new(235, 244, 233);
    private static readonly XsrUiColor InstallBedrockAccent = new(57, 105, 69);
    private static readonly XsrUiColor InstallBedrockHover = new(222, 238, 219);
    private static readonly XsrUiColor InstallSelectedBorder = new(128, 172, 239);

    private static readonly JavaInstallSubpage[] JavaInstallSubpages =
    [
        new("JavaMinecraftPage", "JavaMinecraftTab", InstallMinecraftPageCommand),
        new("JavaForgePage", "JavaForgeTab", InstallForgePageCommand, "Forge"),
        new("JavaCleanroomPage", "JavaCleanroomTab", InstallCleanroomPageCommand, "Cleanroom"),
        new("JavaNeoForgePage", "JavaNeoForgeTab", InstallNeoForgePageCommand, "NeoForge"),
        new("JavaFabricPage", "JavaFabricTab", InstallFabricPageCommand, "Fabric"),
        new("JavaLegacyFabricPage", "JavaLegacyFabricTab", InstallLegacyFabricPageCommand, "Legacy Fabric"),
        new("JavaFabricApiPage", "JavaFabricApiTab", InstallFabricApiPageCommand, RequiresLoader: "Fabric"),
        new("JavaOptiFabricPage", "JavaOptiFabricTab", XsrSemanticId.Parse("ui.install.page.optifabric"), RequiresLoader: "Fabric"),
        new("JavaQuiltPage", "JavaQuiltTab", InstallQuiltPageCommand, "Quilt"),
        new("JavaQslPage", "JavaQslTab", InstallQslPageCommand, RequiresLoader: "Quilt"),
        new("JavaLabyModPage", "JavaLabyModTab", InstallLabyModPageCommand, "LabyMod"),
        new("JavaOptiFinePage", "JavaOptiFineTab", InstallOptiFinePageCommand, "OptiFine"),
        new("JavaLiteLoaderPage", "JavaLiteLoaderTab", InstallLiteLoaderPageCommand, "LiteLoader"),
    ];

    private static readonly string[] JavaInstallLoaderKeys =
    [
        "JavaLoaderForge", "JavaLoaderCleanroom", "JavaLoaderNeoForge", "JavaLoaderFabric",
        "JavaLoaderLegacyFabric", "JavaLoaderQuilt", "JavaLoaderLabyMod", "JavaLoaderOptiFine", "JavaLoaderLiteLoader",
    ];

    private const string NoAccountName = "未选择账户";
    private const string AccountNeedLoginSummary = "请选择或创建一个账户档案后再启动。";

    private static readonly Dictionary<string, string> LaunchStageDisplay = new(StringComparer.Ordinal)
    {
        ["get_java"] = "获取 Java",
        ["login"] = "登录",
        ["complete_files"] = "补全文件",
        ["get_arguments"] = "获取启动参数",
        ["extract_natives"] = "解压 Natives",
        ["pre_launch"] = "预启动处理",
        ["preflight"] = "检查启动条件",
        ["start_process"] = "启动进程",
        ["wait_window"] = "等待游戏窗口",
        ["end"] = "完成",
    };

    private static readonly Dictionary<string, string> LaunchMethodDisplay = new(StringComparer.Ordinal)
    {
        ["offline"] = "离线模式",
        ["microsoft"] = "微软登录",
    };

    private int _launchInProgress;
    private string? _launchingInstanceId, _launchingRoot;
    private int _pendingCloseLaunching;
    private Guid? _javaAcquisitionDialog;
    private Guid? _preflightDialog;
    private Guid? _preflightAttempt;
    private int _pickingJava;
    private bool _launchingViaKeyboard;
    private XsrUiEntityId _launchingPage;
    private Dictionary<string, XsrUiEntityId> _launchingEntities = [];

    /// <summary>The composition root attaches this observer to the shared store fan-out.</summary>
    public IXsrStateObserver StateObserver { get; }
    private const string ScanningInstances = "正在扫描本地版本…";
    private const string NoInstances = "未找到可启动的游戏版本";
    private const string NoSelectedProfileLabel = "未选择档案";
    private const string DownloadLabel = "下载游戏";
    private const string LaunchLabel = "启动游戏";
    private const string LaunchUnavailableLabel = "暂不支持启动";

    private readonly XsrUiShell _shell;
    private readonly DesktopUiIntentSink _intents;
    private readonly MinecraftRuntime _minecraft;
    private readonly XsrCommandRouter _foundationCommands;
    private readonly XsrCommandRouter? _accountCommands;
    private long _skinRevision = -1;
    private readonly XsrStateStore _store;
    private readonly DesktopFeedbackService _feedback;
    private readonly XsrCommandRouter _libraryCommands;
    internal VersionSelectionController Versions { get; }
    private readonly XsrUiEntityId _launchPage;
    private readonly XsrUiEntityId _placeholderPage;
    private readonly XsrUiEntityId _versionListPage;
    private XsrUiEntityId _versionSettingsPage;
    private readonly XsrUiEntityId _wardrobePage;
    private readonly XsrUiEntityId _installPage;
    private readonly XsrUiEntityId _javaInstallPage;
    private readonly XsrUiEntityId _bedrockInstallPage;
    private readonly Dictionary<string, XsrUiEntityId> _javaInstallEntities;
    private int _presentedJavaInstallPage = -1;
    private string _selectedInstallVersion = "";
    private string _selectedInstallLoader = "原版 Minecraft";
    private readonly HashSet<string> _selectedInstallAddons = new(StringComparer.Ordinal);
    private string _activeJavaInstallPage = "JavaMinecraftPage";
    private readonly Dictionary<string, XsrUiEntityId> _titleEntities = [];
    private int _titleNavigationDepth = 1;
    private readonly Stack<XsrUiEntityId> _returnFocus = [];
    private readonly Dictionary<string, XsrUiEntityId> _pageEntities;
    private readonly Dictionary<int, XsrUiEntityId> _accountRowEntities = [];
    private readonly Dictionary<XsrUiEntityId, int> _accountRowIndexes = [];
    private readonly PxmlHostIr _accountRowTemplate = PxmlCompiler.Compile(
        PxmlParser.Parse(ReadEmbeddedResource("Ui.AccountProfileRow.pxml")));
    private long _accountRosterRevision = -1;
    private int _presentedAccountIndex = -2;
    private bool? _presentedAccountPicker;
    private string? _accountMotionKey;
    private bool _accountKeyboardFocus;
    private int _presentedWidgetIndex = -1;
    private double _indicatorPosition = double.NaN;
    private int _hintIndex = Random.Shared.Next(LaunchWidgetHints.BuiltIn.Count);
    private readonly object _hintGate = new();
    private readonly TimeProvider _timeProvider;
    private ITimer? _hintTimer;
    private readonly object _refreshGate = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private Task _refreshTask = Task.CompletedTask;
    private bool _attached;
    private bool _disposed;

    public LaunchPageController(
        XsrUiShell shell,
        DesktopUiIntentSink intents,
        MinecraftRuntime minecraft,
        XsrCommandRouter foundationCommands,
        XsrStateStore store,
        MinecraftLibraryRuntime library,
        DesktopFeedbackService feedback,
        XsrCommandRouter? accountCommands = null,
        TimeProvider? timeProvider = null,
        IVersionDirectoryEffects? directoryEffects = null,
        XsrCommandRouter? installCatalogCommands = null, XsrQueryRouter? installCatalogQueries = null,
        XsrCommandRouter? installRunCommands = null, XsrQueryRouter? recoveryQueries = null)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(intents);
        ArgumentNullException.ThrowIfNull(minecraft);
        ArgumentNullException.ThrowIfNull(foundationCommands);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(feedback);
        _shell = shell;
        _intents = intents;
        _minecraft = minecraft;
        _foundationCommands = foundationCommands;
        _accountCommands = accountCommands;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _installCatalogCommands = installCatalogCommands;
        _installCatalogQueries = installCatalogQueries;
        _installRunCommands = installRunCommands;
        _recoveryQueries = recoveryQueries;
        _store = store;
        _feedback = feedback;
        StateObserver = new LaunchingStateObserver(this);
        _libraryCommands = library.Commands;
        (_launchPage, _pageEntities) = LoadLaunchPage();
        _placeholderPage = BuildPlaceholderPage();
        Versions = new VersionSelectionController(shell, intents, library.Commands, store, feedback, directoryEffects);
        _versionListPage = Versions.Page;
        _versionSettingsPage = LoadVersionSubpage("VersionSettingsPage", "版本设置");
        _wardrobePage = LoadVersionSubpage("AccountWardrobePage", "更衣橱");
        (_installPage, _) = LoadInstallPage();
        (_javaInstallPage, _javaInstallEntities) = LoadJavaInstallPage();
        InitializeInstallCatalog();
        UpdateJavaInstallSubpageVisibility();
        _bedrockInstallPage = LoadBedrockInstallPage();
        (_launchingPage, _launchingEntities) = LoadLaunchingPage();
        _shell.Tree.Walk(_shell.TitleBar, entity =>
        {
            _titleEntities[_shell.Tree.Name(entity)] = entity;
            return true;
        });
    }

    /// <summary>Subscribes to renderer intents and shows the initial launch page.</summary>
    public void Attach()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_attached)
        {
            return;
        }

        _attached = true;
        _intents.IntentEmitted += OnIntentEmitted;
        _shell.Renderer.FramePreparing += OnFramePreparing;
        _shell.StyleChanged += OnShellStyleChanged;
        ShowLaunch();
        Publish(LaunchPageState.ProfileNameKey, NoAccountName);
        Publish(LaunchPageState.InstanceSummaryKey, ScanningInstances);
        Publish(LaunchPageState.SelectedInstanceKey, string.Empty);
        Publish(LaunchPageState.ActionLabelKey, DownloadLabel);
        RefreshAccountPresentation();
        if (_store.TryResolve(XsrSemanticId.Parse("UiLaunchWidgetPage"), out XsrStateId widgetPage)
            && _store.ReadAppliedValue(widgetPage) is int savedPage)
            _shell.Renderer.RebasePagerPage(_pageEntities["LaunchWidgetPager"], Math.Clamp(savedPage, 0, 2));
        RefreshWidgetPresentation();
        Publish(LaunchPageState.WidgetHintKey, LaunchWidgetHints.BuiltIn[_hintIndex]);
        _hintTimer = _timeProvider.CreateTimer(_ => AdvanceHint(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        _ = QueueRefresh();
    }

    /// <summary>Completes when the in-flight instance scan has published its facts.</summary>
    public Task WaitUntilIdle()
    {
        lock (_refreshGate)
        {
            return Task.WhenAll(_refreshTask, _installCatalogTask, _installPrefetchTask, _installEditRead ?? Task.CompletedTask);
        }
    }

    internal XsrUiEntityId AccountBody => _pageEntities["AccountBody"];

    /// <summary>
    /// Re-queries the installed instances and re-commits the version card facts. Exposed for
    /// tests so the asynchronous scan can be awaited deterministically.
    /// </summary>
    public Task RefreshInstancesAsync() => QueueRefresh();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_foundationCommands.TryResolve(FoundationRouteIds.SettingsSet, out XsrCommandId saveWidget))
            _foundationCommands.Dispatch(saveWidget, new SettingsSetCommand("UiLaunchWidgetPage",
                _shell.Tree.GetComponent<XsrUiPager>(_pageEntities["LaunchWidgetPager"])!.PageIndex.ToString(CultureInfo.InvariantCulture))).Completion.GetAwaiter().GetResult();
        lock (_hintGate) { _disposed = true; _hintTimer?.Dispose(); }
        if (_attached)
        {
            _intents.IntentEmitted -= OnIntentEmitted;
            _shell.Renderer.FramePreparing -= OnFramePreparing;
            _shell.StyleChanged -= OnShellStyleChanged;
            _attached = false;
        }

        _lifetimeCancellation.Cancel();
        Versions.Dispose();
        foreach (var dock in _processDocks.Values) { _shell.Tree.Destroy(dock.Power); _shell.Tree.Destroy(dock.Logs); }
        _processDocks.Clear();
        if (_javaChoicePage.IsAssigned) _shell.Tree.Destroy(_javaChoicePage);
        DismissAcquisitionDialog();

        _lifetimeCancellation.Dispose();
    }

    private Task QueueRefresh()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_refreshGate)
        {
            _refreshTask = RefreshLibraryAsync();
            return _refreshTask;
        }
    }

    private async Task RefreshLibraryAsync()
    {
        if (!_libraryCommands.TryResolve(MinecraftLibraryRoutes.Refresh, out XsrCommandId id)) return;
        _ = await _libraryCommands.Dispatch(id, new MinecraftLibraryRefreshCommand(), cancellationToken: _lifetimeCancellation.Token).Completion.ConfigureAwait(false);
        ProjectLibrary();
    }

    private void ProjectLibrary()
    {
        if (_disposed || _store.ReadAppliedValue(_store.Resolve(MinecraftLibraryService.StateKey)) is not MinecraftLibrarySnapshot snapshot) return;
        Publish(LaunchPageState.SelectedInstanceKey, snapshot.SelectedInstance?.Id ?? "");
        Publish(LaunchPageState.InstanceSummaryKey, snapshot.SelectedInstance?.Id ?? (snapshot.IsLoading ? ScanningInstances : NoInstances));
        Publish(LaunchPageState.InstanceDirectoryKey, snapshot.RootDirectory);
        UpdateLaunchButton();
    }

    /// <summary>
    /// Publishes primary-action facts only. Downloading does not require an account; launching
    /// requires a selected profile. Safe from an instance worker as well as the render thread.
    /// </summary>
    private void UpdateLaunchButton()
    {
        bool hasProfile = ReadProfiles().Any(profile => profile.Index == SelectedAccountIndex);
        bool hasInstance = !string.IsNullOrWhiteSpace(ReadCell(LaunchPageState.SelectedInstanceKey));
        string label;
        bool enabled;
        if (!hasInstance)
        {
            label = DownloadLabel;
            enabled = true;
        }
        else if (!hasProfile)
        {
            label = NoSelectedProfileLabel;
            enabled = false;
        }
        else if (!SelectedProfileCanLaunch())
        {
            // The account capability logs these kinds in successfully, so the action says
            // honestly that they cannot start a game yet. UpdateLaunchButton is a per-frame
            // projection: it must stay side-effect free — a toast here would re-raise the
            // feedback Changed event every frame and spin a render loop forever.
            label = LaunchUnavailableLabel;
            enabled = false;
        }
        else
        {
            label = LaunchLabel;
            enabled = true;
        }

        bool busy = _launchInProgress != 0 && _launchingInstanceId == ReadCell(LaunchPageState.SelectedInstanceKey)
            && MinecraftLibraryService.PathComparer.Equals(_launchingRoot, ReadCell(LaunchPageState.InstanceDirectoryKey));
        if (busy) { label = "正在启动…"; enabled = false; }
        Publish(LaunchPageState.ActionBusyKey, busy);
        Publish(LaunchPageState.ActionLabelKey, label);
        Publish(LaunchPageState.ActionEnabledKey, enabled);
        Publish(LaunchPageState.InstanceAvailableKey, hasInstance);
    }

    private bool SelectedProfileCanLaunch()
    {
        LaunchProfileView? profile = ReadProfiles().FirstOrDefault(candidate => candidate.Index == SelectedAccountIndex);
        return profile is not { } selected
            || selected.Kind is LaunchProfileKind.Offline or LaunchProfileKind.Microsoft or LaunchProfileKind.LittleSkin;
    }

    private void OnIntentEmitted(object? sender, DesktopUiIntentEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (HandleProcessIntent(e)) return;
        if (HandleInstallCatalogIntent(e)) return;
        XsrSemanticId command = e.Intent.Command;
        if (command == LaunchRoute)
        {
            ShowLaunch();
            PublishProfileFacts();
            _ = QueueRefresh();
        }
        else if (command == InstallRoute)
        {
            ShowInstallRoot();
        }
        else if (command == InstallJavaCommand)
        {
            OpenSubpage(_javaInstallPage, e.Intent.Source);
            RequestInstallCatalog();
            RefreshJavaInstallPresentation();
        }
        else if (command == InstallBedrockCommand)
        {
            OpenSubpage(_bedrockInstallPage, e.Intent.Source);
        }
        else if (TryGetJavaInstallSubpage(command, out JavaInstallSubpage subpage))
        {
            ShowJavaInstallSubpage(subpage.PageKey);
        }
        else if (command == InstallVersion1211Command)
        {
            SelectInstallVersion("1.21.1", "JavaVersion1211");
        }
        else if (command == InstallVersion1206Command)
        {
            SelectInstallVersion("1.20.6", "JavaVersion1206");
        }
        else if (command == InstallVersion1201Command)
        {
            SelectInstallVersion("1.20.1", "JavaVersion1201");
        }
        else if (command == InstallLoaderFabricCommand)
        {
            SelectInstallLoader("Fabric", "JavaLoaderFabric");
        }
        else if (command == InstallLoaderVanillaCommand)
        {
            SelectInstallLoader("原版 Minecraft", string.Empty);
        }
        else if (command == InstallLoaderForgeCommand)
        {
            SelectInstallLoader("Forge", "JavaLoaderForge");
        }
        else if (command == InstallLoaderNeoForgeCommand)
        {
            SelectInstallLoader("NeoForge", "JavaLoaderNeoForge");
        }
        else if (command == InstallLoaderQuiltCommand)
        {
            SelectInstallLoader("Quilt", "JavaLoaderQuilt");
        }
        else if (command == InstallLoaderOptiFineCommand)
        {
            SelectInstallLoader("OptiFine", "JavaLoaderOptiFine");
        }
        else if (command == InstallLoaderCleanroomCommand)
        {
            SelectInstallLoader("Cleanroom", "JavaLoaderCleanroom");
        }
        else if (command == InstallLoaderLiteLoaderCommand)
        {
            SelectInstallLoader("LiteLoader", "JavaLoaderLiteLoader");
        }
        else if (command == InstallLoaderLegacyFabricCommand)
        {
            SelectInstallLoader("Legacy Fabric", "JavaLoaderLegacyFabric");
        }
        else if (command == InstallLoaderLabyModCommand)
        {
            SelectInstallLoader("LabyMod", "JavaLoaderLabyMod");
        }
        else if (command == InstallFabricApiAddonCommand)
        {
            ToggleInstallAddon("Fabric API", "JavaFabricApiSelect");
        }
        else if (command == InstallQslAddonCommand)
        {
            ToggleInstallAddon("QSL", "JavaQslSelect");
        }
        else if (command == InstallStartCommand)
        {
            NotifyInstallUnavailable();
        }
        else if (command == LaunchPrimaryCommand)
        {
            string instanceId = ReadCell(LaunchPageState.SelectedInstanceKey);
            if (string.IsNullOrWhiteSpace(instanceId))
            {
                NavigateToDownload();
            }
            else
            {
                _ = StartLaunchAsync(instanceId, e.Intent.Source);
            }
        }
        else if (command == LaunchCancelCommand)
        {
            _ = CancelLaunchAsync();
        }
        else if (command == LaunchInstancesCommand)
        {
            OpenSubpage(_versionListPage, e.Intent.Source);
        }
        else if (command == LaunchSettingsCommand)
        {
            OpenSubpage(_versionSettingsPage, e.Intent.Source);
        }
        else if (command == LaunchModifyCommand)
        {
            OpenInstallEditor(e.Intent.Source);
        }
        else if (command == AccountWardrobeCommand)
        {
            OpenSubpage(_wardrobePage, e.Intent.Source);
        }
        else if (command.Value == "ui.launch.restore")
        {
            if (_launchInProgress != 0) ShowLaunchingPage(e.Intent.Source);
        }
        else if (command == PageBackCommand)
        {
            if (_shell.Stage.Navigation.Pop())
            {
                UpdateTitleBar();
                if (_returnFocus.TryPop(out XsrUiEntityId focus))
                    _shell.Renderer.Focus(focus, IsKeyboardIntent(e.Intent.Source));
            }
        }
        else if (command == WidgetAboutCommand || command == WidgetTriviaCommand || command == WidgetEchoCommand)
        {
            XsrUiEntityId pager = _pageEntities["LaunchWidgetPager"];
            _ = _shell.Renderer.SelectPagerPage(pager, command == WidgetTriviaCommand ? 1 : command == WidgetEchoCommand ? 2 : 0);
        }
        else if (command == WidgetHintCommand)
        {
            AdvanceHint();
        }
        else if (command == AccountDeleteCommand)
        {
            XsrUiEntityId row = e.Intent.Source;
            while (row.IsAssigned && !_accountRowIndexes.ContainsKey(row)) row = _shell.Tree.Parent(row);
            if (_accountRowIndexes.TryGetValue(row, out int index)) _ = RemoveAccountAsync(index, _accountRosterRevision);
        }
        else if (command == AccountSelectCommand)
        {
            if (_accountRowIndexes.TryGetValue(e.Intent.Source, out int index))
            {
                _accountKeyboardFocus = IsKeyboardIntent(e.Intent.Source);
                _ = SelectAccountAsync(index, _accountRosterRevision);
            }
        }
        else if (command == AccountSwitchCommand)
        {
            _accountKeyboardFocus = IsKeyboardIntent(e.Intent.Source);
            Publish(LaunchPageState.AccountPickerKey, true);
        }
        else if (command == AccountDismissCommand)
        {
            _accountKeyboardFocus = IsKeyboardIntent(e.Intent.Source);
            Publish(LaunchPageState.AccountPickerKey, false);
        }
        else if (IsDestinationCommand(command))
        {
            ShowPlaceholder(command.Value == "ui.navigation.settings");
        }
    }

    private bool IsDestinationCommand(XsrSemanticId command) =>
        _shell.NavigationItems.Any(item => item.Command == command);

    private void AdvanceHint()
    {
        lock (_hintGate)
        {
            if (_disposed) return;
            _hintIndex = (_hintIndex + Random.Shared.Next(1, LaunchWidgetHints.BuiltIn.Count)) % LaunchWidgetHints.BuiltIn.Count;
            Publish(LaunchPageState.WidgetHintKey, LaunchWidgetHints.BuiltIn[_hintIndex]);
        }
    }

    private async Task RemoveAccountAsync(int index, long revision)
    {
        if (!_foundationCommands.TryResolve(FoundationRouteIds.AccountRemoveProfile, out XsrCommandId route)) return;
        XsrResult result = await _foundationCommands.Dispatch(route, new AccountRemoveProfileCommand(index, revision),
            cancellationToken: _lifetimeCancellation.Token).Completion.ConfigureAwait(false);
        if (!_disposed && !result.IsSuccess) _feedback.Error($"删除档案失败：{result.Error?.Message}");
    }

    private async Task SelectAccountAsync(int index, long revision)
    {
        if (!_foundationCommands.TryResolve(FoundationRouteIds.AccountSelectProfile, out XsrCommandId route))
        {
            _feedback.Error("账户切换命令未注册。");
            return;
        }

        XsrResult result = await _foundationCommands.Dispatch(route,
            new AccountSelectProfileCommand(index, revision), cancellationToken: _lifetimeCancellation.Token)
            .Completion.ConfigureAwait(false);
        if (_disposed) return;
        if (result.IsSuccess) Publish(LaunchPageState.AccountPickerKey, false);
        else _feedback.Error($"切换档案失败：{result.Error?.Message}");
    }

    private void ShowLaunch()
    {
        ClearSubpageHistory();
        if (!_shell.Stage.Navigation.Current.Equals(_launchPage))
        {
            // Destination switches replace the page: the navigator's back stack is reserved
            // for hierarchical drill-in, not for moving between primary destinations.
            _shell.Stage.Navigation.Replace(_launchPage);
        }

    }

    /// <summary>
    /// Shows the first installation decision as a primary destination. Java and Bedrock are
    /// peer product choices here; their detail pages are pushed only after an explicit choice,
    /// so the title-bar back affordance never doubles as primary navigation.
    /// </summary>
    private void ShowInstallRoot()
    {
        ClearSubpageHistory();
        if (!_shell.Stage.Navigation.Current.Equals(_installPage))
        {
            _shell.Stage.Navigation.Replace(_installPage);
        }
    }

    private static bool TryGetJavaInstallSubpage(XsrSemanticId command, out JavaInstallSubpage subpage)
    {
        foreach (JavaInstallSubpage candidate in JavaInstallSubpages)
        {
            if (candidate.Command == command)
            {
                subpage = candidate;
                return true;
            }
        }

        subpage = null!;
        return false;
    }

    /// <summary>
    /// Selects one embedded configuration surface below the installation input. It does not
    /// push a product navigation page: switching Java/loader details is local configuration,
    /// not a new destination in the shell hierarchy.
    /// </summary>
    private void ShowJavaInstallSubpage(string pageKey)
    {
        if (!_javaInstallEntities.TryGetValue("JavaInstallPager", out XsrUiEntityId pager)
            || !_javaInstallEntities.TryGetValue(pageKey, out XsrUiEntityId page)
            || !IsInstallEntityVisible(page))
        {
            return;
        }

        XsrUiEntityId[] pages = VisibleJavaInstallPages(pager);
        int index = Array.IndexOf(pages, page);
        if (index < 0)
        {
            return;
        }

        _activeJavaInstallPage = pageKey;
        RequestInstallCatalog();
        _ = _shell.Renderer.SelectPagerPage(pager, index);
        RefreshJavaInstallPresentation();
    }

    private void SelectInstallVersion(string version, string key)
    {
        if (_editingInstall) return;
        _selectedInstallVersion = version;
        ChooseInstallGame(version);
        _ = _shell.Renderer.SetTextInputValue(_javaInstallEntities["JavaInstallVersionInput"], version);
        StyleInstallChoices(
            _javaInstallEntities,
            ["JavaVersion1211", "JavaVersion1206", "JavaVersion1201"],
            key,
            InstallJavaTint,
            InstallJavaAccent);
    }

    private void SelectInstallLoader(string loader, string key)
    {
        _selectedInstallLoader = loader;
        if (loader == "原版 Minecraft") _selectedInstallBuilds.Clear();
        _selectedInstallAddons.Clear();
        foreach (InstallLoader addon in _selectedInstallBuilds.Keys.Where(kind => QueryInstallEligibility()?.Loaders.Any(item => item.Loader == kind && item.IsAddon) == true))
            _selectedInstallAddons.Add(addon == InstallLoader.FabricApi ? "Fabric API" : addon == InstallLoader.Qsl ? "QSL" : "OptiFabric");
        StyleInstallChoices(
            _javaInstallEntities,
            JavaInstallLoaderKeys,
            key,
            InstallJavaTint,
            InstallJavaAccent);
        UpdateInstallAddonStyle("Fabric API", "JavaFabricApiSelect", selected: false);
        UpdateInstallAddonStyle("QSL", "JavaQslSelect", selected: false);
        JavaInstallSubpage? selectedPage = JavaInstallSubpages.FirstOrDefault(candidate => candidate.Loader == loader);
        UpdateJavaInstallSubpageVisibility(selectedPage?.PageKey ?? "JavaMinecraftPage");
    }

    private void ToggleInstallAddon(string addon, string key)
    {
        string requiredLoader = addon == "Fabric API" ? "Fabric" : "Quilt";
        if (_selectedInstallLoader != requiredLoader)
        {
            return;
        }

        bool selected = _selectedInstallAddons.Add(addon);
        if (!selected)
        {
            _selectedInstallAddons.Remove(addon);
        }

        UpdateInstallAddonStyle(addon, key, selected);
    }

    private void NotifyInstallUnavailable()
    {
        if (_editingInstall && _installEdit is null) { _feedback.Warn("正在读取版本信息。"); return; }
        if (!_installGameChosen) { _feedback.Warn("请先选择 Minecraft 版本。"); return; }
        if (QueryInstallEligibility()?.CommitError is { } conflict) { _feedback.Warn(conflict); return; }
        string requested = _shell.Tree.GetComponent<XsrUiTextInput>(_javaInstallEntities["JavaInstallVersionInput"])
            ?.ReadDraft().Trim() ?? string.Empty;
        if (_editingInstall && requested.Length == 0) { _feedback.Warn("请输入版本名称。"); return; }
        if (_editingInstall && _editPlan?.Kind == MinecraftInstallEditKind.Unchanged && requested == _installEdit?.InstanceId) return;
        string version = requested.Length == 0 ? _selectedInstallVersion : requested;
        string selection = _selectedInstallBuilds.Count == 0 ? _selectedInstallLoader : string.Join(" + ", _selectedInstallBuilds.Select(pair => pair.Key + " " + pair.Value));
        // The selection is real: dispatch the install run and follow it in the task center.
        if (!BeginInstallRun(version)) { _feedback.Warn($"无法开始安装 Java 版 {version}（{selection}）。"); }
    }

    /// <summary>
    /// Dispatches the real install pipeline with the current selection. The primary loader is
    /// the first selected build the eligibility query does not classify as an addon; every
    /// other selected build rides along as an addon jar. Returns false when the install route
    /// is unavailable (tests without the runtime) or the selection is empty.
    /// </summary>
    private bool BeginInstallRun(string version)
    {
        if (_installRunCommands is null
            || !_installRunCommands.TryResolve(MinecraftInstallRoutes.Run, out XsrCommandId route))
        {
            return false;
        }

        InstallEligibilityResult? eligibility = QueryInstallEligibility();
        (InstallLoader Kind, string Build)? primary = null;
        List<MinecraftInstallAddon> addons = [];
        foreach (KeyValuePair<InstallLoader, string> build in _selectedInstallBuilds)
        {
            bool isAddon = eligibility?.Loaders.Any(
                loader => loader.Loader == build.Key && loader.IsAddon) == true;
            if (isAddon)
            {
                var catalog = _store.ReadAppliedValue(_store.Resolve(InstallCatalogStateContract.StateKey)) as InstallCatalogState;
                var downloads = catalog?.Catalogs.FirstOrDefault(item => item.Loader == build.Key && item.GameVersion == _selectedInstallVersion)
                    ?.Versions.FirstOrDefault(item => item.Id == build.Value)?.Downloads;
                addons.Add(new MinecraftInstallAddon(build.Key, build.Value, downloads));
            }
            else if (primary is null)
            {
                primary = (build.Key, build.Value);
            }
            else
            {
                _feedback.Warn("一次只能选择一个主加载器。");
                return false;
            }
        }

        string root = _installEdit?.RootDirectory ?? ReadCell(LaunchPageState.InstanceDirectoryKey);
        if (string.IsNullOrWhiteSpace(root)) { _feedback.Warn("尚未确定 Minecraft 目录。"); return false; }
        _ = _installRunCommands.Dispatch(route, new MinecraftInstallCommand(
            root,
            _selectedInstallVersion,
            primary?.Kind,
            primary?.Build,
            addons, _installEdit?.InstanceId ?? (version == _selectedInstallVersion ? null : version), _installEdit?.Fingerprint)
        { NewInstanceName = _installEdit is null ? null : version });
        // Manual starts watch their task immediately (parity with the legacy task manager).
        _intents.Emit(XsrSemanticId.Parse("ui.tasks.open"), default, XsrCorrelationId.Create());
        return true;
    }

    private bool IsKeyboardIntent(XsrUiEntityId source) => source.IsAssigned
        && _shell.Tree.IsAlive(source) && _shell.Tree.GetComponent<XsrUiInput>(source)?.IsFocusVisible == true;

    private void ClearSubpageHistory()
    {
        while (_shell.Stage.Navigation.Depth > 1) _shell.Stage.Navigation.Pop();
        _returnFocus.Clear();
        UpdateTitleBar();
    }

    private void OpenSubpage(XsrUiEntityId page, XsrUiEntityId source)
    {
        if (_shell.Stage.Navigation.Current == page) return;
        bool keyboard = IsKeyboardIntent(source);
        _returnFocus.Push(source);
        _shell.Stage.Navigation.Push(page);
        UpdateTitleBar();
        _shell.Renderer.Focus(_titleEntities["TitleBack"], keyboard);
    }

    private void OnShellStyleChanged(object? sender, EventArgs e) => UpdateTitleBar();

    private void UpdateTitleBar()
    {
        int depth = _shell.Stage.Navigation.Depth;
        XsrUiTransition main = _shell.Tree.GetComponent<XsrUiTransition>(_titleEntities["TitleMain"])!;
        XsrUiTransition title = _shell.Tree.GetComponent<XsrUiTransition>(_titleEntities["TitleSubpage"])!;
        main.Source = _titleEntities["TitleSubpage"];
        title.Source = _titleEntities["TitleMain"];
        if (depth != _titleNavigationDepth)
        {
            main.OffsetX = title.OffsetX = depth > _titleNavigationDepth ? 128 : -128;
            _titleNavigationDepth = depth;
        }
        bool subpage = _shell.Stage.Navigation.Depth > 1;
        Publish(LaunchPageState.TitleTransitionKey, subpage
            ? _shell.Tree.GetComponent<XsrUiSemantic>(_shell.Stage.Navigation.Current)?.Label ?? string.Empty : "NexaCL");
        foreach (string key in new[] { "TitleMain", "TitleBack", "TitleSubpage" })
        {
            XsrUiEntityId entity = _titleEntities[key];
            _shell.Tree.GetComponent<XsrUiElement>(entity)!.IsVisible = key == "TitleMain" ? !subpage : subpage;
            XsrUiVisualStyle style = RequireVisual(entity);
            style.Foreground = _shell.Palette.TitleBarText;
            style.FontSize = 17;
            style.FontWeight = 600;
            if (key == "TitleBack")
            {
                style.Hover = new XsrUiColor(255, 255, 255, 50);
                style.CornerRadius = XsrUiCornerRadii.Pill(30);
            }
            if (key == "TitleSubpage")
                _shell.Tree.GetComponent<XsrUiText>(entity)!.Content = subpage
                    ? _shell.Tree.GetComponent<XsrUiSemantic>(_shell.Stage.Navigation.Current)?.Label ?? string.Empty
                    : string.Empty;
            _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
        }
    }

    private (XsrUiEntityId Page, Dictionary<string, XsrUiEntityId> Entities) LoadInstallPage()
    {
        (XsrUiEntityId page, Dictionary<string, XsrUiEntityId> entities) = LoadStandalonePage(
            "Ui.InstallPage.pxml", "install-page-loader");
        StyleInstallPage(entities);
        return (page, entities);
    }

    private (XsrUiEntityId Page, Dictionary<string, XsrUiEntityId> Entities) LoadJavaInstallPage()
    {
        (XsrUiEntityId page, Dictionary<string, XsrUiEntityId> entities) = LoadStandalonePage(
            "Ui.JavaInstallPage.pxml", "java-install-page-loader");
        StyleJavaInstallPage(entities);
        _ = _shell.Renderer.SetTextInputValue(entities["JavaInstallVersionInput"], _selectedInstallVersion);
        StyleInstallChoices(
            entities,
            ["JavaVersion1211", "JavaVersion1206", "JavaVersion1201"],
            "JavaVersion1211",
            InstallJavaTint,
            InstallJavaAccent);
        StyleInstallChoices(
            entities,
            JavaInstallLoaderKeys,
            string.Empty,
            InstallJavaTint,
            InstallJavaAccent);
        return (page, entities);
    }

    private XsrUiEntityId LoadBedrockInstallPage()
    {
        (XsrUiEntityId page, Dictionary<string, XsrUiEntityId> entities) = LoadStandalonePage(
            "Ui.BedrockInstallPage.pxml", "bedrock-install-page-loader");
        StyleBedrockInstallPage(entities);
        return page;
    }

    private (XsrUiEntityId Page, Dictionary<string, XsrUiEntityId> Entities) LoadStandalonePage(
        string resource,
        string hostKey)
    {
        PxmlHostIr ir = PxmlCompiler.Compile(PxmlParser.Parse(ReadEmbeddedResource(resource)));
        XsrUiEntityId host = _shell.Tree.Create(hostKey);
        XsrUiEntityId page = PxmlUiLoader.Load(ir, _shell.Tree, _store, host);
        _shell.Tree.Detach(page);
        _shell.Tree.Destroy(host);

        Dictionary<string, XsrUiEntityId> entities = [];
        _shell.Tree.Walk(page, entity =>
        {
            string key = _shell.Tree.Name(entity);
            if (key.Length > 0)
            {
                entities[key] = entity;
            }

            return true;
        });
        return (page, entities);
    }

    /// <summary>
    /// Applies a restrained, solid-surface hierarchy: one obvious primary choice per card,
    /// compact supporting copy, and no glass material. Motion itself remains renderer-owned
    /// (press/hover and navigator presentation), so this styling never invents a second clock.
    /// </summary>
    private void StyleInstallPage(Dictionary<string, XsrUiEntityId> entities)
    {
        ApplyVisual(entities["InstallJavaChoice"], InstallJavaTint, PrimaryText,
            XsrUiCornerRadii.Surface, border: CardBorder, hover: InstallJavaHover);
        ApplyVisual(entities["InstallBedrockChoice"], InstallBedrockTint, PrimaryText,
            XsrUiCornerRadii.Surface, border: CardBorder, hover: InstallBedrockHover);
        ApplyVisual(entities["InstallJavaArtwork"], XsrUiColor.Transparent, InstallJavaAccent,
            XsrUiCornerRadii.Inset);
        ApplyVisual(entities["InstallBedrockArtwork"], XsrUiColor.Transparent, InstallBedrockAccent,
            XsrUiCornerRadii.Inset);
        StyleText(entities, "InstallJavaTitle", PrimaryText, 28, 650);
        StyleText(entities, "InstallBedrockTitle", PrimaryText, 28, 650);
        AlignText(entities, "InstallJavaTitle", XsrUiTextAlignment.Center);
        AlignText(entities, "InstallBedrockTitle", XsrUiTextAlignment.Center);
    }

    private void StyleJavaInstallPage(Dictionary<string, XsrUiEntityId> entities)
    {
        ApplyVisual(entities["JavaInstallVersionInput"], new(255, 255, 255), PrimaryText,
            XsrUiCornerRadii.Inset, border: CardBorder);
        StyleText(entities, "JavaInstallVersionInput", PrimaryText, 14);
        ApplyVisual(entities["JavaInstallStart"], InstallJavaAccent, new(255, 255, 255),
            XsrUiCornerRadii.Pill(40), hover: LaunchButtonHover);
        StyleText(entities, "JavaInstallStart", new(255, 255, 255), 13, 650);
        AlignText(entities, "JavaInstallStart", XsrUiTextAlignment.Center);

        // Navigation, catalog and commit action are separate spatial groups. The pager and
        // press transitions remain owned by UI.Next, preserving interruptible motion.
        ApplyVisual(entities["JavaInstallPagerTabs"], ProfileSurface, PrimaryText,
            10);
        _shell.Tree.SetComponent(entities["JavaInstallPagerTabs"], new XsrUiSegmentedTrack(entities["JavaInstallThumb"]));
        _shell.Tree.SetComponent(entities["JavaInstallThumb"], new XsrUiTransition());
        ApplyVisual(entities["JavaInstallThumb"], new(255, 255, 255), PrimaryText, 8);
        ApplyVisual(entities["JavaInstallEntryRow"], XsrUiColor.Transparent, PrimaryText,
            XsrUiCornerRadii.Surface);

        ApplyVisual(entities["JavaMinecraftVersions"], new(255, 255, 255), PrimaryText, XsrUiCornerRadii.Surface, border: CardBorder);
        foreach (JavaInstallSubpage subpage in JavaInstallSubpages)
        {
            if (entities.TryGetValue(subpage.PageKey, out XsrUiEntityId page))
            {
                ApplyVisual(page, XsrUiColor.Transparent, PrimaryText, 0);
            }

            StyleText(entities, subpage.PageKey + "Title", PrimaryText, 19, 650);
            _shell.Tree.SetComponent(entities[subpage.TabKey], new XsrUiSegmentReveal(_shell.Tree.GetComponent<XsrUiElement>(entities[subpage.TabKey])!.Width!.Value));
            ApplyPagerTab(entities, subpage.TabKey, active: subpage.PageKey == "JavaMinecraftPage");
        }
    }

    private void StyleBedrockInstallPage(Dictionary<string, XsrUiEntityId> entities)
    {
        StyleText(entities, "BedrockInstallTitle", InstallBedrockAccent, 20, 650);
        StyleText(entities, "BedrockInstallDescription", SecondaryText, 13);
        SetWrap(entities, "BedrockInstallDescription");
        ApplyVisual(entities["BedrockInstallCard"], InstallBedrockTint, PrimaryText,
            XsrUiCornerRadii.Surface, border: CardBorder);
        StyleText(entities, "BedrockInstallReturn", SecondaryText, 13);
        SetWrap(entities, "BedrockInstallReturn");
    }

    private void UpdateJavaInstallSubpageVisibility(string? preferredPage = null)
    {
        XsrUiEntityId pagerEntity = _javaInstallEntities["JavaInstallPager"];
        XsrUiEntityId[] previousPages = VisibleJavaInstallPages(pagerEntity);
        foreach (JavaInstallSubpage subpage in JavaInstallSubpages)
        {
            bool visible = ShouldShowJavaInstallSubpage(subpage);
            SetInstallEntityVisible(subpage.PageKey, visible);
            SetInstallEntityVisible(subpage.TabKey, visible);
        }

        if (_selectedInstallLoader != "Fabric")
        {
            _selectedInstallAddons.Remove("Fabric API");
            UpdateInstallAddonStyle("Fabric API", "JavaFabricApiSelect", selected: false);
        }
        if (_selectedInstallLoader != "Quilt")
        {
            _selectedInstallAddons.Remove("QSL");
            UpdateInstallAddonStyle("QSL", "JavaQslSelect", selected: false);
        }

        string target = preferredPage ?? _activeJavaInstallPage;
        if (!_javaInstallEntities.TryGetValue(target, out XsrUiEntityId targetEntity)
            || !IsInstallEntityVisible(targetEntity))
        {
            target = "JavaMinecraftPage";
        }

        var nextPages = VisibleJavaInstallPages(pagerEntity);
        if (!previousPages.SequenceEqual(nextPages))
            _shell.Renderer.RebasePagerPage(pagerEntity, Array.IndexOf(nextPages, _javaInstallEntities[target]));
        ShowJavaInstallSubpage(target);
    }

    private bool ShouldShowJavaInstallSubpage(JavaInstallSubpage subpage)
    {
        if (subpage.PageKey == "JavaMinecraftPage") return true;
        if (!_installGameChosen) return false;
        InstallLoader? loader = subpage.PageKey switch
        {
            "JavaFabricApiPage" => InstallLoader.FabricApi,
            "JavaOptiFabricPage" => InstallLoader.OptiFabric,
            "JavaQslPage" => InstallLoader.Qsl,
            _ => ParseInstallLoader(subpage.Loader)
        };
        return QueryInstallEligibility()?.Loaders.Any(item => item.Loader == loader && item.Visible) == true;
    }

    private void SetInstallEntityVisible(string key, bool visible)
    {
        if (!_javaInstallEntities.TryGetValue(key, out XsrUiEntityId entity))
        {
            return;
        }

        if (_shell.Tree.GetComponent<XsrUiSegmentReveal>(entity) is not null)
        {
            _shell.Renderer.SetSegmentExpanded(entity, visible, immediate: !_attached);
            return;
        }
        XsrUiElement element = _shell.Tree.GetComponent<XsrUiElement>(entity) ?? new XsrUiElement();
        if (element.IsVisible == visible)
        {
            return;
        }

        element.IsVisible = visible;
        _shell.Tree.SetComponent(entity, element);
        _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }

    private bool IsInstallEntityVisible(XsrUiEntityId entity) =>
        _shell.Tree.GetComponent<XsrUiElement>(entity)?.IsVisible ?? true;

    private XsrUiEntityId[] VisibleJavaInstallPages(XsrUiEntityId pager) =>
        [.. _shell.Tree.Children(pager).Where(IsInstallEntityVisible)];

    private void RefreshJavaInstallPresentation()
    {
        if (!_javaInstallEntities.TryGetValue("JavaInstallPager", out XsrUiEntityId pagerEntity)
            || _shell.Tree.GetComponent<XsrUiPager>(pagerEntity) is not { } pager)
        {
            return;
        }

        XsrUiEntityId[] visiblePages = VisibleJavaInstallPages(pagerEntity);
        if (visiblePages.Length == 0)
        {
            return;
        }

        int current = Math.Clamp(pager.PageIndex, 0, visiblePages.Length - 1);
        JavaInstallSubpage? active = JavaInstallSubpages.FirstOrDefault(subpage =>
            _javaInstallEntities.TryGetValue(subpage.PageKey, out XsrUiEntityId page)
            && page == visiblePages[current]);
        if (active is null)
        {
            return;
        }

        if (current == _presentedJavaInstallPage && _activeJavaInstallPage == active.PageKey)
        {
            return;
        }

        _presentedJavaInstallPage = current;
        _activeJavaInstallPage = active.PageKey;
        foreach (JavaInstallSubpage subpage in JavaInstallSubpages)
        {
            ApplyPagerTab(_javaInstallEntities, subpage.TabKey, subpage.PageKey == active.PageKey);
        }
    }

    private void ApplyPagerTab(Dictionary<string, XsrUiEntityId> entities, string key, bool active)
    {
        if (!entities.TryGetValue(key, out XsrUiEntityId entity))
        {
            return;
        }

        ApplyVisual(entity,
            XsrUiColor.Transparent,
            active ? BadgeText : DesktopUiPalette.CapsuleForeground,
            0,
            hover: XsrUiColor.Transparent);
        if (active && entities.TryGetValue("JavaInstallPagerTabs", out XsrUiEntityId tabs))
            _shell.Tree.GetComponent<XsrUiSegmentedTrack>(tabs)!.Selected = entity;
        StyleText(entity, active ? BadgeText : DesktopUiPalette.CapsuleForeground, 13, active ? 650 : 500);
        AlignText(entity, XsrUiTextAlignment.Center);
        XsrUiSelection selection = _shell.Tree.GetComponent<XsrUiSelection>(entity) ?? new XsrUiSelection();
        selection.IsSelected = active;
        _shell.Tree.SetComponent(entity, selection);
        XsrUiSemantic? semantic = _shell.Tree.GetComponent<XsrUiSemantic>(entity);
        if (semantic is not null)
        {
            string name = JavaInstallSubpageName(key);
            semantic.Label = active
                ? $"{name}，当前页"
                : $"查看 {name} 设置";
        }

        _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }

    private void UpdateInstallAddonStyle(string addon, string key, bool selected)
    {
        if (!_javaInstallEntities.TryGetValue(key, out XsrUiEntityId entity))
        {
            return;
        }

        ApplyVisual(entity,
            selected ? InstallJavaTint : new(255, 255, 255),
            selected ? InstallJavaAccent : PrimaryText,
            XsrUiCornerRadii.Pill(36),
            border: selected ? InstallSelectedBorder : CardBorder,
            hover: selected ? InstallJavaTint : PickerBackground);
        StyleText(entity, selected ? InstallJavaAccent : PrimaryText, 13, selected ? 650 : 550);
        AlignText(entity, XsrUiTextAlignment.Center);
        XsrUiSelection selection = _shell.Tree.GetComponent<XsrUiSelection>(entity) ?? new XsrUiSelection();
        selection.IsSelected = selected;
        _shell.Tree.SetComponent(entity, selection);
        XsrUiSemantic? semantic = _shell.Tree.GetComponent<XsrUiSemantic>(entity);
        if (semantic is not null)
        {
            semantic.Label = selected ? $"移除 {addon}" : $"添加 {addon}";
        }
        _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }

    private static string JavaInstallSubpageName(string tabKey) => tabKey switch
    {
        "JavaMinecraftTab" => "Minecraft",
        "JavaForgeTab" => "Forge",
        "JavaCleanroomTab" => "Cleanroom",
        "JavaNeoForgeTab" => "NeoForge",
        "JavaFabricTab" => "Fabric",
        "JavaLegacyFabricTab" => "Legacy Fabric",
        "JavaFabricApiTab" => "Fabric API",
        "JavaQuiltTab" => "Quilt",
        "JavaQslTab" => "QSL",
        "JavaOptiFabricTab" => "OptiFabric",
        "JavaLabyModTab" => "LabyMod",
        "JavaOptiFineTab" => "OptiFine",
        "JavaLiteLoaderTab" => "LiteLoader",
        _ => "安装选项",
    };

    private void StyleInstallChoices(
        Dictionary<string, XsrUiEntityId> entities,
        IReadOnlyList<string> keys,
        string selectedKey,
        XsrUiColor selectedBackground,
        XsrUiColor accent)
    {
        foreach (string key in keys)
        {
            if (!entities.TryGetValue(key, out XsrUiEntityId entity))
            {
                continue;
            }

            bool selected = key == selectedKey;
            if (key.StartsWith("JavaVersion", StringComparison.Ordinal))
            {
                ApplyVisual(entity, selected ? ProfileSurface : XsrUiColor.Transparent, PrimaryText,
                    XsrUiCornerRadii.Inset, hover: PickerBackground);
                StyleText(entities, key + "Name", selected ? BadgeText : PrimaryText, 15, selected ? 600 : 400);
                XsrUiEntityId check = entities[key + "Check"];
                ApplyVisual(check, XsrUiColor.Transparent, selected ? BadgeText : XsrUiColor.Transparent, 0);
                XsrUiSelection rowSelection = _shell.Tree.GetComponent<XsrUiSelection>(entity) ?? new XsrUiSelection();
                rowSelection.IsSelected = selected;
                _shell.Tree.SetComponent(entity, rowSelection);
                _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
                continue;
            }

            ApplyVisual(entity,
                selected ? selectedBackground : new(255, 255, 255),
                selected ? accent : PrimaryText,
                XsrUiCornerRadii.Inset,
                border: selected ? InstallSelectedBorder : CardBorder,
                hover: selected ? selectedBackground : PickerBackground);
            StyleText(entity, selected ? accent : PrimaryText, 13, selected ? 650 : 550);
            XsrUiSelection selection = _shell.Tree.GetComponent<XsrUiSelection>(entity) ?? new XsrUiSelection();
            selection.IsSelected = selected;
            _shell.Tree.SetComponent(entity, selection);
            _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
        }
    }

    private void SetWrap(Dictionary<string, XsrUiEntityId> entities, string key)
    {
        if (entities.TryGetValue(key, out XsrUiEntityId entity))
        {
            RequireVisual(entity).WrapText = true;
            _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
        }
    }

    private sealed record JavaInstallSubpage(
        string PageKey,
        string TabKey,
        XsrSemanticId Command,
        string? Loader = null,
        string? RequiresLoader = null);

    private XsrUiEntityId LoadVersionSubpage(string key, string title)
    {
        PxmlHostIr template = PxmlCompiler.Compile(PxmlParser.Parse(ReadEmbeddedResource("Ui.VersionSubpage.pxml")));
        PxmlIrNode Project(PxmlIrNode node) => node with
        {
            Key = node.Key == "VersionSubpage" ? key : node.Key,
            Label = node.Key == "VersionSubpage" ? title : node.Label,
            Content = node.Key == "MigrationTitle" ? title + " · 尚未迁移" : node.Content,
            Children = [.. node.Children.Select(Project)],
        };
        XsrUiEntityId parent = _shell.Tree.Create("subpage-loader");
        XsrUiEntityId page = PxmlUiLoader.Load(new PxmlHostIr(Project(template.Root)), _shell.Tree, _store, parent);
        _shell.Tree.Detach(page);
        _shell.Tree.Destroy(parent);
        _shell.Tree.Walk(page, entity =>
        {
            string name = _shell.Tree.Name(entity);
            XsrUiVisualStyle style = new() { Foreground = PrimaryText, FontSize = 14, TextAlignment = XsrUiTextAlignment.Center };
            if (name == "MigrationCard") { style.Background = new(245, 248, 252); style.CornerRadius = 20; }
            if (name == "MigrationTitle") { style.FontSize = 22; style.FontWeight = 600; }
            if (name == "MigrationMessage") { style.Foreground = SecondaryText; style.WrapText = true; }
            if (name == "MigrationIcon") style.Foreground = LaunchButtonBackground;
            if (name == "MigrationReturn") { style.Background = LaunchButtonBackground; style.Foreground = new(255, 255, 255); style.CornerRadius = 19; }
            _shell.Tree.SetComponent(entity, style);
            return true;
        });
        return page;
    }

    /// <summary>
    /// Loads the dedicated launching page: the legacy launching card (centered 420px card with
    /// the progress bar, key/value rows, trivia hint, and cancel) as its own navigation page.
    /// </summary>
    private (XsrUiEntityId Page, Dictionary<string, XsrUiEntityId> Entities) LoadLaunchingPage()
    {
        PxmlDocument document = PxmlParser.Parse(ReadEmbeddedResource("Ui.LaunchingPage.pxml"));
        PxmlHostIr ir = PxmlCompiler.Compile(document);
        XsrUiEntityId parent = _shell.Tree.Create("launching-page-loader");
        XsrUiEntityId page = PxmlUiLoader.Load(ir, _shell.Tree, _store, parent);
        _shell.Tree.Detach(page);
        _shell.Tree.Destroy(parent);

        Dictionary<string, XsrUiEntityId> entities = [];
        _shell.Tree.Walk(
            page,
            entity =>
            {
                string key = _shell.Tree.Name(entity);
                if (key.Length > 0)
                {
                    entities[key] = entity;
                }

                return true;
            });

        XsrUiVisualStyle card = RequireVisual(entities["LaunchingCard"]);
        card.Background = CardBackground;
        card.Foreground = PrimaryText;
        card.Border = CardBorder;
        card.BorderWidth = 1;
        card.Surface = XsrUiSurfaceKind.Solid;
        card.CornerRadius = XsrUiCornerRadii.Surface;
        _shell.Tree.MarkDirty(entities["LaunchingCard"], XsrUiDirtyKinds.Paint);
        StyleText(entities, "LaunchingTitle", PrimaryText, 22, 600);
        StyleText(entities, "LaunchingName", SecondaryText, 14);
        foreach ((string label, string value) in new[]
        {
            ("LaunchingStageLabel", "LaunchingStageValue"),
            ("LaunchingMethodLabel", "LaunchingMethodValue"),
            ("LaunchingPercentLabel", "LaunchingPercentValue"),
            ("LaunchingSpeedLabel", "LaunchingSpeedValue"),
        })
        {
            StyleText(entities, label, SecondaryText, 12);
            StyleText(entities, value, PrimaryText, 12);
        }

        StyleText(entities, "LaunchingHintTitle", SecondaryText, 11, 600);
        StyleText(entities, "LaunchingHintValue", SecondaryText, 12);
        AlignText(entities, "LaunchingHintTitle", XsrUiTextAlignment.Center);
        AlignText(entities, "LaunchingHintValue", XsrUiTextAlignment.Center);
        ApplyVisual(entities["LaunchProgressTrack"], LaunchProgressTrack, PrimaryText, cornerRadius: 2);
        ApplyVisual(entities["LaunchProgressFill"], LaunchProgressFill, LaunchProgressFill, cornerRadius: 2);
        ApplyVisual(entities["LaunchingHintBox"], PickerBackground, PrimaryText, XsrUiCornerRadii.Inset);
        ApplyVisual(entities["LaunchingCancelButton"], PickerBackground, PrimaryText,
            XsrUiCornerRadii.Pill(40));
        StyleText(entities, "LaunchingCancelButton", PrimaryText, 14, 600);
        AlignText(entities, "LaunchingCancelButton", XsrUiTextAlignment.Center);
        return (page, entities);
    }

    private XsrUiEntityId _observedPage;
    private void OnFramePreparing(object? sender, EventArgs e)
    {
        var currentPage = _shell.Stage.Navigation.Current;
        if (_observedPage != currentPage)
        {
            if (_observedPage == _javaInstallPage) ResetInstallSelection();
            _observedPage = currentPage;
            UpdateTitleBar();
        }
        ProjectInstallEditor();
        ProjectProcessFeedback();
        Publish(LaunchPageState.LaunchingVisibleKey, _launchInProgress != 0 && currentPage != _launchingPage);
        ProjectLibrary();
        RefreshJavaInstallPresentation();
        ProjectInstallCatalog();
        ProjectInstallEditPlan();
        if (Interlocked.Exchange(ref _pendingCloseLaunching, 0) == 1)
        {
            CloseLaunchingPage();
        }

        RefreshAccountPresentation();
        RefreshWidgetPresentation();
    }

    private void RefreshWidgetPresentation()
    {
        XsrUiPager pager = _shell.Tree.GetComponent<XsrUiPager>(_pageEntities["LaunchWidgetPager"])!;
        int index = pager.PageIndex;
        if (index != _presentedWidgetIndex)
        {
            _presentedWidgetIndex = index;
            Publish(LaunchPageState.WidgetAboutLabelKey, index == 0 ? "关于 Nexa，当前卡片" : "查看关于 Nexa");
            Publish(LaunchPageState.WidgetTriviaLabelKey, index == 1 ? "你知道吗，当前卡片" : "查看你知道吗");
            Publish(LaunchPageState.WidgetEchoLabelKey, index == 2 ? "回声洞，当前卡片" : "查看回声洞");
        }
        double position = Math.Clamp(pager.Position, 0, 2);
        if (position == _indicatorPosition) return;
        _indicatorPosition = position;
        UpdateWidgetDot("WidgetAboutDot", "WidgetAboutIndicator", Math.Max(0, 1 - position));
        UpdateWidgetDot("WidgetTriviaDot", "WidgetTriviaIndicator", Math.Max(0, 1 - Math.Abs(position - 1)));
        UpdateWidgetDot("WidgetEchoDot", "WidgetEchoIndicator", Math.Max(0, position - 1));
    }

    private void UpdateWidgetDot(string key, string buttonKey, double activation)
    {
        XsrUiEntityId entity = _pageEntities[key];
        _shell.Tree.GetComponent<XsrUiElement>(entity)!.Height = 6 + 10 * activation;
        XsrUiEntityId button = _pageEntities[buttonKey];
        _shell.Tree.GetComponent<XsrUiElement>(button)!.Height = 6 + 10 * activation;
        _shell.Tree.GetComponent<XsrUiVisualStyle>(entity)!.Background = new XsrUiColor(11, 91, 203,
            (byte)Math.Round(64 + 191 * activation));
        _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
        _shell.Tree.MarkDirty(button, XsrUiDirtyKinds.Layout);
    }

    private void RefreshAccountPresentation()
    {
        XsrCollectionSnapshot<LaunchProfileView> roster = _store.ReadCollection<LaunchProfileView>(
            _store.Resolve(AccountService.ProfilesKey));
        int selected = SelectedAccountIndex;
        bool refreshSkins = roster.Revision != _accountRosterRevision || selected != _presentedAccountIndex;
        if (roster.Revision != _accountRosterRevision)
        {
            BuildAccountRows(roster.Items);
            _accountRosterRevision = roster.Revision;
            _presentedAccountIndex = -2;
            _skinRevision = -1;
        }

        if (selected != _presentedAccountIndex)
        {
            StyleAccountRows();
            _presentedAccountIndex = selected;
            LaunchProfileView profile = roster.Items.FirstOrDefault(item => item.Index == selected);
            string avatar = LaunchProfilePresentation.Avatar(profile.Uuid ?? string.Empty);
            XsrUiEntityId image = _pageEntities["AccountAvatar"];
            _shell.Tree.GetComponent<XsrUiImage>(image)!.Source = avatar;
            _shell.Tree.MarkDirty(image, XsrUiDirtyKinds.Paint);
            _skinRevision = -1;
        }

        if (refreshSkins && _accountCommands?.TryResolve(AccountSkinService.RefreshRoute, out XsrCommandId skinRoute) == true)
            _ = _accountCommands.Dispatch(skinRoute, new AccountRefreshSkinsCommand(), cancellationToken: _lifetimeCancellation.Token).Completion;
        RefreshSkinPresentation(roster.Items, selected);

        bool hasSelection = roster.Items.Any(profile => profile.Index == selected);
        bool picker = !hasSelection || _store.ReadAppliedValue(_store.Resolve(LaunchPageState.AccountPickerKey)) is true;
        bool onboarding = _store.ReadAppliedValue(_store.Resolve(AccountFormState.Open)) is true;
        string motionKey = onboarding
            ? "form:" + ReadCell(AccountFormState.Mode) + ":" + _store.Read<AccountLoginSnapshot>(_store.Resolve(AccountOnboardingState.Login)).Value?.Phase
            : picker ? "roster" : "selected:" + selected;
        Publish(LaunchPageState.AccountTransitionKey, motionKey);
        if (_accountMotionKey != motionKey)
        {
            XsrUiTransition.ConfigureIndependent(_shell.Tree, _pageEntities["AccountBody"], "account:" + motionKey);
            _accountMotionKey = motionKey;
        }
        Publish(LaunchPageState.AccountRosterVisibleKey, picker && !onboarding);
        Publish(LaunchPageState.AccountSelectedVisibleKey, !picker && !onboarding);
        Publish(LaunchPageState.AccountCanReturnKey, hasSelection);
        Publish(LaunchPageState.AccountTitleKey, onboarding
            ? _store.ReadAppliedValue(_store.Resolve(AccountFormState.Key("title"))) as string ?? "添加账户"
            : picker && hasSelection ? "切换档案" : "账户");
        Publish(LaunchPageState.AccountBackVisibleKey, onboarding || (picker && hasSelection));
        Publish(LaunchPageState.AccountAddVisibleKey, !onboarding);
        Publish(LaunchPageState.AccountHintVisibleKey, roster.Count == 0 || roster.Availability == XsrStateAvailability.Unavailable);
        Publish(LaunchPageState.AccountHintKey, roster.Availability == XsrStateAvailability.Unavailable
            ? "账户安全存储不可用，原档案已保留。\n请解锁系统密钥库并重启后重试。" : roster.Count == 0
            ? "还没有账户档案。\n点击上方＋添加，或导入旧档案。"
            : string.Empty);
        if (_presentedAccountPicker is { } previous && previous != picker
            && _shell.Stage.Navigation.Current == _launchPage)
        {
            XsrUiEntityId focus = picker
                ? _accountRowEntities.GetValueOrDefault(selected, _accountRowEntities.Values.FirstOrDefault())
                : _pageEntities["AccountSwitch"];
            if (focus.IsAssigned) _shell.Renderer.Focus(focus, _accountKeyboardFocus);
        }
        _presentedAccountPicker = picker;
        PublishProfileFacts();
        UpdateLaunchButton();
    }

    private void RefreshSkinPresentation(IReadOnlyList<LaunchProfileView> profiles, int selected)
    {
        var skins = _store.ReadCollection<AccountSkinSnapshot>(_store.Resolve(AccountSkinService.SkinsKey));
        if (_skinRevision == skins.Revision) return;
        _skinRevision = skins.Revision;
        Dictionary<string, AccountSkinSnapshot> images = skins.Items.ToDictionary(item => item.ProfileKey);
        foreach (LaunchProfileView profile in profiles)
        {
            XsrUiRasterImage? raster = images.TryGetValue(AccountSkinService.ProfileKey(profile), out AccountSkinSnapshot? skin)
                && skin.Image is { } png ? LaunchProfilePresentation.Head(png) : null;
            if (_accountRowEntities.TryGetValue(profile.Index, out XsrUiEntityId row))
                _shell.Tree.Walk(row, entity =>
                {
                    if (_shell.Tree.Name(entity).StartsWith("ProfileAvatar:", StringComparison.Ordinal)) SetRaster(entity, raster);
                    return true;
                });
            if (profile.Index == selected) SetRaster(_pageEntities["AccountAvatar"], raster);
        }
    }

    private void SetRaster(XsrUiEntityId entity, XsrUiRasterImage? raster)
    {
        _shell.Tree.GetComponent<XsrUiImage>(entity)!.Raster = raster;
        _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
    }

    /// <summary>
    /// Rebuilds the account card's profile list: one clickable row per roster profile, the
    /// selected one highlighted. Rows emit <see cref="AccountSelectCommand"/> with themselves
    /// as the intent source, so the renderer keeps owning invocation and correlation.
    /// </summary>
    private void BuildAccountRows(IReadOnlyList<LaunchProfileView> profiles)
    {
        if (!_pageEntities.TryGetValue("AccountRows", out XsrUiEntityId rowsHost))
        {
            return;
        }

        XsrUiTree tree = _shell.Tree;
        foreach (XsrUiEntityId row in _accountRowEntities.Values)
        {
            tree.Destroy(row);
        }

        _accountRowEntities.Clear();
        _accountRowIndexes.Clear();

        foreach (LaunchProfileView profile in profiles)
        {
            XsrUiEntityId row = PxmlUiLoader.Load(new PxmlHostIr(ProjectProfileNode(_accountRowTemplate.Root, profile)),
                tree, _store, rowsHost);
            tree.SetComponent(row, new XsrUiSelection());
            _accountRowEntities[profile.Index] = row;
            _accountRowIndexes[row] = profile.Index;
        }

        tree.MarkDirty(rowsHost, XsrUiDirtyKinds.Structure);
        StyleAccountRows();
    }

    private static PxmlIrNode ProjectProfileNode(PxmlIrNode node, LaunchProfileView profile) => node with
    {
        Key = node.Key == "AccountRow" ? $"account-row:{profile.Index}" : $"{node.Key}:{profile.Index}",
        Label = node.Key == "AccountRow" ? $"选择 {profile.Username}，{ProfileKind(profile.Kind)}"
            : node.Key == "ProfileDelete" ? $"删除档案 {profile.Username}" : node.Label,
        Content = node.Key switch
        {
            "ProfileName" => profile.Username,
            "ProfileDetail" => LaunchProfilePresentation.Description(profile),
            _ => node.Content,
        },
        ImageSource = node.Key == "ProfileAvatar" ? LaunchProfilePresentation.Avatar(profile.Uuid) : node.ImageSource,
        Children = [.. node.Children.Select(child => ProjectProfileNode(child, profile))],
    };

    private static string ProfileKind(LaunchProfileKind kind) => kind switch
    {
        LaunchProfileKind.Microsoft => "Microsoft 账户",
        LaunchProfileKind.ThirdParty => "第三方账户",
        LaunchProfileKind.Offline => "离线账户",
        LaunchProfileKind.LittleSkin => "LittleSkin 账户",
        LaunchProfileKind.NCloud => "NCloud 账户",
        _ => "账户档案",
    };

    private void StyleAccountRows()
    {
        foreach ((int index, XsrUiEntityId row) in _accountRowEntities)
        {
            bool selected = index == SelectedAccountIndex;
            if (_shell.Tree.GetComponent<XsrUiSelection>(row) is { } selection)
            {
                selection.IsSelected = selected;
            }

            ApplyVisual(
                row,
                selected ? BadgeBackground : ProfileSurface,
                PrimaryText,
                cornerRadius: XsrUiCornerRadii.Inset, hover: PickerBackground);
            _shell.Tree.Walk(row, entity =>
            {
                string key = _shell.Tree.Name(entity);
                if (key.StartsWith("ProfileDelete:", StringComparison.Ordinal))
                {
                    ApplyVisual(entity, XsrUiColor.Transparent, ProfileSecondaryText, XsrUiCornerRadii.Pill(28),
                        hover: new XsrUiColor(255, 224, 224));
                    return true;
                }
                bool detail = key.StartsWith("ProfileDetail:", StringComparison.Ordinal);
                StyleText(entity, detail ? ProfileSecondaryText : selected ? BadgeText : PrimaryText,
                    fontSize: detail ? 12 : 14, weight: detail ? 400 : 600);
                return true;
            });
        }
    }

    internal XsrUiEntityId VersionSettingsPage
    {
        set { _shell.Tree.Destroy(_versionSettingsPage); _versionSettingsPage = value; }
    }
    internal XsrUiEntityId SettingsPage { get; set; }

    private void ShowPlaceholder(bool settings = false)
    {
        ClearSubpageHistory();
        if (settings && SettingsPage.IsAssigned)
        {
            _shell.Stage.Navigation.Replace(SettingsPage);
            return;
        }
        if (!_shell.Stage.Navigation.Current.Equals(_placeholderPage))
        {
            _shell.Stage.Navigation.Replace(_placeholderPage);
        }
    }

    private void NavigateToDownload()
    {
        _ = _shell.Select(DownloadNavigationId);
        ShowInstallRoot();
        _feedback.Info("请在安装页选择或下载游戏版本。");
    }

    private void PublishProfileFacts()
    {
        IReadOnlyList<LaunchProfileView> profiles = ReadProfiles();
        int index = SelectedAccountIndex;
        if (index >= 0 && index < profiles.Count)
        {
            Publish(LaunchPageState.ProfileNameKey, profiles[index].Username);
            Publish(LaunchPageState.ProfileKindKey, LaunchProfilePresentation.Description(profiles[index]));
        }
        else
        {
            Publish(LaunchPageState.ProfileNameKey, NoAccountName);
            Publish(LaunchPageState.ProfileKindKey, string.Empty);
        }
    }

    private async Task StartLaunchAsync(string instanceId, XsrUiEntityId source)
    {
        IReadOnlyList<LaunchProfileView> profiles = ReadProfiles();
        int selected = SelectedAccountIndex;
        if (!profiles.Any(profile => profile.Index == selected))
        {
            _feedback.Warn(AccountNeedLoginSummary);
            return;
        }

        if (!_minecraft.Commands.TryResolve(MinecraftRouteIds.Start, out XsrCommandId commandId))
        {
            _feedback.Error("启动失败：产品启动命令未注册。");
            return;
        }

        if (_launchInProgress != 0) return;
        _launchingInstanceId = instanceId;
        _launchingRoot = ReadCell(LaunchPageState.InstanceDirectoryKey);
        ShowLaunchingPage(source);

        // The launching page replicates the legacy launching card: reset facts, narrate the
        // pipeline through the launch progress cells, and return on failure or cancellation.
        _launchInProgress = 1;
        Publish(LaunchPageState.LaunchingTitleKey, "正在启动");
        Publish(LaunchPageState.LaunchingNameKey, ReadCell(LaunchPageState.InstanceSummaryKey));
        Publish(LaunchPageState.LaunchingStageKey, "初始化");
        Publish(LaunchPageState.LaunchingMethodKey, "等待账户档案");
        Publish(LaunchPageState.LaunchingPercentKey, "0%");
        Publish(LaunchPageState.LaunchingSpeedVisibleKey, false);
        Publish(LaunchPageState.LaunchingHintKey, LaunchWidgetHints.BuiltIn[_hintIndex]);
        RefreshLaunchingDisplay();

        XsrCommandDispatch dispatch = _minecraft.Commands.Dispatch(
            commandId,
            new MinecraftStartCommand(instanceId, selected)
            { MinecraftRootDirectory = ReadCell(LaunchPageState.InstanceDirectoryKey) },
            cancellationToken: _lifetimeCancellation.Token);
        XsrResult result = await dispatch.Completion.ConfigureAwait(false);
        if (_disposed)
        {
            return;
        }

        if (result.IsSuccess)
        {
            // The window confirmation (or its platform fallback) already passed: success
            // closes the page immediately so the user is back at the library while the game
            // warms up, instead of parking on a 游戏已启动 card until the session ends.
            _feedback.Info("Minecraft 已启动。");
            RequestCloseLaunchingPage();
            return;
        }

        _feedback.Error($"启动失败：{result.Error?.Message}");
        RequestCloseLaunchingPage();
    }

    private async Task CancelLaunchAsync()
    {
        if (_launchInProgress == 0)
        {
            return;
        }

        // Once the game is running there is no pipeline left to cancel — the button has become
        // "back" — so just leave the page without touching the process.
        bool launched = _store.ReadAppliedValue(_store.Resolve(MinecraftLaunchProgressState.SnapshotKey))
            is MinecraftLaunchProgressSnapshot snapshot && snapshot.IsLaunched;
        if (launched)
        {
            RequestCloseLaunchingPage();
            return;
        }

        Publish(LaunchPageState.LaunchingStageKey, "已请求取消启动");
        if (_minecraft.Commands.TryResolve(MinecraftRouteIds.LaunchCancel, out XsrCommandId route))
        {
            await _minecraft.Commands.Dispatch(route, new MinecraftCancelLaunchCommand(),
                cancellationToken: _lifetimeCancellation.Token).Completion.ConfigureAwait(false);
        }

        if (!_disposed)
        {
            RequestCloseLaunchingPage();
        }
    }

    /// <summary>
    /// Requests the launching page to close from any thread. The close itself mutates the
    /// navigation stack, tree components, and focus — all render-thread state — so it is
    /// drained on the next frame preparation instead of running here.
    /// </summary>
    private void RequestCloseLaunchingPage()
    {
        Interlocked.Exchange(ref _pendingCloseLaunching, 1);
    }

    /// <summary>
    /// Opens the dedicated launching page (a navigation push, mirroring the subpage flow) and
    /// records where to restore focus when it closes.
    /// </summary>
    private void ShowLaunchingPage(XsrUiEntityId source)
    {
        if (_shell.Stage.Navigation.Current == _launchingPage) return;
        _launchingViaKeyboard = IsKeyboardIntent(source);
        _returnFocus.Push(source);
        _shell.Stage.Navigation.Push(_launchingPage);
        UpdateTitleBar();
        _shell.Renderer.Focus(_launchingEntities.GetValueOrDefault("LaunchingCancelButton"), _launchingViaKeyboard);
    }

    private async Task DecideAcquisitionAsync(bool approve)
    {
        if (_launchInProgress == 0
            || !_minecraft.Commands.TryResolve(MinecraftRouteIds.AcquireDecide, out XsrCommandId route))
        {
            _feedback.Error("Java 下载确认命令未注册。");
            return;
        }

        XsrResult result = await _minecraft.Commands.Dispatch(
            route,
            new MinecraftDecideJavaAcquisitionCommand(approve),
            cancellationToken: _lifetimeCancellation.Token).Completion.ConfigureAwait(false);
        if (!_disposed && !result.IsSuccess)
        {
            _feedback.Error($"Java 下载确认失败：{result.Error?.Message}");
        }
    }

    private void CloseLaunchingPage()
    {
        Interlocked.Exchange(ref _launchInProgress, 0);
        UpdateLaunchButton();
        DismissAcquisitionDialog();
        if (_javaChoicePage.IsAssigned && _shell.Stage.Navigation.Current == _javaChoicePage)
        { _shell.Stage.Navigation.Pop(); _returnFocus.TryPop(out _); }
        if (_shell.Stage.Navigation.Current != _launchingPage)
        {
            return;
        }

        _ = _shell.Stage.Navigation.Pop();
        UpdateTitleBar();
        if (_returnFocus.TryPop(out XsrUiEntityId focus))
        {
            _shell.Renderer.Focus(focus, _launchingViaKeyboard);
        }
    }

    /// <summary>
    /// Projects the services launch progress cells into the overlay display strings: stage
    /// tokens become legacy stage labels, the progress fraction formats as whole percent, and
    /// the title switches to the launched state once the pipeline reports the game running.
    /// </summary>
    private void RefreshLaunchingDisplay()
    {
        string stage = ReadServiceCell(MinecraftLaunchProgressState.StageKey);
        if (stage.Length > 0)
        {
            Publish(LaunchPageState.LaunchingStageKey,
                LaunchStageDisplay.GetValueOrDefault(stage, stage));
        }

        double progress = _store.ReadAppliedValue(_store.Resolve(MinecraftLaunchProgressState.ProgressKey)) is double value
            ? Math.Clamp(value, 0d, 1d)
            : 0d;
        Publish(LaunchPageState.LaunchingPercentKey, Math.Round(progress * 100) + "%");

        string method = ReadServiceCell(MinecraftLaunchProgressState.MethodKey);
        Publish(LaunchPageState.LaunchingMethodKey,
            method.Length == 0 ? "等待账户档案" : LaunchMethodDisplay.GetValueOrDefault(method, method));

        string speed = ReadServiceCell(MinecraftLaunchProgressState.SpeedKey);
        Publish(LaunchPageState.LaunchingSpeedKey, speed);
        Publish(LaunchPageState.LaunchingSpeedVisibleKey, speed.Length > 0);

        bool launched = _store.ReadAppliedValue(_store.Resolve(MinecraftLaunchProgressState.LaunchedKey)) is bool running && running;
        Publish(LaunchPageState.LaunchingTitleKey, launched ? "游戏已启动" : "正在启动");
        Publish(LaunchPageState.LaunchingCancelLabelKey, launched ? "返回" : "取消");
    }

    /// <summary>
    /// Projects the acquisition cells into the shared window-internal dialog. The feedback
    /// service is thread-safe; its presenter performs all PXML mutations at frame preparation.
    /// </summary>
    private void RefreshAcquisitionPrompt()
    {
        bool pending = _store.ReadAppliedValue(_store.Resolve(MinecraftLaunchProgressState.AcquirePendingKey)) is bool waiting && waiting;
        string component = ReadServiceCell(MinecraftLaunchProgressState.AcquireComponentKey);
        int major = _store.ReadAppliedValue(_store.Resolve(MinecraftLaunchProgressState.AcquireMajorKey)) is int version ? version : 0;
        if (_launchInProgress != 0 && pending && major > 0 && component.Length > 0)
        {
            _javaAcquisitionDialog = _feedback.ShowDialog(
                "minecraft.java.acquire",
                $"需要 Java {major}",
                $"未找到兼容的 Java {major}。请选择已安装的 Java，或自动下载。",
                "自动下载",
                "取消",
                approve => _ = DecideAcquisitionAsync(approve),
                "选择 Java 版本", ShowJavaVersionChoice);
        }
        else if (!pending && _javaAcquisitionDialog is { } dialog)
        {
            _feedback.DismissDialog(dialog);
            _javaAcquisitionDialog = null;
        }
    }

    private void RefreshPreflightPrompt()
    {
        var prompt = _store.ReadAppliedValue(_store.Resolve(LaunchPreflightGate.StateKey)) as LaunchPreflightPrompt;
        if (prompt is null)
        {
            if (_preflightDialog is { } old) _feedback.DismissDialog(old);
            _preflightDialog = _preflightAttempt = null;
            return;
        }
        if (_preflightAttempt == prompt.Attempt) return;
        _preflightAttempt = prompt.Attempt;
        bool blocked = prompt.Report.Issues.Any(static issue => issue.Severity == Nexa.Services.Capabilities.PreflightSeverity.Blocked);
        string message = string.Join("\n\n", prompt.Report.CollapsedIssues.Select(issue => $"{issue.Title}\n{issue.Description}"));
        _preflightDialog = _feedback.ShowDialog("minecraft.preflight", blocked ? "需要先处理启动问题" : "启动前请确认",
            message, blocked ? "返回" : "仍然启动", "取消", approve => _ = DecidePreflightAsync(prompt.Attempt, approve && !blocked));
    }

    private async Task DecidePreflightAsync(Guid attempt, bool proceed)
    {
        if (!_minecraft.Commands.TryResolve(LaunchPreflightGate.DecisionCommand, out XsrCommandId route)) return;
        await _minecraft.Commands.Dispatch(route, new LaunchPreflightDecision(attempt, proceed),
            cancellationToken: _lifetimeCancellation.Token).Completion.ConfigureAwait(false);
    }

    private void DismissAcquisitionDialog()
    {
        if (_javaAcquisitionDialog is not { } dialog)
        {
            return;
        }

        _feedback.DismissDialog(dialog);
        _javaAcquisitionDialog = null;
    }

    private string ReadServiceCell(XsrSemanticId key) =>
        Convert.ToString(_store.ReadAppliedValue(_store.Resolve(key)), CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>
    /// Receives host state publications. Launch progress changes refresh the overlay; a
    /// terminal process session while the game was reported launched closes it, mirroring the
    /// legacy flow that returns to the launch page when the game exits.
    /// </summary>
    private sealed class LaunchingStateObserver(LaunchPageController owner) : IXsrStateObserver
    {
        public void OnChanged(XsrStateChange change)
        {
            if (change.SemanticId == LaunchPreflightGate.StateKey) { owner.RefreshPreflightPrompt(); return; }
            if (change.SemanticId == MinecraftLibraryService.StateKey) { owner.ProjectLibrary(); return; }
            if (change.SemanticId.Equals(MinecraftLaunchProgressState.StageKey)
                || change.SemanticId.Equals(MinecraftLaunchProgressState.ProgressKey)
                || change.SemanticId.Equals(MinecraftLaunchProgressState.MethodKey)
                || change.SemanticId.Equals(MinecraftLaunchProgressState.SpeedKey)
                || change.SemanticId.Equals(MinecraftLaunchProgressState.LaunchedKey))
            {
                owner.RefreshLaunchingDisplay();
                return;
            }

            if (change.SemanticId.Equals(MinecraftLaunchProgressState.AcquirePendingKey)
                || change.SemanticId.Equals(MinecraftLaunchProgressState.AcquireComponentKey)
                || change.SemanticId.Equals(MinecraftLaunchProgressState.AcquireMajorKey))
            {
                owner.RefreshAcquisitionPrompt();
                return;
            }

            // The narration belongs to one session: only THAT game's terminal state closes the
            // page. Other running games must keep the flow alive.
            if (owner._launchInProgress != 0
                && change.SemanticId.Equals(MinecraftProcessStateComposition.SessionsKey)
                && owner.LaunchedSessionId() is { } sessionId
                && sessionId != Guid.Empty
                && owner.IsSessionTerminal(sessionId))
            {
                owner.RequestCloseLaunchingPage();
            }
        }
    }

    /// <summary>The session this narration launched, from the coherent snapshot truth.</summary>
    private Guid? LaunchedSessionId() =>
        _store.ReadAppliedValue(_store.Resolve(MinecraftLaunchProgressState.SnapshotKey)) is MinecraftLaunchProgressSnapshot snapshot
            ? snapshot.SessionId
            : null;

    private bool IsSessionTerminal(Guid sessionId) =>
        _store.ReadCollection<MinecraftProcessSnapshot>(_store.Resolve(MinecraftProcessStateComposition.SessionsKey))
            .Items.Any(snapshot => snapshot.SessionId == sessionId
                && snapshot.State is MinecraftProcessState.Exited
                    or MinecraftProcessState.Failed
                    or MinecraftProcessState.Cancelled);


    private int SelectedAccountIndex => _store.ReadAppliedValue(_store.Resolve(AccountService.SelectedKey)) is int index ? index : -1;

    private IReadOnlyList<LaunchProfileView> ReadProfiles() =>
        _store.ReadCollection<LaunchProfileView>(_store.Resolve(AccountService.ProfilesKey)).Items;

    private void Publish<T>(XsrSemanticId key, T value)
    {
        XsrStateId id = _store.Resolve(key);
        if (!Equals(_store.ReadAppliedValue(id), value)) _store.Publish(id, value);
    }

    private string ReadCell(XsrSemanticId key) =>
        _store.Read<string>(_store.Resolve(key)).Value ?? string.Empty;

    private (XsrUiEntityId Page, Dictionary<string, XsrUiEntityId> Entities) LoadLaunchPage()
    {
        PxmlDocument document = PxmlParser.Parse(ReadEmbeddedResource("Ui.LaunchPage.pxml"));
        PxmlHostIr ir = PxmlCompiler.Compile(document);
        XsrUiEntityId host = _shell.Tree.Create("launch-page-host");
        XsrUiEntityId page = PxmlUiLoader.Load(ir, _shell.Tree, _store, host);
        _shell.Tree.Detach(page);
        _shell.Tree.Destroy(host);

        Dictionary<string, XsrUiEntityId> entities = [];
        _shell.Tree.Walk(
            page,
            entity =>
            {
                string key = _shell.Tree.Name(entity);
                if (key.Length > 0)
                {
                    entities[key] = entity;
                }

                return true;
            });
        StyleLaunchPage(page, entities);
        return (page, entities);
    }

    /// <summary>
    /// Applies the legacy experimental launch-home styling that the PXML control vocabulary
    /// cannot express: card surfaces, section typography, the badge, the picker row, and the
    /// accent launch button. PXML keys name internal handles; semantic labels remain human text.
    /// </summary>
    private void StyleLaunchPage(XsrUiEntityId page, Dictionary<string, XsrUiEntityId> entities)
    {
        StyleCard(entities, "CardAccount", cornerRadius: XsrUiCornerRadii.Surface);
        StyleCard(entities, "CardVersion", cornerRadius: XsrUiCornerRadii.Surface);
        StyleCard(entities, "CardAbout", cornerRadius: XsrUiCornerRadii.Surface);
        StyleText(entities, "AccountHeader", PrimaryText, fontSize: 18, weight: 600);
        StyleText(entities, "VersionHeader", SecondaryText, fontSize: 12, weight: 600);
        StyleText(entities, "AboutTitle", SecondaryText, fontSize: 12, weight: 600);
        StyleText(entities, "TriviaTitle", SecondaryText, fontSize: 12, weight: 600);
        StyleText(entities, "EchoTitle", SecondaryText, fontSize: 12, weight: 600);
        StyleText(entities, "AboutMessage", PrimaryText, fontSize: 14, weight: 600);
        StyleText(entities, "TriviaMessage", PrimaryText, fontSize: 14, weight: 600);
        StyleText(entities, "EchoMessage", PrimaryText, fontSize: 14, weight: 600);
        foreach (string key in new[] { "AboutMessage", "TriviaMessage", "EchoMessage" })
            _shell.Tree.GetComponent<XsrUiVisualStyle>(entities[key])!.WrapText = true;
        foreach (string key in new[] { "WidgetAboutIndicator", "WidgetTriviaIndicator", "WidgetEchoIndicator" })
            ApplyVisual(entities[key], XsrUiColor.Transparent, PrimaryText, cornerRadius: 3, hover: BadgeBackground);
        foreach (string key in new[] { "WidgetAboutDot", "WidgetTriviaDot", "WidgetEchoDot" })
            ApplyVisual(entities[key], BadgeText, PrimaryText, cornerRadius: 3);
        StyleText(entities, "AccountKind", ProfileSecondaryText, fontSize: 13);
        StyleText(entities, "AccountHint", ProfileSecondaryText, fontSize: 12);
        _shell.Tree.GetComponent<XsrUiVisualStyle>(entities["AccountHint"])!.WrapText = true;
        ApplyVisual(entities["AccountAvatarSurface"], ProfileSurface, BadgeText, XsrUiCornerRadii.Surface);
        ApplyVisual(entities["AccountBack"], ProfileSurface, ProfileSecondaryText, XsrUiCornerRadii.Pill(32), hover: BadgeBackground);
        ApplyVisual(entities["AccountAdd"], ProfileSurface, BadgeText, XsrUiCornerRadii.Pill(32),
            hover: BadgeBackground, hoverExpand: true);
        StyleText(entities, "AccountAdd", BadgeText, 13, 600);
        AlignText(entities, "AccountAdd", XsrUiTextAlignment.Center);
        ApplyVisual(entities["AccountImport"], ProfileSurface, BadgeText, XsrUiCornerRadii.Pill(34), hover: BadgeBackground);
        StyleText(entities, "AccountImport", BadgeText, 13, 600);
        AlignText(entities, "AccountImport", XsrUiTextAlignment.Center);
        StyleText(entities, "AccountAvatar", BadgeText, fontSize: 14);
        foreach (string key in new[] { "AccountName", "AccountKind" })
            AlignText(entities, key, XsrUiTextAlignment.Center);
        foreach (string key in new[] { "AccountSwitch", "AccountWardrobe" })
        {
            ApplyVisual(entities[key], DesktopUiPalette.CapsuleBackground, DesktopUiPalette.CapsuleForeground, XsrUiCornerRadii.Pill(36), hover: DesktopUiPalette.CapsuleHover, hoverExpand: true);
            StyleText(entities, key, BadgeText, 13, 600);
        }
        StyleText(entities, "VersionAction", SecondaryText, fontSize: 11);
        if (entities.TryGetValue("AccountName", out XsrUiEntityId accountName))
        {
            StyleText(accountName, PrimaryText, fontSize: 22, weight: 600);
        }

        if (entities.TryGetValue("VersionName", out XsrUiEntityId versionName))
        {
            StyleText(versionName, PrimaryText, fontSize: 20, weight: 600);
        }

        if (entities.TryGetValue("InstanceRow", out XsrUiEntityId pickerRow))
        {
            ApplyVisual(pickerRow, PickerBackground, PrimaryText, cornerRadius: XsrUiCornerRadii.Inset);
        }

        if (entities.TryGetValue("InstanceListButton", out XsrUiEntityId instanceListButton))
        {
            // Hover-expanding capsule: at rest an icon circle pinned to the right edge; on
            // hover the pill grows leftward and the function name fades in beside the icon.
            ApplyVisual(
                instanceListButton,
                PickerBackground,
                PrimaryText,
                cornerRadius: XsrUiCornerRadii.Pill(36),
                border: CardBorder,
                hoverExpand: true);
            StyleText(instanceListButton, PrimaryText, fontSize: 13, weight: 600);
            AlignText(instanceListButton, XsrUiTextAlignment.Center);
        }

        foreach (string key in new[] { "InstanceSettings", "InstanceModify" })
        {
            XsrUiEntityId action = entities[key];
            ApplyVisual(
                action,
                PickerBackground,
                PrimaryText,
                cornerRadius: XsrUiCornerRadii.Pill(36),
                border: CardBorder,
                hoverExpand: true);
            StyleText(action, PrimaryText, fontSize: 13, weight: 600);
            AlignText(action, XsrUiTextAlignment.Center);
        }


        if (entities.TryGetValue("LaunchButton", out XsrUiEntityId button))
        {
            // Legacy accent button: normal #0b5bcb, hover #1370f3, white 13 px semibold label.
            ApplyVisual(
                button,
                LaunchButtonBackground,
                new XsrUiColor(255, 255, 255),
                cornerRadius: XsrUiCornerRadii.Pill(44),
                hover: LaunchButtonHover);
            StyleText(button, new XsrUiColor(255, 255, 255), fontSize: 13, weight: 600);
            AlignText(button, XsrUiTextAlignment.Center);
        }
    }

    private void StyleCard(
        Dictionary<string, XsrUiEntityId> entities,
        string label,
        double cornerRadius)
    {
        if (entities.TryGetValue(label, out XsrUiEntityId card))
        {
            ApplyVisual(card, CardBackground, PrimaryText, cornerRadius, border: CardBorder);
        }
    }

    private void StyleText(Dictionary<string, XsrUiEntityId> entities, string label, XsrUiColor foreground, double fontSize, double weight = 400)
    {
        if (entities.TryGetValue(label, out XsrUiEntityId entity))
        {
            StyleText(entity, foreground, fontSize, weight);
        }
    }

    private void StyleText(XsrUiEntityId entity, XsrUiColor foreground, double fontSize, double weight = 400)
    {
        XsrUiVisualStyle visual = RequireVisual(entity);
        visual.Foreground = foreground;
        visual.FontSize = fontSize;
        visual.FontWeight = weight;
        _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Paint | XsrUiDirtyKinds.Layout);
    }

    private void AlignText(Dictionary<string, XsrUiEntityId> entities, string key, XsrUiTextAlignment alignment)
    {
        if (entities.TryGetValue(key, out XsrUiEntityId entity))
        {
            AlignText(entity, alignment);
        }
    }

    private void AlignText(XsrUiEntityId entity, XsrUiTextAlignment alignment)
    {
        RequireVisual(entity).TextAlignment = alignment;
        _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
    }

    private void ApplyVisual(
        XsrUiEntityId entity,
        XsrUiColor background,
        XsrUiColor foreground,
        double cornerRadius,
        XsrUiColor? border = null,
        XsrUiColor? hover = null,
        bool hoverExpand = false)
    {
        XsrUiVisualStyle visual = RequireVisual(entity);
        visual.Background = background;
        visual.Foreground = foreground;
        visual.Border = border ?? XsrUiColor.Transparent;
        visual.BorderWidth = border is null ? 0 : 1;
        visual.Hover = hover ?? XsrUiColor.Transparent;
        visual.HoverExpand = hoverExpand;
        visual.Surface = XsrUiSurfaceKind.Solid;
        visual.CornerRadius = cornerRadius;
        _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
    }

    private XsrUiVisualStyle RequireVisual(XsrUiEntityId entity)
    {
        XsrUiVisualStyle? visual = _shell.Tree.GetComponent<XsrUiVisualStyle>(entity);
        if (visual is null)
        {
            visual = new XsrUiVisualStyle();
            _shell.Tree.SetComponent(entity, visual);
        }

        return visual;
    }

    private XsrUiEntityId BuildPlaceholderPage() => LoadVersionSubpage("placeholder-page", "此功能");

    private static string ReadEmbeddedResource(string suffix)
    {
        System.Reflection.Assembly assembly = typeof(LaunchPageController).Assembly;
        string resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(suffix, StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The embedded resource '{resourceName}' is missing.");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}

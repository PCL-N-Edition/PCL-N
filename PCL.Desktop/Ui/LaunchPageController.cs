using System.Globalization;
using PCL.Pxml;
using PCL.Services.Accounts;
using PCL.Services.Composition;
using PCL.Services.Foundation;
using PCL.Services.Minecraft;
using PCL.Services.Minecraft.Launch;
using PCL.Services.Minecraft.Process;
using PCL.UI.Next;
using PCL.Xsr;
using PCL.Xsr.Runtime;
using PCL.Xsr.State;

namespace PCL.Desktop.Ui;

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
internal sealed class LaunchPageController : IDisposable
{
    private static readonly XsrSemanticId LaunchRoute = XsrSemanticId.Parse("ui.navigation.launch");

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
        ["start_process"] = "启动进程",
        ["end"] = "完成",
    };

    private static readonly Dictionary<string, string> LaunchMethodDisplay = new(StringComparer.Ordinal)
    {
        ["offline"] = "离线模式",
        ["microsoft"] = "微软登录",
    };

    private int _launchInProgress;
    private int _pendingCloseLaunching;
    private Guid? _javaAcquisitionDialog;
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
    private readonly ILaunchPageInstanceSource _instanceSource;
    private readonly XsrUiEntityId _launchPage;
    private readonly XsrUiEntityId _placeholderPage;
    private readonly XsrUiEntityId _versionListPage;
    private readonly XsrUiEntityId _versionSettingsPage;
    private readonly XsrUiEntityId _versionModifyPage;
    private readonly XsrUiEntityId _wardrobePage;
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
    private CancellationTokenSource? _refreshCancellation;
    private Task _refreshTask = Task.CompletedTask;
    private long _refreshGeneration;
    private bool _attached;
    private bool _disposed;

    public LaunchPageController(
        XsrUiShell shell,
        DesktopUiIntentSink intents,
        MinecraftRuntime minecraft,
        XsrCommandRouter foundationCommands,
        XsrStateStore store,
        string minecraftRootDirectory,
        DesktopFeedbackService feedback,
        ILaunchPageInstanceSource? instanceSource = null,
        XsrCommandRouter? accountCommands = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(intents);
        ArgumentNullException.ThrowIfNull(minecraft);
        ArgumentNullException.ThrowIfNull(foundationCommands);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(feedback);
        if (instanceSource is null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(minecraftRootDirectory);
        }
        _shell = shell;
        _intents = intents;
        _minecraft = minecraft;
        _foundationCommands = foundationCommands;
        _accountCommands = accountCommands;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _store = store;
        _feedback = feedback;
        StateObserver = new LaunchingStateObserver(this);
        _instanceSource = instanceSource
            ?? new MinecraftRuntimeLaunchPageInstanceSource(
                minecraft.Queries,
                minecraftRootDirectory);
        (_launchPage, _pageEntities) = LoadLaunchPage();
        _placeholderPage = BuildPlaceholderPage();
        _versionListPage = LoadVersionSubpage("VersionListPage", "版本列表");
        _versionSettingsPage = LoadVersionSubpage("VersionSettingsPage", "版本设置");
        _versionModifyPage = LoadVersionSubpage("VersionModifyPage", "版本修改");
        _wardrobePage = LoadVersionSubpage("AccountWardrobePage", "更衣橱");
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
            return _refreshTask;
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

        lock (_hintGate) { _disposed = true; _hintTimer?.Dispose(); }
        if (_attached)
        {
            _intents.IntentEmitted -= OnIntentEmitted;
            _shell.Renderer.FramePreparing -= OnFramePreparing;
            _shell.StyleChanged -= OnShellStyleChanged;
            _attached = false;
        }

        _lifetimeCancellation.Cancel();
        DismissAcquisitionDialog();
        lock (_refreshGate)
        {
            _refreshCancellation?.Cancel();
            _refreshCancellation?.Dispose();
            _refreshCancellation = null;
        }

        _lifetimeCancellation.Dispose();
    }

    private Task QueueRefresh()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CancellationTokenSource? previous;
        Task task;
        lock (_refreshGate)
        {
            previous = _refreshCancellation;
            _refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token);
            long generation = ++_refreshGeneration;
            task = RefreshGenerationAsync(generation, _refreshCancellation.Token);
            _refreshTask = task;
        }

        previous?.Cancel();
        previous?.Dispose();
        return task;
    }

    private async Task RefreshGenerationAsync(long generation, CancellationToken cancellationToken)
    {
        XsrResult<IReadOnlyList<MinecraftInstanceDescriptor>> result;
        try
        {
            result = await _instanceSource.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        lock (_refreshGate)
        {
            if (_disposed || cancellationToken.IsCancellationRequested || generation != _refreshGeneration)
            {
                return;
            }

            if (result.IsSuccess && result.Value is { Count: > 0 } instances)
            {
                MinecraftInstanceDescriptor selected = instances[0];
                Publish(LaunchPageState.SelectedInstanceKey, selected.Id);
                Publish(LaunchPageState.InstanceSummaryKey, selected.Id);
            }
            else
            {
                Publish(LaunchPageState.SelectedInstanceKey, string.Empty);
                Publish(LaunchPageState.InstanceSummaryKey, NoInstances);
                if (!result.IsSuccess)
                {
                    _feedback.Error($"实例扫描失败：{result.Error?.Message}");
                }
            }

            UpdateLaunchButton();
        }
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

        Publish(LaunchPageState.ActionLabelKey, label);
        Publish(LaunchPageState.ActionEnabledKey, enabled);
        Publish(LaunchPageState.InstanceAvailableKey, hasInstance);
    }

    private bool SelectedProfileCanLaunch()
    {
        LaunchProfileView? profile = ReadProfiles().FirstOrDefault(candidate => candidate.Index == SelectedAccountIndex);
        return profile is not { } selected
            || selected.Kind is LaunchProfileKind.Offline or LaunchProfileKind.Microsoft;
    }

    private void OnIntentEmitted(object? sender, DesktopUiIntentEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        XsrSemanticId command = e.Intent.Command;
        if (command == LaunchRoute)
        {
            ShowLaunch();
            PublishProfileFacts();
            _ = QueueRefresh();
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
            OpenSubpage(_versionModifyPage, e.Intent.Source);
        }
        else if (command == AccountWardrobeCommand)
        {
            OpenSubpage(_wardrobePage, e.Intent.Source);
        }
        else if (command == PageBackCommand)
        {
            if (_launchInProgress != 0)
            {
                return;
            }

            if (_shell.Stage.Navigation.Pop() && _returnFocus.TryPop(out XsrUiEntityId focus))
            {
                UpdateTitleBar();
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
            ShowPlaceholder();
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
            ? _shell.Tree.GetComponent<XsrUiSemantic>(_shell.Stage.Navigation.Current)?.Label ?? string.Empty : "Nexa Launcher");
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

    private XsrUiEntityId LoadVersionSubpage(string key, string title)
    {
        PxmlHostIr template = PxmlCompiler.Compile(PxmlParser.Parse(ReadEmbeddedResource("Ui.VersionSubpage.pxml")));
        PxmlIrNode Project(PxmlIrNode node) => node with
        {
            Key = node.Key == "VersionSubpage" ? key : node.Key,
            Label = node.Key == "VersionSubpage" ? title : node.Label,
            Children = [.. node.Children.Select(Project)],
        };
        XsrUiEntityId parent = _shell.Tree.Create("subpage-loader");
        XsrUiEntityId page = PxmlUiLoader.Load(new PxmlHostIr(Project(template.Root)), _shell.Tree, _store, parent);
        _shell.Tree.Detach(page);
        _shell.Tree.Destroy(parent);
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

    private void OnFramePreparing(object? sender, EventArgs e)
    {
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
            Publish(LaunchPageState.WidgetAboutLabelKey, index == 0 ? "关于 PCL N Edition，当前卡片" : "查看关于 PCL N Edition");
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
            ? "账户档案读取失败。\n请检查文件或导入备份。" : roster.Count == 0
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

    private void ShowPlaceholder()
    {
        ClearSubpageHistory();
        if (!_shell.Stage.Navigation.Current.Equals(_placeholderPage))
        {
            _shell.Stage.Navigation.Replace(_placeholderPage);
        }
    }

    private void NavigateToDownload()
    {
        _ = _shell.Select(DownloadNavigationId);
        ShowPlaceholder();
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
            new MinecraftStartCommand(instanceId, selected),
            cancellationToken: _lifetimeCancellation.Token);
        XsrResult result = await dispatch.Completion.ConfigureAwait(false);
        if (_disposed)
        {
            return;
        }

        if (result.IsSuccess)
        {
            // The pipeline keeps narrating (游戏已启动) until the process session ends.
            _feedback.Info("Minecraft 已启动。");
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
        DismissAcquisitionDialog();
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
                $"需要下载 Java {major}",
                $"未找到兼容的 Java {major} 运行库（{component}）。启动游戏前需要下载，是否继续？",
                "自动下载",
                "取消下载",
                approve => _ = DecideAcquisitionAsync(approve));
        }
        else if (!pending && _javaAcquisitionDialog is { } dialog)
        {
            _feedback.DismissDialog(dialog);
            _javaAcquisitionDialog = null;
        }
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
        ApplyVisual(entities["AccountAdd"], ProfileSurface, BadgeText, XsrUiCornerRadii.Pill(28), hover: BadgeBackground);
        ApplyVisual(entities["AccountImport"], ProfileSurface, BadgeText, XsrUiCornerRadii.Pill(34), hover: BadgeBackground);
        StyleText(entities, "AccountImport", BadgeText, 13, 600);
        AlignText(entities, "AccountImport", XsrUiTextAlignment.Center);
        StyleText(entities, "AccountAvatar", BadgeText, fontSize: 14);
        foreach (string key in new[] { "AccountName", "AccountKind" })
            AlignText(entities, key, XsrUiTextAlignment.Center);
        foreach (string key in new[] { "AccountSwitch", "AccountWardrobe" })
        {
            ApplyVisual(entities[key], BadgeBackground, BadgeText, XsrUiCornerRadii.Pill(36), hover: new XsrUiColor(207, 225, 254), hoverExpand: true);
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

    private XsrUiEntityId BuildPlaceholderPage()
    {
        XsrUiTree tree = _shell.Tree;
        XsrUiEntityId page = tree.Create("placeholder-page");
        tree.SetComponent(page, new XsrUiStackPanel(XsrUiOrientation.Vertical) { Spacing = 12 });
        tree.SetComponent(page, new XsrUiSemantic(XsrUiSemanticRole.Page, "建设中"));
        XsrUiEntityId text = tree.Create("placeholder-text");
        tree.SetComponent(text, new XsrUiText("该分区将在后续单元中迁移。"));
        tree.SetComponent(text, new XsrUiSemantic(XsrUiSemanticRole.Text, "建设中"));
        tree.SetComponent(text, new XsrUiVisualStyle
        {
            Foreground = SecondaryText,
            FontSize = 14,
        });
        tree.Attach(text, page);
        tree.MarkDirty(page, XsrUiDirtyKinds.Structure);
        return page;
    }

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

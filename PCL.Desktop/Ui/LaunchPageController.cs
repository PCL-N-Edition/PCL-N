using PCL.Pxml;
using PCL.Services.Accounts;
using PCL.Services.Composition;
using PCL.Services.Foundation;
using PCL.Services.Minecraft;
using PCL.UI.Next;
using PCL.Xsr;
using PCL.Xsr.Runtime;
using PCL.Xsr.State;

namespace PCL.Desktop.Ui;

/// <summary>
/// The product launch page: the first vertical slice attached to the shell's content host,
/// replicating the legacy experimental launch home's information architecture — an account
/// card (profile identity / picker), a version card (版本 header, instance picker row,
/// the big accent launch button and operational feedback), and the community about card. It reads its
/// facts from host state cells, emits the launch intent through the renderer's normal sink,
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
    private static readonly XsrSemanticId WidgetHintCommand = XsrSemanticId.Parse("ui.launch.hint.refresh");

    private static readonly XsrSemanticId AccountSelectCommand = XsrSemanticId.Parse("ui.account.select");
    private static readonly XsrSemanticId AccountSwitchCommand = XsrSemanticId.Parse("ui.account.switch");
    private static readonly XsrSemanticId AccountDismissCommand = XsrSemanticId.Parse("ui.account.dismiss");

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

    private const string NoAccountName = "未选择账户";
    private const string AccountNeedLoginSummary = "请选择或创建一个账户档案后再启动。";
    private const string ScanningInstances = "正在扫描本地版本…";
    private const string NoInstances = "未找到可启动的游戏版本";
    private const string NoSelectedProfileLabel = "未选择档案";
    private const string DownloadLabel = "下载游戏";
    private const string LaunchLabel = "启动游戏";

    private readonly XsrUiShell _shell;
    private readonly DesktopUiIntentSink _intents;
    private readonly MinecraftRuntime _minecraft;
    private readonly XsrCommandRouter _foundationCommands;
    private readonly XsrStateStore _store;
    private readonly ILaunchPageInstanceSource _instanceSource;
    private readonly XsrUiEntityId _launchPage;
    private readonly XsrUiEntityId _placeholderPage;
    private readonly XsrUiEntityId _versionListPage;
    private readonly XsrUiEntityId _versionSettingsPage;
    private readonly XsrUiEntityId _versionModifyPage;
    private readonly Dictionary<string, XsrUiEntityId> _titleEntities = [];
    private readonly Stack<XsrUiEntityId> _returnFocus = [];
    private readonly Dictionary<string, XsrUiEntityId> _pageEntities;
    private readonly Dictionary<int, XsrUiEntityId> _accountRowEntities = [];
    private readonly Dictionary<XsrUiEntityId, int> _accountRowIndexes = [];
    private readonly PxmlHostIr _accountRowTemplate = PxmlCompiler.Compile(
        PxmlParser.Parse(ReadEmbeddedResource("Ui.AccountProfileRow.pxml")));
    private long _accountRosterRevision = -1;
    private int _presentedAccountIndex = -2;
    private bool? _presentedAccountPicker;
    private bool _accountKeyboardFocus;
    private int _presentedWidgetIndex = -1;
    private double _indicatorPosition = double.NaN;
    private int _hintIndex = Random.Shared.Next(LaunchWidgetHints.BuiltIn.Count);
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
        ILaunchPageInstanceSource? instanceSource = null)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(intents);
        ArgumentNullException.ThrowIfNull(minecraft);
        ArgumentNullException.ThrowIfNull(foundationCommands);
        ArgumentNullException.ThrowIfNull(store);
        if (instanceSource is null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(minecraftRootDirectory);
        }
        _shell = shell;
        _intents = intents;
        _minecraft = minecraft;
        _foundationCommands = foundationCommands;
        _store = store;
        _instanceSource = instanceSource
            ?? new MinecraftRuntimeLaunchPageInstanceSource(
                minecraft.Queries,
                minecraftRootDirectory);
        (_launchPage, _pageEntities) = LoadLaunchPage();
        _placeholderPage = BuildPlaceholderPage();
        _versionListPage = LoadVersionSubpage("VersionListPage", "版本列表");
        _versionSettingsPage = LoadVersionSubpage("VersionSettingsPage", "版本设置");
        _versionModifyPage = LoadVersionSubpage("VersionModifyPage", "版本修改");
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
        Publish(LaunchPageState.StatusKey, string.Empty);
        Publish(LaunchPageState.ProfileNameKey, NoAccountName);
        Publish(LaunchPageState.InstanceSummaryKey, ScanningInstances);
        Publish(LaunchPageState.SelectedInstanceKey, string.Empty);
        Publish(LaunchPageState.ActionLabelKey, DownloadLabel);
        RefreshAccountPresentation();
        RefreshWidgetPresentation();
        Publish(LaunchPageState.WidgetHintKey, LaunchWidgetHints.BuiltIn[_hintIndex]);
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

        _disposed = true;
        if (_attached)
        {
            _intents.IntentEmitted -= OnIntentEmitted;
            _shell.Renderer.FramePreparing -= OnFramePreparing;
            _shell.StyleChanged -= OnShellStyleChanged;
            _attached = false;
        }

        _lifetimeCancellation.Cancel();
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
                    Publish(
                        LaunchPageState.StatusKey,
                        $"实例扫描失败：{result.Error?.Message}");
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
        else
        {
            label = LaunchLabel;
            enabled = true;
        }

        Publish(LaunchPageState.ActionLabelKey, label);
        Publish(LaunchPageState.ActionEnabledKey, enabled);
        Publish(LaunchPageState.InstanceAvailableKey, hasInstance);
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
                _ = StartLaunchAsync(instanceId);
            }
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
        else if (command == PageBackCommand)
        {
            if (_shell.Stage.Navigation.Pop() && _returnFocus.TryPop(out XsrUiEntityId focus))
            {
                UpdateTitleBar();
                _shell.Renderer.Focus(focus, IsKeyboardIntent(e.Intent.Source));
            }
        }
        else if (command == WidgetAboutCommand || command == WidgetTriviaCommand)
        {
            XsrUiEntityId pager = _pageEntities["LaunchWidgetPager"];
            int current = _shell.Tree.GetComponent<XsrUiPager>(pager)!.PageIndex;
            _ = _shell.Renderer.MovePager(pager, (command == WidgetTriviaCommand ? 1 : 0) - current);
        }
        else if (command == WidgetHintCommand)
        {
            _hintIndex = (_hintIndex + Random.Shared.Next(1, LaunchWidgetHints.BuiltIn.Count)) % LaunchWidgetHints.BuiltIn.Count;
            Publish(LaunchPageState.WidgetHintKey, LaunchWidgetHints.BuiltIn[_hintIndex]);
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

    private async Task SelectAccountAsync(int index, long revision)
    {
        if (!_foundationCommands.TryResolve(FoundationRouteIds.AccountSelectProfile, out XsrCommandId route))
        {
            Publish(LaunchPageState.StatusKey, "账户切换命令未注册。");
            return;
        }

        XsrResult result = await _foundationCommands.Dispatch(route,
            new AccountSelectProfileCommand(index, revision), cancellationToken: _lifetimeCancellation.Token)
            .Completion.ConfigureAwait(false);
        if (_disposed) return;
        if (result.IsSuccess) Publish(LaunchPageState.AccountPickerKey, false);
        else Publish(LaunchPageState.StatusKey, $"切换档案失败：{result.Error?.Message}");
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
        bool subpage = _shell.Stage.Navigation.Depth > 1;
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

    private void OnFramePreparing(object? sender, EventArgs e)
    {
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
        }
        double position = Math.Clamp(pager.Position, 0, 1);
        if (position == _indicatorPosition) return;
        _indicatorPosition = position;
        UpdateWidgetDot("WidgetAboutDot", "WidgetAboutIndicator", 1 - position);
        UpdateWidgetDot("WidgetTriviaDot", "WidgetTriviaIndicator", position);
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
        if (roster.Revision != _accountRosterRevision)
        {
            BuildAccountRows(roster.Items);
            _accountRosterRevision = roster.Revision;
            _presentedAccountIndex = -2;
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
        }

        bool hasSelection = roster.Items.Any(profile => profile.Index == selected);
        bool picker = !hasSelection || _store.ReadAppliedValue(_store.Resolve(LaunchPageState.AccountPickerKey)) is true;
        Publish(LaunchPageState.AccountRosterVisibleKey, picker);
        Publish(LaunchPageState.AccountSelectedVisibleKey, !picker);
        Publish(LaunchPageState.AccountCanReturnKey, hasSelection);
        Publish(LaunchPageState.AccountHintKey, roster.Count == 0
            ? "还没有账户档案。\n请先创建或导入档案。"
            : "选择用于启动游戏的档案");
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
        Label = node.Key == "AccountRow" ? $"选择 {profile.Username}，{ProfileKind(profile.Kind)}" : node.Label,
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
                if (key.StartsWith("ProfileCheck:", StringComparison.Ordinal))
                {
                    _shell.Tree.GetComponent<XsrUiElement>(entity)!.IsVisible = selected;
                    StyleText(entity, BadgeText, 14);
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
        Publish(LaunchPageState.StatusKey, "请在安装页选择或下载游戏版本");
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

    private async Task StartLaunchAsync(string instanceId)
    {
        IReadOnlyList<LaunchProfileView> profiles = ReadProfiles();
        int selected = SelectedAccountIndex;
        if (!profiles.Any(profile => profile.Index == selected))
        {
            Publish(LaunchPageState.StatusKey, AccountNeedLoginSummary);
            return;
        }

        Publish(LaunchPageState.StatusKey, "正在启动 Minecraft…");
        if (!_minecraft.Commands.TryResolve(MinecraftRouteIds.Start, out XsrCommandId commandId))
        {
            Publish(LaunchPageState.StatusKey, "启动失败：产品启动命令未注册。");
            return;
        }

        XsrCommandDispatch dispatch = _minecraft.Commands.Dispatch(
            commandId,
            new MinecraftStartCommand(instanceId, selected),
            cancellationToken: _lifetimeCancellation.Token);
        XsrResult result = await dispatch.Completion.ConfigureAwait(false);
        if (!_disposed)
        {
            Publish(
                LaunchPageState.StatusKey,
                result.IsSuccess ? "Minecraft 已启动" : $"启动失败：{result.Error?.Message}");
        }
    }

    private int SelectedAccountIndex => _store.ReadAppliedValue(_store.Resolve(AccountService.SelectedKey)) is int index ? index : -1;

    private IReadOnlyList<LaunchProfileView> ReadProfiles() =>
        _store.ReadCollection<LaunchProfileView>(_store.Resolve(AccountService.ProfilesKey)).Items;

    private void Publish<T>(XsrSemanticId key, T value)
    {
        XsrStateId id = _store.Resolve(key);
        if (!Equals(_store.ReadAppliedValue(id), value)) _store.Publish(id, value);
        if (key == LaunchPageState.StatusKey)
            Publish(LaunchPageState.StatusVisibleKey, value is string text && !string.IsNullOrWhiteSpace(text));
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
        StyleText(entities, "AccountHeader", SecondaryText, fontSize: 12, weight: 600);
        StyleText(entities, "VersionHeader", SecondaryText, fontSize: 12, weight: 600);
        StyleText(entities, "AboutTitle", SecondaryText, fontSize: 12, weight: 600);
        StyleText(entities, "TriviaTitle", SecondaryText, fontSize: 12, weight: 600);
        StyleText(entities, "AboutMessage", PrimaryText, fontSize: 14, weight: 600);
        StyleText(entities, "TriviaMessage", PrimaryText, fontSize: 14, weight: 600);
        foreach (string key in new[] { "AboutMessage", "TriviaMessage" })
            _shell.Tree.GetComponent<XsrUiVisualStyle>(entities[key])!.WrapText = true;
        foreach (string key in new[] { "WidgetAboutIndicator", "WidgetTriviaIndicator" })
            ApplyVisual(entities[key], XsrUiColor.Transparent, PrimaryText, cornerRadius: 3, hover: BadgeBackground);
        foreach (string key in new[] { "WidgetAboutDot", "WidgetTriviaDot" })
            ApplyVisual(entities[key], BadgeText, PrimaryText, cornerRadius: 3);
        StyleText(entities, "AccountKind", ProfileSecondaryText, fontSize: 13);
        StyleText(entities, "AccountHint", ProfileSecondaryText, fontSize: 12);
        StyleText(entities, "AccountPickerTitle", PrimaryText, fontSize: 18, weight: 600);
        StyleText(entities, "LaunchFeedback", SecondaryText, fontSize: 12);
        _shell.Tree.GetComponent<XsrUiVisualStyle>(entities["AccountHint"])!.WrapText = true;
        ApplyVisual(entities["AccountAvatarSurface"], ProfileSurface, BadgeText, XsrUiCornerRadii.Surface);
        ApplyVisual(entities["AccountPickerBack"], ProfileSurface, ProfileSecondaryText, XsrUiCornerRadii.Pill(32), hover: BadgeBackground);
        StyleText(entities, "AccountAvatar", BadgeText, fontSize: 14);
        foreach (string key in new[] { "AccountName", "AccountKind" })
            AlignText(entities, key, XsrUiTextAlignment.Center);
        if (entities.TryGetValue("AccountSwitch", out XsrUiEntityId switchButton))
        {
            ApplyVisual(switchButton, BadgeBackground, BadgeText, XsrUiCornerRadii.Pill(36), hover: new XsrUiColor(207, 225, 254));
            StyleText(entities, "AccountSwitchIcon", BadgeText, 13);
            StyleText(entities, "AccountSwitchText", BadgeText, 13, 600);
            AlignText(entities, "AccountSwitchText", XsrUiTextAlignment.Center);
        }
        StyleText(entities, "VersionAction", SecondaryText, fontSize: 11);
        if (entities.TryGetValue("AccountBadge", out XsrUiEntityId badge))
        {
            ApplyVisual(badge, BadgeBackground, BadgeText, cornerRadius: XsrUiCornerRadii.Compact);
            StyleText(entities, "AccountBadgeText", BadgeText, fontSize: 10, weight: 600);
            AlignText(entities, "AccountBadgeText", XsrUiTextAlignment.Center);
        }

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

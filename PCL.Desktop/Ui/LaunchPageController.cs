using PCL.Pxml;
using PCL.Services.Accounts;
using PCL.Services.Composition;
using PCL.Services.Minecraft;
using PCL.UI.Next;
using PCL.Xsr;
using PCL.Xsr.Runtime;
using PCL.Xsr.State;

namespace PCL.Desktop.Ui;

/// <summary>
/// The product launch page: the first vertical slice attached to the shell's content host,
/// replicating the legacy experimental launch home's information architecture — an account
/// card (账户 / 实验 badge / name / summary), a version card (版本 header, instance picker row,
/// the big accent launch button, status line), and the community about card. It reads its
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

    private static readonly XsrSemanticId AccountSelectCommand = XsrSemanticId.Parse("ui.account.select");

    private static readonly XsrSemanticId DownloadNavigationId = XsrSemanticId.Parse("navigation.download");

    private const string NavigationCommandPrefix = "ui.navigation.";

    // The shell-owned presentation commands share the ui.navigation prefix but are not
    // destinations: routing them away used to wipe the page on every rail expand/collapse.
    private static readonly XsrSemanticId NavigationExpandCommand = XsrSemanticId.Parse("ui.navigation.expand");

    private static readonly HashSet<string> DestinationCommandValues =
    [
        "ui.navigation.launch",
        "ui.navigation.download",
        "ui.navigation.community",
        "ui.navigation.settings",
    ];

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

    private const string NoAccountName = "未选择账户";
    private const string NoAccountSummary = "登录与档案管理在这里完成。";
    private const string AccountReadySummary = "账户已就绪，可以开始游戏。";
    private const string AccountNeedLoginSummary = "请选择或创建一个账户档案后再启动。";
    private const string ScanningInstances = "正在扫描本地版本…";
    private const string NoInstances = "未找到可启动的游戏版本";
    private const string SelectFromToolbar = "使用右上角按钮选择或安装版本";
    private const string InstanceSettingsAction = "版本设置";
    private const string DownloadLabel = "下载游戏";
    private const string LaunchLabel = "启动游戏";

    private readonly XsrUiShell _shell;
    private readonly DesktopUiIntentSink _intents;
    private readonly MinecraftRuntime _minecraft;
    private readonly AccountService _accounts;
    private readonly XsrStateStore _store;
    private readonly ILaunchPageInstanceSource _instanceSource;
    private readonly XsrUiEntityId _launchPage;
    private readonly XsrUiEntityId _placeholderPage;
    private readonly Dictionary<string, XsrUiEntityId> _pageEntities;
    private readonly Dictionary<int, XsrUiEntityId> _accountRowEntities = [];
    private readonly Dictionary<XsrUiEntityId, int> _accountRowIndexes = [];
    private XsrUiEntityId _accountRowsHost;
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
        AccountService accounts,
        XsrStateStore store,
        string minecraftRootDirectory,
        ILaunchPageInstanceSource? instanceSource = null)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(intents);
        ArgumentNullException.ThrowIfNull(minecraft);
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(store);
        if (instanceSource is null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(minecraftRootDirectory);
        }
        _shell = shell;
        _intents = intents;
        _minecraft = minecraft;
        _accounts = accounts;
        _store = store;
        _instanceSource = instanceSource
            ?? new MinecraftRuntimeLaunchPageInstanceSource(
                minecraft.Queries,
                minecraftRootDirectory);
        (_launchPage, _pageEntities) = LoadLaunchPage();
        _placeholderPage = BuildPlaceholderPage();
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
        ShowLaunch();
        Publish(LaunchPageState.StatusKey, "就绪");
        Publish(LaunchPageState.ProfileNameKey, NoAccountName);
        Publish(LaunchPageState.ProfileSummaryKey, NoAccountSummary);
        Publish(LaunchPageState.InstanceSummaryKey, ScanningInstances);
        Publish(LaunchPageState.InstanceDetailKey, SelectFromToolbar);
        Publish(LaunchPageState.SelectedInstanceKey, string.Empty);
        Publish(LaunchPageState.ActionLabelKey, DownloadLabel);
        PublishProfileSummary();
        BuildAccountRows();
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
                Publish(LaunchPageState.InstanceDetailKey, InstanceSettingsAction);
                Publish(LaunchPageState.ActionLabelKey, LaunchLabel);
            }
            else
            {
                Publish(LaunchPageState.SelectedInstanceKey, string.Empty);
                Publish(LaunchPageState.InstanceSummaryKey, NoInstances);
                Publish(LaunchPageState.InstanceDetailKey, SelectFromToolbar);
                Publish(LaunchPageState.ActionLabelKey, DownloadLabel);
                if (!result.IsSuccess)
                {
                    Publish(
                        LaunchPageState.StatusKey,
                        $"实例扫描失败：{result.Error?.Message}");
                }
            }
        }
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
            PublishProfileSummary();
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
            NavigateToDownload();
        }
        else if (command == AccountSelectCommand)
        {
            if (_accountRowIndexes.TryGetValue(e.Intent.Source, out int index)
                && _accounts.SelectProfile(index) is null)
            {
                PublishProfileSummary();
                StyleAccountRows();
            }
        }
        else if (IsDestinationCommand(command))
        {
            ShowPlaceholder();
        }
    }

    private static bool IsDestinationCommand(XsrSemanticId command) =>
        command.Value.StartsWith(NavigationCommandPrefix, StringComparison.Ordinal)
        && DestinationCommandValues.Contains(command.Value)
        && command != NavigationExpandCommand;

    private void ShowLaunch()
    {
        if (!_shell.Stage.Navigation.Current.Equals(_launchPage))
        {
            // Destination switches replace the page: the navigator's back stack is reserved
            // for hierarchical drill-in, not for moving between primary destinations.
            _shell.Stage.Navigation.Replace(_launchPage);
        }

        BuildAccountRows();
    }

    /// <summary>
    /// Rebuilds the account card's profile list: one clickable row per roster profile, the
    /// selected one highlighted. Rows emit <see cref="AccountSelectCommand"/> with themselves
    /// as the intent source, so the renderer keeps owning invocation and correlation.
    /// </summary>
    private void BuildAccountRows()
    {
        if (!_pageEntities.TryGetValue("AccountRows", out XsrUiEntityId rowsHost))
        {
            return;
        }

        _accountRowsHost = rowsHost;
        XsrUiTree tree = _shell.Tree;
        foreach (XsrUiEntityId row in _accountRowEntities.Values)
        {
            tree.Destroy(row);
        }

        _accountRowEntities.Clear();
        _accountRowIndexes.Clear();

        IReadOnlyList<LaunchProfileView> profiles = ReadProfiles();
        foreach (LaunchProfileView profile in profiles)
        {
            XsrUiEntityId row = tree.Create($"account-row:{profile.Index}");
            tree.SetComponent(row, new XsrUiElement
            {
                Height = 52,
                HorizontalAlignment = XsrUiAlignment.Stretch,
            });
            tree.SetComponent(row, new XsrUiStackPanel(XsrUiOrientation.Vertical) { Spacing = 2 });
            tree.SetComponent(row, new XsrUiSemantic(XsrUiSemanticRole.Button, profile.Username));
            tree.SetComponent(row, new XsrUiInput { Focusable = true, Clickable = true });
            tree.SetComponent(row, new XsrUiCommandBinding(AccountSelectCommand));
            tree.SetComponent(row, new XsrUiSelection { IsSelected = profile.Index == _accounts.SelectedIndex });
            tree.SetComponent(row, new XsrUiVisualStyle());
            tree.Attach(row, rowsHost);

            XsrUiEntityId name = tree.Create($"account-row-name:{profile.Index}");
            tree.SetComponent(name, new XsrUiText(profile.Username));
            tree.SetComponent(name, new XsrUiSemantic(XsrUiSemanticRole.Text, profile.Username));
            tree.SetComponent(name, new XsrUiVisualStyle());
            tree.Attach(name, row);

            XsrUiEntityId info = tree.Create($"account-row-info:{profile.Index}");
            tree.SetComponent(info, new XsrUiText(profile.Info));
            tree.SetComponent(info, new XsrUiSemantic(XsrUiSemanticRole.Text, profile.Info));
            tree.SetComponent(info, new XsrUiVisualStyle());
            tree.Attach(info, row);

            _accountRowEntities[profile.Index] = row;
            _accountRowIndexes[row] = profile.Index;
        }

        tree.MarkDirty(rowsHost, XsrUiDirtyKinds.Structure);
        StyleAccountRows();
    }

    private void StyleAccountRows()
    {
        foreach ((int index, XsrUiEntityId row) in _accountRowEntities)
        {
            bool selected = index == _accounts.SelectedIndex;
            if (_shell.Tree.GetComponent<XsrUiSelection>(row) is { } selection)
            {
                selection.IsSelected = selected;
            }

            ApplyVisual(
                row,
                selected ? BadgeBackground : XsrUiColor.Transparent,
                PrimaryText,
                cornerRadius: 8);
            if (_shell.Tree.Children(row).FirstOrDefault(child =>
                    _shell.Tree.GetComponent<XsrUiText>(child) is not null) is { } nameText
                && RequireVisual(nameText) is { } nameVisual)
            {
                nameVisual.FontWeight = selected ? 600 : 400;
                nameVisual.Foreground = selected ? BadgeText : PrimaryText;
                nameVisual.FontSize = 14;
                _shell.Tree.MarkDirty(nameText, XsrUiDirtyKinds.Paint);
            }
        }
    }

    private void ShowPlaceholder()
    {
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

    private void PublishProfileSummary()
    {
        IReadOnlyList<LaunchProfileView> profiles = ReadProfiles();
        if (profiles.Count > 0)
        {
            int index = Math.Clamp(_accounts.SelectedIndex, 0, profiles.Count - 1);
            Publish(LaunchPageState.ProfileNameKey, profiles[index].Username);
            Publish(LaunchPageState.ProfileSummaryKey, AccountReadySummary);
        }
        else
        {
            Publish(LaunchPageState.ProfileNameKey, NoAccountName);
            Publish(LaunchPageState.ProfileSummaryKey, NoAccountSummary);
        }
    }

    private async Task StartLaunchAsync(string instanceId)
    {
        IReadOnlyList<LaunchProfileView> profiles = ReadProfiles();
        if (profiles.Count == 0 || _accounts.SelectedIndex < 0)
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
            new MinecraftStartCommand(instanceId, _accounts.SelectedIndex),
            cancellationToken: _lifetimeCancellation.Token);
        XsrResult result = await dispatch.Completion.ConfigureAwait(false);
        if (!_disposed)
        {
            Publish(
                LaunchPageState.StatusKey,
                result.IsSuccess ? "Minecraft 已启动" : $"启动失败：{result.Error?.Message}");
        }
    }

    private IReadOnlyList<LaunchProfileView> ReadProfiles() => _accounts.GetViews();

    private void Publish(XsrSemanticId key, string value)
    {
        _store.Publish(_store.Resolve(key), value);
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
        StyleCard(entities, "CardAccount", cornerRadius: 16);
        StyleCard(entities, "CardVersion", cornerRadius: 16);
        StyleCard(entities, "CardAbout", cornerRadius: 18);
        StyleText(entities, "AccountHeader", SecondaryText, fontSize: 12, weight: 600);
        StyleText(entities, "VersionHeader", SecondaryText, fontSize: 12, weight: 600);
        StyleText(entities, "AboutTitle", SecondaryText, fontSize: 12, weight: 600);
        StyleText(entities, "AboutMessage", PrimaryText, fontSize: 14, weight: 600);
        StyleText(entities, "AccountSummary", SecondaryText, fontSize: 13);
        StyleText(entities, "LaunchStatus", SecondaryText, fontSize: 13);
        StyleText(entities, "VersionAction", SecondaryText, fontSize: 11);
        if (entities.TryGetValue("AccountBadge", out XsrUiEntityId badge))
        {
            ApplyVisual(badge, BadgeBackground, BadgeText, cornerRadius: 6);
            StyleText(entities, "AccountBadgeText", BadgeText, fontSize: 10, weight: 600);
        }

        if (entities.TryGetValue("AccountName", out XsrUiEntityId accountName))
        {
            StyleText(accountName, PrimaryText, fontSize: 16, weight: 600);
        }

        if (entities.TryGetValue("VersionName", out XsrUiEntityId versionName))
        {
            StyleText(versionName, PrimaryText, fontSize: 16, weight: 600);
        }

        if (entities.TryGetValue("InstanceRow", out XsrUiEntityId pickerRow))
        {
            ApplyVisual(pickerRow, PickerBackground, PrimaryText, cornerRadius: 12);
        }

        if (entities.TryGetValue("InstanceListButton", out XsrUiEntityId instanceListButton))
        {
            ApplyVisual(
                instanceListButton,
                XsrUiColor.Transparent,
                SecondaryText,
                cornerRadius: 14,
                hover: BadgeBackground);
            StyleText(instanceListButton, SecondaryText, fontSize: 16, weight: 600);
            AlignText(instanceListButton, XsrUiTextAlignment.Center);
        }

        if (entities.TryGetValue("InstanceChevron", out XsrUiEntityId instanceChevron))
        {
            ApplyVisual(
                instanceChevron,
                CardBackground,
                SecondaryText,
                cornerRadius: 13,
                border: CardBorder);
            StyleText(entities, "InstanceChevronText", SecondaryText, fontSize: 18);
        }

        if (entities.TryGetValue("LaunchButton", out XsrUiEntityId button))
        {
            // Legacy accent button: normal #0b5bcb, hover #1370f3, white 13 px semibold label.
            ApplyVisual(
                button,
                LaunchButtonBackground,
                new XsrUiColor(255, 255, 255),
                cornerRadius: 11,
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
        XsrUiColor? hover = null)
    {
        XsrUiVisualStyle visual = RequireVisual(entity);
        visual.Background = background;
        visual.Foreground = foreground;
        visual.Border = border ?? XsrUiColor.Transparent;
        visual.BorderWidth = border is null ? 0 : 1;
        visual.Hover = hover ?? XsrUiColor.Transparent;
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

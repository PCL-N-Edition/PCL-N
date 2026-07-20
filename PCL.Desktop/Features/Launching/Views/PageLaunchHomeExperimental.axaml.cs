// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using IoPath = System.IO.Path;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using PCL.Core.Logging;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Diagnostics;
using PCL.Desktop.Hosting;
using PCL.Desktop.Localization;

namespace PCL.Desktop.Features.Launching.Views;

/// <summary>
/// Experimental full redesign of the Launch homepage (account, version, launch, activity).
/// Implements the same surface as classic <see cref="PageLaunchLeft"/> for MainWindow wiring.
/// </summary>
public partial class PageLaunchHomeExperimental : MyPageRight, ILaunchHomeSurface, IDisposable
{
    private const int ProgressAnimDurationMs = 240;
    private const string WidgetPageAnimId = "Launch Experimental Widget Flip";
    private const double WidgetSwipeThresholdPx = 48d;
    private const int WidgetFlipMs = 220;

    /// <summary>Built-in N-Edition notice flip card.</summary>
    public const string CardIdCommunityHint = "pcl.builtin.community-hint";

    /// <summary>Built-in trivia flip card.</summary>
    public const string CardIdTrivia = "pcl.builtin.trivia";

    /// <summary>Experimental iOS-style shortcut dock for pinned worlds/servers.</summary>
    public const string CardIdShortcuts = "pcl.builtin.shortcuts";

    /// <summary>
    /// Compatibility shell for deprecated slot <c>primary-actions.after</c>.
    /// Prefer <c>cards.flip</c> for new plugin contributions.
    /// </summary>
    public const string CardIdLegacyPrimaryActions = "pcl.compat.primary-actions.after";

    private PageLaunchLeft.LaunchButtonAction _launchButtonAction = PageLaunchLeft.LaunchButtonAction.Loading;
    private CancellationTokenSource? _refreshCancellation;
    private Task? _refreshInstancesTask;
    private bool _isLoadedOnce;
    private bool _isInstanceLoadFinished;
    private long _refreshGeneration;
    private string? _minecraftRootDirectory;
    private string? _preferredInstanceDirectory;
    private double _showProgress;
    private double _targetProgress;
    private bool _showLaunchingHint = true;
    private int _maximumLogLines = 200;
    private bool _disposed;

    private readonly List<WidgetCardRegistration> _widgetCards = [];
    private Control[] _widgetPages = [];
    private string? _activeWidgetCardId;
    private int _widgetPageIndex;
    private bool _widgetDragging;
    private Point _widgetDragStart;
    private bool _widgetPageAnimating;
    private Panel? _flipCardSlot;
    private readonly Dictionary<string, Control> _flipCardSurfaces = new(StringComparer.OrdinalIgnoreCase);
    private bool _flipSlotSyncing;

    public PageLaunchHomeExperimental()
    {
        AvaloniaXamlLoader.Load(this);
        // Experimental homepage fills the pane without internal scroll.
        PanScroll = null;
        InitWidgetPager();
        RegisterBuiltinWidgetCards();
        RegisterPluginUiSurfaces();
        SetLoadingState();
        SeedCommunityHints();
        RefreshShortcutDock();
        AttachedToVisualTree += (_, _) =>
        {
            RefreshShortcutDock();
            if (_isLoadedOnce)
                return;
            _isLoadedOnce = true;
            _ = EnsureInstancesLoadedAsync();
        };
        DetachedFromVisualTree += (_, _) => UnregisterPluginUiSurfaces();
    }

    private sealed class WidgetCardRegistration
    {
        public required string Id { get; init; }
        public required Control Surface { get; init; }
        public int Order { get; init; }
    }

    public IReadOnlyList<LaunchInstanceInfo> Instances { get; private set; } = [];

    public LaunchInstanceInfo? SelectedInstance { get; private set; }

    public string? PreferredInstanceDirectory => _preferredInstanceDirectory;

    public string? MinecraftRootDirectory => _minecraftRootDirectory;

    public Control? CurrentLoginPage { get; private set; }

    public PageLaunchLeft.LaunchLoginPageType CurrentLoginPageType { get; private set; } =
        PageLaunchLeft.LaunchLoginPageType.None;

    public bool HasSelectedProfile { get; private set; }

    public bool IsLaunchInProgress { get; private set; }

    public double DisplayedLaunchProgress => _showProgress;

    public Func<bool>? CanLaunchByPageState { get; set; }

    public event EventHandler? InstanceSelectRequested;

    public event EventHandler? InstanceSettingsRequested;

    public event EventHandler? DownloadRequested;

    public event EventHandler<LaunchInstanceInfo>? LaunchRequested;

    public event EventHandler? CancelLaunchRequested;

    public event EventHandler<string>? StatusMessage;

    public event EventHandler<PageLaunchLeft.LaunchLoginPageType>? LoginPageRequested;

    public event EventHandler? CommunityHintHideRequested;

    /// <summary>Raised when the user activates a pin on the experimental shortcut dock.</summary>
    public event EventHandler<LaunchShortcutPin>? ShortcutActivated;

    public Task EnsureInstancesLoadedAsync()
    {
        if (_isInstanceLoadFinished)
            return Task.CompletedTask;
        if (_refreshInstancesTask is { IsCompleted: false })
            return _refreshInstancesTask;
        _refreshInstancesTask = RefreshInstancesAsync();
        return _refreshInstancesTask;
    }

    /// <summary>
    /// Sync the shortcut flip card with the experimental setting and pin store.
    /// Call after settings change or when returning to the launch home.
    /// </summary>
    public void RefreshShortcutDock()
    {
        if (_disposed)
            return;

        if (!LaunchShortcutStore.IsFeatureEnabled())
        {
            UnregisterWidgetCard(CardIdShortcuts);
            if (this.FindControl<Control>("PanShortcuts") is { } hidden)
            {
                hidden.IsVisible = false;
                hidden.IsHitTestVisible = false;
            }
            return;
        }

        if (this.FindControl<Control>("PanShortcuts") is not { } surface)
            return;

        RegisterWidgetCard(CardIdShortcuts, surface, order: 20);
        RebuildShortcutDockItems();
    }

    public async Task RefreshInstancesAsync()
    {
        long refreshGeneration = Interlocked.Increment(ref _refreshGeneration);
        string? selectedDirectory = NormalizeInstanceDirectory(SelectedInstance?.InstanceDirectory)
                                    ?? _preferredInstanceDirectory;
        IReadOnlyList<LaunchInstanceInfo> previousInstances = Instances;
        LaunchInstanceInfo? previousSelected = SelectedInstance;
        bool hadInstances = previousInstances.Count > 0;

        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = new CancellationTokenSource();
        _refreshCancellation.CancelAfter(TimeSpan.FromSeconds(20));
        CancellationToken cancellationToken = _refreshCancellation.Token;

        // Soft refresh: keep previous selection visible when we already have versions.
        if (!hadInstances)
            await RunOnUiThreadAsync(SetLoadingState).ConfigureAwait(false);
        else
            await RunOnUiThreadAsync(() => StatusMessage?.Invoke(this, "正在刷新游戏版本列表…")).ConfigureAwait(false);

        try
        {
            Progress<LaunchInstanceDiscoveryProgress> progress = new(value =>
            {
                if (refreshGeneration != Volatile.Read(ref _refreshGeneration))
                    return;
                // Only overwrite the version label on first load (soft refresh keeps the name).
                if (!hadInstances)
                    _ = RunOnUiThreadAsync(() => UpdateInstanceDiscoveryProgress(value));
            });
            IReadOnlyList<string> roots = !string.IsNullOrWhiteSpace(_minecraftRootDirectory)
                ? [_minecraftRootDirectory]
                : LaunchInstanceDiscovery.GetCandidateRoots();
            IReadOnlyList<LaunchInstanceInfo> instances = await LaunchInstanceDiscovery.DiscoverAsync(
                    roots,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);

            if (refreshGeneration != Volatile.Read(ref _refreshGeneration))
                return;

            await RunOnUiThreadAsync(() =>
            {
                Instances = instances;
                SelectedInstance = FindInstanceByDirectory(Instances, selectedDirectory)
                                   ?? previousSelected
                                   ?? (Instances.Count > 0 ? Instances[0] : null);
                if (SelectedInstance is not null &&
                    FindInstanceByDirectory(Instances, SelectedInstance.InstanceDirectory) is null &&
                    Instances.Count > 0)
                {
                    // Previous selection not in new root — fall back to first.
                    SelectedInstance = Instances[0];
                }

                RememberSelectedInstance();
                _isInstanceLoadFinished = true;
                RefreshButtonsUI();
                RefreshPage(anim: false);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (refreshGeneration != Volatile.Read(ref _refreshGeneration))
                return;
            await RunOnUiThreadAsync(() =>
            {
                Instances = previousInstances;
                SelectedInstance = previousSelected;
                _isInstanceLoadFinished = true;
                RefreshButtonsUI();
                if (!hadInstances)
                    StatusMessage?.Invoke(this, "检查游戏版本已取消或超时。");
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (refreshGeneration != Volatile.Read(ref _refreshGeneration))
                return;
            await RunOnUiThreadAsync(() =>
            {
                if (hadInstances)
                {
                    Instances = previousInstances;
                    SelectedInstance = previousSelected;
                    _isInstanceLoadFinished = true;
                    RefreshButtonsUI();
                    StatusMessage?.Invoke(this, "刷新游戏版本失败：" + ex.Message);
                }
                else
                {
                    Instances = [];
                    SelectedInstance = null;
                    SetDisabledState("检查游戏版本时遇到问题");
                    StatusMessage?.Invoke(this, "未能检查本地游戏版本：" + ex.Message);
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            // Never leave the homepage stuck on “正在加载 / 正在检查游戏版本”.
            if (refreshGeneration == Volatile.Read(ref _refreshGeneration) && !_isInstanceLoadFinished)
            {
                await RunOnUiThreadAsync(() =>
                {
                    if (_isInstanceLoadFinished)
                        return;
                    Instances = previousInstances;
                    SelectedInstance = previousSelected ?? SelectedInstance;
                    _isInstanceLoadFinished = true;
                    RefreshButtonsUI();
                }).ConfigureAwait(false);
            }
        }
    }

    public void SetInstances(IReadOnlyList<LaunchInstanceInfo> instances, LaunchInstanceInfo? selectedInstance = null)
    {
        string? selectedDirectory = NormalizeInstanceDirectory(selectedInstance?.InstanceDirectory)
                                    ?? NormalizeInstanceDirectory(SelectedInstance?.InstanceDirectory)
                                    ?? _preferredInstanceDirectory;
        Instances = instances ?? [];
        // Prefer the explicit selection even if discovery list is empty/stale.
        SelectedInstance = selectedInstance
                           ?? FindInstanceByDirectory(Instances, selectedDirectory)
                           ?? (Instances.Count > 0 ? Instances[0] : null);
        if (SelectedInstance is not null &&
            FindInstanceByDirectory(Instances, SelectedInstance.InstanceDirectory) is null)
        {
            // Keep selection visible even when the snapshot list is momentarily empty.
            List<LaunchInstanceInfo> merged = [SelectedInstance];
            merged.AddRange(Instances);
            Instances = merged;
        }

        RememberSelectedInstance();
        _isInstanceLoadFinished = true;
        RefreshButtonsUI();
    }

    public void SetPreferredInstanceDirectory(string? instanceDirectory)
    {
        _preferredInstanceDirectory = NormalizeInstanceDirectory(instanceDirectory);
        if (!_isInstanceLoadFinished || Instances.Count == 0)
            return;
        LaunchInstanceInfo? preferred = FindInstanceByDirectory(Instances, _preferredInstanceDirectory);
        if (preferred is null)
            return;
        SelectedInstance = preferred;
        RememberSelectedInstance();
        RefreshButtonsUI();
        RefreshPage(anim: false);
    }

    public void SetMinecraftRootDirectory(string? minecraftRootDirectory)
    {
        string? normalized = NormalizeMinecraftRoot(minecraftRootDirectory);
        if (string.Equals(_minecraftRootDirectory, normalized, StringComparison.OrdinalIgnoreCase))
            return;
        _minecraftRootDirectory = normalized;
        // Invalidate cache only — caller must RefreshInstancesAsync. Do not flip the UI to
        // perpetual loading here; RefreshInstancesAsync owns the loading chrome.
        _isInstanceLoadFinished = false;
    }

    public void SetInstanceLoading(bool isLoading)
    {
        if (isLoading)
        {
            SetLoadingState();
            return;
        }

        _isInstanceLoadFinished = true;
        RefreshButtonsUI();
    }

    public void SetSelectedProfilePresent(bool hasSelectedProfile)
    {
        HasSelectedProfile = hasSelectedProfile;
        RefreshButtonsUI();
        RefreshPage(anim: false);
    }

    public void SetLoginPage(
        Control page,
        bool animate,
        PageLaunchLeft.LaunchLoginPageType pageType = PageLaunchLeft.LaunchLoginPageType.None)
    {
        Grid? panLogin = this.FindControl<Grid>("PanLogin");
        if (panLogin is null)
            return;

        // Cancel any in-flight swap so a deferred Add cannot re-parent the same control.
        ModAnimation.AniStop("FrmLogin PageChange");
        CurrentLoginPage = page;
        if (pageType != PageLaunchLeft.LaunchLoginPageType.None)
            CurrentLoginPageType = pageType;
        page.Opacity = 1d;
        if (page is PageLaunchLeft.ILoginPage loginPage)
            loginPage.Reload();

        if (!animate || IsAlreadyLoginChild(panLogin, page))
        {
            MountLoginPage(panLogin, page);
            panLogin.Opacity = 1d;
            return;
        }

        ModAnimation.AniStart(
            new List<ModAnimation.AniData>
            {
                ModAnimation.AaOpacity(panLogin, -panLogin.Opacity, 100, ease: new ModAnimation.AniEaseOutFluent()),
                ModAnimation.AaCode(() => MountLoginPage(panLogin, page), 100),
                ModAnimation.AaOpacity(panLogin, 1d, 150, 100, new ModAnimation.AniEaseOutFluent())
            },
            "FrmLogin PageChange");
    }

    private static bool IsAlreadyLoginChild(Grid panLogin, Control page) =>
        page.Parent == panLogin && panLogin.Children.Contains(page);

    /// <summary>
    /// Safely (re)parents a login control into PanLogin. Avalonia rejects Add when the
    /// control still has another visual parent — common when the same profile skin page
    /// is re-applied or an interrupted animation retries the mount.
    /// </summary>
    private static void MountLoginPage(Grid panLogin, Control page)
    {
        if (page.Parent is Panel parentPanel && parentPanel.Children.Contains(page))
            parentPanel.Children.Remove(page);
        else if (page.Parent is Decorator decorator && ReferenceEquals(decorator.Child, page))
            decorator.Child = null;
        else if (page.Parent is ContentControl contentControl && ReferenceEquals(contentControl.Content, page))
            contentControl.Content = null;

        for (int index = panLogin.Children.Count - 1; index >= 0; index--)
        {
            if (!ReferenceEquals(panLogin.Children[index], page))
                panLogin.Children.RemoveAt(index);
        }

        if (!panLogin.Children.Contains(page))
            panLogin.Children.Add(page);
    }

    public void PageChangeToLogin()
    {
        Grid? launching = this.FindControl<Grid>("PanLaunching");
        if (launching is null)
            return;

        IsLaunchInProgress = false;
        SetVisible("PanLaunchingHint", false);
        launching.IsHitTestVisible = false;
        if (this.FindControl<Control>("PanBack") is { } idleSurface)
            idleSurface.IsHitTestVisible = true;

        ModAnimation.AniStart(
            new List<ModAnimation.AniData>
            {
                ModAnimation.AaOpacity(launching, -launching.Opacity, 120, ease: new ModAnimation.AniEaseInFluent()),
                ModAnimation.AaCode(() =>
                {
                    launching.IsVisible = false;
                    launching.Opacity = 0d;
                    if (this.FindControl<Control>("PanBack") is { } idle)
                        idle.Opacity = 1d;
                }, 120)
            },
            "Launch State Page");
        RefreshButtonsUI();
    }

    public void ConfigureLaunchingHint(bool isEnabled)
    {
        _showLaunchingHint = isEnabled;
        SetVisible("PanLaunchingHint", isEnabled && IsLaunchInProgress);
    }

    public void ShowLaunching(LaunchInstanceInfo? instance)
    {
        if (this.FindControl<Control>("PanBack") is not { } idle ||
            this.FindControl<Grid>("PanLaunching") is not { } launching)
        {
            return;
        }

        IsLaunchInProgress = true;
        _showProgress = 0d;
        _targetProgress = 0d;
        SetText("LabLaunchingTitle", AvaloniaLocalizationManager.GetText("Launch.Status.Title.Launching", "正在启动"));
        SetText("LabLaunchingName", instance?.Name ?? "等待选择版本");
        SetText("LabLaunchingStage", AvaloniaLocalizationManager.GetText("Common.Action.Initialize", "初始化"));
        SetText("LabLaunchingMethod", "等待账户档案");
        SetText("LabLaunchingHint", PageLaunchRight.GetRandomHint(enableLengthLimit: true, raw: true));
        SetVisible("PanLaunchingHint", _showLaunchingHint);
        ApplyLaunchProgressVisual(0d);
        SetVisible("LabLaunchingDownloadLeft", false);
        SetVisible("LabLaunchingDownload", false);
        if (this.FindControl<MyLoading>("LoadLaunching") is { } loading)
            loading.State.LoadingState = MyLoading.MyLoadingState.Run;

        idle.IsHitTestVisible = false;
        launching.IsHitTestVisible = false;
        launching.IsVisible = true;
        launching.Opacity = 0d;
        ModAnimation.AniStart(
            new List<ModAnimation.AniData>
            {
                ModAnimation.AaOpacity(idle, -idle.Opacity, 120, ease: new ModAnimation.AniEaseInFluent()),
                ModAnimation.AaOpacity(launching, 1d - launching.Opacity, 180, 80, new ModAnimation.AniEaseOutFluent()),
                ModAnimation.AaCode(() => launching.IsHitTestVisible = true, 180)
            },
            "Launch State Page");
    }

    public void ShowRepairing() =>
        ShowRepairWorkflow(
            AvaloniaLocalizationManager.GetText("Crash.Repair.Title", "正在修补 Minecraft"),
            AvaloniaLocalizationManager.GetText("Crash.Repair.Stage.Parse", "解析 Minecraft 异常"),
            0d);

    public void UpdateRepairStep(int current, int total)
    {
        if (total <= 0)
            return;
        double ratio = Math.Clamp(current / (double)total, 0d, 1d);
        ShowRepairWorkflow(
            AvaloniaLocalizationManager.GetText("Crash.Repair.Title", "正在修补 Minecraft"),
            AvaloniaLocalizationManager.GetText("Crash.Repair.Stage.Execute", "正在执行修复") +
            $" ({current}/{total})",
            ratio);
    }

    public void ShowRepairWorkflow(
        string title,
        string stage,
        double progress,
        string? method = null,
        LaunchInstanceInfo? instance = null)
    {
        if (!IsLaunchInProgress)
            ShowLaunching(instance ?? SelectedInstance);
        SetText("LabLaunchingTitle", title);
        SetText("LabLaunchingStage", stage);
        if (!string.IsNullOrWhiteSpace(method))
            SetText("LabLaunchingMethod", method);
        double normalized = Math.Clamp(progress, 0d, 1d);
        _targetProgress = normalized;
        _showProgress = normalized;
        ApplyLaunchProgressVisual(normalized);
    }

    public void HideRepairing()
    {
        SetText("LabLaunchingTitle", AvaloniaLocalizationManager.GetText("Launch.Status.Title.Launching", "正在启动"));
        SetText("LabLaunchingStage", AvaloniaLocalizationManager.GetText("Common.Action.Initialize", "初始化"));
        _targetProgress = 0d;
        _showProgress = 0d;
        ApplyLaunchProgressVisual(0d);
    }

    public void UpdateLaunchingStatus(string stage, double progress, string? method = null) =>
        LaunchingRefresh(stage, progress, isLaunched: false, method);

    public void LaunchingRefresh(
        string stage,
        double actualProgress,
        bool isLaunched = false,
        string? method = null,
        string? downloadSpeed = null)
    {
        actualProgress = Math.Clamp(actualProgress, 0d, 1d);
        _targetProgress = isLaunched ? 1d : actualProgress;
        if (isLaunched)
            _showProgress = 1d;
        else if (actualProgress < _showProgress)
            _showProgress = actualProgress;
        else
        {
            _showProgress += (_targetProgress - _showProgress) * 0.2d + 0.005d;
            if (_showProgress > _targetProgress)
                _showProgress = _targetProgress;
        }

        _showProgress = Math.Clamp(_showProgress, 0d, 1d);
        ApplyLaunchProgressVisual(_showProgress);
        SetText(
            "LabLaunchingTitle",
            isLaunched
                ? AvaloniaLocalizationManager.GetText("Launch.Status.Title.Launched", "游戏已启动")
                : AvaloniaLocalizationManager.GetText("Launch.Status.Title.Launching", "正在启动"));
        SetText("LabLaunchingStage", stage);
        if (!string.IsNullOrWhiteSpace(method))
            SetText("LabLaunchingMethod", method);

        bool hasDownloadSpeed = !string.IsNullOrWhiteSpace(downloadSpeed);
        SetVisible("LabLaunchingDownloadLeft", hasDownloadSpeed);
        SetVisible("LabLaunchingDownload", hasDownloadSpeed);
        if (hasDownloadSpeed)
        {
            SetOpacity("LabLaunchingDownloadLeft", 1d);
            SetOpacity("LabLaunchingDownload", 1d);
            SetText("LabLaunchingDownload", downloadSpeed!);
        }
    }

    public void RefreshButtonsUI()
    {
        if (!_isInstanceLoadFinished)
        {
            SetLoadingState();
            return;
        }

        if (this.FindControl<MyLoading>("LoadInstanceCheck") is { } check)
        {
            check.State.LoadingState = MyLoading.MyLoadingState.Stop;
            check.IsVisible = false;
        }

        if (SelectedInstance is null)
        {
            _launchButtonAction = PageLaunchLeft.LaunchButtonAction.Download;
            SetLaunchButton(
                AvaloniaLocalizationManager.GetText("Launch.Home.Button.Download", "下载游戏"),
                isEnabled: true);
            SetText("LabVersion", AvaloniaLocalizationManager.GetText(
                "Launch.Experimental.Version.Empty",
                "未找到可启动的游戏版本"));
            SetText("LabVersionAction", AvaloniaLocalizationManager.GetText(
                "Launch.Experimental.Version.TapToSelect",
                "轻点以选择或安装版本"));
            SetVersionPickerEnabled(true);
            SetVisible("BtnMore", false);
            SetAccountSummary(AvaloniaLocalizationManager.GetText(
                "Launch.Experimental.Account.NoVersion",
                "可以先登录账户；没有本地版本时会引导你安装游戏。"));
            return;
        }

        _launchButtonAction = PageLaunchLeft.LaunchButtonAction.Launch;
        SetLaunchButton(
            AvaloniaLocalizationManager.GetText("Launch.Home.Button.Launch", "启动游戏"),
            isEnabled: HasSelectedProfile);
        SetText("LabVersion", SelectedInstance.Name);
        SetText("LabVersionAction", AvaloniaLocalizationManager.GetText(
            "Launch.Experimental.Version.TapToSwitch",
            "轻点以切换版本"));
        SetVersionPickerEnabled(true);
        SetVisible("BtnMore", true);
        SetAccountSummary(HasSelectedProfile
            ? AvaloniaLocalizationManager.GetText(
                "Launch.Experimental.Account.Ready",
                "账户已就绪，可以开始游戏。")
            : AvaloniaLocalizationManager.GetText(
                "Launch.Experimental.Account.NeedLogin",
                "请选择或创建一个账户档案后再启动。"));
    }

    public void LaunchButtonClick()
    {
        if (IsLaunchInProgress ||
            this.FindControl<MyButton>("BtnLaunch") is not { IsEnabled: true } ||
            CanLaunchByPageState?.Invoke() == false)
        {
            return;
        }

        switch (_launchButtonAction)
        {
            case PageLaunchLeft.LaunchButtonAction.Launch when SelectedInstance is not null:
                if (HasIgnoreMarker(SelectedInstance))
                {
                    StatusMessage?.Invoke(this, "该版本仍在安装中，暂时不能启动。");
                    return;
                }

                LaunchInstanceInfo instance = SelectedInstance;
                ShowLaunching(instance);
                Dispatcher.UIThread.Post(
                    () => LaunchRequested?.Invoke(this, instance),
                    DispatcherPriority.Background);
                break;
            case PageLaunchLeft.LaunchButtonAction.Download:
                DownloadRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    public void RefreshPage(
        bool anim,
        PageLaunchLeft.LaunchLoginPageType targetLoginType = PageLaunchLeft.LaunchLoginPageType.None)
    {
        PageLaunchLeft.LaunchLoginPageType type = targetLoginType;
        if (type == PageLaunchLeft.LaunchLoginPageType.None)
        {
            type = HasSelectedProfile
                ? PageLaunchLeft.LaunchLoginPageType.ProfileSkin
                : PageLaunchLeft.LaunchLoginPageType.Profile;
        }

        if (CurrentLoginPageType == type)
            return;

        CurrentLoginPageType = type;
        LoginPageRequested?.Invoke(this, type);
        if (!HasSelectedProfile && _launchButtonAction != PageLaunchLeft.LaunchButtonAction.Download)
        {
            SetLaunchButton(
                AvaloniaLocalizationManager.GetText("Launch.Home.Button.Launch", "启动游戏"),
                isEnabled: false);
        }
    }

    public void TriggerEnterAnimation()
    {
        if (this.FindControl<Control>("PanBack") is { } root)
            TriggerEnterAnimation(root);
    }

    // Instance API (MainWindow calls page.AppendLog); experimental surface has no on-page log UI.
#pragma warning disable CA1822
    public void AppendLog(string message)
    {
        message = PortableLog.Redact(message);
        DesktopFileLog.Info("LaunchUI", message);
    }
#pragma warning restore CA1822

    public void SetMaximumLogLines(int maximumLogLines) =>
        _maximumLogLines = maximumLogLines <= 0 ? 1 : maximumLogLines;

    public override void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        UnregisterPluginUiSurfaces();
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = null;
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private void RegisterPluginUiSurfaces()
    {
        DesktopHostUiComposition.Instance.RegisterTarget("pcl.page.launch", this);
        if (this.FindControl<MyButton>("BtnLaunch") is { } launchButton)
            DesktopHostUiComposition.Instance.RegisterTarget("pcl.component.launch-button", launchButton);

        // Preferred: each inject on cards.flip becomes its own registered flip card.
        _flipCardSlot = new StackPanel
        {
            Name = "PanFlipCardSlot",
            Spacing = 0,
            IsVisible = false,
            Width = 0,
            Height = 0,
            IsHitTestVisible = false
        };
        // Keep off-layout but attached so Avalonia name/logical tree stays consistent.
        if (this.FindControl<Grid>("PanWidgetPager") is { } pagerHost)
            pagerHost.Children.Add(_flipCardSlot);
        _flipCardSlot.Children.CollectionChanged += OnFlipCardSlotChanged;
        DesktopHostUiComposition.Instance.RegisterSlot("pcl.page.launch", "cards.flip", _flipCardSlot);

        // Deprecated compatibility shell: primary-actions.after → single registered flip card.
        if (this.FindControl<Panel>("PanPluginWidgets") is { } inject)
        {
            DesktopHostUiComposition.Instance.RegisterSlot(
                "pcl.page.launch",
                "primary-actions.after",
                inject);
            inject.Children.CollectionChanged += OnPluginWidgetsChanged;
            RefreshPluginEmptyState();
        }
    }

    private void UnregisterPluginUiSurfaces()
    {
        if (this.FindControl<Panel>("PanPluginWidgets") is { } inject)
            inject.Children.CollectionChanged -= OnPluginWidgetsChanged;
        if (_flipCardSlot is not null)
        {
            _flipCardSlot.Children.CollectionChanged -= OnFlipCardSlotChanged;
            if (_flipCardSlot.Parent is Panel parent)
                parent.Children.Remove(_flipCardSlot);
            _flipCardSlot = null;
        }

        foreach (string tag in _flipCardSurfaces.Keys.ToList())
            UnregisterWidgetCard(tag);
        _flipCardSurfaces.Clear();

        DesktopHostUiComposition.Instance.UnregisterTarget("pcl.page.launch");
        DesktopHostUiComposition.Instance.UnregisterTarget("pcl.component.launch-button");
        DesktopHostUiComposition.Instance.UnregisterSlot("pcl.page.launch", "cards.flip");
        DesktopHostUiComposition.Instance.UnregisterSlot("pcl.page.launch", "primary-actions.after");
    }

    private void SeedCommunityHints()
    {
        if (PageLaunchRight.IsCommunityHintPermanentlyHidden())
        {
            UnregisterCommunityHintCard();
        }
        else if (this.FindControl<Control>("PanHint") is { } tips)
        {
            string message = AvaloniaLocalizationManager.GetText(
                "Launch.Right.CommunityHint.Message",
                "你正在使用 PCL N Edition！\n\n此版本由独立开发者维护，与官方 PCL 的开发路径与体验并不相同。\n\n若你误下载了 N 版，强烈建议改用官方 PCL 做长期使用；并将 N 版问题提交到 N 版仓库，不要反馈给官方仓库。");
            string[] parts = message.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            SetText("LabHint1", parts.Length > 0 ? parts[0] : message);
            SetText(
                "LabHint2",
                parts.Length > 1
                    ? string.Join("\n\n", parts.Skip(1))
                    : AvaloniaLocalizationManager.GetText(
                        "Launch.Right.CommunityHint.HidePrompt",
                        "若要永久隐藏此提示，请点击右上角关闭并输入正确的 PCL N 开发者名称。"));
            RegisterWidgetCard(CardIdCommunityHint, tips, order: 0);
        }

        SetText("LabHintExtra", PageLaunchRight.GetRandomHint(raw: true));
    }

    /// <summary>
    /// Permanently hide path: drop the community-hint flip card from the registry
    /// (not merely Opacity/IsVisible), so dots and swipe targets update.
    /// </summary>
    public void UnregisterCommunityHintCard()
    {
        UnregisterWidgetCard(CardIdCommunityHint);
        if (this.FindControl<Control>("PanHint") is { } tips)
        {
            tips.IsVisible = false;
            tips.IsHitTestVisible = false;
        }
    }

    /// <summary>Register a full-bleed flip card. <paramref name="order"/> sorts ascending.</summary>
    public void RegisterWidgetCard(string id, Control surface, int order = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(surface);

        _widgetCards.RemoveAll(entry => string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase));
        _widgetCards.Add(new WidgetCardRegistration
        {
            Id = id,
            Surface = surface,
            Order = order
        });
        RebuildWidgetPages(preferCardId: _activeWidgetCardId ?? id);
    }

    /// <summary>Remove a flip card from the pager registry.</summary>
    public void UnregisterWidgetCard(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;
        int removed = _widgetCards.RemoveAll(entry => string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
            return;
        RebuildWidgetPages(preferCardId: _activeWidgetCardId);
    }

    private void RegisterBuiltinWidgetCards()
    {
        // Community hint is registered in SeedCommunityHints (respects permanent hide).
        if (this.FindControl<Control>("PanHintExtra") is { } trivia)
            RegisterWidgetCard(CardIdTrivia, trivia, order: 10);

        // Shortcut dock is registered in RefreshShortcutDock when the experimental flag is on.

        // Deprecated primary-actions.after shell — always present as one card.
        if (this.FindControl<Control>("PanPluginPage") is { } plugin)
            RegisterWidgetCard(CardIdLegacyPrimaryActions, plugin, order: 100);
    }

    private void RebuildShortcutDockItems()
    {
        if (this.FindControl<Panel>("PanShortcutsDockItems") is not { } items ||
            this.FindControl<Control>("PanShortcutsEmpty") is not { } empty ||
            this.FindControl<Control>("PanShortcutsDock") is not { } dock)
        {
            return;
        }

        items.Children.Clear();
        IReadOnlyList<LaunchShortcutPin> pins = LaunchShortcutStore.Load();
        bool hasPins = pins.Count > 0;
        empty.IsVisible = !hasPins;
        dock.IsVisible = hasPins;
        if (!hasPins)
            return;

        foreach (LaunchShortcutPin pin in pins)
            items.Children.Add(CreateShortcutDockItem(pin));
    }

    private StackPanel CreateShortcutDockItem(LaunchShortcutPin pin)
    {
        Border iconShell = new()
        {
            Width = 48,
            Height = 48,
            CornerRadius = new CornerRadius(12),
            Background = ResolveBrush("ColorBrushWhite") ?? Brushes.White,
            BorderBrush = ResolveBrush("ColorBrushGray6"),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = new SvgIcon
            {
                Width = 22,
                Height = 22,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Icon = pin.Kind == LaunchShortcutKind.Server ? "lucide/server" : "lucide/globe",
                IconBrush = ResolveBrush("ColorBrush2") ?? Brushes.Gray
            }
        };

        TextBlock label = new()
        {
            Text = pin.Title,
            FontSize = 11,
            MaxWidth = 64,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = ResolveBrush("ColorBrush1") ?? Brushes.Black
        };

        StackPanel stack = new()
        {
            Spacing = 4,
            Width = 64,
            Cursor = new Cursor(StandardCursorType.Hand),
            Children = { iconShell, label }
        };

        ToolTip.SetTip(
            stack,
            pin.Kind == LaunchShortcutKind.Server
                ? $"{pin.Title}\n{pin.Target}\n右键取消固定"
                : $"{pin.Title}\n右键取消固定");

        stack.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(stack).Properties.IsRightButtonPressed)
            {
                e.Handled = true;
                LaunchShortcutStore.Remove(pin.Id);
                RefreshShortcutDock();
                return;
            }

            if (e.GetCurrentPoint(stack).Properties.IsLeftButtonPressed)
            {
                e.Handled = true;
                ShortcutActivated?.Invoke(this, pin);
            }
        };

        return stack;
    }

    private void InitWidgetPager()
    {
        if (this.FindControl<Control>("PanWidgetPager") is { } pager)
        {
            pager.PointerWheelChanged += OnWidgetPagerWheel;
            pager.PointerPressed += OnWidgetPagerPressed;
            pager.PointerMoved += OnWidgetPagerMoved;
            pager.PointerReleased += OnWidgetPagerReleased;
            pager.PointerCaptureLost += OnWidgetPagerCaptureLost;
        }

        // Hide all known card surfaces until registry rebuild selects one.
        foreach (string name in new[] { "PanHint", "PanHintExtra", "PanPluginPage", "PanShortcuts" })
        {
            if (this.FindControl<Control>(name) is { } surface)
            {
                surface.IsVisible = false;
                surface.Opacity = 0d;
                surface.IsHitTestVisible = false;
                surface.RenderTransform = new TranslateTransform();
            }
        }
    }

    private void RebuildWidgetPages(string? preferCardId)
    {
        ModAnimation.AniStop(WidgetPageAnimId);
        _widgetPageAnimating = false;
        _widgetDragging = false;

        List<WidgetCardRegistration> ordered = _widgetCards
            .OrderBy(static entry => entry.Order)
            .ThenBy(static entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _widgetPages = ordered.Select(static entry => entry.Surface).ToArray();

        int index = 0;
        if (!string.IsNullOrWhiteSpace(preferCardId))
        {
            int found = ordered.FindIndex(entry =>
                string.Equals(entry.Id, preferCardId, StringComparison.OrdinalIgnoreCase));
            if (found >= 0)
                index = found;
        }

        _widgetPageIndex = _widgetPages.Length == 0 ? 0 : Math.Clamp(index, 0, _widgetPages.Length - 1);
        _activeWidgetCardId = ordered.Count > 0 ? ordered[_widgetPageIndex].Id : null;

        // Detach dynamic flip surfaces that are no longer registered from the pages host.
        if (this.FindControl<Panel>("PanWidgetPages") is { } pagesHost)
        {
            foreach (Control orphan in pagesHost.Children
                         .OfType<Control>()
                         .Where(child => child.Tag is string tag &&
                                         tag.StartsWith("pcl.flip.card:", StringComparison.Ordinal) &&
                                         ordered.All(entry => !ReferenceEquals(entry.Surface, child)))
                         .ToList())
            {
                pagesHost.Children.Remove(orphan);
            }
        }

        for (int i = 0; i < _widgetPages.Length; i++)
        {
            Control page = _widgetPages[i];
            bool active = i == _widgetPageIndex;
            page.IsVisible = active;
            page.Opacity = active ? 1d : 0d;
            page.IsHitTestVisible = active;
            if (page.RenderTransform is TranslateTransform tf)
                tf.Y = 0d;
            else
                page.RenderTransform = new TranslateTransform();
        }

        // Hide built-in surfaces that are not registered.
        foreach (string name in new[] { "PanHint", "PanHintExtra", "PanPluginPage", "PanShortcuts" })
        {
            if (this.FindControl<Control>(name) is not { } surface)
                continue;
            if (_widgetPages.Contains(surface))
                continue;
            surface.IsVisible = false;
            surface.Opacity = 0d;
            surface.IsHitTestVisible = false;
        }

        BuildWidgetDots();
        ApplyWidgetDotState();
    }

    private void BuildWidgetDots()
    {
        if (this.FindControl<Panel>("PanWidgetDots") is not { } dots)
            return;

        dots.Children.Clear();
        for (int i = 0; i < _widgetPages.Length; i++)
        {
            int pageIndex = i;
            Border dot = new()
            {
                Width = 7,
                Height = 7,
                CornerRadius = new CornerRadius(4),
                Cursor = new Cursor(StandardCursorType.Hand),
                Tag = pageIndex
            };
            dot.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                GoToWidgetPage(pageIndex, animate: true);
            };
            dots.Children.Add(dot);
        }
    }

    private void ApplyWidgetDotState()
    {
        if (this.FindControl<Panel>("PanWidgetDots") is not { } dots)
            return;

        IBrush? active = ResolveBrush("ColorBrush3")
                         ?? ResolveBrush("ColorBrushHighlight")
                         ?? Brushes.DodgerBlue;
        IBrush? idle = ResolveBrush("ColorBrushGray5")
                       ?? ResolveBrush("ColorBrushGray6")
                       ?? Brushes.Gray;

        for (int i = 0; i < dots.Children.Count; i++)
        {
            if (dots.Children[i] is not Border dot)
                continue;
            bool isActive = i == _widgetPageIndex;
            dot.Background = isActive ? active : idle;
            // Vertical indicator: active pill stretches tall, not wide.
            dot.Width = 7;
            dot.Height = isActive ? 16 : 7;
            dot.Opacity = isActive ? 1d : 0.55d;
        }
    }

    private void OnWidgetPagerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_widgetPages.Length <= 1 || _widgetPageAnimating)
            return;

        // Vertical wheel: down → next card, up → previous.
        if (Math.Abs(e.Delta.Y) < 0.01d && Math.Abs(e.Delta.X) < 0.01d)
            return;

        double delta = Math.Abs(e.Delta.Y) >= Math.Abs(e.Delta.X) ? e.Delta.Y : e.Delta.X;
        if (delta < 0)
            GoToWidgetPage(_widgetPageIndex + 1, animate: true);
        else
            GoToWidgetPage(_widgetPageIndex - 1, animate: true);
        e.Handled = true;
    }

    private void OnWidgetPagerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_widgetPages.Length <= 1 || _widgetPageAnimating)
            return;
        if (!e.GetCurrentPoint(this.FindControl<Control>("PanWidgetPager")).Properties.IsLeftButtonPressed)
            return;

        _widgetDragging = true;
        _widgetDragStart = e.GetPosition(this.FindControl<Control>("PanWidgetPager"));
        e.Pointer.Capture(this.FindControl<Control>("PanWidgetPager"));
    }

    private void OnWidgetPagerMoved(object? sender, PointerEventArgs e)
    {
        if (!_widgetDragging || _widgetPages.Length == 0 || _widgetPageAnimating)
            return;

        Control? pager = this.FindControl<Control>("PanWidgetPager");
        if (pager is null)
            return;

        Point pos = e.GetPosition(pager);
        double dy = pos.Y - _widgetDragStart.Y;
        Control current = _widgetPages[_widgetPageIndex];
        if (current.RenderTransform is not TranslateTransform tf)
        {
            tf = new TranslateTransform();
            current.RenderTransform = tf;
        }

        // Rubber-band slightly at ends.
        bool atStart = _widgetPageIndex <= 0 && dy > 0;
        bool atEnd = _widgetPageIndex >= _widgetPages.Length - 1 && dy < 0;
        tf.Y = atStart || atEnd ? dy * 0.35d : dy * 0.85d;
    }

    private void OnWidgetPagerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_widgetDragging)
            return;
        FinishWidgetDrag(e.GetPosition(this.FindControl<Control>("PanWidgetPager")));
        e.Pointer.Capture(null);
    }

    private void OnWidgetPagerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_widgetDragging)
            return;
        FinishWidgetDrag(_widgetDragStart);
    }

    private void FinishWidgetDrag(Point end)
    {
        _widgetDragging = false;
        if (_widgetPages.Length == 0)
            return;

        double dy = end.Y - _widgetDragStart.Y;
        Control current = _widgetPages[_widgetPageIndex];
        if (Math.Abs(dy) >= WidgetSwipeThresholdPx)
        {
            // Finger up → next page (content moves up); finger down → previous.
            if (dy < 0)
                GoToWidgetPage(_widgetPageIndex + 1, animate: true);
            else
                GoToWidgetPage(_widgetPageIndex - 1, animate: true);
            return;
        }

        // Snap back.
        if (current.RenderTransform is TranslateTransform tf && Math.Abs(tf.Y) > 0.5d)
        {
            ModAnimation.AniStop(WidgetPageAnimId);
            ModAnimation.AniStart(
                new List<ModAnimation.AniData>
                {
                    ModAnimation.AaTranslateY(current, -tf.Y, 140, ease: new ModAnimation.AniEaseOutFluent())
                },
                WidgetPageAnimId);
        }
    }

    private IBrush? ResolveBrush(string key) =>
        TryGetResource(key, null, out object? resource) && resource is IBrush brush ? brush : null;

    private void GoToWidgetPage(int index, bool animate)
    {
        if (_widgetPages.Length == 0)
            return;

        index = Math.Clamp(index, 0, _widgetPages.Length - 1);
        if (index == _widgetPageIndex)
        {
            // Ensure current page is fully visible (e.g. after cancelled drag).
            Control stay = _widgetPages[index];
            stay.IsVisible = true;
            stay.Opacity = 1d;
            stay.IsHitTestVisible = true;
            if (stay.RenderTransform is TranslateTransform stayTf)
                stayTf.Y = 0d;
            ApplyWidgetDotState();
            return;
        }

        int from = _widgetPageIndex;
        Control leaving = _widgetPages[from];
        Control entering = _widgetPages[index];
        bool goingNext = index > from;
        double travel = Math.Max(80d, this.FindControl<Control>("PanWidgetPager")?.Bounds.Height * 0.28d ?? 120d);

        _widgetPageIndex = index;
        if (index >= 0 && index < _widgetCards.Count)
        {
            List<WidgetCardRegistration> ordered = _widgetCards
                .OrderBy(static entry => entry.Order)
                .ThenBy(static entry => entry.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (index < ordered.Count)
                _activeWidgetCardId = ordered[index].Id;
        }

        ApplyWidgetDotState();

        if (!animate)
        {
            for (int i = 0; i < _widgetPages.Length; i++)
            {
                Control page = _widgetPages[i];
                page.IsVisible = i == index;
                page.Opacity = i == index ? 1d : 0d;
                page.IsHitTestVisible = i == index;
                if (page.RenderTransform is TranslateTransform reset)
                    reset.Y = 0d;
                else
                    page.RenderTransform = new TranslateTransform();
            }

            return;
        }

        _widgetPageAnimating = true;
        ModAnimation.AniStop(WidgetPageAnimId);

        if (leaving.RenderTransform is not TranslateTransform leaveTf)
        {
            leaveTf = new TranslateTransform();
            leaving.RenderTransform = leaveTf;
        }

        if (entering.RenderTransform is not TranslateTransform enterTf)
        {
            enterTf = new TranslateTransform();
            entering.RenderTransform = enterTf;
        }

        enterTf.Y = goingNext ? travel : -travel;
        entering.Opacity = 0d;
        entering.IsVisible = true;
        entering.IsHitTestVisible = false;
        leaving.IsHitTestVisible = false;

        double leaveTo = goingNext ? -travel : travel;
        ModAnimation.AniStart(
            new List<ModAnimation.AniData>
            {
                ModAnimation.AaTranslateY(leaving, leaveTo - leaveTf.Y, WidgetFlipMs,
                    ease: new ModAnimation.AniEaseInFluent()),
                ModAnimation.AaOpacity(leaving, -leaving.Opacity, WidgetFlipMs - 40,
                    ease: new ModAnimation.AniEaseInFluent()),
                ModAnimation.AaTranslateY(entering, -enterTf.Y, WidgetFlipMs,
                    ease: new ModAnimation.AniEaseOutFluent()),
                ModAnimation.AaOpacity(entering, 1d - entering.Opacity, WidgetFlipMs,
                    ease: new ModAnimation.AniEaseOutFluent()),
                ModAnimation.AaCode(() =>
                {
                    leaving.IsVisible = false;
                    leaving.Opacity = 0d;
                    if (leaving.RenderTransform is TranslateTransform doneLeave)
                        doneLeave.Y = 0d;
                    if (entering.RenderTransform is TranslateTransform doneEnter)
                        doneEnter.Y = 0d;
                    entering.Opacity = 1d;
                    entering.IsHitTestVisible = true;
                    _widgetPageAnimating = false;
                }, after: true)
            },
            WidgetPageAnimId);
    }

    private void OnPluginWidgetsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RefreshPluginEmptyState();

    private void RefreshPluginEmptyState()
    {
        if (this.FindControl<Panel>("PanPluginWidgets") is not { } inject)
            return;
        bool hasPlugins = inject.Children.Count > 0;
        if (this.FindControl<Control>("LabPluginEmpty") is { } empty)
            empty.IsVisible = !hasPlugins;

        // Keep the compatibility card registered so legacy inject still has a home,
        // but hide empty chrome from the pager when nothing is contributed.
        if (hasPlugins)
        {
            if (this.FindControl<Control>("PanPluginPage") is { } plugin &&
                _widgetCards.All(entry => !string.Equals(entry.Id, CardIdLegacyPrimaryActions, StringComparison.OrdinalIgnoreCase)))
            {
                RegisterWidgetCard(CardIdLegacyPrimaryActions, plugin, order: 100);
            }
        }
        else
        {
            // Leave registered with empty state so first inject has a ready card;
            // still show the empty shell as one page (helps discoverability of the slot).
        }
    }

    /// <summary>
    /// Preferred slot <c>cards.flip</c>: each inject is promoted to a full-bleed registered card.
    /// Sentinels stay in the slot panel so RemoveInjection can still find the tag.
    /// </summary>
    private void OnFlipCardSlotChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_flipCardSlot is null || _flipSlotSyncing)
            return;

        _flipSlotSyncing = true;
        try
        {
            HashSet<string> liveTags = new(StringComparer.OrdinalIgnoreCase);

            foreach (Control child in _flipCardSlot.Children.OfType<Control>().ToList())
            {
                if (child.Tag is not string tag ||
                    !tag.StartsWith("pcl.plugin.inject:", StringComparison.Ordinal))
                {
                    continue;
                }

                liveTags.Add(tag);

                // Sentinel left behind after promotion — keep for RemoveInjection.
                if (string.Equals(child.Name, "FlipCardSentinel", StringComparison.Ordinal))
                    continue;

                // Already promoted (should not remain as raw child).
                if (_flipCardSurfaces.ContainsKey(tag))
                    continue;

                int order = 200 + _flipCardSurfaces.Count;
                _flipCardSlot.Children.Remove(child);

                Border card = WrapAsFlipCard(child, tag);
                Border sentinel = new()
                {
                    Name = "FlipCardSentinel",
                    Tag = tag,
                    Width = 0,
                    Height = 0,
                    IsVisible = false,
                    IsHitTestVisible = false
                };
                _flipCardSlot.Children.Add(sentinel);

                _flipCardSurfaces[tag] = card;
                if (this.FindControl<Panel>("PanWidgetPages") is { } pagesHost &&
                    !pagesHost.Children.Contains(card))
                {
                    pagesHost.Children.Add(card);
                }

                RegisterWidgetCard(tag, card, order: order);
            }

            // Drop cards whose inject was removed (sentinel gone).
            foreach (string tag in _flipCardSurfaces.Keys.ToList())
            {
                if (liveTags.Contains(tag))
                    continue;

                if (_flipCardSurfaces.TryGetValue(tag, out Control? surface) &&
                    surface.Parent is Panel parent)
                {
                    parent.Children.Remove(surface);
                }

                _flipCardSurfaces.Remove(tag);
                UnregisterWidgetCard(tag);
            }
        }
        finally
        {
            _flipSlotSyncing = false;
        }
    }

    private Border WrapAsFlipCard(Control content, string tag)
    {
        Border card = new()
        {
            Name = "FlipCard_" + tag.Replace(':', '_'),
            Tag = "pcl.flip.card:" + tag,
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(16, 12, 32, 12),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsVisible = false,
            Opacity = 0d,
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                AllowAutoHide = true,
                Content = content
            }
        };
        card.Background = ResolveBrush("ColorBrushWhite") ?? Brushes.White;
        card.BorderBrush = ResolveBrush("ColorBrushGray6") ?? Brushes.LightGray;
        return card;
    }

    private void BtnLaunch_Click(object? sender, EventArgs e) => LaunchButtonClick();

    private void BtnInstance_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left)
            return;
        if (this.FindControl<Control>("BtnInstance") is not { IsEnabled: true })
            return;
        if (IsLaunchInProgress)
            return;

        InstanceSelectRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void BtnMore_Click(object? sender, EventArgs e)
    {
        if (IsLaunchInProgress || SelectedInstance is null)
            return;
        if (HasIgnoreMarker(SelectedInstance))
        {
            StatusMessage?.Invoke(this, "该版本仍在安装中，暂时不能调整设置。");
            return;
        }

        InstanceSettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SetVersionPickerEnabled(bool isEnabled)
    {
        if (this.FindControl<Control>("BtnInstance") is not { } picker)
            return;
        picker.IsEnabled = isEnabled;
        picker.Opacity = isEnabled ? 1d : 0.55d;
        picker.Cursor = isEnabled ? new Cursor(StandardCursorType.Hand) : new Cursor(StandardCursorType.Arrow);
    }

    private void BtnCancel_Click(object? sender, EventArgs e)
    {
        if (!IsLaunchInProgress)
            return;
        SetText("LabLaunchingStage", AvaloniaLocalizationManager.GetText(
            "Minecraft.Launch.Cancelled.Request",
            "已请求取消启动"));
        CancelLaunchRequested?.Invoke(this, EventArgs.Empty);
        if (IsLaunchInProgress)
            PageChangeToLogin();
    }

    private void BtnHintClose_Click(object? sender, EventArgs e) =>
        CommunityHintHideRequested?.Invoke(this, EventArgs.Empty);

    private void SetLoadingState()
    {
        _isInstanceLoadFinished = false;
        _launchButtonAction = PageLaunchLeft.LaunchButtonAction.Loading;
        SetLaunchButton(
            AvaloniaLocalizationManager.GetText("Launch.Home.Button.Loading", "正在加载"),
            isEnabled: false);
        SetText("LabVersion", AvaloniaLocalizationManager.GetText(
            "Launch.Home.VersionList.Loading",
            "正在检查游戏版本"));
        SetText("LabVersionAction", AvaloniaLocalizationManager.GetText(
            "Launch.Experimental.Version.Scanning",
            "正在扫描本地版本…"));
        // Keep version picker usable so a stuck discovery never traps the user.
        SetVersionPickerEnabled(true);
        SetVisible("BtnMore", false);
        if (this.FindControl<MyLoading>("LoadInstanceCheck") is { } check)
        {
            check.IsVisible = true;
            check.State.LoadingState = MyLoading.MyLoadingState.Run;
        }
    }

    private void SetDisabledState(string message)
    {
        _isInstanceLoadFinished = true;
        _launchButtonAction = PageLaunchLeft.LaunchButtonAction.Disabled;
        SetLaunchButton(
            AvaloniaLocalizationManager.GetText("Launch.Home.Button.Launch", "启动游戏"),
            isEnabled: false);
        SetText("LabVersion", message);
        SetText("LabVersionAction", AvaloniaLocalizationManager.GetText(
            "Launch.Experimental.Version.TapToSelect",
            "轻点以选择或安装版本"));
        SetVersionPickerEnabled(true);
        SetVisible("BtnMore", false);
        if (this.FindControl<MyLoading>("LoadInstanceCheck") is { } check)
        {
            check.IsVisible = true;
            check.State.LoadingState = MyLoading.MyLoadingState.Error;
        }
    }

    private void UpdateInstanceDiscoveryProgress(LaunchInstanceDiscoveryProgress progress)
    {
        string detail = progress.Total > 0 && progress.Stage == "正在检查游戏版本"
            ? $"{progress.Stage} ({Math.Min(progress.Current, progress.Total)}/{progress.Total}) · 已找到 {progress.Found} 个"
            : progress.Stage;
        SetText("LabVersion", detail);
        SetText("LabVersionAction", AvaloniaLocalizationManager.GetText(
            "Launch.Experimental.Version.Scanning",
            "正在扫描本地版本…"));
    }

    private void ApplyLaunchProgressVisual(double ratio)
    {
        ratio = Math.Clamp(ratio, 0d, 1d);
        SetText("LabLaunchingProgress", ratio.ToString("P0", CultureInfo.CurrentCulture));
        if (this.FindControl<Grid>("PanLaunching") is null)
            return;
        // Progress uses a two-column star grid approximated via star weights on the fill rect's parent.
        if (this.FindControl<Rectangle>("RectLaunchProgressFill")?.Parent is Grid { ColumnDefinitions.Count: >= 2 } bar)
        {
            bar.ColumnDefinitions[0].Width = new GridLength(Math.Max(0.001d, ratio), GridUnitType.Star);
            bar.ColumnDefinitions[1].Width = new GridLength(Math.Max(0.001d, 1d - ratio), GridUnitType.Star);
        }
    }

    private void SetLaunchButton(string text, bool isEnabled)
    {
        if (this.FindControl<MyButton>("BtnLaunch") is { } button)
        {
            button.Text = text;
            button.IsEnabled = isEnabled;
        }
    }

    private void SetButtonEnabled(string name, bool isEnabled)
    {
        if (this.FindControl<Control>(name) is { } control)
            control.IsEnabled = isEnabled;
    }

    private void SetVisible(string name, bool isVisible)
    {
        if (this.FindControl<Control>(name) is { } control)
            control.IsVisible = isVisible;
    }

    private void SetOpacity(string name, double opacity)
    {
        if (this.FindControl<Control>(name) is { } control)
            control.Opacity = opacity;
    }

    private void SetText(string name, string text)
    {
        if (this.FindControl<TextBlock>(name) is { } block)
            block.Text = text;
    }

    private void SetAccountSummary(string text) => SetText("LabAccountSummary", text);

    private void RememberSelectedInstance()
    {
        if (SelectedInstance is not null)
            _preferredInstanceDirectory = NormalizeInstanceDirectory(SelectedInstance.InstanceDirectory);
    }

    private static bool HasIgnoreMarker(LaunchInstanceInfo instance) =>
        File.Exists(instance.InstanceDirectory + ".pclignore");

    private static LaunchInstanceInfo? FindInstanceByDirectory(
        IReadOnlyList<LaunchInstanceInfo> instances,
        string? instanceDirectory)
    {
        if (string.IsNullOrWhiteSpace(instanceDirectory))
            return null;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        foreach (LaunchInstanceInfo instance in instances)
        {
            string? candidate = NormalizeInstanceDirectory(instance.InstanceDirectory);
            if (candidate is not null && string.Equals(candidate, instanceDirectory, comparison))
                return instance;
        }

        return null;
    }

    private static string? NormalizeInstanceDirectory(string? instanceDirectory)
    {
        if (string.IsNullOrWhiteSpace(instanceDirectory))
            return null;
        try
        {
            return IoPath.TrimEndingDirectorySeparator(IoPath.GetFullPath(instanceDirectory.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? NormalizeMinecraftRoot(string? minecraftRootDirectory)
    {
        if (string.IsNullOrWhiteSpace(minecraftRootDirectory))
            return null;
        try
        {
            return IoPath.TrimEndingDirectorySeparator(IoPath.GetFullPath(minecraftRootDirectory.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static Task RunOnUiThreadAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }
}

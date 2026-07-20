// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PCL.Application.Accounts;
using PCL.Application.Downloads;
using PCL.Application.Hosting.RuntimeExtensions;
using PCL.Application.Instances;
using PCL.Application.Launching;
using PCL.Application.Minecraft.Launch.Arguments;
using PCL.Application.Minecraft.Java;
using PCL.Application.Minecraft.Launch;
using PCL.Application.Settings;
using PCL.Core.App;
using PCL.Core.Logging;
using PCL.Domain.Minecraft.Launch;
using PCL.Desktop.Composition;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Diagnostics;
using PCL.Desktop.Features.Community;
using PCL.Desktop.Hosting;
using PCL.Desktop.Localization;
using PCL.Desktop.Messaging;
using PCL.Desktop.Session;
using PCL.Desktop.Shell;
using PCL.Desktop.Theme;
using PCL.Desktop.Platform;
using PCL.Desktop.Features.Downloads.Views;
using PCL.Desktop.Features.Instances;
using PCL.Desktop.Features.Instances.Views;
using PCL.Desktop.Features.Launching;
using PCL.Desktop.Features.Launching.Views;
using CommunityToolkit.Mvvm.Messaging;
using PCL.Desktop.Features.Settings.Views;
using PCL.Desktop.Features.Shared;
using PCL.Desktop.Features.Tasks.Views;
using PCL.Platform.Java;
using PCL.Platform.Paths;
using PCL.UI.Abstractions.Navigation;
using PCL.UI.Abstractions.Pages;

namespace PCL.Desktop.Views;

public partial class MainWindow : Window, IDisposable
{
    private static readonly IWebProxy SystemDefaultProxy = HttpClient.DefaultProxy;
    private Control? _showAnimationRoot;
    private RotateTransform? _showAnimationRotate;
    private TranslateTransform? _showAnimationTranslate;
    private bool _showAnimationStarted;
    private bool _isNavExpanded;
    private DispatcherTimer? _navAnimTimer;
    private double _navExpandedWidth = 200d;
    private double _navAnimStart;
    private double _navAnimTarget;
    private int _navAnimElapsed;
    private NavigationRouteId? _currentNavRoute;
    private NavigationRouteId? _pendingNavRoute;
    private bool _isMainWindowOpened;
    private ILaunchHomeSurface? _launchLeft;
    private PageLaunchRight? _launchRight;
    private PageLaunchHomeExperimental? _launchHomeExperimental;
    private bool _useExperimentalLaunchHome;
    private bool _experimentalChromeApplied;
    private readonly AppShellViewModel _shellViewModel;
    private readonly TitleBarViewModel _titleBarViewModel;
    private readonly ExtraDockViewModel _extraDockViewModel;
    private readonly MinecraftFolderStore _folderStore;
    private readonly InstanceSelectionStore _instanceSelectionStore;
    private readonly TaskSessionStore _taskSessionStore;
    private readonly GameSessionStore _gameSessionStore;
    private readonly InstancesSelectSurface _instancesSelect;
    private readonly LaunchHomeProfileResolver _launchHomeProfile;
    private bool _isDisposed;
    private PageLoginProfile? _loginProfilePage;
    private PageLoginProfileSkin? _loginProfileSkinPage;
    private PageLoginMs? _loginMsPage;
    private PageLoginAuth? _loginAuthPage;
    private PageLoginOffline? _loginOfflinePage;
    private PageDownloadLeft? _downloadLeft;
    private PageDownloadInstall? _downloadInstallPage;
    private PageCommunityLeft? _communityLeft;
    private PageCommunityRight? _communityRight;
    private PageCommunityDetail? _communityDetail;
    private PageCommunityFavoritesRight? _communityFavoritesRight;
    private readonly CommunityFavoritesStore _communityFavorites = new();
    private PageSpeedLeft? _speedLeft;
    private PageSpeedRight? _speedRight;
    private PageInstanceLeft? _instanceLeft;
    private PageInstanceManageRight? _instanceManagePage;
    private PageInstanceSetupRight? _instanceSetupPage;
    private PageInstanceExportRight? _instanceExportPage;
    private PageInstanceInstallRight? _instanceInstallPage;
    private PageInstanceSavesRight? _instanceSavesPage;
    private PageInstanceSavesInfoRight? _instanceSavesInfoPage;
    private PageInstanceScreenshotRight? _instanceScreenshotPage;
    private PageInstanceToolsRight? _instanceToolsPage;
    private PageInstanceModDisabledRight? _instanceModDisabledPage;
    private PageInstanceResourceRight? _instanceResourcePage;
    private PageInstanceResourceRight? _instanceDatapackPage;
    private PageInstanceServerRight? _instanceServerPage;
    private LaunchInstanceInfo? _managedInstance;
    private bool _isTitleSubPageVisible;
    private Action? _titleInnerBackAction;
    private MyScrollViewer? _backButtonScrollViewer;
    private CancellationTokenSource? _launchCancellation;
    private CancellationTokenSource? _microsoftLoginCancellation;
    private readonly MinecraftVanillaInstallService _minecraftInstallService = new();
    private readonly MinecraftLaunchCoordinator _launchCoordinator;
    private readonly MinecraftAiRepairAdvisor _minecraftAiRepairAdvisor = new();
    private readonly ThirdPartyAuthService _thirdPartyAuthService = new();
    private readonly IMicrosoftMinecraftAuthService _microsoftAuthService;
    private readonly Action<string> _externalUrlOpener;
    private readonly Func<string, Task>? _clipboardWriter;
    private PageSetupLeft? _setupLeft;
    private MyPageRight? _setupRight;
    private readonly List<LoginProfileInfo> _loginProfiles = [];
    private NavigationPageDescriptor[] _navigationPages;
    private readonly Dictionary<string, CancellationTokenSource> _taskCancellations = [];
    private readonly DesktopPageAdapter _pageAdapter = new();
    private readonly DesktopPageContext _desktopPageContext;
    private int _registeredPageRequestId;
    private NavigationRouteId? _taskManagerBackRoute;
    private Action? _taskManagerBackAction;
    private Process? _runningGameProcess;
    private RunningGameContext? _runningGameContext;
    private double _targetWindowOpacity = 1d;
    private string? _backgroundStamp;
    private string? _backgroundFile;
    private string? _backgroundRefreshToken;
    private Bitmap? _backgroundBitmap;
    private string? _titleLogoFile;
    private Bitmap? _titleLogoBitmap;
    private string? _homepageSignature;
    private CancellationTokenSource? _homepageLoadCancellation;
    private readonly IDisposable _windowStateSubscription;
    private string? _registeredPluginPageSurfaceId;

    private const double NavCollapsedWidth = 50d;
    private const int NavAnimDuration = 200;

    private static readonly NavigationRouteId LaunchRoute = DesktopNavigationRegistry.LaunchRoute;
    private static readonly NavigationRouteId DownloadRoute = DesktopNavigationRegistry.DownloadRoute;
    private static readonly NavigationRouteId CommunityRoute = DesktopNavigationRegistry.CommunityRoute;
    private static readonly NavigationRouteId SettingsRoute = DesktopNavigationRegistry.SettingsRoute;

    public MainWindow()
        : this(new MicrosoftMinecraftAuthService())
    {
    }

    public MainWindow(
        IMicrosoftMinecraftAuthService microsoftAuthService,
        Action<string>? externalUrlOpener = null,
        Func<string, Task>? clipboardWriter = null)
    {
        _microsoftAuthService = microsoftAuthService ?? throw new ArgumentNullException(nameof(microsoftAuthService));
        _externalUrlOpener = externalUrlOpener ?? OpenExternalUrlCore;
        _clipboardWriter = clipboardWriter;
        _launchCoordinator = new MinecraftLaunchCoordinator(_minecraftInstallService);
        if (!DesktopCompositionRoot.IsInitialized)
            DesktopCompositionRoot.Initialize();
        _shellViewModel = DesktopCompositionRoot.GetRequiredService<AppShellViewModel>();
        _titleBarViewModel = DesktopCompositionRoot.GetRequiredService<TitleBarViewModel>();
        _extraDockViewModel = DesktopCompositionRoot.GetRequiredService<ExtraDockViewModel>();
        _folderStore = DesktopCompositionRoot.GetRequiredService<MinecraftFolderStore>();
        _instanceSelectionStore = DesktopCompositionRoot.GetRequiredService<InstanceSelectionStore>();
        _taskSessionStore = DesktopCompositionRoot.GetRequiredService<TaskSessionStore>();
        _gameSessionStore = DesktopCompositionRoot.GetRequiredService<GameSessionStore>();
        _instancesSelect = DesktopCompositionRoot.GetRequiredService<InstancesSelectSurface>();
        _launchHomeProfile = DesktopCompositionRoot.GetRequiredService<LaunchHomeProfileResolver>();
        _extraDockViewModel.PropertyChanged += ExtraDockViewModel_PropertyChanged;
        AvaloniaXamlLoader.Load(this);
        DesktopHostUiComposition.Instance.RegisterTarget("pcl.window.main", this);
        if (this.FindControl<Panel>("PanTitleSelect") is { } navigationPanel)
        {
            DesktopHostUiComposition.Instance.RegisterTarget("pcl.navigation.main", navigationPanel);
            DesktopHostUiComposition.Instance.RegisterSlot(
                "pcl.navigation.main",
                "items.after-download",
                navigationPanel);
        }
        _windowStateSubscription = this.GetObservable(WindowStateProperty).Subscribe(_ =>
        {
            UpdateBackgroundVideoPlayback();
            UpdateWindowChrome();
        });
        if (this.FindControl<MediaElement>("VideoBack") is { } video)
            video.MediaFailed += VideoFailed;
        _navigationPages = CreateNavigationPageMap(DesktopHost.Current.Navigation);
        BuildMainNavigationItems();
        _desktopPageContext = new DesktopPageContext(
            CreateLaunchMainPage,
            CreateDownloadMainPage,
            CreateCommunityMainPage,
            CreateSettingsMainPage,
            CreatePlaceholderMainPage);
        // Headless tests skip the load animation so the window is not left at Opacity 0.
        Opacity = ShouldSuppressStartupDialogs() ? 1d : 0d;
        CanResize = true;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        SetWindowIcon();
        ApplyMacOsChromeIfNeeded();
        LauncherSettingsPageBinder.SettingsChanged += LauncherSettingsChanged;
        AvaloniaThemeManager.ThemeChanged += ThemeChanged;
        AvaloniaLocalizationManager.LanguageChanged += LocalizationChanged;
        ApplyRuntimeSettings(LauncherSettingsPageBinder.LoadSettings());
        RefreshTitleButtonsBeforeFirstFrame();
        RefreshNavigationText();
        CaptureShowAnimationTransforms();
        Opened += OnMainWindowOpened;
        DesktopHostNotifications.Instance.Attach(OnPluginHostNotification);
        DesktopHostNotifications.Instance.AttachChoice(OnPluginHostChoiceAsync);
        DesktopHostBackgroundTasks.Instance.Attach(BeginHostBackgroundTask);
        DesktopHost.Current.Navigation.Changed += NavigationRegistryChanged;
        DesktopHostNavigation.Instance.Attach(NavigateToPluginRoute);
        _ = LoadProfilesAsync();
        SelectNavRoute(LaunchRoute, animate: false);
    }

    private void OnPluginHostNotification(string message, bool critical) =>
        ShowHint(message, critical);

    private Task<int> OnPluginHostChoiceAsync(
        string title,
        string markdown,
        string primaryButton,
        string secondaryButton,
        string thirdButton,
        bool isWarn)
    {
        TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Show()
        {
            ShowMarkdownDialog(
                title,
                markdown,
                result => completion.TrySetResult(result),
                primaryButton,
                secondaryButton,
                thirdButton,
                isWarn);
        }

        if (Dispatcher.UIThread.CheckAccess())
            Show();
        else
            Dispatcher.UIThread.Post(Show);
        return completion.Task;
    }

    /// <summary>MC install-style task manager bridge used by plugin market downloads.</summary>
    private IHostBackgroundTask BeginHostBackgroundTask(string title, bool openTaskManager)
    {
        string taskId = CreateTaskId("plugin", Guid.NewGuid().ToString("N")[..8]);
        CancellationTokenSource cancellation = RegisterTrackedTask(taskId);
        if (openTaskManager)
            ActivateTaskManagerPage(animate: true);
        TrackTaskBegin(taskId, title, "准备任务");
        return new HostBackgroundTaskProxy(this, taskId, title, cancellation);
    }

    private sealed class HostBackgroundTaskProxy : IHostBackgroundTask
    {
        private readonly MainWindow _window;
        private readonly string _taskId;
        private string _title;
        private readonly CancellationTokenSource _cancellation;
        private bool _disposed;

        public HostBackgroundTaskProxy(
            MainWindow window,
            string taskId,
            string title,
            CancellationTokenSource cancellation)
        {
            _window = window;
            _taskId = taskId;
            _title = title;
            _cancellation = cancellation;
        }

        public CancellationToken Token => _cancellation.Token;

        public void Report(HostBackgroundTaskProgress progress)
        {
            if (_disposed)
                return;
            void Apply()
            {
                _title = string.IsNullOrWhiteSpace(progress.Stage) ? _title : _title;
                TaskManagerSubTaskSnapshot[]? steps = progress.Steps is { Count: > 0 }
                    ? progress.Steps.Select(static step => new TaskManagerSubTaskSnapshot(
                        step.Name,
                        step.Detail,
                        step.Progress,
                        step.State switch
                        {
                            HostBackgroundTaskStepState.Waiting => TaskManagerTaskState.Waiting,
                            HostBackgroundTaskStepState.Finished => TaskManagerTaskState.Finished,
                            HostBackgroundTaskStepState.Failed => TaskManagerTaskState.Failed,
                            _ => TaskManagerTaskState.Running
                        })).ToArray()
                    : null;
                _window._taskSessionStore.Upsert(_taskId, new TaskManagerEntrySnapshot(
                    _taskId,
                    _title,
                    string.IsNullOrWhiteSpace(progress.Stage) ? "正在下载" : progress.Stage,
                    progress.Detail,
                    Math.Clamp(progress.Progress, 0d, 1d),
                    progress.CompletedFiles,
                    progress.TotalFiles,
                    progress.SpeedBytesPerSecond,
                    TaskManagerTaskState.Running,
                    Steps: steps));
                _window.UpdateTaskManagerViews();
                _window.RefreshTaskManagerButton();
            }

            if (Dispatcher.UIThread.CheckAccess())
                Apply();
            else
                Dispatcher.UIThread.Post(Apply);
        }

        public void Complete(string stage)
        {
            if (_disposed)
                return;
            void Apply()
            {
                _window.TrackTaskFinished(_taskId, _title, stage);
                _window.ShowHint(stage);
            }

            if (Dispatcher.UIThread.CheckAccess())
                Apply();
            else
                Dispatcher.UIThread.Post(Apply);
        }

        public void Fail(string message, bool canceled = false)
        {
            if (_disposed)
                return;
            void Apply()
            {
                _window.TrackTaskFailed(_taskId, _title, message, canceled);
                _window.ShowHint(
                    (canceled ? "任务已取消：" : "任务失败：") + TruncateHint(message),
                    critical: !canceled);
            }

            if (Dispatcher.UIThread.CheckAccess())
                Apply();
            else
                Dispatcher.UIThread.Post(Apply);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _window.UnregisterTrackedTask(_taskId, _cancellation);
            try
            {
                _cancellation.Dispose();
            }
            catch
            {
                // ignore
            }
        }
    }

    private void NavigationRegistryChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => NavigationRegistryChanged(sender, e));
            return;
        }

        NavigationRouteId selected = _currentNavRoute ?? LaunchRoute;
        _navigationPages = CreateNavigationPageMap(DesktopHost.Current.Navigation);
        BuildMainNavigationItems();
        RefreshNavigationText();
        if (FindNavigationPage(selected) is not null)
            SelectNavRoute(selected, animate: false);
    }

    private void NavigateToPluginRoute(string route)
    {
        if (!string.IsNullOrWhiteSpace(route))
            SelectNavRoute(NavigationRouteId.Parse(route), animate: true);
    }

    private void RefreshTitleButtonsBeforeFirstFrame()
    {
        foreach (string name in new[] { "BtnTitleClose", "BtnTitleMax", "BtnTitleMin", "BtnTitleHelp" })
            this.FindControl<MyIconButton>(name)?.RefreshAnim();
    }

    private MacOsTrafficLights? _macTrafficLights;

    private void ApplyMacOsChromeIfNeeded()
    {
        if (!MacOsWindowChrome.IsActivePlatform)
            return;

        _macTrafficLights = MacOsWindowChrome.Apply(this);
        if (_macTrafficLights is not null)
            MacOsWindowChrome.WireWindowEvents(this, _macTrafficLights);
    }

    private void FormMain_KeyDown(object? sender, KeyEventArgs e)
    {
        if (this.FindControl<Panel>("PanMsg") is { Children.Count: > 0 })
            return;

        // macOS window shortcuts: ⌘W close, ⌘M minimize, ⌃⌘F full screen.
        if (OperatingSystem.IsMacOS() && e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            if (e.Key == Key.W && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                Close();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.M)
            {
                WindowState = WindowState.Minimized;
                e.Handled = true;
                return;
            }

            if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                WindowState = WindowState == WindowState.FullScreen
                    ? WindowState.Normal
                    : WindowState.FullScreen;
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Escape && _isTitleSubPageVisible)
        {
            BtnTitleInner_Click(sender, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F11 &&
            this.FindControl<Border>("PanMainRight")?.Child is PageInstanceSelectRight instanceSelectPage)
        {
            instanceSelectPage.ShowHidden = !instanceSelectPage.ShowHidden;
            e.Handled = true;
        }
    }

    private void FormMain_MouseDown(object? sender, PointerPressedEventArgs e)
    {
        if (IsTextInputEventSource(e.Source))
            return;

        // Do not start a window drag when pressing traffic lights.
        if (e.Source is Visual visual &&
            visual.FindAncestorOfType<MacOsTrafficLights>() is not null)
        {
            return;
        }

        double titleHeight = OperatingSystem.IsMacOS() ? 52d : 48d;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed &&
            e.GetPosition(this).Y <= titleHeight)
        {
            if (e.ClickCount == 2 && CanResize && OperatingSystem.IsMacOS())
            {
                // Double-click title bar zooms (macOS).
                ToggleMaximized();
                e.Handled = true;
                return;
            }

            BeginMoveDrag(e);
        }
    }

    private static bool IsTextInputEventSource(object? source)
    {
        if (source is not Visual visual)
            return false;

        for (Visual? current = visual; current is not null; current = current.GetVisualParent())
        {
            if (current is TextBox)
                return true;
            if (current is ComboBox { IsEditable: true })
                return true;
        }

        return false;
    }

    private void FormMain_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        SyncMainSize();
    }

    private void FormMain_Closing(object? sender, WindowClosingEventArgs e)
    {
        DesktopFileLog.Info("Window", "主窗口正在关闭。");
        LauncherSettingsPageBinder.SettingsChanged -= LauncherSettingsChanged;
        AvaloniaThemeManager.ThemeChanged -= ThemeChanged;
        AvaloniaLocalizationManager.LanguageChanged -= LocalizationChanged;
        CancelAllTrackedTasks();
        _launchCancellation?.Cancel();
        _minecraftAiRepairAdvisor.StopLocalServer();
        this.FindControl<MediaElement>("VideoBack")?.Stop();
    }

    private void FormMain_Activated(object? sender, EventArgs e)
    {
        UpdateBackgroundVideoPlayback();
    }

    private void FrmMain_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private async void FrmMain_Drop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        try
        {
            IReadOnlyList<IStorageItem>? files = e.DataTransfer.TryGetFiles();
            string[] localPaths = files?
                .Select(static file => file.TryGetLocalPath())
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Select(static path => path!)
                .ToArray() ?? [];
            if (localPaths.Length == 0)
            {
                ShowTextDialog("无法读取拖入文件", "拖入内容没有可访问的本地文件路径。");
                return;
            }

            await InstallLocalArtifactsAsync(localPaths).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            _launchRight?.AppendLog("拖入文件安装已取消。");
        }
        catch (Exception ex)
        {
            DesktopFileLog.Error("FileArtifact", "处理拖入文件失败。", ex);
            ShowTextDialog("安装失败", "未能处理拖入文件。\n\n详细信息：" + ex.Message);
        }
    }

    private void FormMain_MouseMove(object? sender, PointerEventArgs e)
    {
    }

    private void VideoEnded(object? sender, EventArgs e)
    {
        if (sender is not MediaElement video || video.Source is null)
            return;

        video.Stop();
        UpdateBackgroundVideoPlayback();
    }

    private void VideoFailed(object? sender, MediaFailedEventArgs e)
    {
        if (sender is MediaElement video)
            video.IsVisible = false;
        Debug.WriteLine($"[UI] 背景视频播放失败：{e.Exception.Message}");
    }

    private void BtnTitleClose_Click(object? sender, EventArgs e) => Close();

    private void BtnTitleMin_Click(object? sender, EventArgs e) =>
        WindowState = WindowState.Minimized;

    private void BtnTitleMax_Click(object? sender, EventArgs e) => ToggleMaximized();

    private void BtnTitleHelp_Click(object? sender, EventArgs e)
    {
    }

    private void BtnTitleInner_Click(object? sender, EventArgs e)
    {
        if (_titleInnerBackAction is { } backAction)
        {
            _titleInnerBackAction = null;
            backAction();
            return;
        }

        SelectNavRoute(LaunchRoute, animate: true);
    }

    private void BtnNavItem_Click(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not MyListItem item || !TryGetNavRoute(item, out NavigationRouteId route))
            return;

        SelectNavPage(route, animate: _isMainWindowOpened);
        e.Handled = true;
    }

    private void BtnNavToggle_Click(object? sender, EventArgs e)
    {
        if (this.FindControl<Control>("PanNavLayer") is not { } navLayer)
            return;

        _isNavExpanded = !_isNavExpanded;
        if (_isNavExpanded)
            _navExpandedWidth = MeasureNavExpandedWidth(navLayer);

        _navAnimStart = GetCurrentNavWidth(navLayer);
        _navAnimTarget = _isNavExpanded ? _navExpandedWidth : NavCollapsedWidth;
        _navAnimElapsed = 0;
        _navAnimTimer?.Stop();
        _navAnimTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _navAnimTimer.Tick += NavAnimTimer_Tick;
        _navAnimTimer.Start();
    }

    private void PanMainLeft_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // WPF FormMain.PanMainLeft_Resize tracked RectLeftBackground width.
        // Frosted fill is applied on the PanMainLeft host; keep names for compatibility.
        if (this.FindControl<AnimatedBackgroundGrid>("RectLeftBackground") is { } rectBg)
            rectBg.Width = Math.Max(0d, e.NewSize.Width);
    }

    private void BtnExtraUpdateRestart_Click(object? sender, EventArgs e)
    {
    }

    private void BtnExtraBack_Click(object? sender, EventArgs e)
    {
        if (GetCurrentRightScroll() is { } scroll)
            scroll.PerformVerticalOffsetDelta(-scroll.Offset.Y);
    }

    private void BtnExtraDownload_Click(object? sender, EventArgs e)
    {
        ApplyTaskManagerPage(animate: true);
    }

    private void BtnExtraApril_Click(object? sender, EventArgs e)
    {
    }

    private void BtnExtraShutdown_Click(object? sender, EventArgs e)
    {
        // WPF: kill the watched Minecraft process and hide the shutdown extra button.
        Process? process = _runningGameProcess;
        if (process is null || process.HasExited)
        {
            SetGameRunningExtras(null);
            return;
        }

        ShowConfirmDialog(
            "关闭游戏",
            "确定要强制结束正在运行的 Minecraft 进程吗？未保存的进度可能会丢失。",
            confirmed =>
            {
                if (!confirmed)
                    return;
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    ShowHint("结束游戏失败：" + TruncateHint(ex.Message), critical: true);
                }
                finally
                {
                    SetGameRunningExtras(null);
                }
            },
            "结束游戏",
            "取消");
    }

    private void BtnExtraLog_Click(object? sender, EventArgs e)
    {
        // WPF BtnExtraLog: open the running instance's logs folder / latest.log.
        try
        {
            LaunchInstanceInfo? instance = _launchLeft?.SelectedInstance ?? _managedInstance;
            if (instance is null)
            {
                ShowHint("当前没有可查看的游戏日志");
                return;
            }

            string logsDir = Path.Combine(instance.InstanceDirectory, "logs");
            string latestLog = Path.Combine(logsDir, "latest.log");
            string openTarget = File.Exists(latestLog)
                ? latestLog
                : Directory.Exists(logsDir)
                    ? logsDir
                    : instance.InstanceDirectory;

            Process.Start(new ProcessStartInfo
            {
                FileName = openTarget,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowHint("打开游戏日志失败：" + TruncateHint(ex.Message), critical: true);
        }
    }

    private void BtnExtraMusic_Click(object? sender, EventArgs e)
    {
    }

    private void BtnExtraMusic_RightClick(object? sender, PointerReleasedEventArgs e)
    {
    }

    private void FormDragMove(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.ClickCount == 2 && CanResize)
        {
            ToggleMaximized();
            e.Handled = true;
            return;
        }

        BeginMoveDrag(e);
    }

    private void ToggleMaximized()
    {
        if (!CanResize)
            return;

        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void ResizeGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!CanResize || WindowState != WindowState.Normal ||
            sender is not Control { Tag: string edgeName } ||
            !Enum.TryParse(edgeName, ignoreCase: false, out WindowEdge edge) ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        BeginResizeDrag(edge, e);
        e.Handled = true;
    }

    private void UpdateWindowChrome()
    {
        bool maximized = WindowState is WindowState.Maximized or WindowState.FullScreen;
        if (this.FindControl<Border>("PanBack") is { } background)
        {
            // macOS full-screen / zoom: flush to edges; keep small radius in normal zoomed-like max.
            bool flushChrome = maximized || OperatingSystem.IsMacOS() && WindowState == WindowState.FullScreen;
            background.Margin = flushChrome ? new Thickness(0d) : new Thickness(10d);
            background.CornerRadius = flushChrome
                ? new CornerRadius(0d)
                : new CornerRadius(OperatingSystem.IsMacOS() ? 10d : 8d);
        }
        if (this.FindControl<Border>("PanWindowShadow") is { } shadow)
            shadow.IsVisible = !maximized;

        if (this.FindControl<MyIconButton>("BtnTitleMax") is { } maximizeButton)
            maximizeButton.SvgIcon = maximized ? "pcl/window-restore" : "lucide/square";

        // The title layers stretch with PanTitle in XAML, so maximizing and
        // restoring cannot leave them at the previous window width.
        if (!_isTitleSubPageVisible && this.FindControl<Control>("PanTitleMain") is { } titleMain)
        {
            titleMain.IsVisible = true;
            titleMain.Opacity = 1d;
        }
    }

    private void EnterTitleSubPage(string title)
    {
        Control? panTitleMain = this.FindControl<Control>("PanTitleMain");
        Control? panTitleInner = this.FindControl<Control>("PanTitleInner");
        TextBlock? labTitleInner = this.FindControl<TextBlock>("LabTitleInner");
        if (panTitleMain is null || panTitleInner is null || labTitleInner is null)
            return;

        if (_isTitleSubPageVisible)
        {
            if (labTitleInner.Text == title)
                return;

            if (_isMainWindowOpened)
            {
                ModAnimation.AniStart(
                    new List<ModAnimation.AniData>
                    {
                        ModAnimation.AaOpacity(labTitleInner, -labTitleInner.Opacity, 130),
                        ModAnimation.AaCode(() => labTitleInner.Text = title, after: true),
                        ModAnimation.AaOpacity(labTitleInner, 1d, 150, 30)
                    },
                    "FrmMain Titlebar SubLayer");
            }
            else
            {
                labTitleInner.Text = title;
                labTitleInner.Opacity = 1d;
            }
            return;
        }

        _isTitleSubPageVisible = true;
        panTitleInner.IsVisible = true;
        panTitleInner.IsHitTestVisible = true;
        panTitleMain.IsHitTestVisible = false;
        labTitleInner.Text = title;

        if (!_isMainWindowOpened)
        {
            panTitleMain.IsVisible = false;
            panTitleMain.Opacity = 0d;
            panTitleInner.Opacity = 1d;
            panTitleInner.Margin = new Thickness(-16d, 0d, 0d, 0d);
            return;
        }

        panTitleMain.IsVisible = true;
        panTitleInner.Opacity = 0d;
        panTitleInner.Margin = new Thickness(-16d, 0d, 0d, 0d);
        ModAnimation.AniStart(
            new List<ModAnimation.AniData>
            {
                ModAnimation.AaOpacity(panTitleMain, -panTitleMain.Opacity, 150),
                ModAnimation.AaX(panTitleMain, 12d - panTitleMain.Margin.Left, 150,
                    ease: new ModAnimation.AniEaseInFluent(ModAnimation.AniEasePower.Weak)),
                ModAnimation.AaOpacity(panTitleInner, 1d - panTitleInner.Opacity, 150, 200),
                ModAnimation.AaX(panTitleInner, -panTitleInner.Margin.Left, 350, 200,
                    new ModAnimation.AniEaseOutBack()),
                ModAnimation.AaCode(() => panTitleMain.IsVisible = false, after: true)
            },
            "FrmMain Titlebar FirstLayer");
    }

    private void ExitTitleSubPage()
    {
        if (!_isTitleSubPageVisible)
            return;

        Control? panTitleMain = this.FindControl<Control>("PanTitleMain");
        Control? panTitleInner = this.FindControl<Control>("PanTitleInner");
        if (panTitleMain is null || panTitleInner is null)
            return;

        _isTitleSubPageVisible = false;
        panTitleMain.IsVisible = true;
        panTitleMain.IsHitTestVisible = true;
        panTitleInner.IsHitTestVisible = false;

        if (!_isMainWindowOpened)
        {
            panTitleMain.Opacity = 1d;
            panTitleMain.Margin = new Thickness(0d);
            panTitleInner.Opacity = 0d;
            panTitleInner.Margin = new Thickness(-16d, 0d, 0d, 0d);
            panTitleInner.IsVisible = false;
            return;
        }

        ModAnimation.AniStart(
            new List<ModAnimation.AniData>
            {
                ModAnimation.AaOpacity(panTitleInner, -panTitleInner.Opacity, 150),
                ModAnimation.AaX(panTitleInner, -18d - panTitleInner.Margin.Left, 150,
                    ease: new ModAnimation.AniEaseInFluent()),
                ModAnimation.AaOpacity(panTitleMain, 1d - panTitleMain.Opacity, 150, 200),
                ModAnimation.AaX(panTitleMain, -panTitleMain.Margin.Left, 350, 200,
                    new ModAnimation.AniEaseOutBack(ModAnimation.AniEasePower.Weak)),
                ModAnimation.AaCode(() => panTitleInner.IsVisible = false, after: true)
            },
            "FrmMain Titlebar FirstLayer");
    }

    private void RefreshBackToTopBinding()
    {
        if (_backButtonScrollViewer is not null)
            _backButtonScrollViewer.ScrollChanged -= BackButtonScrollViewer_ScrollChanged;

        _backButtonScrollViewer = GetCurrentRightScroll();
        if (_backButtonScrollViewer is not null)
            _backButtonScrollViewer.ScrollChanged += BackButtonScrollViewer_ScrollChanged;

        UpdateBackToTopButton();
    }

    private void BackButtonScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e) =>
        UpdateBackToTopButton();

    private void UpdateBackToTopButton()
    {
        if (this.FindControl<MyExtraButton>("BtnExtraBack") is not { } back)
            return;

        MyScrollViewer? scroll = _backButtonScrollViewer ?? GetCurrentRightScroll();
        // WPF: show once scrolled roughly one viewport (not window height + 700px).
        double threshold = scroll is null
            ? double.MaxValue
            : Math.Max(240d, scroll.Viewport.Height * 0.55d);
        bool show = scroll is { IsVisible: true } && scroll.Offset.Y > threshold;
        _extraDockViewModel.SetBackToTopVisible(show);
        back.Show = _extraDockViewModel.ShowBackToTop;
        // Keep IsVisible in sync so the button is actually clickable (Show alone is not enough
        // before the extra-button host has applied scale animations on first frame).
        if (!back.IsVisible && show)
            back.IsVisible = true;
        RefreshExtraDockChrome();
    }

    private MyScrollViewer? GetCurrentRightScroll() =>
        this.FindControl<Border>("PanMainRight")?.Child is MyPageRight page ? page.PanScroll : null;

    public void ActivateExistingInstance()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
        WindowActivationApi.BringToForeground(this);
    }

    private void SetWindowIcon()
    {
        try
        {
            using Stream iconStream = Avalonia.Platform.AssetLoader.Open(
                new Uri("avares://PCL.Desktop/Assets/icon.ico", UriKind.Absolute));
            Icon = new WindowIcon(iconStream);
        }
        catch (IOException)
        {
        }
    }

    private void SyncMainSize(double? navWidth = null)
    {
        Control? panBack = this.FindControl<Control>("PanBack");
        Control? panForm = this.FindControl<Control>("PanForm");
        Control? panTitle = this.FindControl<Control>("PanTitle");
        Control? panMain = this.FindControl<Control>("PanMain");
        Control? navLayer = this.FindControl<Control>("PanNavLayer");
        Control? videoBack = this.FindControl<Control>("VideoBack");
        if (panBack is null)
            return;

        double formWidth = panBack.Bounds.Width;
        double formHeight = panBack.Bounds.Height;
        if (formWidth <= 0d)
            formWidth = Math.Max(0d, Width - 20d);
        if (formHeight <= 0d)
            formHeight = Math.Max(0d, Height - 20d);

        if (panForm is not null)
        {
            panForm.Width = formWidth;
            panForm.Height = formHeight;
        }

        if (panMain is not null)
        {
            double currentNavWidth = navWidth ?? GetCurrentNavWidth(navLayer);
            panMain.Width = Math.Max(0d, formWidth - currentNavWidth);
            panMain.Height = Math.Max(0d, formHeight - (panTitle?.Bounds.Height ?? 0d));
        }

        if (videoBack is not null)
        {
            videoBack.Width = formWidth;
            videoBack.Height = formHeight;
        }
    }

    private void SetNavWidth(Control navLayer, double width)
    {
        navLayer.Width = width;
        SyncMainSize(width);
    }

    private double MeasureNavExpandedWidth(Control navLayer)
    {
        double originalWidth = navLayer.Width;
        navLayer.Width = double.NaN;
        navLayer.InvalidateMeasure();
        navLayer.Measure(new Size(double.PositiveInfinity, Math.Max(0d, Bounds.Height)));

        double measuredWidth = navLayer.DesiredSize.Width;
        foreach (MyListItem item in GetNavItems())
        {
            item.Measure(new Size(double.PositiveInfinity, item.Bounds.Height > 0d ? item.Bounds.Height : 42d));
            measuredWidth = Math.Max(measuredWidth, item.DesiredSize.Width + 2d);
        }

        navLayer.Width = originalWidth;
        navLayer.InvalidateMeasure();

        if (double.IsNaN(measuredWidth) || double.IsInfinity(measuredWidth) || measuredWidth <= 0d)
            measuredWidth = _navExpandedWidth;
        return Math.Max(measuredWidth, NavCollapsedWidth + 1d) + 10d;
    }

    private static double GetCurrentNavWidth(Control? navLayer)
    {
        if (navLayer is null)
            return NavCollapsedWidth;
        if (!double.IsNaN(navLayer.Width) && navLayer.Width > 0d)
            return navLayer.Width;
        return navLayer.Bounds.Width > 0d ? navLayer.Bounds.Width : NavCollapsedWidth;
    }

    private void NavAnimTimer_Tick(object? sender, EventArgs e)
    {
        if (this.FindControl<Control>("PanNavLayer") is not { } navLayer)
        {
            _navAnimTimer?.Stop();
            _navAnimTimer = null;
            return;
        }

        _navAnimElapsed += 16;
        double progress = Math.Min(1d, (double)_navAnimElapsed / NavAnimDuration);
        double current = _navAnimStart + (_navAnimTarget - _navAnimStart) * EaseOutCubic(progress);
        SetNavWidth(navLayer, current);
        if (progress < 1d)
            return;

        _navAnimTimer?.Stop();
        _navAnimTimer = null;
        SetNavWidth(navLayer, _navAnimTarget);
    }

    private void SelectNavPage(NavigationRouteId route, bool animate)
    {
        _titleInnerBackAction = null;
        NavigationPageDescriptor? descriptor = FindNavigationPage(route);
        if (descriptor is null)
            descriptor = _navigationPages.Length > 0 ? _navigationPages[0] : null;
        if (descriptor is null)
            return;
        route = descriptor.Route;

        if (_pendingNavRoute is NavigationRouteId pendingRoute && pendingRoute.Equals(route.Value))
        {
            return;
        }

        if (_currentNavRoute is NavigationRouteId currentRoute && currentRoute.Equals(route.Value))
        {
            if (_isTitleSubPageVisible || _taskSessionStore.IsTaskManagerVisible)
                ApplyPagePlaceholder(route);
            return;
        }

        MyListItem? selected = null;
        foreach (MyListItem item in GetNavItems())
        {
            if (TryGetNavRoute(item, out NavigationRouteId itemRoute) && itemRoute.Equals(route.Value))
            {
                selected = item;
                break;
            }
        }

        if (selected is null)
            return;

        DesktopFileLog.Info("Navigation", $"打开 {descriptor.Title}（{route.Value}）。");

        selected.Checked = true;
        foreach (MyListItem item in GetNavItems())
        {
            if (!ReferenceEquals(item, selected))
                item.Checked = false;
        }

        if (!animate)
        {
            ApplyPagePlaceholder(route);
            return;
        }

        BeginPageChangeAnimation(route);
    }

    private void SelectNavRoute(NavigationRouteId route, bool animate) =>
        SelectNavPage(route, animate);

    private NavigationPageDescriptor? FindNavigationPage(NavigationRouteId route)
    {
        foreach (NavigationPageDescriptor page in _navigationPages)
        {
            if (page.Route.Equals(route.Value))
                return page;
        }

        return null;
    }

    private void ApplyPagePlaceholder(NavigationRouteId route)
    {
        NavigationPageDescriptor? descriptor = FindNavigationPage(route);
        if (descriptor is null)
            return;

        _currentNavRoute = descriptor.Route;
        _pendingNavRoute = null;
        int requestId = ++_registeredPageRequestId;
        PageCreateContext context = new(descriptor.Route.Value, DesktopHost.Current.Services, _desktopPageContext);
        ValueTask<DesktopMainPage> createTask;
        try
        {
            createTask = _pageAdapter.CreateMainPageAsync(descriptor.Provider, context, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ApplyPageCreationError(descriptor.Title, ex);
            return;
        }

        if (createTask.IsCompletedSuccessfully)
        {
            ApplyRegisteredMainPage(createTask.Result);
            return;
        }

        ApplyRegisteredMainPage(CreateLoadingMainPage(descriptor.Title));
        _ = CompleteRegisteredPageAsync(createTask.AsTask(), requestId, descriptor.Title);
    }

    private async Task CompleteRegisteredPageAsync(
        Task<DesktopMainPage> createTask,
        int requestId,
        string pageTitle)
    {
        try
        {
            DesktopMainPage page = await createTask.ConfigureAwait(true);
            if (requestId != _registeredPageRequestId)
                return;

            ApplyRegisteredMainPage(page);
        }
        catch (Exception ex)
        {
            if (requestId == _registeredPageRequestId)
                ApplyPageCreationError(pageTitle, ex);
        }
    }

    private void ApplyPageCreationError(string pageTitle, Exception exception)
    {
        ApplyRegisteredMainPage(new DesktopMainPage(
            null,
            CreateTextPlaceholder(pageTitle, "页面暂时无法打开。\n\n详细信息：" + exception.Message)));
    }

    private void ApplyRegisteredMainPage(DesktopMainPage page)
    {
        _titleInnerBackAction = null;
        _taskSessionStore.IsTaskManagerVisible = false;
        RefreshTaskManagerButton();
        if (this.FindControl<Border>("PanMainLeft") is not { } leftHost ||
            this.FindControl<Border>("PanMainRight") is not { } rightHost)
        {
            return;
        }

        if (!ReferenceEquals(leftHost.Child, page.Left))
        {
            if (leftHost.Child is MyPageLeft oldLeft)
                oldLeft.TriggerHideAnimation();
            leftHost.Child = page.Left;
        }

        if (!ReferenceEquals(rightHost.Child, page.Right))
        {
            if (rightHost.Child is MyPageRight oldRight)
                oldRight.PageOnExit();
            rightHost.Child = page.Right;
        }

        RegisterCurrentPluginPageSurface(page.Right);

        if (page.Title is { Length: > 0 } title)
            EnterTitleSubPage(title);
        else
            ExitTitleSubPage();

        RefreshBackToTopBinding();
        page.Activated?.Invoke();
        rightHost.Opacity = 1d;
    }

    private void RegisterCurrentPluginPageSurface(Control page)
    {
        string? surfaceId = _currentNavRoute?.Value switch
        {
            DesktopNavigationRegistry.LaunchRouteValue => "pcl.page.launch",
            DesktopNavigationRegistry.DownloadRouteValue => "pcl.page.download",
            DesktopNavigationRegistry.CommunityRouteValue => "pcl.page.community",
            DesktopNavigationRegistry.SettingsRouteValue => "pcl.page.settings",
            { Length: > 0 } route => route.StartsWith("pcl.", StringComparison.OrdinalIgnoreCase)
                ? "pcl.page." + route[4..]
                : route,
            _ => null
        };
        if (_registeredPluginPageSurfaceId is { } previous &&
            !string.Equals(previous, surfaceId, StringComparison.OrdinalIgnoreCase))
        {
            DesktopHostUiComposition.Instance.UnregisterTarget(previous);
        }
        if (surfaceId is null)
            return;
        _registeredPluginPageSurfaceId = surfaceId;
        DesktopHostUiComposition.Instance.RegisterTarget(surfaceId, page);
    }

    private DesktopMainPage CreateLaunchMainPage()
    {
        LauncherSettings launchSettings = LauncherSettingsPageBinder.LoadSettings();
        // Prefer feature profile resolver (Phase 3); keep settings fallback for parity.
        bool experimental = _launchHomeProfile.UseExperimentalFullPageHome() ||
                            IsExperimentalHomepageUiEnabled(launchSettings);
        ApplyExperimentalChrome(experimental);

        if (experimental)
        {
            EnsureExperimentalLaunchHome(launchSettings);
            _useExperimentalLaunchHome = true;
            return new DesktopMainPage(
                null,
                _launchHomeExperimental!,
                Activated: () =>
                {
                    // Restore chrome after version-select remount (may leave stale loading text).
                    _launchLeft!.RefreshButtonsUI();
                    _ = _launchLeft.EnsureInstancesLoadedAsync();
                    _launchLeft.TriggerEnterAnimation();
                    _launchHomeExperimental?.RefreshShortcutDock();
                });
        }

        // Leaving experimental mode: keep classic left/right pair.
        if (_useExperimentalLaunchHome)
        {
            _launchHomeExperimental = null;
            _launchLeft = null;
            _launchRight = null;
            _useExperimentalLaunchHome = false;
        }

        _launchLeft ??= CreateLaunchLeftPage();
        if (_launchRight is null)
        {
            _launchRight = new PageLaunchRight();
            _launchRight.CommunityHintHideRequested += (_, _) => PromptHideCommunityHint();
        }

        _launchRight.SetMaximumLogLines(ResolveMaximumLogLines(launchSettings));
        ApplyLaunchPageSettings(launchSettings);
        ApplyHomepageSettings(launchSettings);
        ILaunchHomeSurface launchHome = _launchLeft;
        PageLaunchRight launchRight = _launchRight;
        return new DesktopMainPage(
            (Control)launchHome,
            launchRight,
            Activated: () =>
            {
                _ = launchHome.EnsureInstancesLoadedAsync();
                launchHome.TriggerEnterAnimation();
                launchRight.PageOnEnter();
            });
    }

    private void EnsureExperimentalLaunchHome(LauncherSettings launchSettings)
    {
        // Must load folders before first discovery, otherwise root is null and the
        // homepage reports “未找到版本” until the user switches folders once.
        EnsureMinecraftFoldersLoaded();

        if (_launchHomeExperimental is not null && _launchLeft is PageLaunchHomeExperimental)
        {
            _launchHomeExperimental.SetMaximumLogLines(ResolveMaximumLogLines(launchSettings));
            // Re-apply root if folders finished loading after first construction.
            _launchHomeExperimental.SetMinecraftRootDirectory(_folderStore.SelectedRoot);
            _launchHomeExperimental.SetPreferredInstanceDirectory(LoadPreferredInstanceDirectory());
            ApplyLaunchPageSettings(launchSettings);
            return;
        }

        PageLaunchHomeExperimental page = new();
        WireLaunchHomeSurface(page);
        page.CommunityHintHideRequested += (_, _) => PromptHideCommunityHint();
        page.SetMaximumLogLines(ResolveMaximumLogLines(launchSettings));
        page.SetPreferredInstanceDirectory(LoadPreferredInstanceDirectory());
        page.SetMinecraftRootDirectory(_folderStore.SelectedRoot);
        page.ConfigureLaunchingHint(launchSettings.GetBooleanOption(
            "UiShowLaunchingHint",
            LauncherSettingDefaults.GetBoolean("UiShowLaunchingHint")));
        _launchHomeExperimental = page;
        _launchLeft = page;
        _launchRight = null;
        ApplyLaunchPageSettings(launchSettings);
    }

    private void WireLaunchHomeSurface(ILaunchHomeSurface page)
    {
        page.DownloadRequested += (_, _) => SelectNavRoute(DownloadRoute, animate: true);
        page.InstanceSelectRequested += (_, _) => ApplyInstanceSelectPage();
        page.InstanceSettingsRequested += (_, _) =>
        {
            if (page.SelectedInstance is not null)
                ApplyInstanceManagePage(page.SelectedInstance);
        };
        page.CancelLaunchRequested += (_, _) =>
        {
            _launchCancellation?.Cancel();
            HandleStatusMessage("已取消启动。");
        };
        page.StatusMessage += (_, message) => HandleStatusMessage(message);
        page.LoginPageRequested += (_, type) => ApplyLaunchLoginPage(page, type);
        page.LaunchRequested += (_, instance) => _ = StartMinecraftAsync(page, instance);
        if (page is PageLaunchHomeExperimental experimental)
            experimental.ShortcutActivated += (_, pin) => _ = ActivateLaunchShortcutAsync(pin);
    }

    private async Task ActivateLaunchShortcutAsync(LaunchShortcutPin pin)
    {
        LaunchInstanceInfo? instance = (_launchLeft?.Instances ?? [])
            .FirstOrDefault(candidate =>
                string.Equals(
                    candidate.InstanceDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    pin.InstanceDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase));
        if (instance is null)
        {
            ShowHint("固定目标对应的版本不存在或未加载", critical: true);
            return;
        }

        ILaunchHomeSurface? launchHome = _launchLeft;
        if (launchHome is null)
            return;

        if (pin.Kind == LaunchShortcutKind.Server)
            await StartMinecraftAsync(launchHome, instance, serverAddress: pin.Target).ConfigureAwait(true);
        else
            await StartMinecraftAsync(launchHome, instance, worldName: pin.Target).ConfigureAwait(true);
    }

    private void PromptHideCommunityHint()
    {
        ShowInputDialog(
            AvaloniaLocalizationManager.GetText(
                "Launch.Right.CommunityHint.InputTitle",
                "输入 PCL N 开发者名称"),
            AvaloniaLocalizationManager.GetText(
                "Launch.Right.CommunityHint.HidePrompt",
                "若要永久隐藏此提示，请输入正确的 PCL N 开发者名称。"),
            string.Empty,
            "开发者名称",
            answer =>
            {
                if (answer is null)
                    return;

                // WPF: must match developer name (MUXUE1230, case-insensitive).
                if (string.Equals(answer.Trim(), "MUXUE1230", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(answer.Trim(), "muxue1230-owo", StringComparison.OrdinalIgnoreCase))
                {
                    PageLaunchRight.SetCommunityHintPermanentlyHidden(true);
                    if (_launchRight?.FindControl<MyCard>("PanHint") is { } card)
                        card.IsVisible = false;
                    // Experimental homepage: unregister the flip card (not only hide).
                    _launchHomeExperimental?.UnregisterCommunityHintCard();
                    ShowHint("已永久隐藏 N 版提示");
                }
                else
                {
                    ShowTextDialog(
                        AvaloniaLocalizationManager.GetText(
                            "Launch.Right.CommunityHint.Title",
                            "社区提示"),
                        AvaloniaLocalizationManager.GetText(
                            "Launch.Right.CommunityHint.WrongInput",
                            "不太对哦……"));
                }
            });
    }

    private void OnMainWindowOpened(object? sender, EventArgs e)
    {
        _isMainWindowOpened = true;
        DesktopFileLog.Info(
            "Window",
            $"主窗口已显示；客户区={ClientSize.Width:0}x{ClientSize.Height:0}；缩放={RenderScaling:0.##}。");

        // Headless/automation: skip show animation + first-run dialogs so Window.Show() can finish.
        if (ShouldSuppressStartupDialogs())
        {
            Opacity = _targetWindowOpacity;
            if (_showAnimationRoot is not null)
                _showAnimationRoot.RenderTransform = null;
            return;
        }

        _ = LauncherUpdateCoordinator.Current.StartAutomaticUpdateOnceAsync();
        StartShowAnimation();
        // First-run chain: community welcome → special build notice (no EULA gate).
        Dispatcher.UIThread.Post(MaybeShowFirstRunDialogs, DispatcherPriority.Background);
    }

    private void MaybeShowFirstRunDialogs()
    {
        // Headless unit tests / automation never click dialogs; the chain would hang the UI dispatcher.
        if (ShouldSuppressStartupDialogs())
            return;

        try
        {
            LauncherSettings settings = LauncherSettingsPageBinder.LoadSettings();
            MaybeShowCommunityWelcome(
                settings,
                () => MaybeShowSpecialVersionNotice(() => _ = MaybeShowLauncherAnnouncementsAsync()));
        }
        catch (Exception ex)
        {
            DesktopFileLog.Warn("FirstRun", "首次运行引导加载失败，将继续显示特殊版本提示。", ex);
            MaybeShowSpecialVersionNotice(() => _ = MaybeShowLauncherAnnouncementsAsync());
        }
    }

    /// <summary>
    /// Skip community / special-build modal chains under automated hosts.
    /// <c>PCL_DISABLE_FIRST_RUN</c> or <c>PCL_DISABLE_DEBUG_HINT</c> (any non-empty value).
    /// </summary>
    private static bool ShouldSuppressStartupDialogs() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PCL_DISABLE_FIRST_RUN")) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PCL_DISABLE_DEBUG_HINT"));

    private void MaybeShowCommunityWelcome(LauncherSettings settings, Action completed)
    {
        string currentVersion = PclBuildInfo.DisplayVersion;
        string seen = settings.GetTextOption("UiCommunityNoticeVersion", string.Empty);
        if (string.Equals(seen, currentVersion, StringComparison.Ordinal))
        {
            completed();
            return;
        }

        string title = AvaloniaLocalizationManager.GetText("Update.CommunityNotice.Title", "PCL N Edition");
        string body = AvaloniaLocalizationManager.GetText(
            "Update.CommunityNotice.Body",
            "欢迎使用 Plain Craft Launcher N Edition！\n\n" +
            "PCL N 是由 MUXUE1230 维护的个人分支，与官方 / 社区版无隶属关系。\n" +
            "请将问题反馈到本项目的 GitHub Issues。\n\n" +
            "此提示在每次版本更新后显示一次。");
        string confirm = AvaloniaLocalizationManager.GetText("Update.CommunityNotice.Confirm", "开始使用");

        ShowMarkdownDialog(
            title,
            body,
            _ =>
            {
                settings.SetTextOption("UiCommunityNoticeVersion", currentVersion);
                LauncherSettingsPageBinder.SaveSettings(settings);
                completed();
            },
            confirm);
    }

    private void MaybeShowSpecialVersionNotice(Action completed)
    {
        // WPF FormMain special build notice (Debug / CI).
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PCL_DISABLE_DEBUG_HINT")))
        {
            completed();
            return;
        }

        bool isDebug =
#if DEBUG
            true;
#else
            false;
#endif
        bool isCi =
            string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase) ||
            PclBuildInfo.InformationalVersion.Contains("ci", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(GetAssemblyConfiguration(), "CI", StringComparison.OrdinalIgnoreCase);

        if (!isDebug && !isCi)
        {
            completed();
            return;
        }

        string title = AvaloniaLocalizationManager.GetText("Main.SpecialVersion.Title", "特殊版本提示");
        string body = isDebug
            ? AvaloniaLocalizationManager.GetText(
                "Main.SpecialVersion.DebugHint",
                "当前 PCL N Edition 为 Debug 构建。\n该构建仅用于开发调试。")
            : AvaloniaLocalizationManager.GetText(
                "Main.SpecialVersion.CiHint",
                "当前 PCL N Edition 为自动化 CI 构建。\n稳定性较低，不适合日常使用。");
        string hideNotice = AvaloniaLocalizationManager.GetText(
            "Main.SpecialVersion.HideHintNotice",
            "可设置环境变量 PCL_DISABLE_DEBUG_HINT 为任意值以隐藏此提示。");

        ShowConfirmDialog(
            title,
            body + "\n\n" + hideNotice,
            confirmed =>
            {
                if (confirmed)
                {
                    completed();
                    return;
                }

                // Secondary: open download page and exit.
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "https://github.com/MuXue1230-owo/PCL-N/releases/latest",
                        UseShellExecute = true
                    });
                }
                catch
                {
                    // ignore
                }

                Close();
            },
            AvaloniaLocalizationManager.GetText("Main.SpecialVersion.IUnderstand", "我知道我在做什么"),
            AvaloniaLocalizationManager.GetText("Main.SpecialVersion.OpenDownloadPageAndExit", "打开最新下载页并退出"),
            isWarn: true);
    }

    private async Task MaybeShowLauncherAnnouncementsAsync()
    {
        try
        {
            LauncherSettings settings = LauncherSettingsPageBinder.LoadSettings();
            int activityMode = Math.Clamp(settings.GetIntegerOption(
                "SystemSystemActivity",
                LauncherSettingDefaults.GetInteger("SystemSystemActivity")), 0, 2);
            if (activityMode == 2)
                return;
            HashSet<string> seen = settings.GetTextOption("SystemAnnouncementSeen", string.Empty)
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.Ordinal);
            LauncherUpdatePolicy policy = LauncherUpdatePolicy.Resolve(settings, GetAssemblyConfiguration());
            string channel = policy.Channel switch
            {
                UpdateChannel.Beta => "beta",
                UpdateChannel.CI => "ci",
                _ => "release"
            };
            string platform = OperatingSystem.IsWindows() ? "windows" :
                OperatingSystem.IsMacOS() ? "macos" : "linux";
            IReadOnlyList<LauncherAnnouncement> announcements = await new LauncherAnnouncementService()
                .FetchEligibleAsync(
                    PclBuildInfo.DisplayVersion,
                    channel,
                    platform,
                    AvaloniaLocalizationManager.CurrentLanguageCode,
                    activityMode,
                    seen)
                .ConfigureAwait(true);
            ShowLauncherAnnouncement(announcements, 0, settings, seen);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            DesktopFileLog.Warn("Announcement", "启动器公告获取失败；不会阻塞启动。", exception);
        }
    }

    private void ShowLauncherAnnouncement(
        IReadOnlyList<LauncherAnnouncement> announcements,
        int index,
        LauncherSettings settings,
        HashSet<string> seen)
    {
        if (index >= announcements.Count)
            return;
        LauncherAnnouncement announcement = announcements[index];
        ShowMarkdownDialog(
            announcement.Title,
            announcement.Markdown,
            result =>
            {
                if (announcement.Dismissible)
                {
                    seen.Add(announcement.SeenKey);
                    settings.SetTextOption("SystemAnnouncementSeen", string.Join('\n', seen.TakeLast(200)));
                    LauncherSettingsPageBinder.SaveSettings(settings);
                }
                if (result == 2 && announcement.ActionUri is not null)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = announcement.ActionUri.AbsoluteUri,
                            UseShellExecute = true
                        });
                    }
                    catch (Exception exception)
                    {
                        DesktopFileLog.Warn("Announcement", "无法打开公告链接。", exception);
                    }
                }
                ShowLauncherAnnouncement(announcements, index + 1, settings, seen);
            },
            announcement.PrimaryLabel,
            announcement.ActionLabel ?? string.Empty,
            thirdButton: string.Empty,
            isWarn: announcement.Severity is "important" or "security");
    }

    private static string GetAssemblyConfiguration()
    {
        try
        {
            return Assembly.GetEntryAssembly()
                       ?.GetCustomAttribute<AssemblyConfigurationAttribute>()
                       ?.Configuration
                   ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private PageLaunchLeft CreateLaunchLeftPage()
    {
        EnsureMinecraftFoldersLoaded();
        PageLaunchLeft page = new();
        page.SetPreferredInstanceDirectory(LoadPreferredInstanceDirectory());
        page.SetMinecraftRootDirectory(_folderStore.SelectedRoot);
        WireLaunchHomeSurface(page);
        return page;
    }

    private void HandleStatusMessage(string message)
    {
        // WPF: most status strings only go to the launch log; bottom Hint is reserved for short toasts.
        if (_launchHomeExperimental is not null)
            _launchHomeExperimental.AppendLog(message);
        else
            _launchRight?.AppendLog(message);
    }

    /// <summary>
    /// WPF FormMain.Hint — single-line bottom toast (PanHint). No multi-line wrap.
    /// </summary>
    private void ShowHint(string message, bool critical = false)
    {
        if (this.FindControl<StackPanel>("PanHint") is not { } host)
            return;

        string text = TruncateHint(message);
        if (text.Length == 0)
            return;

        Border? duplicate = host.Children.OfType<Border>()
            .FirstOrDefault(border => string.Equals(border.Tag as string, text, StringComparison.Ordinal));
        if (duplicate is not null)
        {
            host.Children.Remove(duplicate);
            host.Children.Add(duplicate);
            return;
        }

        // WPF keeps a very short stack (typically ≤3).
        while (host.Children.Count >= 3)
            host.Children.RemoveAt(0);

        bool alignRight = host.HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Right;
        double enterOffset = 28d;
        Thickness startMargin = alignRight
            ? new Thickness(0d, 0d, -enterOffset, 6d)
            : new Thickness(-enterOffset, 0d, 0d, 6d);
        Thickness restMargin = new(0d, 0d, 0d, 6d);

        Border bar = new()
        {
            Tag = text,
            Height = 34d,
            MaxHeight = 34d,
            MaxWidth = 420d,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Margin = startMargin,
            Padding = new Thickness(14d, 0d, 14d, 0d),
            CornerRadius = new CornerRadius(3d),
            Opacity = 0d,
            ClipToBounds = true,
            Background = new SolidColorBrush(
                critical ? Color.Parse("#E0CE2111") : Color.Parse("#E0259BFC")),
            Child = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 13d,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                MaxLines = 1
            }
        };
        host.Children.Add(bar);

        string aniKey = "FrmMain Hint " + text.GetHashCode(StringComparison.Ordinal);
        List<ModAnimation.AniData> enter =
        [
            ModAnimation.AaOpacity(bar, 1d - bar.Opacity, 150, ease: new ModAnimation.AniEaseOutFluent())
        ];
        if (!alignRight)
            enter.Add(ModAnimation.AaX(bar, enterOffset, 200, ease: new ModAnimation.AniEaseOutFluent()));
        else
            enter.Add(ModAnimation.AaCode(() => bar.Margin = restMargin, 0));
        ModAnimation.AniStart(enter, aniKey + " In");

        DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(3.8d) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            List<ModAnimation.AniData> exit =
            [
                ModAnimation.AaOpacity(bar, -bar.Opacity, 160, ease: new ModAnimation.AniEaseInFluent())
            ];
            if (!alignRight)
                exit.Add(ModAnimation.AaX(bar, -enterOffset, 160, ease: new ModAnimation.AniEaseInFluent()));
            exit.Add(ModAnimation.AaCode(() => host.Children.Remove(bar), after: true));
            ModAnimation.AniStart(exit, aniKey + " Out");
        };
        timer.Start();
    }

    private static string TruncateHint(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return string.Empty;

        string text = message
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        // WPF Hint is single-line; keep captions short so they don't overflow the bar.
        const int maxChars = 48;
        return text.Length <= maxChars ? text : text[..(maxChars - 1)] + "…";
    }

    private DesktopMainPage CreateDownloadMainPage()
    {
        _downloadLeft ??= CreateDownloadLeftPage();
        MyPageRight rightPage = _downloadLeft.GetOrCreateCurrentPage();
        return new DesktopMainPage(
            _downloadLeft,
            rightPage,
            Activated: () =>
            {
                _downloadLeft.TriggerShowAnimation();
                if (rightPage is PageDownloadInstall installPage)
                {
                    if (!installPage.HasPendingFocusedNavigation)
                        installPage.ClearInstallTargetOverride();
                    installPage.PageOnEnter();
                }
                else
                {
                    rightPage.PageOnEnter();
                }
            });
    }

    private DesktopMainPage CreateCommunityMainPage()
    {
        _communityRight ??= CreateCommunityRightPage();
        _communityLeft ??= CreateCommunityLeftPage(_communityRight);
        return new DesktopMainPage(
            _communityLeft,
            _communityRight,
            Activated: () =>
            {
                _communityLeft.TriggerShowAnimation();
                _communityRight.PageOnEnter();
            });
    }

    private PageCommunityLeft CreateCommunityLeftPage(PageCommunityRight rightPage)
    {
        PageCommunityLeft page = new();
        page.CategoryChanged += (_, category) =>
        {
            ApplyCommunityRightPage(rightPage);
            _ = rightPage.SetCategoryAsync(category);
        };
        page.RefreshRequested += (_, category) =>
        {
            if (rightPage.Category == category)
                _ = rightPage.RefreshAsync();
            else
                _ = rightPage.SetCategoryAsync(category);
        };
        page.FavoritesRequested += (_, _) =>
        {
            _communityFavoritesRight ??= CreateCommunityFavoritesRightPage();
            _communityFavoritesRight.Refresh();
            ApplyCommunityRightPage(_communityFavoritesRight);
        };
        return page;
    }

    private PageCommunityRight CreateCommunityRightPage()
    {
        PageCommunityRight page = new(new CompositeCommunityResourceCatalog(), ownsCatalog: true, _communityFavorites);
        page.OpenProjectRequested += (_, entry) => _ = OpenCommunityDetailAsync(entry, page.Category, page.CurrentSearchOptions);
        page.DownloadRequested += (_, request) => _ = DownloadCommunityResourceAsync(request);
        return page;
    }

    private PageCommunityDetail CreateCommunityDetailPage()
    {
        PageCommunityDetail page = new(new CompositeCommunityResourceCatalog(), ownsCatalog: true, _communityFavorites);
        page.BackRequested += (_, _) => CloseCommunityDetail();
        page.OpenWebRequested += (_, entry) => OpenExternalUrl(entry.WebsiteUrl);
        page.OpenUrlRequested += (_, url) => OpenExternalUrl(url);
        page.MessageRequested += (_, message) => ShowTextDialog(message.Title, message.Message, "知道了");
        page.DownloadRequested += (_, request) => _ = DownloadCommunityResourceAsync(request);
        return page;
    }

    private PageCommunityFavoritesRight CreateCommunityFavoritesRightPage()
    {
        PageCommunityFavoritesRight page = new(_communityFavorites);
        page.OpenProjectRequested += (_, favorite) =>
            _ = OpenCommunityDetailAsync(
                favorite.Entry,
                favorite.Category,
                new CommunitySearchOptions(Source: favorite.Entry.Source));
        page.DownloadRequested += (_, request) => _ = DownloadCommunityResourceAsync(request);
        return page;
    }

    private void ApplyCommunityRightPage(MyPageRight target)
    {
        if (this.FindControl<Border>("PanMainRight") is not { } rightHost)
            return;

        MyPageRight? oldRight = rightHost.Child as MyPageRight;
        if (ReferenceEquals(oldRight, target))
            return;

        oldRight?.PageOnExit();
        rightHost.Child = target;
        RefreshBackToTopBinding();
        target.PageOnEnter();
    }

    /// <summary>
    /// Open resource detail as a fully independent page (WPF FormMain PageDownloadCompDetail):
    /// replaces both left + right content, not only the right pane.
    /// </summary>
    private async Task OpenCommunityDetailAsync(
        CommunityResourceEntry entry,
        CommunityResourceCategory category,
        CommunitySearchOptions options)
    {
        _communityDetail ??= CreateCommunityDetailPage();
        _communityRight ??= CreateCommunityRightPage();
        _communityLeft ??= CreateCommunityLeftPage(_communityRight);

        if (this.FindControl<Border>("PanMainLeft") is not { } leftHost ||
            this.FindControl<Border>("PanMainRight") is not { } rightHost)
        {
            return;
        }

        // Full-frame detail: clear left rail, host detail in right (full content width).
        if (leftHost.Child is MyPageLeft oldLeft)
            oldLeft.TriggerHideAnimation();
        leftHost.Child = null;

        MyPageRight? oldRight = rightHost.Child as MyPageRight;
        if (!ReferenceEquals(oldRight, _communityDetail))
        {
            oldRight?.PageOnExit();
            rightHost.Child = _communityDetail;
            RefreshBackToTopBinding();
            _communityDetail.PageOnEnter();
        }

        EnterTitleSubPage(entry.Title);
        _titleInnerBackAction = CloseCommunityDetail;
        await _communityDetail.ShowAsync(entry, category, options).ConfigureAwait(true);
    }

    private void CloseCommunityDetail()
    {
        _titleInnerBackAction = null;
        _communityRight ??= CreateCommunityRightPage();
        _communityLeft ??= CreateCommunityLeftPage(_communityRight);

        if (this.FindControl<Border>("PanMainLeft") is { } leftHost)
        {
            if (!ReferenceEquals(leftHost.Child, _communityLeft))
            {
                leftHost.Child = _communityLeft;
                _communityLeft.TriggerShowAnimation();
            }
        }

        if (this.FindControl<Border>("PanMainRight") is { } rightHost)
        {
            MyPageRight? oldRight = rightHost.Child as MyPageRight;
            MyPageRight target = _communityLeft.IsFavoritesSelected
                ? _communityFavoritesRight ??= CreateCommunityFavoritesRightPage()
                : _communityRight;
            if (!ReferenceEquals(oldRight, target))
            {
                oldRight?.PageOnExit();
                rightHost.Child = target;
                RefreshBackToTopBinding();
                target.PageOnEnter();
            }
        }

        ExitTitleSubPage();
    }

    private async Task DownloadCommunityResourceAsync(CommunityResourceDownloadRequest request)
    {
        LaunchInstanceInfo? instance = _launchLeft?.SelectedInstance ?? _managedInstance;
        string taskId = CreateTaskId("community", request.Entry.ProjectId);
        using CancellationTokenSource cancellation = RegisterTrackedTask(taskId);
        string taskTitle = "下载 " + request.Entry.Title;
        DesktopFileLog.Info(
            "CommunityDownload",
            $"开始下载社区资源 {request.Entry.Title}；分类={request.Category}；来源={request.Entry.Source}；实例={instance?.Name ?? "(桌面下载)"}。");
        DesktopFileLog.Debug(
            "CommunityDownload",
            $"下载请求：ProjectId={request.Entry.ProjectId}；PreferredVersion={request.PreferredVersion?.VersionId ?? "(自动)"}；PreferredFile={request.PreferredFile?.FileName ?? "(自动)"}。");

        // WPF: stay on community list (or return from detail) — do not jump to task manager.
        if (this.FindControl<Border>("PanMainRight")?.Child is PageCommunityDetail)
            CloseCommunityDetail();

        TrackTaskBegin(taskId, taskTitle, "解析下载地址");
        ShowHint("已开始下载：" + request.Entry.Title);

        try
        {
            CommunitySearchOptions downloadOptions = instance is null
                ? request.Options
                : CommunityInstanceCompatibility.Apply(request.Options, request.Category, instance);
            using CompositeCommunityResourceCatalog catalog = new();
            CommunityResourceVersion? selectedVersion = request.PreferredVersion;
            CommunityResourceDownloadFile? file = request.PreferredFile;
            if (selectedVersion is null)
            {
                IReadOnlyList<CommunityResourceVersion> versions = await catalog.GetVersionsAsync(
                        request.Entry,
                        downloadOptions,
                        cancellation.Token)
                    .ConfigureAwait(true);
                selectedVersion = file is null
                    ? versions.OrderByDescending(static version => version.PublishedAt ?? DateTimeOffset.MinValue)
                        .FirstOrDefault()
                    : versions.FirstOrDefault(version =>
                        string.Equals(version.VersionId, file.VersionId, StringComparison.OrdinalIgnoreCase));
            }

            file ??= selectedVersion is { Files.Count: > 0 } ? selectedVersion.Files[0] : null;

            if (file is null)
            {
                DesktopFileLog.Warn("CommunityDownload", $"未找到符合筛选条件的文件：{request.Entry.Title}");
                TrackTaskFailed(taskId, taskTitle, "未找到匹配当前筛选条件的版本文件。", canceled: false);
                ShowHint("下载失败：未找到可下载的文件", critical: true);
                return;
            }

            selectedVersion ??= new CommunityResourceVersion(
                file.VersionId,
                file.VersionName,
                file.VersionName,
                null,
                null,
                [],
                [],
                [file]);

            LauncherSettings downloadSettings = LauncherSettingsPageBinder.LoadSettings();
            bool autoInstallDependencies = downloadSettings.GetBooleanOption(
                "ToolDownloadAutoInstallDependencies",
                LauncherSettingDefaults.GetBoolean("ToolDownloadAutoInstallDependencies", true));

            string baseDirectory;
            string? saveAsPath = null;
            if (request.SaveAs)
            {
                IStorageProvider? storage = StorageProvider;
                if (storage is null)
                {
                    TrackTaskFailed(taskId, taskTitle, "当前窗口无法打开保存对话框。", canceled: false);
                    ShowHint("另存为失败：无法打开保存对话框", critical: true);
                    return;
                }

                IStorageFile? target = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "另存为 — " + request.Entry.Title,
                    SuggestedFileName = file.FileName,
                    FileTypeChoices =
                    [
                        new FilePickerFileType("资源文件")
                        {
                            Patterns = ["*" + (Path.GetExtension(file.FileName) is { Length: > 0 } ext ? ext : ".*")]
                        }
                    ]
                }).ConfigureAwait(true);
                if (target is null)
                {
                    TrackTaskFailed(taskId, taskTitle, "已取消另存为。", canceled: true);
                    ShowHint("已取消另存为");
                    return;
                }

                saveAsPath = target.Path.LocalPath;
                baseDirectory = Path.GetDirectoryName(saveAsPath) ??
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                        "PCL-N Downloads");
            }
            else
            {
                baseDirectory = instance is null
                    ? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                        "PCL-N Downloads")
                    : await InstanceGameDirectory.ResolveAsync(instance, cancellation.Token).ConfigureAwait(true);
            }

            IReadOnlyList<CommunityResourceDownloadPlanItem> plan;
            // 另存为只保存选中文件本身；自动依赖受设置「自动安装必需依赖」控制（#17）。
            if (request.Category == CommunityResourceCategory.Mod &&
                !request.SaveAs &&
                autoInstallDependencies)
            {
                TrackTaskBegin(taskId, taskTitle, "正在解析必需前置");
                plan = await CommunityResourceDependencyResolver.ResolveRequiredDownloadsAsync(
                        catalog,
                        request.Entry,
                        selectedVersion,
                        file,
                        downloadOptions,
                        cancellation.Token)
                    .ConfigureAwait(true);
            }
            else
            {
                plan = [new CommunityResourceDownloadPlanItem(request.Entry, selectedVersion, file, false)];
            }

            using HttpClient client = new() { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-N/1.0");
            string completedPath = string.Empty;
            int dependencyCount = plan.Count(static item => item.IsDependency);
            DesktopFileLog.Info(
                "CommunityDownload",
                $"下载计划已生成；资源={request.Entry.Title}；项目数={plan.Count}；必需前置={dependencyCount}；目标={baseDirectory}。");
            if (dependencyCount > 0)
            {
                _launchRight?.AppendLog(
                    $"社区资源：{request.Entry.Title} 需要 {dependencyCount} 个必需前置，将自动下载。");
            }

            foreach (CommunityResourceDownloadPlanItem item in plan)
            {
                CommunityResourceCategory itemCategory = item.IsDependency
                    ? CommunityResourceCategory.Mod
                    : request.Category;
                string path = await DownloadCommunityPlanItemAsync(
                        client,
                        item,
                        itemCategory,
                        baseDirectory,
                        taskId,
                        taskTitle,
                        cancellation.Token,
                        explicitTargetPath: item.IsDependency ? null : saveAsPath)
                    .ConfigureAwait(true);
                if (item.IsDependency)
                    _launchRight?.AppendLog($"已安装前置：{item.Entry.Title} → {path}");
                else
                    completedPath = path;
            }

            TrackTaskFinished(taskId, taskTitle, "已保存到 " + completedPath);
            DesktopFileLog.Info("CommunityDownload", $"社区资源下载完成：{request.Entry.Title} -> {completedPath}");
            _launchRight?.AppendLog($"社区资源已下载：{request.Entry.Title} → {completedPath}");
            ShowHint(request.SaveAs
                ? "已另存为：" + Path.GetFileName(completedPath)
                : request.Category == CommunityResourceCategory.World
                    ? "世界安装完成：" + Path.GetFileName(completedPath)
                    : "下载完成：" + Path.GetFileName(completedPath));
        }
        catch (OperationCanceledException)
        {
            DesktopFileLog.Warn("CommunityDownload", $"社区资源下载已取消：{request.Entry.Title}");
            TrackTaskFailed(taskId, taskTitle, "下载已取消。", canceled: true);
            ShowHint("下载已取消");
        }
        catch (Exception ex)
        {
            DesktopFileLog.Error("CommunityDownload", $"社区资源下载失败：{request.Entry.Title}", ex);
            TrackTaskFailed(taskId, taskTitle, ex.Message, canceled: false);
            ShowHint("下载失败：" + TruncateHint(ex.Message), critical: true);
        }
        finally
        {
            UnregisterTrackedTask(taskId, cancellation);
        }
    }

    private async Task<string> DownloadCommunityPlanItemAsync(
        HttpClient client,
        CommunityResourceDownloadPlanItem item,
        CommunityResourceCategory category,
        string baseDirectory,
        string taskId,
        string taskTitle,
        CancellationToken cancellationToken,
        string? explicitTargetPath = null)
    {
        string targetDirectory = ResolveCommunityDownloadDirectory(category, baseDirectory);
        string targetPath;
        if (!string.IsNullOrWhiteSpace(explicitTargetPath))
        {
            targetPath = Path.GetFullPath(explicitTargetPath);
            string? parent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(parent))
                Directory.CreateDirectory(parent);
        }
        else
        {
            Directory.CreateDirectory(targetDirectory);
            targetPath = Path.Combine(targetDirectory, SanitizeFileName(item.File.FileName));
        }

        string temporaryPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".PCLDownloading";
        string phase = item.IsDependency
            ? "正在下载前置 " + item.Entry.Title
            : "正在下载 " + item.File.FileName;
        TrackTaskBegin(taskId, taskTitle, phase);

        try
        {
            Exception? lastDownloadError = null;
            foreach (string candidateUrl in item.File.CandidateUrls.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                    using HttpResponseMessage response = await client.GetAsync(
                            candidateUrl,
                            HttpCompletionOption.ResponseHeadersRead,
                            cancellationToken)
                        .ConfigureAwait(true);
                    response.EnsureSuccessStatusCode();
                    long? total = response.Content.Headers.ContentLength;
                    await using Stream network = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(true);
                    await using FileStream output = new(
                        temporaryPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        64 * 1024,
                        useAsync: true);
                    byte[] buffer = new byte[64 * 1024];
                    long written = 0;
                    int read;
                    while ((read = await network.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                               .ConfigureAwait(true)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(true);
                        written += read;
                        double progress = total is > 0 ? written / (double)total.Value : 0d;
                        DesktopFileLog.RealTime(
                            "CommunityDownload",
                            $"下载进度：{item.File.FileName}；字节={written}/{total?.ToString(CultureInfo.InvariantCulture) ?? "?"}；进度={progress:P1}。");
                        TrackTaskProgress(
                            taskId,
                            taskTitle,
                            Math.Clamp(progress, 0d, 1d),
                            $"{written.ToString(CultureInfo.InvariantCulture)} / {(total?.ToString(CultureInfo.InvariantCulture) ?? "?")} 字节");
                    }
                    lastDownloadError = null;
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    lastDownloadError = ex;
                    DesktopFileLog.Warn("CommunityDownload", $"下载候选失败，将尝试下一来源：{new Uri(candidateUrl).Host}。", ex);
                }
            }
            if (lastDownloadError is not null || !File.Exists(temporaryPath))
                throw lastDownloadError ?? new HttpRequestException("所有下载候选均失败。");

            if (category == CommunityResourceCategory.Mod)
            {
                targetPath = MinecraftModArchiveInstaller.Install(
                    temporaryPath,
                    targetDirectory,
                    Path.GetFileName(targetPath));
            }
            else
            {
                File.Move(temporaryPath, targetPath, overwrite: true);
            }

            if (category != CommunityResourceCategory.World)
                return targetPath;

            TrackTaskBegin(taskId, taskTitle, "正在安装世界");
            string installed = await MinecraftWorldArchiveInstaller
                .InstallAsync(targetPath, targetDirectory, cancellationToken)
                .ConfigureAwait(true);
            File.Delete(targetPath);
            return installed;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A failed cleanup must not mask the original download result.
                    DesktopFileLog.Warn("CommunityDownload", $"清理临时下载文件失败：{temporaryPath}", ex);
                }
            }
        }
    }

    private static string ResolveCommunityDownloadDirectory(
        CommunityResourceCategory category,
        string baseDirectory)
    {
        return category switch
        {
            CommunityResourceCategory.Mod => Path.Combine(baseDirectory, "mods"),
            CommunityResourceCategory.ResourcePack => Path.Combine(baseDirectory, "resourcepacks"),
            CommunityResourceCategory.Shader => Path.Combine(baseDirectory, "shaderpacks"),
            CommunityResourceCategory.DataPack => Path.Combine(baseDirectory, "datapacks"),
            CommunityResourceCategory.Modpack => Path.Combine(baseDirectory, "modpacks"),
            CommunityResourceCategory.World => Path.Combine(baseDirectory, "saves"),
            _ => baseDirectory
        };
    }

    private PageDownloadLeft CreateDownloadLeftPage()
    {
        PageDownloadLeft page = new(CreateDownloadInstallPage);
        page.PageChanged += (_, args) => ApplyDownloadRightPage(args.Page);
        return page;
    }

    private PageDownloadInstall CreateDownloadInstallPage()
    {
        if (_downloadInstallPage is not null)
            return _downloadInstallPage;

        PageDownloadInstall page = new(_minecraftInstallService);
        page.InstallRequested += (_, request) => _ = StartInstallAsync(request);
        _downloadInstallPage = page;
        return _downloadInstallPage;
    }

    private PageSpeedRight CreateTaskManagerRightPage()
    {
        if (_speedRight is not null)
            return _speedRight;

        PageSpeedRight page = new();
        page.CancelRequested += (_, args) => CancelTrackedTask(args.TaskId);
        page.DismissRequested += (_, args) => RemoveTask(args.TaskId);
        _speedRight = page;
        return _speedRight;
    }

    private void ApplyDownloadRightPage(MyPageRight target)
    {
        if (this.FindControl<Border>("PanMainRight") is not { } rightHost)
            return;

        MyPageRight? oldRight = rightHost.Child as MyPageRight;
        if (ReferenceEquals(oldRight, target))
            return;

        oldRight?.PageOnExit();
        rightHost.Child = target;
        RefreshBackToTopBinding();
        target.PageOnEnter();
    }

    private static bool IsExperimentalHomepageUiEnabled(LauncherSettings? settings = null)
    {
        try
        {
            LauncherSettings resolved = settings ?? LauncherSettingsPageBinder.LoadSettings();
            return resolved.GetBooleanOption(
                LauncherSettingKeys.ExperimentalHomepageUi,
                LauncherSettingDefaults.GetBoolean(LauncherSettingKeys.ExperimentalHomepageUi.Value));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return LauncherSettingDefaults.GetBoolean(LauncherSettingKeys.ExperimentalHomepageUi.Value);
        }
    }

    /// <summary>
    /// Apple-style title bar + frosted FAB dock. Driven by <see cref="AppShellViewModel"/> /
    /// <see cref="TitleBarViewModel"/> / <see cref="ExtraDockViewModel"/> (Phase 1 shell).
    /// </summary>
    private void ApplyExperimentalChrome(bool experimental)
    {
        _experimentalChromeApplied = experimental;
        _shellViewModel.UseExperimentalChrome = experimental;
        _extraDockViewModel.UseGlassChrome = experimental;
        _titleBarViewModel.ApplyChrome(experimental);

        if (this.FindControl<Control>("PanTitle") is { } title)
            title.Height = _titleBarViewModel.TitleHeight;
        if (this.FindControl<Control>("PanTitleGlassOverlay") is { } glassOverlay)
            glassOverlay.IsVisible = experimental;

        if (this.FindControl<TextBlock>("LabTitleInner") is { } titleInner)
        {
            titleInner.FontSize = _titleBarViewModel.TitleFontSize;
            titleInner.FontWeight = experimental ? FontWeight.SemiBold : FontWeight.Normal;
            titleInner.LetterSpacing = _titleBarViewModel.TitleLetterSpacing;
            titleInner.Margin = experimental
                ? new Thickness(50d, 1d, 60d, 0d)
                : new Thickness(47d, 1d, 60d, 0d);
        }

        if (this.FindControl<MyIconButton>("BtnTitleInner") is { } backBtn)
        {
            double size = _titleBarViewModel.BackButtonSize;
            backBtn.Width = size;
            backBtn.Height = size;
            backBtn.Margin = experimental ? new Thickness(14d, 0d, 0d, 0d) : new Thickness(12d, 0d, 0d, 0d);
        }

        if (this.FindControl<StackPanel>("PanExtraButtons") is { } stack)
            stack.Spacing = experimental ? 2d : 0d;

        foreach (string name in ExtraButtonNames)
        {
            if (this.FindControl<MyExtraButton>(name) is { } extra)
                extra.UseGlassChrome = experimental;
        }

        RefreshExtraDockChrome();
    }

    private static readonly string[] ExtraButtonNames =
    [
        "BtnExtraUpdateRestart", "BtnExtraBack", "BtnExtraDownload", "BtnExtraApril",
        "BtnExtraShutdown", "BtnExtraLog", "BtnExtraMusic"
    ];

    private bool HasVisibleExtraButtonOnControls()
    {
        foreach (string name in ExtraButtonNames)
        {
            if (this.FindControl<MyExtraButton>(name) is { } extra &&
                (extra.Show || (extra.IsVisible && extra.Height > 0.5d)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Frosted dock chrome only when experimental UI is on <em>and</em> ExtraDock reports a visible FAB.
    /// </summary>
    private void RefreshExtraDockChrome()
    {
        if (this.FindControl<Border>("PanExtraDock") is not { } dock)
            return;

        bool experimental = _extraDockViewModel.UseGlassChrome || _experimentalChromeApplied;
        // Prefer VM state; also honor any FAB still toggled only on controls (migration hybrid).
        bool showChrome = experimental &&
                          (_extraDockViewModel.HasAnyVisibleButton || HasVisibleExtraButtonOnControls());

        if (showChrome)
        {
            dock.Padding = new Thickness(6d, 8d);
            dock.CornerRadius = new CornerRadius(26d);
            dock.Background = new SolidColorBrush(Color.Parse("#CCF5F5F7"));
            dock.BorderBrush = new SolidColorBrush(Color.Parse("#33FFFFFF"));
            dock.BorderThickness = new Thickness(1d);
            dock.BoxShadow = new BoxShadows(new BoxShadow
            {
                Blur = 22,
                OffsetY = 8,
                Color = Color.Parse("#2A000000")
            });
            dock.Margin = new Thickness(20d);
            dock.IsHitTestVisible = true;
        }
        else
        {
            dock.Padding = new Thickness(0d);
            dock.CornerRadius = new CornerRadius(0d);
            dock.Background = Brushes.Transparent;
            dock.BorderBrush = Brushes.Transparent;
            dock.BorderThickness = new Thickness(0d);
            dock.BoxShadow = default;
            dock.Margin = experimental ? new Thickness(20d) : new Thickness(15d);
            dock.IsHitTestVisible = true;
        }
    }

    private void ApplyInstanceSelectPage()
    {
        if (this.FindControl<Border>("PanMainLeft") is not { } leftHost ||
            this.FindControl<Border>("PanMainRight") is not { } rightHost)
        {
            return;
        }

        EnsureMinecraftFoldersLoaded();
        bool experimental = _instancesSelect.IsFullPageLayout;
        ApplyExperimentalChrome(experimental);

        // Prefer the live launch root so the folder list highlights the folder in use.
        if (_launchLeft?.MinecraftRootDirectory is { Length: > 0 } liveRoot)
        {
            string? normalizedLive = NormalizeDirectoryPath(liveRoot);
            if (normalizedLive is not null && _folderStore.ContainsRoot(normalizedLive))
                _folderStore.SetSelectedRootWithoutPersist(normalizedLive);
        }

        _instancesSelect.WireOnce(CreateInstancesSelectBindings());
        _instancesSelect.Apply(
            leftHost,
            rightHost,
            _launchLeft?.Instances ?? [],
            _launchLeft?.SelectedInstance);

        EnterTitleSubPage("选择版本");
        RefreshBackToTopBinding();
    }

    private InstancesSelectBindings CreateInstancesSelectBindings() =>
        new()
        {
            SelectFolderAsync = SelectMinecraftFolderAsync,
            OpenPath = OpenFolder,
            PromptRenameFolder = folder => ShowInputDialog(
                "重命名游戏文件夹",
                "请输入新的显示名称。",
                folder.Name,
                "游戏文件夹名称",
                result =>
                {
                    RenameMinecraftFolder(folder, result);
                    _instancesSelect.RefreshFolderLists();
                }),
            RemoveFolder = RemoveMinecraftFolder,
            CreateDefaultFolderAsync = CreateDefaultMinecraftFolderAsync,
            AddFolderAsync = AddMinecraftFolderAsync,
            ImportModpackAsync = PickModpackForImportAsync,
            RefreshInstancesAsync = async () =>
            {
                if (_launchLeft is null)
                    return [];
                await _launchLeft.RefreshInstancesAsync().ConfigureAwait(true);
                return _launchLeft.Instances;
            },
            GetSelectedInstance = () => _launchLeft?.SelectedInstance,
            NavigateDownload = () => SelectNavRoute(DownloadRoute, animate: true),
            NavigateLaunch = () => SelectNavRoute(LaunchRoute, animate: true),
            OpenInstanceFolder = instance => OpenFolder(instance.InstanceDirectory),
            DeleteInstance = PromptDeleteInstance,
            SelectInstance = instance =>
            {
                if (_launchLeft is not null)
                {
                    IReadOnlyList<LaunchInstanceInfo> snapshot = _launchLeft.Instances.Count > 0
                        ? _launchLeft.Instances
                        : [instance];
                    _launchLeft.SetInstances(snapshot, instance);
                }

                _instanceSelectionStore.PersistPreferred(
                    _launchLeft?.PreferredInstanceDirectory ?? instance.InstanceDirectory);
                _launchRight?.AppendLog($"已选择游戏版本 {instance.Name}。");
                SelectNavRoute(LaunchRoute, animate: true);
            },
            ManageInstance = instance => ApplyInstanceManagePage(instance)
        };

    private void RefreshInstanceSelectFolderLists() =>
        _instancesSelect.RefreshFolderLists();

    private string? LoadPreferredInstanceDirectory() =>
        _instanceSelectionStore.LoadPreferred();

    private void PersistPreferredInstanceDirectory(string? instanceDirectory)
    {
        try
        {
            _instanceSelectionStore.PersistPreferred(instanceDirectory);
        }
        catch (Exception ex)
        {
            _launchRight?.AppendLog("未能保存所选游戏版本：" + ex.Message);
        }
    }

    private void EnsureMinecraftFoldersLoaded() =>
        _folderStore.EnsureLoaded(LoadPreferredInstanceDirectory());

    private async Task SelectMinecraftFolderAsync(MinecraftFolderInfo folder, bool forceRefresh = false)
    {
        string? normalized = NormalizeDirectoryPath(folder.RootDirectory);
        if (normalized is null || _launchLeft is null)
            return;

        bool changed = _folderStore.TrySetSelectedRoot(normalized);
        if (!changed)
            _folderStore.SetSelectedRootWithoutPersist(normalized);

        RefreshInstanceSelectFolderLists();
        _launchLeft.SetMinecraftRootDirectory(normalized);
        if (changed || forceRefresh)
            await _launchLeft.RefreshInstancesAsync().ConfigureAwait(true);

        _instancesSelect.SetInstances(_launchLeft.Instances, _launchLeft.SelectedInstance);
    }

    private async Task AddMinecraftFolderAsync()
    {
        string? selected = await PickOpenFolderPathAsync("选择 Minecraft 文件夹").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(selected))
            return;

        string root = NormalizeSelectedMinecraftRoot(selected);
        MinecraftFolderInfo folder = AddOrGetMinecraftFolder(root, LaunchInstanceDiscovery.GetMinecraftRootDisplayName(root));
        await SelectMinecraftFolderAsync(folder, forceRefresh: true).ConfigureAwait(true);
    }

    private async Task CreateDefaultMinecraftFolderAsync()
    {
        string root = LaunchInstanceDiscovery.GetCurrentMinecraftRoot();
        Directory.CreateDirectory(Path.Combine(root, "versions"));
        MinecraftFolderInfo folder = AddOrGetMinecraftFolder(root, "当前文件夹");
        await SelectMinecraftFolderAsync(folder, forceRefresh: true).ConfigureAwait(true);
    }

    private async Task PickModpackForImportAsync()
    {
        string? sourcePath = await PickOpenFilePathAsync(
                "选择整合包",
                new FilePickerFileType("整合包压缩文件") { Patterns = ["*.zip", "*.mrpack"] })
            .ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(sourcePath))
            await InstallLocalArtifactsAsync([sourcePath]).ConfigureAwait(true);
    }

    private async Task InstallLocalArtifactsAsync(IReadOnlyList<string> localPaths)
    {
        ArgumentNullException.ThrowIfNull(localPaths);
        foreach (string path in localPaths)
        {
            _launchRight?.AppendLog("正在识别本地文件：" + path);
            HostFileArtifactResult result;
            try
            {
                result = await DesktopFileArtifactHost.Instance.InstallAsync(
                        path,
                        new HostFileArtifactContext(GetDefaultMinecraftRoot()))
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                _launchRight?.AppendLog("已取消安装：" + Path.GetFileName(path));
                continue;
            }
            catch (Exception ex)
            {
                DesktopFileLog.Error("FileArtifact", $"本地文件安装失败：{path}", ex);
                ShowTextDialog("安装失败", $"{Path.GetFileName(path)}\n\n{ex.Message}");
                continue;
            }

            _launchRight?.AppendLog(result.Message.Replace('\n', ' '));
            if (result.RefreshInstances && result.Installed && _launchLeft is not null)
            {
                await _launchLeft.RefreshInstancesAsync().ConfigureAwait(true);
                _instancesSelect.SetInstances(_launchLeft.Instances, _launchLeft.SelectedInstance);
            }

            ShowTextDialog(
                result.Installed ? "安装完成" : "未安装",
                result.Message);
        }
    }

    private MinecraftFolderInfo AddOrGetMinecraftFolder(string rootDirectory, string name)
    {
        MinecraftFolderInfo added = _folderStore.AddOrGet(rootDirectory, name, isCustom: true);
        RefreshInstanceSelectFolderLists();
        return added;
    }

    private void RenameMinecraftFolder(MinecraftFolderInfo folder, string? name)
    {
        if (_folderStore.TryRename(folder, name))
            RefreshInstanceSelectFolderLists();
    }

    private void RemoveMinecraftFolder(MinecraftFolderInfo folder)
    {
        MinecraftFolderInfo? nextSelected = _folderStore.Remove(folder);
        if (nextSelected is not null)
            _ = SelectMinecraftFolderAsync(nextSelected, forceRefresh: true);
        else
            RefreshInstanceSelectFolderLists();
    }

    private void PersistMinecraftFolders() => _folderStore.Persist();

    private static string NormalizeSelectedMinecraftRoot(string selectedDirectory) =>
        SessionPath.NormalizeSelectedMinecraftRoot(selectedDirectory);

    private static string? TryGetMinecraftRootFromInstanceDirectory(string? instanceDirectory) =>
        SessionPath.TryGetMinecraftRootFromInstanceDirectory(instanceDirectory);

    private static string? NormalizeDirectoryPath(string? path) =>
        SessionPath.NormalizeDirectory(path);

    private void ApplyInstanceManagePage(LaunchInstanceInfo instance, InstancePageSubType subPage = InstancePageSubType.Overall)
    {
        _titleInnerBackAction = null;
        if (this.FindControl<Border>("PanMainLeft") is not { } leftHost ||
            this.FindControl<Border>("PanMainRight") is not { } rightHost)
        {
            return;
        }

        _managedInstance = instance;
        _instanceLeft ??= CreateInstanceLeftPage();
        _instanceLeft.SetInstance(instance);
        subPage = _instanceLeft.NormalizePage(subPage);
        if (!ReferenceEquals(leftHost.Child, _instanceLeft))
        {
            if (leftHost.Child is MyPageLeft oldLeft)
                oldLeft.TriggerHideAnimation();
            leftHost.Child = _instanceLeft;
            _instanceLeft.TriggerShowAnimation();
        }
        _instanceLeft.SelectPage(subPage);
        EnterTitleSubPage($"版本设置 - {instance.Name}");

        MyPageRight rightPage = GetInstanceRightPage(instance, subPage);
        MyPageRight? oldRight = rightHost.Child as MyPageRight;
        if (ReferenceEquals(oldRight, rightPage))
            return;

        oldRight?.PageOnExit();
        rightHost.Child = rightPage;
        RefreshBackToTopBinding();
        rightPage.PageOnEnter();
    }

    private PageInstanceLeft CreateInstanceLeftPage()
    {
        PageInstanceLeft page = new();
        page.PageChanged += (_, subPage) =>
        {
            if (_managedInstance is not null)
                ApplyInstanceManagePage(_managedInstance, subPage);
        };
        page.RefreshRequested += (_, subPage) =>
        {
            if (subPage == InstancePageSubType.Overall)
                _ = RefreshInstancesAfterManagementAsync(_managedInstance?.InstanceDirectory);
            else if (subPage == InstancePageSubType.Servers)
                _instanceServerPage?.Reload();
            else if (subPage == InstancePageSubType.Export)
                _instanceExportPage?.RefreshAll();
            else if (subPage == InstancePageSubType.Install)
                _instanceInstallPage?.RefreshAll();
            else if (subPage == InstancePageSubType.Saves)
                _instanceSavesPage?.Reload();
            else if (subPage == InstancePageSubType.Screenshots)
                _ = _instanceScreenshotPage?.Reload();
            else if (subPage is InstancePageSubType.Mods or InstancePageSubType.ResourcePacks or InstancePageSubType.Shaders or InstancePageSubType.Schematics)
                _instanceResourcePage?.Reload();
            else
                _instanceToolsPage?.Reload();
        };
        page.ResetRequested += (_, _) =>
        {
            if (_managedInstance is not null)
                PromptResetInstanceSettings(_managedInstance);
        };
        return page;
    }

    private MyPageRight GetInstanceRightPage(LaunchInstanceInfo instance, InstancePageSubType subPage)
    {
        if (subPage == InstancePageSubType.Overall)
        {
            _instanceManagePage ??= CreateInstanceManagePage();
            _instanceManagePage.SetInstance(instance);
            return _instanceManagePage;
        }

        if (subPage == InstancePageSubType.Servers)
        {
            _instanceServerPage ??= CreateInstanceServerPage();
            _instanceServerPage.SetInstance(instance);
            return _instanceServerPage;
        }

        if (subPage == InstancePageSubType.Setup)
        {
            _instanceSetupPage ??= CreateInstanceSetupPage();
            _instanceSetupPage.SetInstance(instance);
            return _instanceSetupPage;
        }

        if (subPage == InstancePageSubType.Export)
        {
            _instanceExportPage ??= CreateInstanceExportPage();
            _instanceExportPage.SetInstance(instance);
            return _instanceExportPage;
        }

        if (subPage == InstancePageSubType.Install)
        {
            _instanceInstallPage ??= CreateInstanceInstallPage();
            _instanceInstallPage.SetInstance(instance);
            return _instanceInstallPage;
        }

        if (subPage == InstancePageSubType.Screenshots)
        {
            _instanceScreenshotPage ??= CreateInstanceScreenshotPage();
            _instanceScreenshotPage.SetInstance(instance);
            return _instanceScreenshotPage;
        }

        if (subPage == InstancePageSubType.Saves)
        {
            _instanceSavesPage ??= CreateInstanceSavesPage();
            _instanceSavesPage.SetInstance(instance);
            return _instanceSavesPage;
        }

        if (subPage == InstancePageSubType.ModsDisabled)
        {
            _instanceModDisabledPage ??= CreateInstanceModDisabledPage();
            return _instanceModDisabledPage;
        }

        if (subPage is InstancePageSubType.Mods or InstancePageSubType.ResourcePacks or InstancePageSubType.Shaders or InstancePageSubType.Schematics)
        {
            _instanceResourcePage ??= CreateInstanceResourcePage();
            _instanceResourcePage.SetContext(instance, subPage);
            return _instanceResourcePage;
        }

        _instanceToolsPage ??= CreateInstanceToolsPage();
        _instanceToolsPage.SetContext(instance, subPage);
        return _instanceToolsPage;
    }

    private PageInstanceManageRight CreateInstanceManagePage()
    {
        PageInstanceManageRight page = new();
        page.OpenFolderRequested += (_, instance) => OpenFolder(instance.InstanceDirectory);
        page.OpenPathRequested += (_, path) => OpenFolder(path);
        page.RenameRequested += (_, instance) => PromptRenameInstance(instance);
        page.DeleteRequested += (_, instance) => PromptDeleteInstance(instance);
        page.EditDescriptionRequested += (_, instance) => PromptEditInstanceDescription(instance);
        page.ToggleStarRequested += (_, instance) => _ = ToggleInstanceStarAsync(instance);
        page.ExportLaunchScriptRequested += (_, instance) => _ = ExportLaunchScriptAsync(instance);
        page.TestLaunchRequested += (_, instance) => _ = TestLaunchFromInstancePageAsync(instance);
        page.RepairFilesRequested += (_, instance) => _ = RepairInstanceFilesAsync(instance);
        page.ResetSettingsRequested += (_, instance) => PromptResetInstanceSettings(instance);
        page.PatchCoreRequested += (_, instance) => _ = PatchInstanceCoreAsync(instance);
        return page;
    }

    private PageInstanceSetupRight CreateInstanceSetupPage()
    {
        PageInstanceSetupRight page = new();
        page.OpenGlobalSettingsRequested += (_, _) => SelectNavRoute(SettingsRoute, animate: true);
        page.MessageRequested += (_, args) => ShowTextDialog(args.Title, args.Message, args.PrimaryButton);
        page.ConfirmRequested += (_, args) => ShowConfirmDialog(
            args.Title,
            args.Message,
            args.Complete,
            args.PrimaryButton,
            args.SecondaryButton,
            args.IsWarn);
        page.CreateAuthProfileRequested += (_, authServer) =>
        {
            SelectNavRoute(LaunchRoute, animate: true);
            _launchLeft ??= CreateLaunchLeftPage();
            ApplyLaunchLoginPage(_launchLeft, PageLaunchLeft.LaunchLoginPageType.Auth);
            _loginAuthPage?.SetServer(authServer);
        };
        return page;
    }

    private PageInstanceExportRight CreateInstanceExportPage()
    {
        PageInstanceExportRight page = new();
        page.ExportRequested += (_, request) => _ = ExportInstanceZipAsync(request);
        page.ImportConfigRequested += (_, _) => _ = ImportInstanceRulesConfigAsync(page);
        page.ExportConfigRequested += (_, rules) => _ = ExportInstanceRulesConfigAsync(rules);
        return page;
    }

    private PageInstanceInstallRight CreateInstanceInstallPage()
    {
        PageInstanceInstallRight page = new();
        page.ModifyRequested += (_, request) => _ = OpenDownloadInstallForInstanceAsync(request);
        return page;
    }

    private async Task OpenDownloadInstallForInstanceAsync(InstanceInstallModifyRequest request)
    {
        LaunchInstanceInfo instance = request.Instance;
        string versionId = string.IsNullOrWhiteSpace(request.MinecraftVersionId)
            ? ReadMinecraftVersionId(instance)
            : request.MinecraftVersionId;
        string minecraftRoot = GetMinecraftRootFromInstance(instance);
        PageDownloadInstall installPage = ActivateDownloadInstallPage(animate: true);
        if (request.AddonKind is { } addonKind &&
            request.CurrentLoaderKind is { } currentLoaderKind &&
            !string.IsNullOrWhiteSpace(request.CurrentLoaderVersion))
        {
            await installPage.FocusExistingInstallAddonAsync(
                    versionId,
                    instance.Name,
                    minecraftRoot,
                    currentLoaderKind,
                    request.CurrentLoaderVersion,
                    addonKind,
                    request.CurrentOptiFineVersion)
                .ConfigureAwait(true);
            return;
        }

        await installPage.FocusVersionAsync(
                versionId,
                instance.Name,
                preserveInstallNameOnLoaderSelect: true,
                minecraftRootDirectory: minecraftRoot,
                openLoaderKind: request.LoaderKind,
                replaceExistingVersion: true)
            .ConfigureAwait(true);
    }

    private PageDownloadInstall ActivateDownloadInstallPage(bool animate)
    {
        _downloadLeft ??= CreateDownloadLeftPage();
        PageDownloadInstall installPage = CreateDownloadInstallPage();
        _downloadLeft.PageChange(DownloadPageSubType.Install);
        SelectNavRoute(DownloadRoute, animate);
        return installPage;
    }

    private PageSpeedRight ActivateTaskManagerPage(bool animate)
    {
        PageSpeedRight rightPage = CreateTaskManagerRightPage();
        ApplyTaskManagerPage(animate);
        return rightPage;
    }

    private void ApplyTaskManagerPage(bool animate)
    {
        if (this.FindControl<Border>("PanMainLeft") is not { } leftHost ||
            this.FindControl<Border>("PanMainRight") is not { } rightHost)
        {
            return;
        }

        if (!_taskSessionStore.IsTaskManagerVisible)
        {
            _taskManagerBackRoute = GetCurrentNavigationRoute();
            _taskManagerBackAction = CaptureTaskManagerBackAction();
        }

        _registeredPageRequestId++;
        _taskSessionStore.IsTaskManagerVisible = true;
        _titleInnerBackAction = ReturnFromTaskManager;

        _speedLeft ??= new PageSpeedLeft();
        PageSpeedRight rightPage = CreateTaskManagerRightPage();
        UpdateTaskManagerViews();

        if (!ReferenceEquals(leftHost.Child, _speedLeft))
        {
            if (leftHost.Child is MyPageLeft oldLeft)
                oldLeft.TriggerHideAnimation();
            leftHost.Child = _speedLeft;
        }

        MyPageRight? oldRight = rightHost.Child as MyPageRight;
        if (!ReferenceEquals(oldRight, rightPage))
        {
            oldRight?.PageOnExit();
            if (animate && _isMainWindowOpened)
            {
                ModAnimation.AniStart(
                    new List<ModAnimation.AniData>
                    {
                        ModAnimation.AaOpacity(rightHost, -rightHost.Opacity, MotionTokens.NavCrossfadeOutMs),
                        ModAnimation.AaCode(() =>
                        {
                            rightHost.Child = rightPage;
                            rightHost.Opacity = 0d;
                            RefreshBackToTopBinding();
                        }, after: true),
                        ModAnimation.AaOpacity(rightHost, 1d, MotionTokens.NavCrossfadeInMs),
                        ModAnimation.AaCode(rightPage.PageOnEnter, after: true)
                    },
                    "FrmMain PageChangeRight");
            }
            else
            {
                rightHost.Child = rightPage;
                rightHost.Opacity = 1d;
                RefreshBackToTopBinding();
                rightPage.PageOnEnter();
            }
        }
        else
        {
            rightHost.Opacity = 1d;
            RefreshBackToTopBinding();
            rightPage.PageOnEnter();
        }

        EnterTitleSubPage(GetResourceText("Main.Title.TaskManager", "任务管理"));
        _speedLeft.TriggerShowAnimation();
        RefreshTaskManagerButton();
    }

    private NavigationRouteId GetCurrentNavigationRoute() =>
        _currentNavRoute is NavigationRouteId route && FindNavigationPage(route) is not null
            ? route
            : _navigationPages.Length > 0
                ? _navigationPages[0].Route
                : LaunchRoute;

    private Action CaptureTaskManagerBackAction()
    {
        if (this.FindControl<Border>("PanMainRight")?.Child is PageInstanceSelectRight)
            return ApplyInstanceSelectPage;

        if (_managedInstance is not null &&
            this.FindControl<Border>("PanMainLeft")?.Child is PageInstanceLeft &&
            _instanceLeft is not null)
        {
            LaunchInstanceInfo instance = _managedInstance;
            InstancePageSubType subPage = _instanceLeft.PageId;
            return () => ApplyInstanceManagePage(instance, subPage);
        }

        NavigationRouteId route = _taskManagerBackRoute ?? LaunchRoute;
        return () => SelectNavRoute(route, animate: true);
    }

    private void ReturnFromTaskManager()
    {
        Action backAction = _taskManagerBackAction ??
                            (() => SelectNavRoute(_taskManagerBackRoute ?? LaunchRoute, animate: true));
        _taskManagerBackAction = null;
        _taskSessionStore.IsTaskManagerVisible = false;
        backAction();
    }

    private string GetResourceText(string key, string fallback)
    {
        if (TryGetResource(key, null, out object? resource) && resource is string text)
            return text;

        return Avalonia.Application.Current?.TryGetResource(key, null, out resource) == true && resource is string appText
            ? appText
            : fallback;
    }

    private void TrackTaskBegin(string taskId, string title, string stage)
    {
        DesktopFileLog.Info("Task", $"任务开始/进入阶段；Id={taskId}；Title={title}；Stage={stage}。");
        _taskSessionStore.Upsert(taskId, new TaskManagerEntrySnapshot(
            taskId,
            title,
            stage,
            string.Empty,
            0d,
            0,
            0,
            0,
            TaskManagerTaskState.Waiting));
        UpdateTaskManagerViews();
        NotifyTaskManagerButton(ribble: true);
    }

    private void TrackTaskProgress(string taskId, string title, double progress, string detail)
    {
        DesktopFileLog.RealTime("Task", $"任务进度；Id={taskId}；Title={title}；Progress={Math.Clamp(progress, 0d, 1d):P1}；Detail={detail}。");
        TaskManagerEntrySnapshot previous = GetTaskSnapshotOrDefault(taskId, title);
        _taskSessionStore.Upsert(taskId, previous with
        {
            Title = title,
            Stage = previous.Stage,
            Detail = detail,
            Progress = Math.Clamp(progress, 0d, 1d),
            State = TaskManagerTaskState.Running,
            ErrorMessage = null
        });
        UpdateTaskManagerViews();
        RefreshTaskManagerButton();
    }

    private void TrackInstallProgress(string taskId, string title, MinecraftInstallProgress progress)
    {
        string stage = string.IsNullOrWhiteSpace(progress.Stage) ? "正在处理下载任务" : progress.Stage;
        DesktopFileLog.RealTime(
            "Task",
            $"安装任务进度；Id={taskId}；Title={title}；Stage={stage}；Progress={progress.Progress:P1}；" +
            $"Files={progress.CompletedFiles}/{progress.TotalFiles}；Threads={progress.ActiveThreads}/{progress.ThreadLimit}；Speed={progress.SpeedBytesPerSecond}B/s。");
        _taskSessionStore.Upsert(taskId, new TaskManagerEntrySnapshot(
            taskId,
            title,
            stage,
            progress.Detail,
            progress.Progress,
            progress.CompletedFiles,
            progress.TotalFiles,
            progress.SpeedBytesPerSecond,
            TaskManagerTaskState.Running,
            ActiveThreads: progress.ActiveThreads,
            ThreadLimit: progress.ThreadLimit,
            Steps: CreateInstallTaskSteps(progress)));
        UpdateTaskManagerViews();
        RefreshTaskManagerButton();
    }

    private static TaskManagerSubTaskSnapshot[] CreateInstallTaskSteps(
        MinecraftInstallProgress progress)
    {
        if (progress.Steps.Count == 0)
        {
            return
            [
                new TaskManagerSubTaskSnapshot(
                    string.IsNullOrWhiteSpace(progress.Stage) ? "正在处理下载任务" : progress.Stage,
                    progress.Detail,
                    progress.Progress,
                    TaskManagerTaskState.Running)
            ];
        }

        return progress.Steps
            .Select(static step => new TaskManagerSubTaskSnapshot(
                step.Name,
                step.Detail,
                step.Progress,
                MapInstallStepState(step.State)))
            .ToArray();
    }

    private static TaskManagerTaskState MapInstallStepState(MinecraftInstallStepState state) =>
        state switch
        {
            MinecraftInstallStepState.Waiting => TaskManagerTaskState.Waiting,
            MinecraftInstallStepState.Running => TaskManagerTaskState.Running,
            MinecraftInstallStepState.Finished => TaskManagerTaskState.Finished,
            MinecraftInstallStepState.Failed => TaskManagerTaskState.Failed,
            _ => TaskManagerTaskState.Running
        };

    private static TaskManagerSubTaskSnapshot[]? UpdateTaskStepStates(
        IReadOnlyList<TaskManagerSubTaskSnapshot>? steps,
        TaskManagerTaskState state,
        double progress) =>
        steps is null ? null : steps.Select(step => step with { State = state, Progress = progress }).ToArray();

    private void TrackTaskFinished(string taskId, string title, string stage)
    {
        DesktopFileLog.Info("Task", $"任务完成；Id={taskId}；Title={title}；Stage={stage}。");
        TaskManagerEntrySnapshot previous = GetTaskSnapshotOrDefault(taskId, title);
        _taskSessionStore.Upsert(taskId, previous with
        {
            Title = title,
            Stage = stage,
            Detail = "任务已完成",
            Progress = 1d,
            State = TaskManagerTaskState.Finished,
            ErrorMessage = null,
            Steps = UpdateTaskStepStates(previous.Steps, TaskManagerTaskState.Finished, 1d)
        });
        UpdateTaskManagerViews();
        RefreshTaskManagerButton();
        _ = RemoveTaskAfterDelayAsync(taskId, TimeSpan.FromMilliseconds(900));
    }

    private void TrackTaskFailed(string taskId, string title, string message, bool canceled)
    {
        if (canceled)
            DesktopFileLog.Warn("Task", $"任务已取消；Id={taskId}；Title={title}；Message={message}。");
        else
            DesktopFileLog.Error("Task", $"任务失败；Id={taskId}；Title={title}；Message={message}。");
        TaskManagerEntrySnapshot previous = GetTaskSnapshotOrDefault(taskId, title);
        _taskSessionStore.Upsert(taskId, previous with
        {
            Title = title,
            Stage = canceled ? "任务已取消" : "任务失败",
            Detail = canceled ? "已停止下载任务" : "请查看错误信息并稍后重试",
            State = canceled ? TaskManagerTaskState.Canceled : TaskManagerTaskState.Failed,
            ErrorMessage = message,
            Steps = UpdateTaskStepStates(
                previous.Steps,
                canceled ? TaskManagerTaskState.Canceled : TaskManagerTaskState.Failed,
                previous.Progress)
        });
        UpdateTaskManagerViews();
        RefreshTaskManagerButton();
        if (canceled)
            _ = RemoveTaskAfterDelayAsync(taskId, TimeSpan.FromMilliseconds(700));
    }

    private TaskManagerEntrySnapshot GetTaskSnapshotOrDefault(string taskId, string title) =>
        _taskSessionStore.TryGet(taskId, out TaskManagerEntrySnapshot snapshot)
            ? snapshot
            : new TaskManagerEntrySnapshot(
                taskId,
                title,
                "正在准备任务",
                string.Empty,
                0d,
                0,
                0,
                0,
                TaskManagerTaskState.Waiting);

    private async Task RemoveTaskAfterDelayAsync(string taskId, TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(true);
        RemoveTask(taskId);
    }

    private void RemoveTask(string taskId)
    {
        _taskSessionStore.Remove(taskId);
        _speedRight?.RemoveTask(taskId);
        UpdateTaskManagerViews();
        RefreshTaskManagerButton();

        if (_taskSessionStore.IsTaskManagerVisible && _taskSessionStore.Snapshots.Count == 0)
            ReturnFromTaskManager();
    }

    private void UpdateTaskManagerViews()
    {
        if (_taskSessionStore.Snapshots.Count == 0)
        {
            _speedLeft?.SetIdle();
            return;
        }

        foreach (TaskManagerEntrySnapshot snapshot in _taskSessionStore.Snapshots.Values)
            _speedRight?.UpsertTask(snapshot);

        _speedLeft?.UpdateSummary(CreateTaskManagerSummary());
    }

    private TaskManagerSummary CreateTaskManagerSummary()
    {
        TaskManagerEntrySnapshot[] activeTasks = _taskSessionStore.Snapshots.Values
            .Where(static snapshot => snapshot.State is TaskManagerTaskState.Waiting or TaskManagerTaskState.Running)
            .ToArray();
        TaskManagerEntrySnapshot[] sourceTasks = activeTasks.Length == 0
            ? _taskSessionStore.Snapshots.Values.ToArray()
            : activeTasks;

        double progress = sourceTasks.Length == 0
            ? 1d
            : sourceTasks.Average(static snapshot => Math.Clamp(snapshot.Progress, 0d, 1d));
        long speed = activeTasks.Sum(static snapshot => snapshot.SpeedBytesPerSecond);
        int remainingFiles = activeTasks.Sum(static snapshot =>
            snapshot.TotalFiles > 0 ? Math.Max(0, snapshot.TotalFiles - snapshot.CompletedFiles) : 0);
        int threadLimit = activeTasks.Sum(static snapshot => Math.Max(1, snapshot.ThreadLimit));
        if (threadLimit <= 0)
            threadLimit = Math.Max(1, Environment.ProcessorCount);

        return new TaskManagerSummary(
            progress,
            speed,
            remainingFiles,
            activeTasks.Sum(static snapshot => Math.Max(0, snapshot.ActiveThreads)),
            threadLimit);
    }

    private void NotifyTaskManagerButton(bool ribble)
    {
        RefreshTaskManagerButton();
        if (!ribble ||
            this.FindControl<MyExtraButton>("BtnExtraDownload") is not { } button ||
            !button.Show)
        {
            return;
        }

        button.Ribble();
    }

    private void RefreshTaskManagerButton()
    {
        if (this.FindControl<MyExtraButton>("BtnExtraDownload") is not { } button)
            return;

        bool hasActiveTask = _taskSessionStore.HasActiveTask;
        bool hasVisibleTask = _taskSessionStore.HasVisibleTask;
        double progress = hasActiveTask ? CreateTaskManagerSummary().Progress : hasVisibleTask ? 1d : 0d;
        bool show = hasVisibleTask && !_taskSessionStore.IsTaskManagerVisible;
        _extraDockViewModel.SetTaskManager(show, progress);
        button.Progress = _extraDockViewModel.TaskProgress;
        button.Show = _extraDockViewModel.ShowTaskManager;
        _taskSessionStore.PublishProgress();
        RefreshExtraDockChrome();
    }

    private string CreateTaskId(string kind, string identity)
    {
        int sequence = _taskSessionStore.NextSequence();
        string safeIdentity = identity
            .Replace(Path.DirectorySeparatorChar, '_')
            .Replace(Path.AltDirectorySeparatorChar, '_');
        return string.Concat(kind, ":", sequence.ToString(CultureInfo.InvariantCulture), ":", safeIdentity);
    }

    private CancellationTokenSource RegisterTrackedTask(string taskId)
    {
        CancellationTokenSource cancellation = new();
        if (_taskCancellations.Remove(taskId, out CancellationTokenSource? previous))
        {
            previous.Cancel();
            previous.Dispose();
        }

        _taskCancellations.Add(taskId, cancellation);
        return cancellation;
    }

    private void CancelTrackedTask(string taskId)
    {
        if (_taskCancellations.TryGetValue(taskId, out CancellationTokenSource? cancellation))
            cancellation.Cancel();
    }

    private void UnregisterTrackedTask(string taskId, CancellationTokenSource cancellation)
    {
        if (_taskCancellations.TryGetValue(taskId, out CancellationTokenSource? registered) &&
            ReferenceEquals(registered, cancellation))
        {
            _taskCancellations.Remove(taskId);
        }
    }

    private void CancelAllTrackedTasks()
    {
        foreach (CancellationTokenSource cancellation in _taskCancellations.Values)
            cancellation.Cancel();
    }

    private void DisposeTrackedTasks()
    {
        foreach (CancellationTokenSource cancellation in _taskCancellations.Values)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        _taskCancellations.Clear();
    }

    private PageInstanceScreenshotRight CreateInstanceScreenshotPage()
    {
        PageInstanceScreenshotRight page = new();
        page.OpenFolderRequested += (_, path) => OpenFolder(path);
        page.OpenFileRequested += (_, path) => OpenExistingPath(path);
        page.StatusMessage += (_, message) => HandleStatusMessage(message);
        return page;
    }

    private PageInstanceSavesRight CreateInstanceSavesPage()
    {
        PageInstanceSavesRight page = new();
        page.OpenFolderRequested += (_, path) => OpenFolder(path);
        page.SaveDetailsRequested += (_, path) => _ = ShowInstanceSaveDetailsAsync(path);
        page.QuickPlayRequested += (_, worldName) =>
        {
            if (_managedInstance is not null && _launchLeft is not null)
                _ = StartMinecraftAsync(_launchLeft, _managedInstance, worldName);
        };
        page.StatusMessage += (_, message) => HandleStatusMessage(message);
        return page;
    }

    private PageInstanceSavesInfoRight CreateInstanceSavesInfoPage()
    {
        PageInstanceSavesInfoRight page = new();
        page.StatusMessage += (_, message) => HandleStatusMessage(message);
        page.DatapackManageRequested += (_, saveFolder) => ShowInstanceDatapacks(saveFolder);
        return page;
    }

    private async Task ShowInstanceSaveDetailsAsync(string saveFolder)
    {
        if (_managedInstance is null ||
            this.FindControl<Border>("PanMainRight") is not { } rightHost)
        {
            return;
        }

        _instanceSavesInfoPage ??= CreateInstanceSavesInfoPage();
        PageInstanceSavesInfoRight page = _instanceSavesInfoPage;
        _titleInnerBackAction = () =>
        {
            if (_managedInstance is not null)
                ApplyInstanceManagePage(_managedInstance, InstancePageSubType.Saves);
        };
        EnterTitleSubPage("存档详情 - " + Path.GetFileName(saveFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));

        MyPageRight? oldRight = rightHost.Child as MyPageRight;
        if (!ReferenceEquals(oldRight, page))
        {
            oldRight?.PageOnExit();
            rightHost.Child = page;
        }

        RefreshBackToTopBinding();
        page.PageOnEnter();
        await page.SetSaveFolderAsync(saveFolder).ConfigureAwait(true);
    }

    private void ShowInstanceDatapacks(string saveFolder)
    {
        if (this.FindControl<Border>("PanMainRight") is not { } rightHost)
            return;

        _instanceDatapackPage ??= CreateInstanceDatapackPage();
        PageInstanceResourceRight page = _instanceDatapackPage;
        _titleInnerBackAction = () => _ = ShowInstanceSaveDetailsAsync(saveFolder);
        EnterTitleSubPage("数据包 - " + Path.GetFileName(saveFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));

        MyPageRight? oldRight = rightHost.Child as MyPageRight;
        if (!ReferenceEquals(oldRight, page))
        {
            oldRight?.PageOnExit();
            rightHost.Child = page;
        }

        page.SetDataPackFolder(saveFolder);
        RefreshBackToTopBinding();
        page.PageOnEnter();
    }

    private PageInstanceToolsRight CreateInstanceToolsPage()
    {
        PageInstanceToolsRight page = new();
        page.OpenFolderRequested += (_, path) => OpenFolder(path);
        return page;
    }

    private PageInstanceModDisabledRight CreateInstanceModDisabledPage()
    {
        PageInstanceModDisabledRight page = new();
        page.DownloadRequested += (_, _) => SelectNavRoute(DownloadRoute, animate: true);
        page.InstanceSelectRequested += (_, _) =>
        {
            SelectNavRoute(LaunchRoute, animate: true);
            ApplyInstanceSelectPage();
        };
        return page;
    }

    private PageInstanceResourceRight CreateInstanceResourcePage()
    {
        PageInstanceResourceRight page = new();
        page.OpenFolderRequested += (_, path) => OpenFolder(path);
        page.DownloadRequested += (_, subPage) => OpenCommunityForResourcePage(subPage);
        page.StatusMessage += (_, message) =>
        {
            HandleStatusMessage(message);
            ShowHint(message);
        };
        return page;
    }

    private PageInstanceResourceRight CreateInstanceDatapackPage()
    {
        PageInstanceResourceRight page = new();
        page.OpenFolderRequested += (_, path) => OpenFolder(path);
        page.DownloadRequested += (_, _) =>
        {
            SelectNavRoute(CommunityRoute, animate: true);
            _ = _communityLeft?.TrySelectCategory(CommunityResourceCategory.DataPack) == true
                ? _communityRight?.SetCategoryAsync(CommunityResourceCategory.DataPack)
                : Task.CompletedTask;
        };
        page.StatusMessage += (_, message) =>
        {
            HandleStatusMessage(message);
            ShowHint(message);
        };
        return page;
    }

    private void OpenCommunityForResourcePage(InstancePageSubType subPage)
    {
        CommunityResourceCategory category = subPage switch
        {
            InstancePageSubType.Mods => CommunityResourceCategory.Mod,
            InstancePageSubType.ResourcePacks => CommunityResourceCategory.ResourcePack,
            InstancePageSubType.Shaders => CommunityResourceCategory.Shader,
            InstancePageSubType.Schematics => CommunityResourceCategory.World,
            _ => CommunityResourceCategory.Mod
        };

        SelectNavRoute(CommunityRoute, animate: true);
        if (_communityLeft is not null && _communityLeft.TrySelectCategory(category))
            _ = _communityRight?.SetCategoryAsync(category);
    }

    private PageInstanceServerRight CreateInstanceServerPage()
    {
        PageInstanceServerRight page = new();
        page.RefreshRequested += (_, _) => page.Reload();
        page.AddServerRequested += (_, instance) => PromptAddServer(instance, page);
        page.ConnectServerRequested += (_, server) =>
        {
            if (_managedInstance is { } instance && _launchLeft is { } launchPage)
                _ = StartMinecraftAsync(launchPage, instance, serverAddress: server.Address);
        };
        page.EditServerRequested += (_, server) =>
        {
            if (_managedInstance is { } instance)
                PromptEditServer(instance, page, server);
        };
        page.RemoveServerRequested += (_, server) =>
        {
            if (_managedInstance is { } instance)
                PromptRemoveServer(instance, page, server);
        };
        return page;
    }

    private void PromptAddServer(LaunchInstanceInfo instance, PageInstanceServerRight page)
    {
        ShowInputDialog(
            "添加服务器",
            "请输入服务器地址。可以填写域名、IP，或带端口的地址。",
            string.Empty,
            "例如 play.example.net",
            address =>
            {
                if (string.IsNullOrWhiteSpace(address))
                    return;

                string trimmedAddress = address.Trim();
                ShowInputDialog(
                    "服务器名称",
                    "给这个服务器起一个容易识别的名称。",
                    trimmedAddress,
                    "服务器名称",
                    name =>
                    {
                        if (string.IsNullOrWhiteSpace(name))
                            return;

                        _ = AddServerAsync(instance, page, name.Trim(), trimmedAddress);
                    });
            });
    }

    private async Task AddServerAsync(
        LaunchInstanceInfo instance,
        PageInstanceServerRight page,
        string name,
        string address)
    {
        try
        {
            string gameDir = await InstanceGameDirectory.ResolveAsync(instance).ConfigureAwait(true);
            await MinecraftServerListService.AddAsync(
                    gameDir,
                    new MinecraftServerEntry(name, address, null))
                .ConfigureAwait(true);
            page.Reload();
            _launchRight?.AppendLog($"已添加服务器 {name}。");
        }
        catch (Exception ex)
        {
            ShowTextDialog("添加失败", "未能添加服务器。\n\n详细信息：" + ex.Message);
        }
    }

    private void PromptEditServer(
        LaunchInstanceInfo instance,
        PageInstanceServerRight page,
        MinecraftServerEntry server)
    {
        ShowInputDialog(
            "编辑服务器名称",
            "修改服务器在列表中显示的名称。",
            server.Name,
            "服务器名称",
            name =>
            {
                if (string.IsNullOrWhiteSpace(name))
                    return;

                ShowInputDialog(
                    "编辑服务器地址",
                    "请输入服务器域名、IP，或带端口的地址。",
                    server.Address,
                    "例如 play.example.net",
                    address =>
                    {
                        if (!string.IsNullOrWhiteSpace(address))
                        {
                            _ = UpdateServerAsync(
                                instance,
                                page,
                                server,
                                server with { Name = name.Trim(), Address = address.Trim() });
                        }
                    });
            });
    }

    private async Task UpdateServerAsync(
        LaunchInstanceInfo instance,
        PageInstanceServerRight page,
        MinecraftServerEntry original,
        MinecraftServerEntry updated)
    {
        try
        {
            string gameDir = await InstanceGameDirectory.ResolveAsync(instance).ConfigureAwait(true);
            bool changed = await MinecraftServerListService.UpdateAsync(
                    gameDir,
                    original,
                    updated)
                .ConfigureAwait(true);
            if (!changed)
            {
                ShowTextDialog("编辑失败", "服务器条目已不存在，请刷新列表后重试。");
                return;
            }

            page.Reload();
            _launchRight?.AppendLog($"已更新服务器 {updated.Name}。");
        }
        catch (Exception ex)
        {
            ShowTextDialog("编辑失败", "未能更新服务器。\n\n详细信息：" + ex.Message);
        }
    }

    private void PromptRemoveServer(
        LaunchInstanceInfo instance,
        PageInstanceServerRight page,
        MinecraftServerEntry server)
    {
        ShowConfirmDialog(
            "删除服务器",
            $"确定要从列表中删除“{server.Name}”吗？\n\n{server.Address}",
            confirmed =>
            {
                if (confirmed)
                    _ = RemoveServerAsync(instance, page, server);
            },
            "删除",
            "取消",
            isWarn: true);
    }

    private async Task RemoveServerAsync(
        LaunchInstanceInfo instance,
        PageInstanceServerRight page,
        MinecraftServerEntry server)
    {
        try
        {
            string gameDir = await InstanceGameDirectory.ResolveAsync(instance).ConfigureAwait(true);
            bool removed = await MinecraftServerListService.RemoveAsync(
                    gameDir,
                    server)
                .ConfigureAwait(true);
            if (!removed)
            {
                ShowTextDialog("删除失败", "服务器条目已不存在，请刷新列表后重试。");
                return;
            }

            page.Reload();
            _launchRight?.AppendLog($"已删除服务器 {server.Name}。");
        }
        catch (Exception ex)
        {
            ShowTextDialog("删除失败", "未能删除服务器。\n\n详细信息：" + ex.Message);
        }
    }

    private DesktopMainPage CreateSettingsMainPage()
    {
        _setupLeft ??= CreateSetupLeftPage();
        MyPageRight rightPage = _setupLeft.GetOrCreateCurrentPage();
        _setupRight = rightPage;
        return new DesktopMainPage(
            _setupLeft,
            rightPage,
            Activated: () =>
            {
                // Defensive: host fade/sub-page swap races can leave the right pane at 0.
                if (this.FindControl<Border>("PanMainRight") is { } rightHost)
                    rightHost.Opacity = 1d;
                _setupLeft.TriggerShowAnimation();
                // Prefer the live child (may already have swapped to host default page).
                if (this.FindControl<Border>("PanMainRight")?.Child is MyPageRight liveRight)
                    liveRight.PageOnEnter();
                else
                    rightPage.PageOnEnter();
            });
    }

    private PageSetupLeft CreateSetupLeftPage()
    {
        PageSetupLeft page = new();
        page.PageCreated += (_, created) => WireSetupPage(created);
        page.PageChanged += (_, args) => ApplySetupRightPage(args.Page);
        page.ResetRequested += (_, args) =>
            ShowConfirmDialog(
                args.Title,
                args.Message,
                args.Complete,
                args.PrimaryButton,
                args.SecondaryButton,
                args.IsWarn);
        return page;
    }

    private void WireSetupPage(MyPageRight page)
    {
        if (page is PageSetupLaunch launchSettingsPage)
        {
            launchSettingsPage.SwitchToInstanceSetupRequested += (_, _) => _ = SwitchToSelectedInstanceSetupAsync();
        }

        if (page is not ISettingsPageInteractionSource source)
            return;

        source.OpenPathRequested += (_, args) => OpenFolder(args.Path);
        source.OpenUrlRequested += (_, args) => OpenExternalUrl(args.Url);
        source.MessageRequested += (_, args) => ShowTextDialog(args.Title, args.Message, args.PrimaryButton);
        source.ConfirmRequested += (_, args) =>
            ShowConfirmDialog(
                args.Title,
                args.Message,
                args.Complete,
                args.PrimaryButton,
                args.SecondaryButton,
                args.IsWarn);
        source.ColorRequested += (_, args) => ShowColorDialog(args);
    }

    private async Task SwitchToSelectedInstanceSetupAsync()
    {
        _launchLeft ??= CreateLaunchLeftPage();
        await _launchLeft.EnsureInstancesLoadedAsync().ConfigureAwait(true);

        LaunchInstanceInfo? selectedInstance = _launchLeft.SelectedInstance;
        if (selectedInstance is null)
        {
            ShowTextDialog(
                "还没有可设置的版本",
                "当前没有找到可用的 Minecraft 版本。请先下载一个版本，或把已有游戏目录添加到启动器中。",
                "知道了");
            return;
        }

        SelectNavRoute(LaunchRoute, animate: true);
        ApplyInstanceManagePage(selectedInstance, InstancePageSubType.Setup);
    }

    private void ApplySetupRightPage(MyPageRight target)
    {
        if (this.FindControl<Border>("PanMainRight") is not { } rightHost)
            return;

        if (ReferenceEquals(_setupRight, target) && ReferenceEquals(rightHost.Child, target))
        {
            // Parent nav animation may have been interrupted while host opacity was 0.
            rightHost.Opacity = 1d;
            return;
        }

        DesktopFileLog.Info("Settings", $"打开设置页 {target.GetType().Name}。");

        MyPageRight? oldRight = rightHost.Child as MyPageRight;
        _setupRight = target;
        // Do not AniStop("FrmMain PageChangeRight") here: interrupting the main
        // settings enter fade can leave PanMainRight.Opacity at 0 (gray right pane)
        // when the user opens 设置 immediately after launch.
        ModAnimation.AniStop("PageSetupLeft PageChange");
        oldRight?.PageOnExit();
        // Ensure host is visible even if a parent page-change fade was still running.
        rightHost.Opacity = 1d;
        ModAnimation.AniStart(
            new List<ModAnimation.AniData>
            {
                ModAnimation.AaCode(() =>
                {
                    oldRight?.PageOnForceExit();
                    rightHost.Child = target;
                    rightHost.Opacity = 1d;
                    target.Opacity = 0d;
                }, 130),
                ModAnimation.AaCode(() =>
                {
                    rightHost.Opacity = 1d;
                    target.Opacity = 1d;
                    RefreshBackToTopBinding();
                    target.PageOnEnter();
                }, 30, after: true)
            },
            "PageSetupLeft PageChange");
    }

    private void ApplyLaunchLoginPage(ILaunchHomeSurface launchPage, PageLaunchLeft.LaunchLoginPageType type)
    {
        switch (type)
        {
            case PageLaunchLeft.LaunchLoginPageType.ProfileSkin:
                if (_loginProfiles.Count == 0)
                {
                    launchPage.SetSelectedProfilePresent(false);
                    ApplyLaunchLoginPage(launchPage, PageLaunchLeft.LaunchLoginPageType.Profile);
                    return;
                }

                LoginProfileInfo selectedProfile = _loginProfiles[0];
                _loginProfileSkinPage ??= CreateProfileSkinPage(launchPage);
                _loginProfileSkinPage.SetProfile(selectedProfile);
                launchPage.SetLoginPage(_loginProfileSkinPage, animate: true, PageLaunchLeft.LaunchLoginPageType.ProfileSkin);
                break;
            case PageLaunchLeft.LaunchLoginPageType.Profile:
                _loginProfilePage ??= CreateProfilePage(launchPage);
                _loginProfilePage.SetProfiles(_loginProfiles);
                launchPage.SetLoginPage(_loginProfilePage, animate: true, PageLaunchLeft.LaunchLoginPageType.Profile);
                break;
            case PageLaunchLeft.LaunchLoginPageType.Ms:
                _loginMsPage ??= CreateMicrosoftLoginPage(launchPage);
                launchPage.SetLoginPage(_loginMsPage, animate: true, PageLaunchLeft.LaunchLoginPageType.Ms);
                break;
            case PageLaunchLeft.LaunchLoginPageType.Auth:
                _loginAuthPage ??= CreateAuthLoginPage(launchPage);
                launchPage.SetLoginPage(_loginAuthPage, animate: true, PageLaunchLeft.LaunchLoginPageType.Auth);
                break;
            case PageLaunchLeft.LaunchLoginPageType.Offline:
                _loginOfflinePage ??= CreateOfflineLoginPage(launchPage);
                _loginOfflinePage.SetSkinSources(_loginProfiles);
                launchPage.SetLoginPage(_loginOfflinePage, animate: true, PageLaunchLeft.LaunchLoginPageType.Offline);
                break;
            default:
                _loginProfilePage ??= CreateProfilePage(launchPage);
                _loginProfilePage.SetProfiles(_loginProfiles);
                launchPage.SetLoginPage(_loginProfilePage, animate: true, PageLaunchLeft.LaunchLoginPageType.Profile);
                break;
        }
    }

    private PageLoginProfile CreateProfilePage(ILaunchHomeSurface launchPage)
    {
        PageLoginProfile page = new();
        page.ProfileSelected += (_, profile) =>
        {
            _loginProfiles.Remove(profile);
            _loginProfiles.Insert(0, profile);
            launchPage.SetSelectedProfilePresent(true);
            launchPage.RefreshPage(anim: true);
            SaveProfilesInBackground("保存账户档案选择");
            _launchRight?.AppendLog($"已选择账户档案 {profile.Username}。");
        };
        page.ProfileDeleteRequested += (_, profile) =>
        {
            ShowConfirmDialog(
                "删除账户档案",
                $"确定要删除账户档案“{profile.Username}”吗？\n\n删除后需要重新登录才能再次使用此账户。",
                confirmed =>
                {
                    if (confirmed)
                        RemoveLoginProfile(page, launchPage, profile);
                },
                "删除",
                "取消",
                isWarn: true);
        };
        page.CreateProfileRequested += (_, _) =>
        {
            ShowProfileTypeSelector(launchPage);
        };
        page.ImportExportRequested += (_, _) => ShowProfileImportExportSelector(page, launchPage);
        return page;
    }

    private void RemoveLoginProfile(
        PageLoginProfile page,
        ILaunchHomeSurface launchPage,
        LoginProfileInfo profile)
    {
        int removed = _loginProfiles.RemoveAll(existing => IsSameProfile(existing, profile));
        if (removed == 0)
            return;

        LoginProfileInfo? selected = _loginProfiles.FirstOrDefault();
        page.SetProfiles(_loginProfiles, selected);
        launchPage.SetSelectedProfilePresent(selected is not null);
        launchPage.RefreshPage(anim: true, PageLaunchLeft.LaunchLoginPageType.Profile);
        SaveProfilesInBackground("删除账户档案");
        HandleStatusMessage($"已删除账户档案 {profile.Username}。");
    }

    private PageLoginProfileSkin CreateProfileSkinPage(ILaunchHomeSurface launchPage)
    {
        PageLoginProfileSkin page = new();
        page.ChangeProfileRequested += (_, _) =>
        {
            launchPage.SetSelectedProfilePresent(false);
            launchPage.RefreshPage(anim: true);
        };
        page.ChangeSkinRequested += (_, _) => OpenProfileAppearancePage(page.Profile, "更换皮肤");
        page.SaveSkinRequested += (_, _) => _ = SaveProfileSkinAsync(page.Profile);
        page.RefreshSkinRequested += (_, _) => _ = RefreshProfileSkinAsync(page);
        page.ChangeCapeRequested += (_, _) => OpenProfileAppearancePage(page.Profile, "更换披风");
        page.EditPasswordRequested += (_, _) => OpenProfileSecurityPage(page.Profile);
        page.EditNameRequested += (_, _) => OpenProfileNamePage(page.Profile);
        return page;
    }

    private void OpenProfileAppearancePage(LoginProfileInfo? profile, string action)
    {
        if (profile is null)
            return;

        if (profile.Kind == LaunchLoginProfileKind.Microsoft)
        {
            // WPF: ModProfile.ChangeSkinMs — pick local PNG and upload to Minecraft services.
            _ = ChangeMicrosoftSkinAsync(profile, action);
            return;
        }

        if (profile.Kind == LaunchLoginProfileKind.ThirdParty)
        {
            OpenAuthServerProfilePage(profile, action);
            return;
        }

        // WPF offline: borrow MS profile skin or pick local file.
        ShowOfflineSkinOptions(profile, action);
    }

    private void ShowOfflineSkinOptions(LoginProfileInfo profile, string action)
    {
        List<LoginProfileInfo> msProfiles = _loginProfiles
            .Where(static p => p.Kind == LaunchLoginProfileKind.Microsoft)
            .ToList();
        if (msProfiles.Count == 0)
        {
            _ = PickOfflineSkinFileAsync(profile, action);
            return;
        }

        List<MyListItem> items =
        [
            CreateProfileTypeItem("使用本地 PNG 文件", "从磁盘选择皮肤文件作为离线外观。", "lucide/image")
        ];
        foreach (LoginProfileInfo ms in msProfiles)
        {
            items.Add(CreateProfileTypeItem(
                "借用 " + ms.Username + " 的正版皮肤",
                "使用该正版档案当前皮肤作为离线外观来源。",
                "lucide/user"));
        }

        MyMsgSelect dialog = new();
        dialog.Configure(action, items);
        ShowSelectionDialog(dialog, selectedIndex =>
        {
            if (selectedIndex is not int index)
                return;
            if (index == 0)
            {
                _ = PickOfflineSkinFileAsync(profile, action);
                return;
            }

            LoginProfileInfo source = msProfiles[index - 1];
            string skin = MySkin.ResolveSkinAddress(
                source.SkinAddress,
                source.Uuid,
                source.Kind == LaunchLoginProfileKind.ThirdParty ? source.AuthServer : null);
            LoginProfileInfo updated = profile with { SkinAddress = skin };
            ReplaceLoginProfile(profile, updated);
            _loginProfilePage?.SetProfiles(_loginProfiles, updated);
            _loginProfileSkinPage?.SetProfile(updated);
            SaveProfilesInBackground("借用正版皮肤");
            HandleStatusMessage($"已为 {updated.Username} 借用 {source.Username} 的皮肤。");
        });
    }

    private async Task PickOfflineSkinFileAsync(LoginProfileInfo profile, string action)
    {
        string? path = await PickOpenFilePathAsync(
                action,
                new FilePickerFileType("皮肤 PNG") { Patterns = ["*.png"] })
            .ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
            return;

        LoginProfileInfo updated = profile with { SkinAddress = path };
        ReplaceLoginProfile(profile, updated);
        _loginProfilePage?.SetProfiles(_loginProfiles, updated);
        _loginProfileSkinPage?.SetProfile(updated);
        SaveProfilesInBackground("更新离线皮肤");
        ShowTextDialog(action, "已使用本地皮肤文件：\n" + path, "知道了");
    }

    private async Task ChangeMicrosoftSkinAsync(LoginProfileInfo profile, string action)
    {
        string? path = await PickOpenFilePathAsync(
                action,
                new FilePickerFileType("皮肤 PNG") { Patterns = ["*.png"] })
            .ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        if (string.IsNullOrWhiteSpace(profile.AccessToken))
        {
            ShowTextDialog(action, "当前正版档案缺少访问令牌，请先重新登录后再更换皮肤。", "知道了");
            return;
        }

        try
        {
            HandleStatusMessage("正在上传皮肤…");
            byte[] bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(true);
            using MultipartFormDataContent content = new();
            content.Add(new StringContent("classic"), "variant");
            ByteArrayContent fileContent = new(bytes);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            content.Add(fileContent, "file", Path.GetFileName(path));

            using HttpClient client = new() { Timeout = TimeSpan.FromMinutes(2) };
            using HttpRequestMessage request = new(
                HttpMethod.Post,
                "https://api.minecraftservices.com/minecraft/profile/skins")
            {
                Content = content
            };
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", profile.AccessToken);
            request.Headers.TryAddWithoutValidation("Accept", "*/*");
            using HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(true);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            if (!response.IsSuccessStatusCode)
            {
                ShowTextDialog(action, "皮肤上传失败。\n\n" + body, "知道了");
                return;
            }

            string? skinUrl = null;
            using (JsonDocument document = JsonDocument.Parse(body))
            {
                if (document.RootElement.TryGetProperty("skins", out JsonElement skins) &&
                    skins.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement skin in skins.EnumerateArray())
                    {
                        if (skin.ValueKind != JsonValueKind.Object)
                            continue;
                        string state = skin.TryGetProperty("state", out JsonElement stateEl)
                            ? stateEl.GetString() ?? string.Empty
                            : string.Empty;
                        string? url = skin.TryGetProperty("url", out JsonElement urlEl)
                            ? urlEl.GetString()
                            : null;
                        if (string.Equals(state, "ACTIVE", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(url))
                        {
                            skinUrl = url;
                            break;
                        }

                        skinUrl ??= url;
                    }
                }
            }

            LoginProfileInfo updated = profile with
            {
                SkinAddress = skinUrl ?? path
            };
            ReplaceLoginProfile(profile, updated);
            _loginProfilePage?.SetProfiles(_loginProfiles, updated);
            _loginProfileSkinPage?.SetProfile(updated);
            SaveProfilesInBackground("更换 Microsoft 皮肤");
            ShowTextDialog(action, "皮肤已上传并更新。", "知道了");
        }
        catch (Exception ex)
        {
            ShowTextDialog(action, "皮肤上传失败。\n\n详细信息：" + ex.Message, "知道了");
        }
    }

    private void OpenProfileSecurityPage(LoginProfileInfo? profile)
    {
        if (profile is null)
            return;

        if (profile.Kind == LaunchLoginProfileKind.Microsoft)
        {
            OpenExternalUrl("https://account.microsoft.com/security");
            ShowTextDialog("修改密码", "已打开 Microsoft 账户安全页面。密码修改完成后，可能需要在启动器中重新登录。", "知道了");
            return;
        }

        if (profile.Kind == LaunchLoginProfileKind.ThirdParty)
        {
            OpenAuthServerProfilePage(profile, "修改密码");
            return;
        }

        ShowTextDialog("修改密码", "离线档案没有在线密码。若需要更换玩家名或 UUID，请新建一个离线档案。", "知道了");
    }

    private void OpenProfileNamePage(LoginProfileInfo? profile)
    {
        if (profile is null)
            return;

        if (profile.Kind == LaunchLoginProfileKind.Microsoft)
        {
            // WPF: ModProfile.EditProfileId — rename via Minecraft services API.
            ShowInputDialog(
                "修改玩家名",
                "正版玩家名 30 天内通常只能修改一次。请输入 3–16 位字母/数字/下划线。",
                profile.Username,
                "新的玩家名",
                newName =>
                {
                    if (string.IsNullOrWhiteSpace(newName))
                        return;
                    _ = RenameMicrosoftProfileAsync(profile, newName.Trim());
                });
            return;
        }

        if (profile.Kind == LaunchLoginProfileKind.ThirdParty)
        {
            OpenAuthServerProfilePage(profile, "修改玩家名");
            return;
        }

        // WPF offline: rename + regenerate offline UUID from the new name.
        ShowInputDialog(
            "修改档案",
            "请输入新的离线玩家名（3–16 位字母、数字或下划线）。",
            profile.Username,
            "玩家名",
            newName =>
            {
                if (string.IsNullOrWhiteSpace(newName))
                    return;

                string trimmed = newName.Trim();
                if (!System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^[A-Za-z0-9_]{3,16}$"))
                {
                    ShowTextDialog("修改档案", "玩家名不合法。请使用 3–16 位字母、数字或下划线。", "知道了");
                    return;
                }

                if (string.Equals(trimmed, profile.Username, StringComparison.Ordinal))
                    return;

                string uuid = CreateOfflineUuid(trimmed, legacy: false);
                LoginProfileInfo updated = profile with
                {
                    Username = trimmed,
                    Uuid = uuid,
                    Info = string.IsNullOrWhiteSpace(profile.Info) || profile.Info.Contains("离线", StringComparison.Ordinal)
                        ? "离线"
                        : profile.Info
                };
                ReplaceLoginProfile(profile, updated);
                _loginProfilePage?.SetProfiles(_loginProfiles, updated);
                _loginProfileSkinPage?.SetProfile(updated);
                SaveProfilesInBackground("修改离线档案");
                HandleStatusMessage("已将离线档案重命名为 " + trimmed);
            });
    }

    private async Task RenameMicrosoftProfileAsync(LoginProfileInfo profile, string newUsername)
    {
        if (string.IsNullOrWhiteSpace(profile.AccessToken))
        {
            ShowTextDialog("修改玩家名", "当前正版档案缺少访问令牌，请先重新登录。", "知道了");
            return;
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(newUsername, @"^[A-Za-z0-9_]{3,16}$"))
        {
            ShowTextDialog("修改玩家名", "玩家名不合法。请使用 3–16 位字母、数字或下划线。", "知道了");
            return;
        }

        try
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(30) };
            using (HttpRequestMessage check = new(
                       HttpMethod.Get,
                       "https://api.minecraftservices.com/minecraft/profile/name/" +
                       Uri.EscapeDataString(newUsername) + "/available"))
            {
                check.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", profile.AccessToken);
                using HttpResponseMessage checkResponse = await client.SendAsync(check).ConfigureAwait(true);
                string checkBody = await checkResponse.Content.ReadAsStringAsync().ConfigureAwait(true);
                if (checkResponse.IsSuccessStatusCode)
                {
                    using JsonDocument checkDoc = JsonDocument.Parse(checkBody);
                    string status = checkDoc.RootElement.TryGetProperty("status", out JsonElement statusEl)
                        ? statusEl.GetString() ?? string.Empty
                        : string.Empty;
                    if (string.Equals(status, "DUPLICATE", StringComparison.OrdinalIgnoreCase))
                    {
                        ShowTextDialog("修改玩家名", "该玩家名已被占用。", "知道了");
                        return;
                    }

                    if (string.Equals(status, "NOT_ALLOWED", StringComparison.OrdinalIgnoreCase))
                    {
                        ShowTextDialog("修改玩家名", "该玩家名不被允许。", "知道了");
                        return;
                    }
                }
            }

            using HttpRequestMessage put = new(
                HttpMethod.Put,
                "https://api.minecraftservices.com/minecraft/profile/name/" + Uri.EscapeDataString(newUsername));
            put.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", profile.AccessToken);
            put.Content = new StringContent(string.Empty);
            using HttpResponseMessage putResponse = await client.SendAsync(put).ConfigureAwait(true);
            string putBody = await putResponse.Content.ReadAsStringAsync().ConfigureAwait(true);
            if (!putResponse.IsSuccessStatusCode)
            {
                string message = putResponse.StatusCode == System.Net.HttpStatusCode.Forbidden
                    ? "修改被拒绝（可能处于冷却期或权限不足）。"
                    : putBody;
                ShowTextDialog("修改玩家名", "修改失败。\n\n" + message, "知道了");
                return;
            }

            string finalName = newUsername;
            try
            {
                using JsonDocument result = JsonDocument.Parse(putBody);
                if (result.RootElement.TryGetProperty("name", out JsonElement nameEl) &&
                    nameEl.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(nameEl.GetString()))
                {
                    finalName = nameEl.GetString()!;
                }
            }
            catch (JsonException)
            {
            }

            LoginProfileInfo updated = profile with { Username = finalName };
            ReplaceLoginProfile(profile, updated);
            _loginProfilePage?.SetProfiles(_loginProfiles, updated);
            _loginProfileSkinPage?.SetProfile(updated);
            SaveProfilesInBackground("修改 Microsoft 玩家名");
            ShowTextDialog("修改玩家名", "玩家名已更新为：" + finalName, "知道了");
        }
        catch (Exception ex)
        {
            ShowTextDialog("修改玩家名", "修改失败。\n\n详细信息：" + ex.Message, "知道了");
        }
    }

    private static string CreateOfflineUuid(string username, bool legacy)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(legacy ? username : "OfflinePlayer:" + username);
#pragma warning disable CA5351
        byte[] hash = System.Security.Cryptography.MD5.HashData(bytes);
#pragma warning restore CA5351
        hash[6] = (byte)((hash[6] & 0x0f) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash).ToString("N");
    }

    private void OpenAuthServerProfilePage(LoginProfileInfo profile, string action)
    {
        string? url = ResolveAuthServerProfileUrl(profile.AuthServer);
        if (string.IsNullOrWhiteSpace(url))
        {
            ShowTextDialog(action, "第三方账户的资料由认证服务器管理，但当前档案没有记录可打开的服务器地址。请到对应认证服务器的网站中修改。", "知道了");
            return;
        }

        OpenExternalUrl(url);
        ShowTextDialog(action, "已打开此第三方账户所属的认证服务器页面。请在服务器网页中完成账户资料修改。", "知道了");
    }

    private static string? ResolveAuthServerProfileUrl(string? authServer)
    {
        string? normalized = NormalizeAuthServerUrl(authServer ?? string.Empty);
        if (normalized is null || !Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri))
            return null;

        string path = uri.AbsolutePath.TrimEnd('/');
        foreach (string suffix in new[] { "/api/yggdrasil/authserver", "/api/yggdrasil" })
        {
            if (!path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;

            string rootPath = path[..^suffix.Length].TrimEnd('/');
            UriBuilder builder = new(uri)
            {
                Path = rootPath + "/user/profile",
                Query = string.Empty,
                Fragment = string.Empty
            };
            return builder.Uri.ToString();
        }

        return uri.ToString();
    }

    private async Task SaveProfileSkinAsync(LoginProfileInfo? profile)
    {
        if (profile is null)
            return;

        if (string.IsNullOrWhiteSpace(profile.SkinAddress))
        {
            ShowTextDialog("保存皮肤", "当前档案没有可保存的皮肤资源。请先登录带有皮肤的在线档案，或在离线档案中选择一个皮肤来源。", "知道了");
            return;
        }

        string suggestedFileName = SanitizeFileName(profile.Username) + "-skin.png";
        string targetPath = await PickSaveFilePathAsync(
                "保存皮肤",
                suggestedFileName,
                new FilePickerFileType("PNG 图片") { Patterns = ["*.png"] })
            .ConfigureAwait(true)
            ?? Path.Combine(GetDesktopOrBaseDirectory(), suggestedFileName);

        try
        {
            if (TryCreateHttpUri(profile.SkinAddress, out Uri? uri))
            {
                using HttpClient client = new();
                byte[] bytes = await client.GetByteArrayAsync(uri).ConfigureAwait(true);
                await File.WriteAllBytesAsync(targetPath, bytes).ConfigureAwait(true);
            }
            else if (File.Exists(profile.SkinAddress))
            {
                File.Copy(profile.SkinAddress, targetPath, overwrite: true);
            }
            else
            {
                ShowTextDialog("保存皮肤", "当前皮肤资源不存在，可能已经被移动或需要重新登录后刷新。", "知道了");
                return;
            }

            ShowTextDialog("保存完成", "皮肤已保存到：\n" + targetPath);
        }
        catch (Exception ex)
        {
            ShowTextDialog("保存失败", "未能保存皮肤。\n\n详细信息：" + ex.Message);
        }
    }

    private async Task RefreshProfileSkinAsync(PageLoginProfileSkin page)
    {
        LoginProfileInfo? profile = page.Profile;
        if (profile is null)
            return;

        try
        {
            if (profile.Kind == LaunchLoginProfileKind.Microsoft &&
                !string.IsNullOrWhiteSpace(profile.RefreshToken))
            {
                LoginProfileInfo refreshed = await RefreshLaunchProfileAsync(profile, CancellationToken.None)
                    .ConfigureAwait(true);
                AddOrUpdateLoginProfile(refreshed);
                _loginProfilePage?.SetProfiles(_loginProfiles, refreshed);
                page.SetProfile(refreshed);
                SaveProfilesInBackground("刷新 Microsoft 皮肤");
                ShowTextDialog("皮肤已刷新", "已从 Microsoft 重新获取档案与皮肤信息。", "知道了");
                return;
            }

            page.Reload();
            ShowTextDialog(
                "已刷新档案显示",
                profile.Kind == LaunchLoginProfileKind.Offline
                    ? "已重新加载离线档案皮肤。若使用本地 PNG，请确认文件仍然存在。"
                    : "已重新载入档案信息。第三方皮肤请在认证站修改后重新登录。",
                "知道了");
        }
        catch (Exception ex)
        {
            page.Reload();
            ShowTextDialog("刷新失败", "未能刷新皮肤。\n\n详细信息：" + ex.Message, "知道了");
        }
    }

    private void ShowProfileTypeSelector(ILaunchHomeSurface launchPage)
    {
        MyMsgSelect dialog = new();
        dialog.Configure(
            "选择账户类型",
            [
                CreateProfileTypeItem(
                    "Microsoft 登录",
                    "使用正版 Microsoft 账户登录，适合已购买 Minecraft 的玩家。",
                    "lucide/shield-check"),
                CreateProfileTypeItem(
                    "第三方登录",
                    "使用 Authlib-Injector 兼容认证服务器登录。",
                    "lucide/network"),
                CreateProfileTypeItem(
                    "离线登录",
                    "创建本地离线档案。联机服务器可能不会接受此档案。",
                    "lucide/link-2-off")
            ]);
        ShowSelectionDialog(dialog, selectedIndex =>
        {
            if (selectedIndex is not int index)
                return;

            PageLaunchLeft.LaunchLoginPageType? target = index switch
            {
                0 => PageLaunchLeft.LaunchLoginPageType.Ms,
                1 => PageLaunchLeft.LaunchLoginPageType.Auth,
                2 => PageLaunchLeft.LaunchLoginPageType.Offline,
                _ => null
            };
            if (target is null)
                return;

            launchPage.RefreshPage(anim: true, target.Value);
            _launchRight?.AppendLog($"正在创建{dialog.Items[index].Title}档案。");
        });
    }

    private static MyListItem CreateProfileTypeItem(string title, string info, string icon) =>
        new()
        {
            Title = title,
            Info = info,
            SvgIcon = icon,
            LogoScale = 0.82d,
            MinHeight = 42d,
            Margin = new Thickness(0d, 2d)
        };

    private void ShowProfileImportExportSelector(PageLoginProfile page, ILaunchHomeSurface launchPage)
    {
        MyMsgSelect dialog = new();
        dialog.Configure(
            "导入或导出账户档案",
            [
                CreateProfileTypeItem(
                    "导入账户档案",
                    "从本地 JSON 文件导入账户档案，并与当前列表合并。",
                    "lucide/file-input"),
                CreateProfileTypeItem(
                    "导出账户档案",
                    "将当前账户档案保存为 JSON 文件，方便备份或转移到其他设备。",
                    "lucide/file-output")
            ]);

        ShowSelectionDialog(dialog, selectedIndex =>
        {
            if (selectedIndex == 0)
                _ = ImportProfilesAsync(page, launchPage);
            else if (selectedIndex == 1)
                _ = ExportProfilesAsync();
        });
    }

    private void ShowSelectionDialog(MyMsgSelect dialog, Action<int?> closed)
    {
        if (this.FindControl<BlurBorder>("PanMsgBackground") is not { } background ||
            this.FindControl<Grid>("PanMsg") is not { } host)
        {
            closed(null);
            return;
        }

        host.Children.Clear();
        background.IsVisible = true;
        AnimateMsgBackground(background, 90);
        dialog.Closed += (_, args) =>
        {
            host.Children.Remove(dialog);
            if (host.Children.Count == 0)
            {
                AnimateMsgBackground(background, 0, () =>
                {
                    background.Background = Brushes.Transparent;
                    background.IsVisible = false;
                });
            }
            closed(args.SelectedIndex);
        };
        host.Children.Add(dialog);
        dialog.BeginShowAnimation();
    }

    private void ShowColorDialog(SettingsColorRequestedEventArgs request)
    {
        if (this.FindControl<BlurBorder>("PanMsgBackground") is not { } background ||
            this.FindControl<Grid>("PanMsg") is not { } host)
        {
            request.Complete(null);
            return;
        }

        MyMsgColor dialog = new();
        dialog.Configure(request.Title, request.InitialColor);
        dialog.PreviewChanged += (_, color) => request.Preview(color);
        host.Children.Clear();
        background.IsVisible = true;
        AnimateMsgBackground(background, 90);
        dialog.Closed += (_, args) =>
        {
            host.Children.Remove(dialog);
            if (host.Children.Count == 0)
            {
                AnimateMsgBackground(background, 0, () =>
                {
                    background.Background = Brushes.Transparent;
                    background.IsVisible = false;
                });
            }
            request.Complete(args.Color);
        };
        host.Children.Add(dialog);
        dialog.BeginShowAnimation();
    }

    private void ShowTextDialog(string title, string caption, string primaryButton = "确定")
    {
        if (this.FindControl<BlurBorder>("PanMsgBackground") is not { } background ||
            this.FindControl<Grid>("PanMsg") is not { } host)
        {
            _launchRight?.AppendLog($"{title}：{caption}");
            return;
        }

        MyMsgText dialog = new();
        dialog.Configure(title, caption, primaryButton);
        host.Children.Clear();
        background.IsVisible = true;
        AnimateMsgBackground(background, 90);
        dialog.Closed += (_, _) =>
        {
            host.Children.Remove(dialog);
            if (host.Children.Count == 0)
            {
                AnimateMsgBackground(background, 0, () =>
                {
                    background.Background = Brushes.Transparent;
                    background.IsVisible = false;
                });
            }
        };
        host.Children.Add(dialog);
        dialog.BeginShowAnimation();
    }

    private void ShowConfirmDialog(
        string title,
        string caption,
        Action<bool> closed,
        string primaryButton = "确定",
        string secondaryButton = "取消",
        bool isWarn = false)
    {
        ShowMarkdownDialog(
            title,
            caption,
            result => closed(result == 1),
            primaryButton,
            secondaryButton,
            thirdButton: string.Empty,
            isWarn);
    }

    private void ShowMarkdownDialog(
        string title,
        string markdown,
        Action<int> closed,
        string primaryButton,
        string secondaryButton = "",
        string thirdButton = "",
        bool isWarn = false)
    {
        if (this.FindControl<BlurBorder>("PanMsgBackground") is not { } background ||
            this.FindControl<Grid>("PanMsg") is not { } host)
        {
            closed(0);
            return;
        }

        MyMsgMarkdown dialog = new();
        dialog.Configure(title, markdown, primaryButton, secondaryButton, thirdButton, isWarn);
        host.Children.Clear();
        background.IsVisible = true;
        AnimateMsgBackground(background, 90);
        dialog.Closed += (_, args) =>
        {
            host.Children.Remove(dialog);
            if (host.Children.Count == 0)
            {
                AnimateMsgBackground(background, 0, () =>
                {
                    background.Background = Brushes.Transparent;
                    background.IsVisible = false;
                });
            }
            closed(args.Result);
        };
        host.Children.Add(dialog);
        dialog.BeginShowAnimation();
    }

    private void ShowInputDialog(
        string title,
        string caption,
        string content,
        string hintText,
        Action<string?> closed,
        bool isWarn = false)
    {
        if (this.FindControl<BlurBorder>("PanMsgBackground") is not { } background ||
            this.FindControl<Grid>("PanMsg") is not { } host)
        {
            closed(null);
            return;
        }

        MyMsgInput dialog = new();
        dialog.Configure(title, caption, content, hintText, isWarn: isWarn);
        host.Children.Clear();
        background.IsVisible = true;
        AnimateMsgBackground(background, 90);
        dialog.Closed += (_, args) =>
        {
            host.Children.Remove(dialog);
            if (host.Children.Count == 0)
            {
                AnimateMsgBackground(background, 0, () =>
                {
                    background.Background = Brushes.Transparent;
                    background.IsVisible = false;
                });
            }
            closed(args.Result);
        };
        host.Children.Add(dialog);
        dialog.BeginShowAnimation();
    }

    private void ShowLoginDialog(MyMsgLogin dialog, Action closed)
    {
        if (this.FindControl<BlurBorder>("PanMsgBackground") is not { } background ||
            this.FindControl<Grid>("PanMsg") is not { } host)
        {
            _launchRight?.AppendLog($"{dialog.Title}：{dialog.Caption}");
            closed();
            return;
        }

        host.Children.Clear();
        background.IsVisible = true;
        AnimateMsgBackground(background, 90);
        dialog.ReopenWebpageRequested += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(dialog.Website))
                OpenExternalUrl(dialog.Website);
        };
        dialog.CopyCodeRequested += async (_, _) =>
        {
            await CopyLoginCodeAsync(dialog.UserCode).ConfigureAwait(true);
        };
        dialog.CancelRequested += (_, _) => closed();
        dialog.DragRequested += (_, e) => BeginMoveDrag(e);
        dialog.Closed += (_, _) =>
        {
            if (host.Children.Count == 0)
            {
                AnimateMsgBackground(background, 0, () =>
                {
                    background.Background = Brushes.Transparent;
                    background.IsVisible = false;
                });
            }
        };
        host.Children.Add(dialog);
    }

    private async Task CopyLoginCodeAsync(string userCode)
    {
        if (string.IsNullOrWhiteSpace(userCode))
            return;

        try
        {
            if (_clipboardWriter is not null)
            {
                await _clipboardWriter(userCode).ConfigureAwait(true);
                return;
            }

            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(userCode).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _launchRight?.AppendLog("复制登录代码失败：" + ex.Message);
        }
    }

    private async Task PrepareLoginDialogAsync(MyMsgLogin dialog)
    {
        // Device codes are short-lived. Repeat both convenience actions for every
        // newly issued code instead of relying on the first login attempt only.
        await CopyLoginCodeAsync(dialog.UserCode).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(dialog.Website))
            OpenExternalUrl(dialog.Website);
    }

    private async Task StartInstallAsync(DownloadInstallRequest request)
    {
        string taskId = CreateTaskId("install", request.VersionId);
        using CancellationTokenSource cancellation = RegisterTrackedTask(taskId);
        string taskTitle = "安装 " + request.VersionId;
        ActivateTaskManagerPage(animate: true);
        TrackTaskBegin(taskId, taskTitle, "准备安装文件");

        string minecraftRoot = string.IsNullOrWhiteSpace(request.MinecraftRootDirectory)
            ? GetDefaultMinecraftRoot()
            : request.MinecraftRootDirectory;
        Directory.CreateDirectory(minecraftRoot);
        LauncherSettings settings = LauncherSettingsPageBinder.LoadSettings();
        int downloadThreadLimit = Math.Clamp(GetIntegerOption(settings, LauncherSettingKeys.ToolDownloadThread, 63) + 1, 1, 256);
        Progress<MinecraftInstallProgress> progress = new(update => TrackInstallProgress(taskId, taskTitle, update));
        try
        {
            MinecraftInstallResult result = await _minecraftInstallService.InstallAsync(
                    new MinecraftInstallRequest
                    {
                        VersionId = request.VersionId,
                        BaseVersionId = request.BaseVersionId,
                        VersionJsonUrl = request.VersionJsonUrl,
                        MinecraftRootDirectory = minecraftRoot,
                        PreferOfficialSource = true,
                        DownloadThreadLimit = downloadThreadLimit,
                        Loader = request.Loader,
                        Addons = request.Addons ?? [],
                        ReplaceExistingVersion = request.ReplaceExistingVersion,
                        JavaExecutablePath = ResolvePreferredJavaExecutablePath(forceConsole: true)
                    },
                    progress,
                    cancellation.Token)
                .ConfigureAwait(true);
            TrackTaskFinished(taskId, taskTitle, "安装完成");
            _launchRight?.AppendLog($"{request.VersionId} 安装完成。");

            if (_launchLeft is not null)
            {
                await _launchLeft.RefreshInstancesAsync().ConfigureAwait(true);
                LaunchInstanceInfo? installed = _launchLeft.Instances.FirstOrDefault(instance =>
                    string.Equals(instance.InstanceDirectory, result.InstanceDirectory, StringComparison.OrdinalIgnoreCase));
                if (installed is not null)
                    _launchLeft.SetInstances(_launchLeft.Instances, installed);
            }
        }
        catch (OperationCanceledException)
        {
            TrackTaskFailed(taskId, taskTitle, "安装已取消。", canceled: true);
        }
        catch (Exception ex)
        {
            TrackTaskFailed(taskId, taskTitle, ex.Message, canceled: false);
            ShowTextDialog("安装失败", "未能完成 Minecraft 安装。\n\n详细信息：" + ex.Message);
        }
        finally
        {
            UnregisterTrackedTask(taskId, cancellation);
        }
    }

    private async Task StartMinecraftAsync(
        ILaunchHomeSurface launchPage,
        LaunchInstanceInfo instance,
        string? worldName = null,
        string? serverAddress = null,
        MinecraftRepairSession? repairSession = null)
    {
        // Prefer the profile currently shown on the login UI (not always the first saved entry).
        LoginProfileInfo? profile =
            _loginProfileSkinPage?.Profile ??
            _loginProfilePage?.SelectedProfile ??
            _loginProfiles.FirstOrDefault();
        if (profile is null)
        {
            if (repairSession is not null)
                await repairSession.Transaction.RollbackAsync().ConfigureAwait(false);
            if (launchPage.IsLaunchInProgress)
                launchPage.PageChangeToLogin();
            ShowTextDialog("请选择账户档案", "启动游戏前需要先选择或创建一个账户档案。");
            return;
        }

        // Paint launching UI immediately (PageLaunchLeft already calls ShowLaunching on click;
        // keep a fallback for other entry points). Then hop off the UI thread completely.
        if (!launchPage.IsLaunchInProgress)
            launchPage.ShowLaunching(instance);

        // Yield until after layout + animation frames so the launching pane is visible
        // before any disk/network work (WPF ModLaunch similarly paints first).
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Yield();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Task.Delay(32).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        if (repairSession is null)
        {
            _launchCancellation?.Cancel();
            _launchCancellation?.Dispose();
            _launchCancellation = new CancellationTokenSource();
        }
        else if (_launchCancellation is null)
        {
            _launchCancellation = new CancellationTokenSource();
        }
        CancellationToken cancellationToken = _launchCancellation.Token;
        LauncherSettings? runtimeSettingsForRepair = null;

        try
        {
            // Entire prep + coordinate pipeline off UI thread (no UI-thread JSON/IO).
            string instanceDirectory = instance.InstanceDirectory;
            InstanceMetadata metadata = await InstanceMetadataStore.LoadAsync(
                    instanceDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
            LauncherSettings runtimeSettings = await Task.Run(
                    LauncherSettingsPageBinder.LoadSettings,
                    cancellationToken)
                .ConfigureAwait(false);
            runtimeSettingsForRepair = runtimeSettings;
            string method = MinecraftLaunchCoordinator.FormatLoginMethod(profile);
            Dispatcher.UIThread.Post(() => launchPage.LaunchingRefresh(
                AvaloniaLocalizationManager.GetText("Common.Action.Initialize", "初始化"),
                0d,
                method: method), DispatcherPriority.Background);

            MinecraftLaunchCoordinatorResult result = await _launchCoordinator.RunAsync(
                    new MinecraftLaunchCoordinatorRequest
                    {
                        Instance = instance,
                        Profile = profile,
                        Metadata = metadata,
                        Settings = runtimeSettings,
                        MinecraftRootDirectory = GetMinecraftRootFromInstance(instance),
                        PreferOfficialSource = runtimeSettings.DownloadSource !=
                                               DownloadSourcePreference.MirrorOnly,
                        WorldName = worldName,
                        ServerAddress = serverAddress,
                        Report = report =>
                        {
                            // Always Post — never call LaunchingRefresh synchronously on a hot path.
                            Dispatcher.UIThread.Post(
                                () => launchPage.LaunchingRefresh(
                                    report.StageName,
                                    report.Progress,
                                    report.IsLaunched,
                                    report.Method,
                                    report.DownloadSpeed),
                                DispatcherPriority.Background);
                        },
                        Log = message =>
                        {
                            Dispatcher.UIThread.Post(
                                () => _launchRight?.AppendLog(message),
                                DispatcherPriority.Background);
                        },
                        RefreshProfileAsync = RefreshLaunchProfileAsync,
                        CreatePlanAsync = CreateLaunchPlanAsync,
                        RunPreLaunchCommandAsync = RunPreLaunchCommandAsync,
                        ApplyProcessPriority = ApplyProcessPriority,
                        ConfirmJavaDownloadAsync = ConfirmJavaDownloadAsync
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            // Surviving the coordinator's early-exit grace period means the repaired launch is
            // healthy enough to commit. Any later crash starts a fresh diagnosis session.
            repairSession?.Transaction.Commit();
            try
            {
                await _minecraftAiRepairAdvisor.StopLocalServerAsync().ConfigureAwait(false);
            }
            catch (Exception serverStopException)
            {
                DesktopFileLog.Warn(
                    "MinecraftRepairAI",
                    "Minecraft 已成功启动，但释放本地模型服务失败。",
                    serverStopException);
            }

            // Launch pipeline succeeded — never fold post-success UI side effects into "启动失败".
            // (e.g. Close()/Hide launcher visibility can throw ObjectDisposedException.)
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!ReferenceEquals(result.Profile, profile) &&
                        result.Profile.Kind == LaunchLoginProfileKind.Microsoft)
                    {
                        AddOrUpdateLoginProfile(result.Profile);
                        _loginProfilePage?.SetProfiles(_loginProfiles, result.Profile);
                        _loginProfileSkinPage?.SetProfile(result.Profile);
                        SaveProfilesInBackground("刷新 Microsoft 正版档案");
                    }

                    Process process = result.Process;
                    SetGameRunningExtras(
                        process,
                        new RunningGameContext(
                            instance,
                            launchPage,
                            runtimeSettings,
                            result.FaultReport,
                            result.Plan.NativesDirectory,
                            worldName,
                            serverAddress,
                            JavaMajorVersion: result.Plan.JvmHostRequest?.JavaMajorVersion,
                            MemoryMegabytes: TryReadMaximumHeapMegabytes(result.Plan.JvmHostRequest is { } hostRequest
                                ? hostRequest.VmArguments
                                : result.Plan.StartInfo.ArgumentList),
                            LoginMethod: MinecraftLaunchCoordinator.FormatLoginMethod(result.Profile),
                            LoginServerHost: ResolveLoginServerHost(result.Profile.AuthServer),
                            ProfileUsername: result.Profile.Username,
                            ProfileUuid: result.Profile.Uuid,
                            UsedExperimentalJvmHost: result.Plan.JvmHostRequest is not null,
                            JavaExecutableName: Path.GetFileName(result.Plan.JvmHostRequest?.JavaExecutablePath ??
                                                                 result.Plan.StartInfo.FileName),
                            JavaExecutablePathForRedaction: result.Plan.JvmHostRequest?.JavaExecutablePath ??
                                                            result.Plan.StartInfo.FileName,
                            ClasspathEntryCount: result.Plan.ClasspathEntries.Count,
                            VmArgumentCount: result.Plan.JvmHostRequest?.VmArguments.Length ??
                                             result.Plan.StartInfo.ArgumentList.Count(argument =>
                                                 argument.StartsWith('-')),
                            GameArgumentCount: result.Plan.JvmHostRequest?.GameArguments.Length));
                    UpdateBackgroundVideoPlayback(runtimeSettings);
                    _launchRight?.AppendLog(!string.IsNullOrWhiteSpace(worldName)
                        ? $"{instance.Name} 已启动，正在进入存档 {worldName}。"
                        : !string.IsNullOrWhiteSpace(serverAddress)
                            ? $"{instance.Name} 已启动，正在连接服务器 {serverAddress}。"
                            : $"{instance.Name} 已启动。");

                    if (runtimeSettings.GetIntegerOption(
                            "LaunchArgumentVisible",
                            LauncherSettingDefaults.GetInteger("LaunchArgumentVisible")) != 0)
                    {
                        launchPage.PageChangeToLogin();
                    }

                    // Visibility last — Close/Hide must not reverse a successful launch UX.
                    ApplyLauncherVisibility(process, runtimeSettings);
                });
            }
            catch (Exception postEx)
            {
                DesktopFileLog.Warn("LaunchUI", "游戏已启动，但启动后界面处理发生异常。", postEx);
                _launchRight?.AppendLog("启动后界面处理异常（游戏已启动）：" + postEx.Message);
            }

            try
            {
                await IncrementInstanceLaunchCountAsync(instance).ConfigureAwait(false);
            }
            catch (Exception countEx)
            {
                DesktopFileLog.Warn("LaunchHistory", $"记录实例 {instance.Name} 的启动次数失败。", countEx);
                _launchRight?.AppendLog("记录启动次数失败：" + countEx.Message);
            }
        }
        catch (OperationCanceledException)
        {
            await _minecraftAiRepairAdvisor.StopLocalServerAsync().ConfigureAwait(false);
            DesktopFileLog.Warn("LaunchUI", $"实例 {instance.Name} 的启动操作已取消。");
            if (repairSession is not null)
                await repairSession.Transaction.RollbackAsync().ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (launchPage.IsLaunchInProgress)
                    launchPage.PageChangeToLogin();
            });
        }
        catch (Exception ex)
        {
            DesktopFileLog.Error("LaunchUI", $"实例 {instance.Name} 启动失败。", ex);
            LauncherSettings? repairSettings = runtimeSettingsForRepair ?? repairSession?.Settings;
            if (repairSettings is not null)
            {
                MinecraftLaunchFaultReport failureReport = ex is MinecraftLaunchFailureException launchFailure &&
                                                           launchFailure.FaultReport is { } structuredFailure
                    ? structuredFailure
                    : MinecraftLaunchFaultAnalyzer.Analyze(ex, "LaunchCoordinator");
                await Dispatcher.UIThread.InvokeAsync(() =>
                    _launchRight?.AppendLog("启动失败，错误处理器正在分析：" + ex.Message));
                await TryRepairMissingDependenciesAsync(
                        new RunningGameContext(
                            instance,
                            launchPage,
                            repairSettings,
                            Task.FromResult<MinecraftLaunchFaultReport?>(failureReport),
                            WorldName: worldName,
                            ServerAddress: serverAddress,
                            RepairSession: repairSession,
                            LoginMethod: MinecraftLaunchCoordinator.FormatLoginMethod(profile),
                            LoginServerHost: ResolveLoginServerHost(profile.AuthServer),
                            ProfileUsername: profile.Username,
                            ProfileUuid: profile.Uuid,
                            UsedExperimentalJvmHost: repairSettings.GetBooleanOption(
                                LauncherSettingKeys.ExperimentalJvmLifecycleHost,
                                LauncherSettingDefaults.GetBoolean(
                                    LauncherSettingKeys.ExperimentalJvmLifecycleHost.Value))))
                    .ConfigureAwait(false);
            }
            else
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (launchPage.IsLaunchInProgress)
                        launchPage.PageChangeToLogin();
                    ShowTextDialog("启动失败", "未能启动游戏。\n\n详细信息：" + ex.Message);
                    _launchRight?.AppendLog("启动失败：" + ex.Message);
                });
            }
        }
    }

    private void SetGameRunningExtras(Process? process, RunningGameContext? context = null)
    {
        if (!ReferenceEquals(_runningGameProcess, process))
        {
            if (_runningGameProcess is { } previous)
            {
                try
                {
                    previous.EnableRaisingEvents = false;
                    previous.Exited -= RunningGameProcess_Exited;
                }
                catch
                {
                    // process may already be disposed
                }
            }

            _runningGameProcess = process is { HasExited: false } ? process : null;
            _runningGameContext = _runningGameProcess is null ? null : context;
            if (_runningGameProcess is not null && _runningGameContext is { } runningContext)
                _ = ObserveRunningGameFaultAsync(runningContext);
            if (_runningGameProcess is { } current)
            {
                try
                {
                    current.EnableRaisingEvents = true;
                    current.Exited += RunningGameProcess_Exited;
                }
                catch
                {
                    // EnableRaisingEvents can throw if process already exited
                    _runningGameProcess = null;
                    _runningGameContext = null;
                }
            }
        }
        else if (process is null || process.HasExited)
        {
            _runningGameProcess = null;
            _runningGameContext = null;
        }

        _gameSessionStore.SetRunning(_runningGameProcess, _runningGameContext);
        _extraDockViewModel.SetGameRunning(_gameSessionStore.IsRunning);

        if (this.FindControl<MyExtraButton>("BtnExtraShutdown") is { } shutdown)
        {
            // WPF: BtnExtraShutdown.Show = game running (bottom-right extra power button)
            shutdown.Show = _extraDockViewModel.ShowShutdown;
        }

        if (this.FindControl<MyExtraButton>("BtnExtraLog") is { } logBtn)
            logBtn.Show = _extraDockViewModel.ShowGameLog;

        RefreshExtraDockChrome();
    }

    private async Task ObserveRunningGameFaultAsync(RunningGameContext context)
    {
        if (context.FaultReport is null)
            return;
        try
        {
            MinecraftLaunchFaultReport? report = await context.FaultReport.ConfigureAwait(false);
            if (report?.Code != MinecraftLaunchFaultCode.MissingModDependency ||
                !ReferenceEquals(_runningGameContext, context))
            {
                return;
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
                _launchRight?.AppendLog("已在游戏仍运行时检测到 NeoForge 缺失依赖，正在进入修复流程。"));
            await TryRepairMissingDependenciesAsync(context).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            DesktopFileLog.Warn("GameProcess", "观察运行中结构化故障失败。", exception);
        }
    }

    private void RunningGameProcess_Exited(object? sender, EventArgs e)
    {
        RunningGameContext? context = _runningGameContext;
        int exitCode = 0;
        if (sender is Process process)
        {
            try
            {
                exitCode = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                exitCode = -1;
            }
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_runningGameProcess, sender) ||
                _runningGameProcess is null ||
                _runningGameProcess.HasExited)
            {
                SetGameRunningExtras(null);
            }
        }, DispatcherPriority.Background);

        if (exitCode != 0 && context is not null)
            _ = TryRepairMissingDependenciesAsync(context with { ProcessExitCode = exitCode });
    }

    private async Task TryRepairMissingDependenciesAsync(RunningGameContext context)
    {
        _launchCancellation?.Cancel();
        _launchCancellation?.Dispose();
        _launchCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _launchCancellation.Token;
        MinecraftRepairSession session = context.RepairSession ?? new MinecraftRepairSession(context.Settings);
        string gameDirectory = context.Instance.InstanceDirectory;
        MinecraftLaunchFaultReport? fault = null;
        IReadOnlyList<string> crashLines = [];
        string analysisMarkdown = string.Empty;
        bool aiProducedDiagnosis = false;
        MinecraftRepairExecutionResult repair = new("尚未执行修复。", true);
        try
        {
            gameDirectory = await InstanceGameDirectory.ResolveAsync(context.Instance, cancellationToken)
                .ConfigureAwait(false);
            crashLines = await ReadRecentCrashLinesAsync(gameDirectory, cancellationToken).ConfigureAwait(false);
            fault = await AwaitFaultReportAsync(context.FaultReport, cancellationToken).ConfigureAwait(false);
            fault ??= MinecraftLaunchFaultAnalyzer.AnalyzeText(crashLines, "GameProcess");
            IReadOnlyList<MinecraftMissingDependency> dependencies = MinecraftMissingDependencyParser.Parse(crashLines);
            analysisMarkdown = BuildConventionalCrashAnalysis(fault, dependencies);
            if (!string.IsNullOrWhiteSpace(session.LastModelAnalysis))
                analysisMarkdown = session.LastModelAnalysis;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                context.LaunchPage.ShowRepairWorkflow(
                    AvaloniaLocalizationManager.GetText("Crash.Repair.Title", "正在修补 Minecraft"),
                    AvaloniaLocalizationManager.GetText("Crash.Repair.Stage.Parse", "解析 Minecraft 异常"),
                    0.08d,
                    fault.Code.ToString(),
                    context.Instance);
                _launchRight?.AppendLog(
                    $"错误处理器：{fault.Code} · 子系统={fault.Subsystem} · 节点={fault.Stage}" +
                    (string.IsNullOrWhiteSpace(fault.LastClassName) ? string.Empty : " · 类=" + fault.LastClassName));
            });

            if (session.Attempt != MinecraftRepairAttempt.None)
            {
                string failedRepair = BuildFailedRepairFeedback(session, fault, context.ProcessExitCode);
                await Dispatcher.UIThread.InvokeAsync(() => _launchRight?.AppendLog(failedRepair));
            }

            bool aiEnabled = context.Settings.GetBooleanOption(
                LauncherSettingKeys.ExperimentalMinecraftAiRepair,
                LauncherSettingDefaults.GetBoolean(LauncherSettingKeys.ExperimentalMinecraftAiRepair.Value));
            MinecraftRepairActionKind conventionalAction = SelectConventionalRepairAction(
                fault,
                dependencies,
                context.NativesDirectory);
            if (ShouldExecuteConventionalRepairDirectly(
                    session.Attempt == MinecraftRepairAttempt.None,
                    context.Settings.AutomaticallyRepairGameIssues,
                    aiEnabled))
            {
                if (IsAutomaticallyExecutableRepair(conventionalAction))
                {
                    await Dispatcher.UIThread.InvokeAsync(() => context.LaunchPage.ShowRepairWorkflow(
                        AvaloniaLocalizationManager.GetText("Crash.Repair.Title", "正在修补 Minecraft"),
                        AvaloniaLocalizationManager.GetText("Crash.Repair.Stage.Execute", "正在执行修复"),
                        0.28d,
                        conventionalAction.ToString(),
                        context.Instance));
                    try
                    {
                        repair = await ExecuteMinecraftRepairAsync(
                                context,
                                fault,
                                conventionalAction,
                                dependencies,
                                gameDirectory,
                                suggestion: null,
                                session.Transaction,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception conventionalException)
                        when (conventionalException is not OperationCanceledException)
                    {
                        repair = new MinecraftRepairExecutionResult(
                            "常规修复执行失败：" + conventionalException.Message,
                            true);
                        DesktopFileLog.Warn(
                            "MinecraftRepair",
                            "常规修复执行失败，将尝试 AI 修复模型。",
                            conventionalException);
                        await Dispatcher.UIThread.InvokeAsync(() => _launchRight?.AppendLog(repair.Message));
                    }
                    if (!repair.IsFailure && repair.MadeChanges)
                    {
                        session.Attempt = MinecraftRepairAttempt.ConventionalApplied;
                        session.LastRepairSummary = BuildRepairAttemptSummary(
                            "常规自动修复",
                            conventionalAction.ToString(),
                            repair);
                        await RestartMinecraftAfterRepairAsync(context, session, repair.Message, cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }
                    if (!repair.IsFailure)
                    {
                        DesktopFileLog.Info(
                            "MinecraftRepair",
                            "常规修复检查完成但没有产生任何改动，将直接调用 AI 修复模型。");
                        await Dispatcher.UIThread.InvokeAsync(() => _launchRight?.AppendLog(
                            "常规修复没有产生改动，跳过无意义的重启并直接调用 AI 修复模型。"));
                    }
                }
            }

            if (aiEnabled)
            {
                string conventionalSuggestion = IsAutomaticallyExecutableRepair(conventionalAction)
                    ? conventionalAction.ToString()
                    : "无可执行建议";
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    context.LaunchPage.ShowRepairWorkflow(
                        AvaloniaLocalizationManager.GetText("Crash.Model.Title", "正在调用模型"),
                        AvaloniaLocalizationManager.GetText("Crash.Model.Stage.Prepare", "准备 AI 修复模型"),
                        0.01d,
                        $"{MinecraftAiRepairAdvisor.ModelName} · 普通处理器建议：{conventionalSuggestion}",
                        context.Instance);
                    _launchRight?.AppendLog(
                        "实验性 AI 修复已启用；普通错误处理器建议 " + conventionalSuggestion +
                        "，仅转交 AI 判断，不会直接执行。");
                });
                IReadOnlyList<MinecraftModMetadata> installedMods = await Task.Run(
                        () => MinecraftModMetadataReader.ReadDirectory(Path.Combine(gameDirectory, "mods")),
                        cancellationToken)
                    .ConfigureAwait(false);
                MinecraftVersionJsonInfo currentVersion = MinecraftVersionJsonInspector.Read(context.Instance);
                string currentLoader = ResolveCommunityLoader(context.Instance, installedMods);
                MinecraftAiRepairContext modelContext = new(
                    currentVersion.MinecraftVersionId,
                    currentLoader,
                    context.JavaMajorVersion,
                    context.MemoryMegabytes,
                    RuntimeInformation.OSDescription,
                    RuntimeInformation.ProcessArchitecture.ToString(),
                    installedMods.Count,
                    crashLines.Count,
                    dependencies.Select(dependency => dependency.ModId)
                        .Where(modId => !string.IsNullOrWhiteSpace(modId))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    context.ProcessExitCode);
                MinecraftRepairActionKind[] candidateActions = fault.AllowedActions
                    .Where(action => action != MinecraftRepairActionKind.ReextractNatives ||
                                     !string.IsNullOrWhiteSpace(context.NativesDirectory))
                    .Where(action => action != MinecraftRepairActionKind.InstallMissingModDependencies ||
                                     dependencies.Count > 0)
                    .Concat(IsAutomaticallyExecutableRepair(conventionalAction)
                        ? [conventionalAction]
                        : [])
                    .Concat(dependencies.Count > 0
                        ? [
                            MinecraftRepairActionKind.InstallMissingModDependencies,
                            MinecraftRepairActionKind.DownloadMod
                        ]
                        : [])
                    .Distinct()
                    .ToArray();
                if (candidateActions.Length == 0)
                    candidateActions = [MinecraftRepairActionKind.InspectOnly];
                string[] modelCrashLines = crashLines
                    .Select(line => RedactMinecraftAiContext(line, context, gameDirectory))
                    .ToArray();
                string? failedRepairFeedback = session.Attempt == MinecraftRepairAttempt.None
                    ? null
                    : BuildFailedRepairFeedback(session, fault, context.ProcessExitCode);
                MinecraftLaunchFaultReport modelFault = fault with
                {
                    AllowedActions = candidateActions,
                    Message = RedactMinecraftAiContext(
                        string.IsNullOrWhiteSpace(failedRepairFeedback)
                            ? fault.Message
                            : failedRepairFeedback + Environment.NewLine + "本次错误：" + fault.Message,
                        context,
                        gameDirectory),
                    StackTrace = RedactMinecraftAiContext(fault.StackTrace, context, gameDirectory),
                    Evidence = fault.Evidence
                        .Concat(IsAutomaticallyExecutableRepair(conventionalAction)
                            ?
                            [
                                "ConventionalHandlerSuggestion=" + conventionalAction,
                                "ConventionalHandlerSuggestionStatus=AdvisoryOnlyNotExecuted",
                                "Instruction=实验性 AI 修复已启用；普通错误处理器的动作仅作为建议，请结合完整上下文决定是否采用。"
                            ]
                            : [])
                        .Concat(string.IsNullOrWhiteSpace(failedRepairFeedback)
                            ? []
                            :
                            [
                                "PreviousRepairOutcome=FailedAfterRestart",
                                "PreviousRepair=" + session.LastRepairSummary,
                                "Instruction=上次修复已执行但重新启动仍失败；请结合新错误重新判断，不要无依据重复同一修复。"
                            ])
                        .Select(line => RedactMinecraftAiContext(line, context, gameDirectory))
                        .ToArray()
                };
                try
                {
                    int providerValue = context.Settings.GetIntegerOption(
                        LauncherSettingKeys.ExperimentalMinecraftAiProvider,
                        LauncherSettingDefaults.GetInteger(LauncherSettingKeys.ExperimentalMinecraftAiProvider.Value));
                    MinecraftAiProvider provider = Enum.IsDefined(typeof(MinecraftAiProvider), providerValue)
                        ? (MinecraftAiProvider)providerValue
                        : MinecraftAiProvider.Local;
                    int reasoningValue = context.Settings.GetIntegerOption(
                        LauncherSettingKeys.ExperimentalMinecraftAiReasoningEffort,
                        LauncherSettingDefaults.GetInteger(
                            LauncherSettingKeys.ExperimentalMinecraftAiReasoningEffort.Value));
                    MinecraftAiReasoningEffort reasoningEffort =
                        Enum.IsDefined(typeof(MinecraftAiReasoningEffort), reasoningValue)
                            ? (MinecraftAiReasoningEffort)reasoningValue
                            : MinecraftAiReasoningEffort.None;
                    string? apiKey = provider == MinecraftAiProvider.OpenAiCompatible
                        ? await MinecraftAiApiCredentialStore.ReadAsync(cancellationToken).ConfigureAwait(false)
                        : null;
                    int localModelValue = context.Settings.GetIntegerOption(
                        LauncherSettingKeys.ExperimentalMinecraftAiLocalModel,
                        LauncherSettingDefaults.GetInteger(
                            LauncherSettingKeys.ExperimentalMinecraftAiLocalModel.Value));
                    MinecraftAiLocalModel localModel = Enum.IsDefined(typeof(MinecraftAiLocalModel), localModelValue)
                        ? (MinecraftAiLocalModel)localModelValue
                        : MinecraftAiLocalModel.Gemma4E2B;
                    int tokenBudget = context.Settings.GetIntegerOption(
                        LauncherSettingKeys.ExperimentalMinecraftAiTokenBudget,
                        LauncherSettingDefaults.GetInteger(
                            LauncherSettingKeys.ExperimentalMinecraftAiTokenBudget.Value));
                    int downloadThreadLimit = Math.Clamp(
                        context.Settings.GetIntegerOption(
                            LauncherSettingKeys.ToolDownloadThread,
                            LauncherSettingDefaults.GetInteger(LauncherSettingKeys.ToolDownloadThread.Value)) + 1,
                        1,
                        32);
                    MinecraftAiModelOptions modelOptions = new(
                        context.Settings.GetTextOption(
                            LauncherSettingKeys.ExperimentalMinecraftAiModelPath,
                            LauncherSettingDefaults.GetText(LauncherSettingKeys.ExperimentalMinecraftAiModelPath.Value)),
                        context.Settings.GetTextOption(
                            LauncherSettingKeys.ExperimentalMinecraftAiModelSha256,
                            LauncherSettingDefaults.GetText(LauncherSettingKeys.ExperimentalMinecraftAiModelSha256.Value)),
                        context.Settings.GetTextOption(
                            LauncherSettingKeys.ExperimentalMinecraftAiRuntimePath,
                            LauncherSettingDefaults.GetText(LauncherSettingKeys.ExperimentalMinecraftAiRuntimePath.Value)),
                        provider,
                        context.Settings.GetTextOption(
                            LauncherSettingKeys.ExperimentalMinecraftAiApiBaseUrl,
                            LauncherSettingDefaults.GetText(
                                LauncherSettingKeys.ExperimentalMinecraftAiApiBaseUrl.Value)),
                        context.Settings.GetTextOption(
                            LauncherSettingKeys.ExperimentalMinecraftAiApiModel,
                            LauncherSettingDefaults.GetText(LauncherSettingKeys.ExperimentalMinecraftAiApiModel.Value)),
                        apiKey,
                        reasoningEffort,
                        localModel,
                        MinecraftAiRepairAdvisor.NormalizeTokenBudget(tokenBudget),
                        downloadThreadLimit);
                    MinecraftAiRepairSuggestion? aiSuggestion = await _minecraftAiRepairAdvisor.AdviseAsync(
                            modelFault,
                            modelCrashLines,
                            installedMods,
                            modelContext,
                            (scopes, token) => BuildMinecraftAiDetailedContextAsync(
                                context,
                                gameDirectory,
                                crashLines,
                                installedMods,
                                scopes,
                                modelOptions.Provider == MinecraftAiProvider.OpenAiCompatible ? 48_000 : 14_000,
                                token),
                            AvaloniaLocalizationManager.CurrentLanguageCode,
                            modelOptions,
                            progress => Dispatcher.UIThread.Post(() =>
                            {
                                context.LaunchPage.ShowRepairWorkflow(
                                    AvaloniaLocalizationManager.GetText("Crash.Model.Title", "正在调用模型"),
                                    progress.Stage,
                                    progress.Progress,
                                    progress.Detail,
                                    context.Instance);
                                _launchRight?.AppendLog(
                                    "Minecraft 错误修复模型：" + progress.Stage +
                                    (string.IsNullOrWhiteSpace(progress.Detail) ? string.Empty : " · " + progress.Detail));
                            }, DispatcherPriority.Background),
                            cancellationToken,
                            summaryOnly: session.Attempt == MinecraftRepairAttempt.ModelApplied)
                        .ConfigureAwait(false);
                    if (aiSuggestion is not null)
                    {
                        analysisMarkdown = string.IsNullOrWhiteSpace(aiSuggestion.AnalysisMarkdown)
                            ? analysisMarkdown
                            : aiSuggestion.AnalysisMarkdown;
                        session.LastModelAnalysis = analysisMarkdown;
                        aiProducedDiagnosis = !string.IsNullOrWhiteSpace(aiSuggestion.AnalysisMarkdown);
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            context.LaunchPage.ShowRepairWorkflow(
                                AvaloniaLocalizationManager.GetText("Crash.Model.Title", "正在调用模型"),
                                aiSuggestion.Stage,
                                Math.Max(0.94d, aiSuggestion.Progress),
                                aiSuggestion.NoAbility ? "AI 已完成错误总结" : $"{aiSuggestion.RepairSteps.Count} 个修复步骤",
                                context.Instance);
                                _launchRight?.AppendLog(
                                    aiSuggestion.NoAbility
                                        ? "Minecraft 错误修复模型：没有安全修复能力，已开始总结错误。"
                                        : $"Minecraft 错误修复模型：生成 {aiSuggestion.RepairSteps.Count} 个链式修复步骤；可信度={aiSuggestion.Confidence:P0}.");
                        });
                        if (aiSuggestion.NoAbility)
                        {
                            repair = new MinecraftRepairExecutionResult("AI 已完成错误总结，但没有安全可执行的修复动作。", true);
                        }
                        else if (context.Settings.AutomaticallyRepairGameIssues &&
                                 aiSuggestion.RepairSteps.All(step => IsAutomaticallyExecutableRepair(step.Action)) &&
                                 await ConfirmAiRepairActionAsync(aiSuggestion, cancellationToken).ConfigureAwait(false))
                        {
                            bool planMadeChanges = false;
                            List<string> completedMessages = [];
                            List<string> completedActions = [];
                            for (int stepIndex = 0; stepIndex < aiSuggestion.RepairSteps.Count; stepIndex++)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                MinecraftAiRepairStep step = aiSuggestion.RepairSteps[stepIndex];
                                double planProgress = 0.94d + (0.05d * stepIndex / aiSuggestion.RepairSteps.Count);
                                await Dispatcher.UIThread.InvokeAsync(() =>
                                {
                                    context.LaunchPage.ShowRepairWorkflow(
                                        AvaloniaLocalizationManager.GetText("Crash.Model.Title", "正在调用模型"),
                                        $"{step.Stage} ({stepIndex + 1}/{aiSuggestion.RepairSteps.Count})",
                                        planProgress,
                                        step.Action.ToString(),
                                        context.Instance);
                                    _launchRight?.AppendLog(
                                        $"模型链式修复 {stepIndex + 1}/{aiSuggestion.RepairSteps.Count}：{step.Action}" +
                                        (string.IsNullOrWhiteSpace(step.Rationale) ? string.Empty : " · " + step.Rationale));
                                });
                                MinecraftAiRepairSuggestion stepSuggestion = new(
                                    step.Action,
                                    aiSuggestion.AnalysisMarkdown,
                                    aiSuggestion.Confidence,
                                    step.Stage,
                                    step.Progress,
                                    step.Parameters);
                                repair = await ExecuteMinecraftRepairAsync(
                                        context,
                                        fault,
                                        step.Action,
                                        dependencies,
                                        gameDirectory,
                                        stepSuggestion,
                                        session.Transaction,
                                        cancellationToken)
                                    .ConfigureAwait(false);
                                if (repair.IsFailure)
                                    break;
                                planMadeChanges |= repair.MadeChanges;
                                completedActions.Add(step.Action.ToString());
                                completedMessages.Add(repair.Message);
                            }
                            if (!repair.IsFailure && planMadeChanges)
                            {
                                repair = new MinecraftRepairExecutionResult(
                                    string.Join(" ", completedMessages),
                                    false,
                                    true);
                            }
                            if (!repair.IsFailure && repair.MadeChanges)
                            {
                                session.Attempt = MinecraftRepairAttempt.ModelApplied;
                                session.LastRepairSummary = BuildRepairAttemptSummary(
                                    "AI 链式修复",
                                    string.Join(" -> ", completedActions),
                                    repair);
                                await RestartMinecraftAfterRepairAsync(context, session, repair.Message, cancellationToken)
                                    .ConfigureAwait(false);
                                return;
                            }
                            if (!repair.IsFailure)
                            {
                                DesktopFileLog.Info(
                                    "MinecraftRepairAI",
                                    "模型修复计划执行完成但没有产生任何改动，不会重新启动 Minecraft。");
                                await Dispatcher.UIThread.InvokeAsync(() => _launchRight?.AppendLog(
                                    "模型修复没有产生改动，已停止自动重启。"));
                            }
                        }
                    }
                }
                catch (Exception aiException)
                    when (aiException is not OperationCanceledException)
                {
                    DesktopFileLog.Warn("MinecraftRepairAI", "AI 修复模型分析失败，将保留常规分析结果。", aiException);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                        _launchRight?.AppendLog("AI 修复模型分析失败，已回退常规分析器：" + aiException.Message));
                }
            }
            repair = new MinecraftRepairExecutionResult(
                aiProducedDiagnosis
                    ? "AI 已完成错误诊断，但没有建议执行可能破坏游戏文件的自动修改。请根据上方 AI 分析检查模组、资源包和日志。"
                    : aiEnabled
                        ? "AI 修复模型未能返回有效诊断或安全可执行的修复计划。"
                        : "常规分析器未能解决错误，且 AI 修复模型功能未启用。",
                true);
            await FinishFailedRepairAsync(
                    context,
                    session,
                    fault,
                    analysisMarkdown,
                    repair.Message,
                    gameDirectory,
                    crashLines)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await _minecraftAiRepairAdvisor.StopLocalServerAsync().ConfigureAwait(false);
            await session.Transaction.RollbackAsync().ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (context.LaunchPage.IsLaunchInProgress)
                    context.LaunchPage.PageChangeToLogin();
                _launchRight?.AppendLog(
                    session.Transaction.HasChanges
                        ? "Minecraft 修复已取消，本轮更改已回滚。"
                        : "Minecraft 错误分析已取消，本轮未修改任何文件。");
            });
        }
        catch (Exception ex)
        {
            fault ??= MinecraftLaunchFaultAnalyzer.Analyze(ex, "CrashAnalyzer");
            string failure = "崩溃分析或自动修复失败：" + ex.Message;
            await FinishFailedRepairAsync(
                    context,
                    session,
                    fault,
                    string.IsNullOrWhiteSpace(analysisMarkdown)
                        ? BuildConventionalCrashAnalysis(fault, [])
                        : analysisMarkdown,
                    failure,
                    gameDirectory,
                    crashLines)
                .ConfigureAwait(false);
        }
    }

    private static MinecraftRepairActionKind SelectConventionalRepairAction(
        MinecraftLaunchFaultReport fault,
        IReadOnlyList<MinecraftMissingDependency> dependencies,
        string? nativesDirectory)
    {
        if (fault.Code == MinecraftLaunchFaultCode.NativeLibraryFailed &&
            !string.IsNullOrWhiteSpace(nativesDirectory))
            return MinecraftRepairActionKind.ReextractNatives;
        if (fault.Code == MinecraftLaunchFaultCode.MissingModDependency && dependencies.Count > 0)
            return MinecraftRepairActionKind.InstallMissingModDependencies;
        return fault.Code switch
        {
            MinecraftLaunchFaultCode.MainClassMissing or MinecraftLaunchFaultCode.ClasspathDependencyMissing =>
                MinecraftRepairActionKind.RepairVersionFiles,
            MinecraftLaunchFaultCode.JavaRuntimeMissing or MinecraftLaunchFaultCode.JavaRuntimeIncompatible or
                MinecraftLaunchFaultCode.JvmInitializationFailed => MinecraftRepairActionKind.SelectCompatibleJava,
            _ => MinecraftRepairActionKind.InspectOnly
        };
    }

    private async Task<MinecraftRepairExecutionResult> ExecuteMinecraftRepairAsync(
        RunningGameContext context,
        MinecraftLaunchFaultReport fault,
        MinecraftRepairActionKind action,
        IReadOnlyList<MinecraftMissingDependency> dependencies,
        string gameDirectory,
        MinecraftAiRepairSuggestion? suggestion,
        MinecraftRepairTransaction transaction,
        CancellationToken cancellationToken)
    {
        return action switch
        {
            MinecraftRepairActionKind.RepairVersionFiles =>
                await RepairVersionFilesAfterFaultAsync(context, fault, transaction, cancellationToken)
                    .ConfigureAwait(false),
            MinecraftRepairActionKind.ReextractNatives when !string.IsNullOrWhiteSpace(context.NativesDirectory) =>
                await ReextractNativesAfterFaultAsync(context, fault, transaction, cancellationToken).ConfigureAwait(false),
            MinecraftRepairActionKind.InstallMissingModDependencies when dependencies.Count > 0 =>
                await RepairMissingDependenciesAfterFaultAsync(
                        context,
                        dependencies,
                        gameDirectory,
                        transaction,
                        cancellationToken)
                    .ConfigureAwait(false),
            MinecraftRepairActionKind.DownloadMod when suggestion?.Parameters.ModId is { } modId =>
                await RepairRequestedModAsync(
                        context,
                        gameDirectory,
                        modId,
                        suggestion.Parameters.ModVersion,
                        updateExisting: false,
                        transaction,
                        cancellationToken)
                    .ConfigureAwait(false),
            MinecraftRepairActionKind.UpdateMod when suggestion?.Parameters.ModId is { } updateModId =>
                await RepairRequestedModAsync(
                        context,
                        gameDirectory,
                        updateModId,
                        suggestion.Parameters.ModVersion,
                        updateExisting: true,
                        transaction,
                        cancellationToken)
                    .ConfigureAwait(false),
            MinecraftRepairActionKind.DisableMod when suggestion?.Parameters.ModId is { } disableModId =>
                await DisableRequestedModAsync(
                        gameDirectory,
                        disableModId,
                        transaction,
                        cancellationToken)
                    .ConfigureAwait(false),
            MinecraftRepairActionKind.DisableExperimentalJvmHost =>
                DisableExperimentalJvmHost(context),
            MinecraftRepairActionKind.SelectCompatibleJava =>
                await SelectCompatibleJavaAfterFaultAsync(context, transaction, cancellationToken)
                    .ConfigureAwait(false),
            MinecraftRepairActionKind.DownloadCompatibleJava =>
                await DownloadCompatibleJavaAfterFaultAsync(context, transaction, cancellationToken)
                    .ConfigureAwait(false),
            MinecraftRepairActionKind.ReinstallVersionAndUpdateLoader =>
                await ReinstallVersionAndUpdateLoaderAsync(
                        context,
                        suggestion,
                        transaction,
                        cancellationToken)
                    .ConfigureAwait(false),
            _ => new MinecraftRepairExecutionResult(
                "常规错误分析器没有找到可安全自动执行的修复；请查看分析内容和日志。",
                true)
        };
    }

    private static MinecraftRepairExecutionResult DisableExperimentalJvmHost(RunningGameContext context)
    {
        bool enabled = context.Settings.GetBooleanOption(
            LauncherSettingKeys.ExperimentalJvmLifecycleHost,
            LauncherSettingDefaults.GetBoolean(LauncherSettingKeys.ExperimentalJvmLifecycleHost.Value));
        if (!enabled)
        {
            return new MinecraftRepairExecutionResult(
                "实验性 Jvm.NET Host 已处于关闭状态，没有需要修改的设置。",
                false,
                false);
        }
        context.Settings.SetBooleanOption(LauncherSettingKeys.ExperimentalJvmLifecycleHost, false);
        LauncherSettings persisted = LauncherSettingsPageBinder.LoadSettings();
        persisted.SetBooleanOption(LauncherSettingKeys.ExperimentalJvmLifecycleHost, false);
        LauncherSettingsPageBinder.SaveSettings(persisted);
        return new MinecraftRepairExecutionResult(
            "已关闭实验性 Jvm.NET Host；下次启动将使用传统 Java 进程。",
            false,
            true);
    }

    private async Task<MinecraftRepairExecutionResult> RepairVersionFilesAfterFaultAsync(
        RunningGameContext context,
        MinecraftLaunchFaultReport fault,
        MinecraftRepairTransaction transaction,
        CancellationToken cancellationToken)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            context.LaunchPage.ShowRepairWorkflow(
                AvaloniaLocalizationManager.GetText("Crash.Repair.Title", "正在修补 Minecraft"),
                AvaloniaLocalizationManager.GetText("Crash.Repair.Stage.Execute", "正在执行修复"),
                0.35d);
            _launchRight?.AppendLog($"自动修复：{fault.Code}，开始校验并补全版本文件。");
        });
        int changedFiles = 0;
        await _minecraftInstallService.RepairAsync(
                new MinecraftRepairRequest
                {
                    VersionId = context.Instance.Name,
                    VersionJsonPath = context.Instance.VersionJsonPath,
                    MinecraftRootDirectory = GetMinecraftRootFromInstance(context.Instance),
                    InstanceDirectory = context.Instance.InstanceDirectory,
                    PreferOfficialSource = context.Settings.DownloadSource != DownloadSourcePreference.MirrorOnly,
                    BeforeFileChangeAsync = async (path, token) =>
                        await transaction.BackupFileAsync(path, token).ConfigureAwait(false),
                    FileChanged = _ => Interlocked.Increment(ref changedFiles)
                },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new MinecraftRepairExecutionResult(
            changedFiles > 0
                ? $"自动修复完成：已补全或替换 {changedFiles} 个版本文件，请重新启动游戏。"
                : "版本文件校验完成，没有发现需要修改的文件。",
            false,
            changedFiles > 0);
    }

    private async Task<MinecraftRepairExecutionResult> ReextractNativesAfterFaultAsync(
        RunningGameContext context,
        MinecraftLaunchFaultReport fault,
        MinecraftRepairTransaction transaction,
        CancellationToken cancellationToken)
    {
        string nativesDirectory = context.NativesDirectory!;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            context.LaunchPage.ShowRepairWorkflow(
                AvaloniaLocalizationManager.GetText("Crash.Repair.Title", "正在修补 Minecraft"),
                AvaloniaLocalizationManager.GetText("Crash.Repair.Stage.Execute", "正在执行修复"),
                0.5d);
            _launchRight?.AppendLog($"自动修复：{fault.Code}，准备重新解压 Natives。");
        });
        cancellationToken.ThrowIfCancellationRequested();
        bool existed = Directory.Exists(nativesDirectory);
        transaction.BackupDirectoryByMove(nativesDirectory);
        return new MinecraftRepairExecutionResult(
            existed
                ? "自动修复完成：旧 Natives 已清理，下次启动会重新解压。"
                : "Natives 目录不存在，没有可重新提取的文件。",
            false,
            existed);
    }

    private async Task<MinecraftRepairExecutionResult> RepairMissingDependenciesAfterFaultAsync(
        RunningGameContext context,
        IReadOnlyList<MinecraftMissingDependency> dependencies,
        string gameDirectory,
        MinecraftRepairTransaction transaction,
        CancellationToken cancellationToken)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            context.LaunchPage.ShowRepairWorkflow(
                AvaloniaLocalizationManager.GetText("Crash.Repair.Title", "正在修补 Minecraft"),
                AvaloniaLocalizationManager.GetText("Crash.Repair.Stage.Execute", "正在执行修复"),
                0.32d);
            _launchRight?.AppendLog($"自动修复：发现 {dependencies.Count} 个缺失前置模组。");
        });
        string modsDirectory = Path.Combine(gameDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);
        string gameVersion = MinecraftVersionJsonInspector.Read(context.Instance).MinecraftVersionId;
        string loader = ResolveCommunityLoader(
            context.Instance,
            MinecraftModMetadataReader.ReadDirectory(modsDirectory));
        int repaired = 0;
        int changed = 0;
        using CompositeCommunityResourceCatalog catalog = new();
        using HttpClient downloader = new() { Timeout = TimeSpan.FromMinutes(5) };
        downloader.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-N/1.0");
        for (int index = 0; index < dependencies.Count; index++)
        {
            MinecraftMissingDependency dependency = dependencies[index];
            cancellationToken.ThrowIfCancellationRequested();
            await Dispatcher.UIThread.InvokeAsync(() =>
                context.LaunchPage.UpdateRepairStep(index + 1, dependencies.Count));
            ModDownloadResult result = await DownloadMissingDependencyAsync(
                    catalog,
                    downloader,
                    dependency,
                    gameVersion,
                    loader,
                    modsDirectory,
                    transaction,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.Success)
                repaired++;
            if (result.Changed)
                changed++;
        }
        return new MinecraftRepairExecutionResult(
            repaired == dependencies.Count
                ? $"自动修复完成：已安装 {repaired} 个前置模组，请重新启动游戏。"
                : $"自动修复完成：已安装 {repaired}/{dependencies.Count} 个前置模组。",
            repaired != dependencies.Count,
            changed > 0);
    }

    private static async Task<MinecraftRepairExecutionResult> RepairRequestedModAsync(
        RunningGameContext context,
        string gameDirectory,
        string modId,
        string? requestedVersion,
        bool updateExisting,
        MinecraftRepairTransaction transaction,
        CancellationToken cancellationToken)
    {
        string modsDirectory = Path.Combine(gameDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);
        IReadOnlyList<MinecraftModMetadata> installed = MinecraftModMetadataReader.ReadDirectory(modsDirectory);
        MinecraftModMetadata? current = installed.FirstOrDefault(mod =>
            string.Equals(mod.Id, modId, StringComparison.OrdinalIgnoreCase));
        if (updateExisting && current is null)
            return new MinecraftRepairExecutionResult($"未找到要更新的已安装模组：{modId}。", true);

        string gameVersion = MinecraftVersionJsonInspector.Read(context.Instance).MinecraftVersionId;
        string loader = ResolveCommunityLoader(context.Instance, installed);
        CommunitySearchOptions options = new(
            CommunityResourceSort.Relevance,
            GameVersion: gameVersion,
            Loader: loader,
            Source: CommunityResourceSource.All);
        using CompositeCommunityResourceCatalog catalog = new();
        IReadOnlyList<CommunityResourceEntry> projects = await catalog.SearchAsync(
                CommunityResourceCategory.Mod,
                modId,
                options,
                cancellationToken)
            .ConfigureAwait(false);
        CommunityResourceEntry? project = projects
            .OrderBy(entry => string.Equals(entry.ProjectId, modId, StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(entry.Slug, modId, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(static entry => entry.Downloads)
            .FirstOrDefault();
        if (project is null)
            return new MinecraftRepairExecutionResult($"社区资源中未找到模组：{modId}。", true);

        IReadOnlyList<CommunityResourceVersion> versions = await catalog.GetVersionsAsync(
                project,
                options,
                cancellationToken)
            .ConfigureAwait(false);
        CommunityResourceVersion? version = string.IsNullOrWhiteSpace(requestedVersion)
            ? versions.OrderByDescending(static item => item.PublishedAt).FirstOrDefault()
            : versions.FirstOrDefault(item =>
                string.Equals(item.VersionId, requestedVersion, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.VersionNumber, requestedVersion, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Name, requestedVersion, StringComparison.OrdinalIgnoreCase));
        if (version is null)
            return new MinecraftRepairExecutionResult(
                $"未找到 {project.DisplayTitle} 的目标版本 {requestedVersion ?? "(最新兼容版)"}。",
                true);
        CommunityResourceDownloadFile? file = version.Files.FirstOrDefault(candidate =>
                                                  candidate.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                                              ?? (version.Files.Count > 0 ? version.Files[0] : null);
        if (file is null)
            return new MinecraftRepairExecutionResult("目标模组版本没有可下载文件。", true);

        if (current is not null &&
            File.Exists(current.FilePath) &&
            string.Equals(current.Version, version.VersionNumber, StringComparison.OrdinalIgnoreCase))
        {
            return new MinecraftRepairExecutionResult(
                $"{project.DisplayTitle} 已经是目标版本 {version.VersionNumber}，没有需要修改的文件。",
                false,
                false);
        }

        await Dispatcher.UIThread.InvokeAsync(() => context.LaunchPage.ShowRepairWorkflow(
            AvaloniaLocalizationManager.GetText("Crash.Model.Title", "正在调用模型"),
            updateExisting ? "正在更新模组" : "正在下载模组",
            0.96d,
            project.DisplayTitle,
            context.Instance));
        string targetPath = Path.Combine(modsDirectory, SanitizeFileName(file.FileName));
        await transaction.BackupFileAsync(targetPath, cancellationToken).ConfigureAwait(false);
        foreach (MinecraftModMetadata conflict in installed.Where(mod =>
                     string.Equals(mod.Id, modId, StringComparison.OrdinalIgnoreCase) &&
                     !mod.FilePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)))
        {
            string disabledPath = CreateDisabledModPath(conflict.FilePath);
            await transaction.BackupFileAsync(conflict.FilePath, cancellationToken).ConfigureAwait(false);
            await transaction.BackupFileAsync(disabledPath, cancellationToken).ConfigureAwait(false);
            File.Move(conflict.FilePath, disabledPath);
        }

        string temporaryPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".PCLDownloading";
        try
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-N/1.0");
            using HttpResponseMessage response = await client.GetAsync(
                    file.Url,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using FileStream target = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            target.Close();
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
        return new MinecraftRepairExecutionResult(
            updateExisting
                ? $"已将 {project.DisplayTitle} 更新至 {version.VersionNumber}。"
                : $"已下载 {project.DisplayTitle} {version.VersionNumber}。",
            false,
            true);
    }

    private static async Task<MinecraftRepairExecutionResult> DisableRequestedModAsync(
        string gameDirectory,
        string modId,
        MinecraftRepairTransaction transaction,
        CancellationToken cancellationToken)
    {
        string modsDirectory = Path.Combine(gameDirectory, "mods");
        MinecraftModMetadata? metadata = MinecraftModMetadataReader.ReadDirectory(modsDirectory)
            .FirstOrDefault(mod => string.Equals(mod.Id, modId, StringComparison.OrdinalIgnoreCase) &&
                                   !mod.FilePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase));
        if (metadata is null)
            return new MinecraftRepairExecutionResult($"未找到可禁用的模组：{modId}。", true);
        string disabledPath = CreateDisabledModPath(metadata.FilePath);
        await transaction.BackupFileAsync(metadata.FilePath, cancellationToken).ConfigureAwait(false);
        await transaction.BackupFileAsync(disabledPath, cancellationToken).ConfigureAwait(false);
        File.Move(metadata.FilePath, disabledPath);
        return new MinecraftRepairExecutionResult(
            $"已禁用模组 {metadata.Name}（{metadata.Id}），将尝试重新启动。",
            false,
            true);
    }

    private static string CreateDisabledModPath(string path)
    {
        string candidate = path + ".disabled";
        for (int index = 2; File.Exists(candidate); index++)
            candidate = path + "." + index.ToString(CultureInfo.InvariantCulture) + ".disabled";
        return candidate;
    }

    private static string ResolveCommunityLoader(
        LaunchInstanceInfo instance,
        IReadOnlyList<MinecraftModMetadata> installedMods)
    {
        string? metadataLoader = installedMods.Select(static mod => mod.Loader)
            .FirstOrDefault(loader => !string.Equals(loader, "unknown", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(metadataLoader))
            return metadataLoader;
        IReadOnlyList<string> libraries = MinecraftVersionJsonInspector.Read(instance).Libraries;
        if (libraries.Any(library => library.Contains("quilt-loader", StringComparison.OrdinalIgnoreCase)))
            return "quilt";
        if (libraries.Any(library => library.Contains("fabric-loader", StringComparison.OrdinalIgnoreCase)))
            return "fabric";
        if (libraries.Any(library => library.Contains("neoforged", StringComparison.OrdinalIgnoreCase)))
            return "neoforge";
        return "forge";
    }

    private static async Task<MinecraftRepairExecutionResult> SelectCompatibleJavaAfterFaultAsync(
        RunningGameContext context,
        MinecraftRepairTransaction transaction,
        CancellationToken cancellationToken)
    {
        MinecraftLaunchProfile profile = MinecraftLaunchCoordinator.BuildLaunchProfile(context.Instance);
        JavaRequirementResolution requirement = JavaRuntimeRequirementResolver.Resolve(profile);
        if (!requirement.Success)
            return new MinecraftRepairExecutionResult(requirement.Detail ?? "无法解析 Java 要求。", true);
        InstanceMetadata current = await InstanceMetadataStore.LoadAsync(
                context.Instance.InstanceDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<PCL.Domain.Minecraft.Java.JavaRuntimeCandidate> candidates =
            await JavaRuntimeCatalog.LoadAsync(context.Settings, cancellationToken).ConfigureAwait(false);
        PCL.Domain.Minecraft.Java.JavaRuntimeCandidate? best = JavaRuntimeCatalog.SelectBest(
            candidates.Where(candidate => !string.Equals(
                candidate.Installation.JavaExecutablePath,
                current.SelectedJavaPath,
                StringComparison.OrdinalIgnoreCase)),
            requirement.Range);
        if (best is null)
            return new MinecraftRepairExecutionResult("没有找到另一套兼容且已启用的 Java。", true);
        await transaction.BackupFileAsync(
                InstanceMetadataStore.GetMetadataPath(context.Instance.InstanceDirectory),
                cancellationToken)
            .ConfigureAwait(false);
        await InstanceMetadataStore.UpdateAsync(
                context.Instance.InstanceDirectory,
                metadata => metadata with
                {
                    JavaSelectionMode = 2,
                    SelectedJavaPath = best.Installation.JavaExecutablePath
                },
                cancellationToken)
            .ConfigureAwait(false);
        return new MinecraftRepairExecutionResult(
            $"已切换至 Java {best.Installation.MajorVersion}：{best.Installation.JavaExecutablePath}",
            false,
            true);
    }

    private static async Task<MinecraftRepairExecutionResult> DownloadCompatibleJavaAfterFaultAsync(
        RunningGameContext context,
        MinecraftRepairTransaction transaction,
        CancellationToken cancellationToken)
    {
        MinecraftLaunchProfile profile = MinecraftLaunchCoordinator.BuildLaunchProfile(context.Instance);
        JavaRequirementResolution requirement = JavaRuntimeRequirementResolver.Resolve(profile);
        if (!requirement.Success)
            return new MinecraftRepairExecutionResult(requirement.Detail ?? "无法解析 Java 要求。", true);
        JavaRuntimeAcquisitionDecision acquisition = JavaRuntimeAcquisitionPlanner.Plan(requirement, profile.HasForge);
        if (!acquisition.CanAutoDownload || string.IsNullOrWhiteSpace(acquisition.DownloadComponent))
            return new MinecraftRepairExecutionResult("该版本的 Java 要求不能由启动器安全自动下载。", true);
        DefaultPlatformPathProvider paths = new();
        string runtimeRoot = JavaRuntimeInstaller.GetDefaultRuntimeRoot(paths);
        HashSet<string> existingRuntimeDirectories = Directory.Exists(runtimeRoot)
            ? Directory.EnumerateDirectories(runtimeRoot).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using HttpJavaRuntimeMetadataProvider metadataProvider = new();
        JavaRuntimeInstaller installer = new(metadataProvider);
        Progress<JavaRuntimeInstallProgress> progress = new(update => Dispatcher.UIThread.Post(() =>
            context.LaunchPage.ShowRepairWorkflow(
                AvaloniaLocalizationManager.GetText("Crash.Model.Title", "正在调用模型"),
                update.Stage,
                0.94d + (update.Progress * 0.05d),
                update.Detail,
                context.Instance)));
        string javaPath = await installer.InstallAsync(
                acquisition.DownloadComponent,
                runtimeRoot,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        string? installedRuntimeDirectory = FindTopLevelDirectory(runtimeRoot, javaPath);
        if (installedRuntimeDirectory is not null && !existingRuntimeDirectories.Contains(installedRuntimeDirectory))
            transaction.TrackCreatedDirectory(installedRuntimeDirectory);
        await transaction.BackupFileAsync(
                InstanceMetadataStore.GetMetadataPath(context.Instance.InstanceDirectory),
                cancellationToken)
            .ConfigureAwait(false);
        await InstanceMetadataStore.UpdateAsync(
                context.Instance.InstanceDirectory,
                metadata => metadata with { JavaSelectionMode = 2, SelectedJavaPath = javaPath },
                cancellationToken)
            .ConfigureAwait(false);
        return new MinecraftRepairExecutionResult(
            $"已下载并选择兼容 Java：{javaPath}",
            false,
            true);
    }

    private static string? FindTopLevelDirectory(string rootDirectory, string childPath)
    {
        string root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string current = Path.GetFullPath(childPath);
        if (!current.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;
        string relative = Path.GetRelativePath(root, current);
        string? first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(first) ? null : Path.Combine(root, first);
    }

    private async Task<MinecraftRepairExecutionResult> ReinstallVersionAndUpdateLoaderAsync(
        RunningGameContext context,
        MinecraftAiRepairSuggestion? suggestion,
        MinecraftRepairTransaction transaction,
        CancellationToken cancellationToken)
    {
        MinecraftVersionJsonInfo info = MinecraftVersionJsonInspector.Read(context.Instance);
        (MinecraftLoaderKind Kind, string Version)? loader = DetectInstalledLoader(info.Libraries);
        if (loader is null || string.Equals(context.Instance.Name, info.MinecraftVersionId, StringComparison.OrdinalIgnoreCase))
            return new MinecraftRepairExecutionResult("当前版本没有可安全原位更新的模组加载器。", true);
        MinecraftLoaderMetadataService metadataService = new();
        IReadOnlyList<MinecraftLoaderVersionEntry> candidates = await metadataService.GetLoaderVersionsAsync(
                loader.Value.Kind,
                info.MinecraftVersionId,
                cancellationToken)
            .ConfigureAwait(false);
        string? requested = suggestion?.Parameters.LoaderVersion;
        MinecraftLoaderVersionEntry? target = !string.IsNullOrWhiteSpace(requested)
            ? candidates.FirstOrDefault(candidate => string.Equals(candidate.Version, requested, StringComparison.OrdinalIgnoreCase))
            : candidates.FirstOrDefault(static candidate => candidate.Stable) ??
              (candidates.Count > 0 ? candidates[0] : null);
        if (target is null)
            return new MinecraftRepairExecutionResult("未找到兼容的加载器更新版本。", true);
        IReadOnlyList<MinecraftVersionManifestEntry> manifest = await _minecraftInstallService.GetVersionManifestAsync(
                context.Settings.DownloadSource != DownloadSourcePreference.MirrorOnly,
                cancellationToken)
            .ConfigureAwait(false);
        MinecraftVersionManifestEntry? vanilla = manifest.FirstOrDefault(entry =>
            string.Equals(entry.Id, info.MinecraftVersionId, StringComparison.OrdinalIgnoreCase));
        if (vanilla is null)
            return new MinecraftRepairExecutionResult("无法取得基础 Minecraft 版本元数据。", true);

        await transaction.BackupFileAsync(context.Instance.VersionJsonPath, cancellationToken).ConfigureAwait(false);
        await transaction.BackupFileAsync(
                Path.Combine(context.Instance.InstanceDirectory, context.Instance.Name + ".jar"),
                cancellationToken)
            .ConfigureAwait(false);
        await _minecraftInstallService.InstallAsync(
                new MinecraftInstallRequest
                {
                    VersionId = context.Instance.Name,
                    BaseVersionId = info.MinecraftVersionId,
                    VersionJsonUrl = vanilla.Url,
                    MinecraftRootDirectory = GetMinecraftRootFromInstance(context.Instance),
                    PreferOfficialSource = context.Settings.DownloadSource != DownloadSourcePreference.MirrorOnly,
                    Loader = new MinecraftLoaderInstallRequest(loader.Value.Kind, target.Version),
                    ReplaceExistingVersion = true,
                    JavaExecutablePath = ResolvePreferredJavaExecutablePath(forceConsole: true)
                },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new MinecraftRepairExecutionResult(
            $"已重新安装版本，并将 {loader.Value.Kind} 从 {loader.Value.Version} 更新至 {target.Version}。",
            false,
            true);
    }

    private static (MinecraftLoaderKind Kind, string Version)? DetectInstalledLoader(IReadOnlyList<string> libraries)
    {
        (MinecraftLoaderKind Kind, string[] Needles)[] candidates =
        [
            (MinecraftLoaderKind.NeoForge, ["net.neoforged:neoforge", "net.neoforged:forge"]),
            (MinecraftLoaderKind.Forge, ["net.minecraftforge:forge"]),
            (MinecraftLoaderKind.Quilt, ["quilt-loader"]),
            (MinecraftLoaderKind.LegacyFabric, ["legacyfabric", "legacy-fabric"]),
            (MinecraftLoaderKind.Fabric, ["fabric-loader"]),
            (MinecraftLoaderKind.Cleanroom, ["cleanroom"])
        ];
        foreach ((MinecraftLoaderKind kind, string[] needles) in candidates)
        {
            string? version = MinecraftLoaderLibraryDetector.DetectVersion(libraries, needles);
            if (!string.IsNullOrWhiteSpace(version))
                return (kind, version);
        }
        return null;
    }

    internal static bool IsAutomaticallyExecutableRepairForTest(MinecraftRepairActionKind action) =>
        IsAutomaticallyExecutableRepair(action);

    internal static string DescribeAiRepairStepForTest(MinecraftRepairActionKind action) =>
        DescribeAiRepairStep(action, new MinecraftAiRepairParameters());

    private static bool IsAutomaticallyExecutableRepair(MinecraftRepairActionKind action) => action is
        MinecraftRepairActionKind.RepairVersionFiles or
        MinecraftRepairActionKind.ReextractNatives or
        MinecraftRepairActionKind.InstallMissingModDependencies or
        MinecraftRepairActionKind.DownloadMod or
        MinecraftRepairActionKind.DisableMod or
        MinecraftRepairActionKind.UpdateMod or
        MinecraftRepairActionKind.SelectCompatibleJava or
        MinecraftRepairActionKind.DownloadCompatibleJava or
        MinecraftRepairActionKind.ReinstallVersionAndUpdateLoader or
        MinecraftRepairActionKind.DisableExperimentalJvmHost;

    private static async Task<MinecraftLaunchFaultReport?> AwaitFaultReportAsync(
        Task<MinecraftLaunchFaultReport?>? faultReportTask,
        CancellationToken cancellationToken)
    {
        if (faultReportTask is null)
            return null;
        Task completed = await Task.WhenAny(
                faultReportTask,
                Task.Delay(TimeSpan.FromSeconds(1), cancellationToken))
            .ConfigureAwait(false);
        return ReferenceEquals(completed, faultReportTask)
            ? await faultReportTask.ConfigureAwait(false)
            : null;
    }

    private Task<bool> ConfirmAiRepairActionAsync(
        MinecraftAiRepairSuggestion suggestion,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
            completion);
        string target = string.Join(
            "\n",
            suggestion.RepairSteps.Select((step, index) =>
                $"{index + 1}. {DescribeAiRepairStep(step.Action, step.Parameters)}" +
                (string.IsNullOrWhiteSpace(step.Rationale) ? string.Empty : $"\n   依据：{step.Rationale}")));
        Dispatcher.UIThread.Post(() => ShowConfirmDialog(
            AvaloniaLocalizationManager.GetText("Crash.Model.Confirm.Title", "模型请求执行修复"),
            $"AI 修复模型生成了以下链式修复计划：\n\n{target}\n\n可信度：{suggestion.Confidence:P0}\n\n" +
            "每一步都会由启动器重新验证参数并记录到同一个可回滚事务中；模型不会直接访问网络或文件。是否执行？",
            confirmed =>
            {
                registration.Dispose();
                completion.TrySetResult(confirmed);
            },
            AvaloniaLocalizationManager.GetText("Crash.Model.Confirm.Execute", "执行修复"),
            AvaloniaLocalizationManager.GetText("Crash.Model.Confirm.Decline", "不执行"),
            isWarn: true));
        return completion.Task;
    }

    private static string DescribeAiRepairStep(
        MinecraftRepairActionKind action,
        MinecraftAiRepairParameters parameters) => action switch
        {
            MinecraftRepairActionKind.DownloadMod =>
                $"下载模组 {parameters.ModId} {parameters.ModVersion}",
            MinecraftRepairActionKind.DisableMod => $"禁用模组 {parameters.ModId}",
            MinecraftRepairActionKind.UpdateMod =>
                $"将模组 {parameters.ModId} 更新至 {parameters.ModVersion}",
            MinecraftRepairActionKind.DisableExperimentalJvmHost =>
                "关闭实验性 Jvm.NET Host，并改用传统 Java 进程启动",
            MinecraftRepairActionKind.SelectCompatibleJava => "切换至另一套已安装的兼容 Java",
            MinecraftRepairActionKind.DownloadCompatibleJava => "下载并选择兼容 Java",
            MinecraftRepairActionKind.ReinstallVersionAndUpdateLoader => "重新安装版本并更新模组加载器",
            MinecraftRepairActionKind.RepairVersionFiles => "重新校验并补全 Minecraft 版本文件",
            MinecraftRepairActionKind.ReextractNatives => "重新生成 Minecraft Natives",
            MinecraftRepairActionKind.InstallMissingModDependencies => "下载缺失的前置模组",
            _ => action.ToString()
        };

    private async Task RestartMinecraftAfterRepairAsync(
        RunningGameContext context,
        MinecraftRepairSession session,
        string repairMessage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            context.LaunchPage.ShowRepairWorkflow(
                session.Attempt == MinecraftRepairAttempt.ModelApplied
                    ? AvaloniaLocalizationManager.GetText("Crash.Model.Title", "正在调用模型")
                    : AvaloniaLocalizationManager.GetText("Crash.Repair.Title", "正在修补 Minecraft"),
                AvaloniaLocalizationManager.GetText(
                    "Crash.Repair.Stage.Restart",
                    "修复完成，正在重启 Minecraft"),
                1d,
                repairMessage,
                context.Instance);
            _launchRight?.AppendLog(repairMessage + " 正在自动重启 Minecraft。");
        });
        await StartMinecraftAsync(
                context.LaunchPage,
                context.Instance,
                context.WorldName,
                context.ServerAddress,
                session)
            .ConfigureAwait(false);
    }

    private async Task FinishFailedRepairAsync(
        RunningGameContext context,
        MinecraftRepairSession session,
        MinecraftLaunchFaultReport fault,
        string analysisMarkdown,
        string failure,
        string gameDirectory,
        IReadOnlyList<string> crashLines)
    {
        string rollbackMessage;
        if (!session.Transaction.HasChanges)
        {
            rollbackMessage = "本轮仅完成错误分析，未修改任何 Minecraft 文件。";
        }
        else
        {
            try
            {
                await session.Transaction.RollbackAsync().ConfigureAwait(false);
                rollbackMessage = "本轮修复更改已回滚。";
            }
            catch (Exception rollbackException)
            {
                DesktopFileLog.Error("MinecraftRepair", "回滚 Minecraft 修复更改失败。", rollbackException);
                rollbackMessage = "回滚部分修复更改失败：" + rollbackException.Message;
            }
        }
        string[] recentCrashFiles = FindRecentCrashFiles(gameDirectory);
        string? primaryLog = recentCrashFiles.Length > 0 ? recentCrashFiles[0] : null;
        string outcome = failure + " " + rollbackMessage;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!IsVisible)
                Show();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
            context.LaunchPage.ShowRepairWorkflow(
                AvaloniaLocalizationManager.GetText("Crash.MinecraftErrorTitle", "Minecraft 出错"),
                "自动修复未能解决问题，正在生成错误报告",
                1d,
                fault.Code.ToString(),
                context.Instance);
            _launchRight?.AppendLog(outcome);
            ShowHint(outcome, critical: true);
            ShowMinecraftCrashDialog(
                context,
                fault,
                analysisMarkdown,
                outcome,
                gameDirectory,
                primaryLog,
                crashLines);
        });
    }

    private void ShowMinecraftCrashDialog(
        RunningGameContext context,
        MinecraftLaunchFaultReport fault,
        string analysisMarkdown,
        string repairOutcome,
        string gameDirectory,
        string? primaryLog,
        IReadOnlyList<string> crashLines)
    {
        string markdown = $"{analysisMarkdown.Trim()}\n\n---\n\n" +
                          $"**定位节点：** `{fault.Subsystem}/{fault.Stage}`  \n" +
                          $"**错误代码：** `{fault.Code}`" +
                          (string.IsNullOrWhiteSpace(fault.LastClassName)
                              ? string.Empty
                              : $"  \n**最后关键类：** `{fault.LastClassName}`") +
                          $"\n\n### 自动处理\n\n{repairOutcome}";
        ShowMarkdownDialog(
            AvaloniaLocalizationManager.GetText("Crash.MinecraftErrorTitle", "Minecraft 出错"),
            markdown,
            result =>
            {
                try
                {
                    if (result == 2)
                        OpenExistingPath(primaryLog ?? gameDirectory);
                    else if (result == 3)
                        _ = ExportMinecraftCrashReportAsync(
                            context,
                            fault,
                            markdown,
                            gameDirectory,
                            crashLines);
                }
                finally
                {
                    if (context.LaunchPage.IsLaunchInProgress)
                        context.LaunchPage.PageChangeToLogin();
                }
            },
            AvaloniaLocalizationManager.GetText("Common.Action.Confirm", "知道了"),
            AvaloniaLocalizationManager.GetText("Crash.Action.ViewLog", "查看日志"),
            AvaloniaLocalizationManager.GetText("Crash.Action.ExportReport", "导出报告"),
            isWarn: true);
    }

    private async Task ExportMinecraftCrashReportAsync(
        RunningGameContext context,
        MinecraftLaunchFaultReport fault,
        string analysisMarkdown,
        string gameDirectory,
        IReadOnlyList<string> crashLines)
    {
        string suggestedName = "Minecraft-Error-Report-" +
                               DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
                               ".zip";
        string? targetPath = await PickSaveFilePathAsync(
                AvaloniaLocalizationManager.GetText("Crash.Report.Export.Title", "选择错误报告保存位置"),
                suggestedName,
                new FilePickerFileType("ZIP") { Patterns = ["*.zip"] })
            .ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(targetPath))
            return;

        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "PCL-N",
            "CrashReport",
            Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            string structured =
                $"Code: {fault.Code}\nStage: {fault.Stage}\nSubsystem: {fault.Subsystem}\n" +
                $"ExceptionType: {fault.ExceptionType}\nMessage: {fault.Message}\n" +
                $"LastClassName: {fault.LastClassName}\nTimestamp: {fault.Timestamp:O}\n" +
                $"AllowedActions: {string.Join(", ", fault.AllowedActions)}\n";
            await File.WriteAllTextAsync(
                    Path.Combine(temporaryDirectory, "分析结果.md"),
                    PortableLog.Redact(analysisMarkdown),
                    Encoding.UTF8)
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(
                    Path.Combine(temporaryDirectory, "结构化错误.txt"),
                    PortableLog.Redact(structured),
                    Encoding.UTF8)
                .ConfigureAwait(false);
            await File.WriteAllLinesAsync(
                    Path.Combine(temporaryDirectory, "已收集日志片段.txt"),
                    crashLines.Select(PortableLog.Redact),
                    Encoding.UTF8)
                .ConfigureAwait(false);

            List<string> reportFiles =
            [
                .. FindRecentCrashFiles(gameDirectory),
                context.Instance.VersionJsonPath,
                Path.Combine(gameDirectory, "LatestLaunch-PCLN.bat"),
                DesktopFileLog.CurrentLogPath
            ];
            HashSet<string> usedNames = new(StringComparer.OrdinalIgnoreCase);
            foreach (string sourcePath in reportFiles
                         .Where(File.Exists)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                FileInfo info = new(sourcePath);
                if (info.Length > 16L * 1024L * 1024L)
                    continue;
                string name = Path.GetFileName(sourcePath);
                if (!usedNames.Add(name))
                    name = Path.GetFileNameWithoutExtension(name) + "-" + usedNames.Count + Path.GetExtension(name);
                await using FileStream stream = new(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    64 * 1024,
                    useAsync: true);
                using StreamReader reader = new(stream, detectEncodingFromByteOrderMarks: true);
                string content = await reader.ReadToEndAsync().ConfigureAwait(false);
                await File.WriteAllTextAsync(
                        Path.Combine(temporaryDirectory, name),
                        PortableLog.Redact(content),
                        Encoding.UTF8)
                    .ConfigureAwait(false);
            }

            if (File.Exists(targetPath))
                File.Delete(targetPath);
            ZipFile.CreateFromDirectory(temporaryDirectory, targetPath, CompressionLevel.SmallestSize, includeBaseDirectory: false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ShowHint(AvaloniaLocalizationManager.GetText("Crash.Report.Exported", "错误报告已导出"));
                OpenExistingPath(targetPath);
            });
        }
        catch (Exception ex)
        {
            DesktopFileLog.Error("CrashReport", "导出 Minecraft 错误报告失败。", ex);
            await Dispatcher.UIThread.InvokeAsync(() =>
                ShowTextDialog(
                    AvaloniaLocalizationManager.GetText("Crash.Report.Export.Failed.Title", "导出错误报告失败"),
                    ex.Message));
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
                Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static string BuildConventionalCrashAnalysis(
        MinecraftLaunchFaultReport fault,
        IReadOnlyList<MinecraftMissingDependency> dependencies)
    {
        string reason = fault.Code switch
        {
            MinecraftLaunchFaultCode.JavaRuntimeMissing => "所选 Java 缺失，或无法加载 JVM 原生库。",
            MinecraftLaunchFaultCode.JavaRuntimeIncompatible => "当前 Java 版本与游戏或模组加载器要求不兼容。",
            MinecraftLaunchFaultCode.JvmInitializationFailed => "JVM 在 Minecraft 主类运行前初始化失败。",
            MinecraftLaunchFaultCode.MainClassMissing => "Minecraft 主类不存在，版本核心文件可能缺失或损坏。",
            MinecraftLaunchFaultCode.ClasspathDependencyMissing => "类路径中的游戏库缺失或损坏。",
            MinecraftLaunchFaultCode.AuthenticationFailed => "登录凭据或会话验证失败。",
            MinecraftLaunchFaultCode.SessionServiceUnavailable => "账户会话服务暂时不可用。",
            MinecraftLaunchFaultCode.NativeLibraryFailed => "LWJGL 或其他原生库加载失败。",
            MinecraftLaunchFaultCode.GraphicsInitializationFailed => "图形驱动、OpenGL/Vulkan 或游戏窗口初始化失败。",
            MinecraftLaunchFaultCode.ModLoaderBootstrapFailed => "模组加载器在引导阶段失败。",
            MinecraftLaunchFaultCode.ModConflict => "一个或多个模组、Mixin 或加载器组件发生冲突。",
            MinecraftLaunchFaultCode.MissingModDependency => "模组缺少必需前置或前置版本不正确。",
            MinecraftLaunchFaultCode.OutOfMemory => "Minecraft 可用内存不足，或 JVM 无法保留所需内存。",
            MinecraftLaunchFaultCode.FileLocked => "游戏文件正被其他进程占用。",
            MinecraftLaunchFaultCode.AccessDenied => "启动器或 Java 没有访问相关文件的权限。",
            _ => "常规错误分析器尚未识别出唯一原因。"
        };
        string dependencyText = dependencies.Count == 0
            ? string.Empty
            : "\n\n### 缺失前置\n\n" + string.Join(
                "\n",
                dependencies.Select(dependency =>
                    $"- `{dependency.ModId}`" +
                    (string.IsNullOrWhiteSpace(dependency.RequiredVersion)
                        ? string.Empty
                        : "，需要 " + dependency.RequiredVersion)));
        return $"### 常规错误分析\n\n{reason}\n\n**原始信息：** {fault.Message}{dependencyText}";
    }

    private static async Task<ModDownloadResult> DownloadMissingDependencyAsync(
        CompositeCommunityResourceCatalog catalog,
        HttpClient downloader,
        MinecraftMissingDependency dependency,
        string gameVersion,
        string loader,
        string modsDirectory,
        MinecraftRepairTransaction transaction,
        CancellationToken cancellationToken)
    {
        CommunitySearchOptions options = new(
            CommunityResourceSort.Relevance,
            GameVersion: gameVersion,
            Loader: loader,
            Source: CommunityResourceSource.All);
        IReadOnlyList<CommunityResourceEntry> entries = await catalog.SearchAsync(
                CommunityResourceCategory.Mod,
                dependency.ModId,
                options,
                cancellationToken)
            .ConfigureAwait(false);
        CommunityResourceEntry? entry = entries
            .OrderBy(candidate => GetDependencyMatchScore(candidate, dependency))
            .FirstOrDefault();
        if (entry is null && !string.Equals(dependency.Name, dependency.ModId, StringComparison.OrdinalIgnoreCase))
        {
            entries = await catalog.SearchAsync(
                    CommunityResourceCategory.Mod,
                    dependency.Name,
                    options,
                    cancellationToken)
                .ConfigureAwait(false);
            entry = entries.OrderBy(candidate => GetDependencyMatchScore(candidate, dependency)).FirstOrDefault();
        }
        if (entry is null)
            return new ModDownloadResult(false, false);

        CommunityResourceDownloadFile? file = await catalog.ResolveDownloadAsync(entry, options, cancellationToken)
            .ConfigureAwait(false);
        if (file is null)
            return new ModDownloadResult(false, false);
        string targetPath = Path.Combine(modsDirectory, SanitizeFileName(file.FileName));
        if (File.Exists(targetPath))
            return new ModDownloadResult(true, false);

        await transaction.BackupFileAsync(targetPath, cancellationToken).ConfigureAwait(false);

        string temporaryPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".PCLDownloading";
        try
        {
            using HttpResponseMessage response = await downloader.GetAsync(
                    file.Url,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (FileStream target = new(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             useAsync: true))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            if (MinecraftModMetadataReader.TryRead(temporaryPath, out MinecraftModMetadata? incoming) && incoming is not null)
            {
                foreach (MinecraftModMetadata conflict in MinecraftModMetadataReader.ReadDirectory(modsDirectory)
                             .Where(mod => string.Equals(mod.Id, incoming.Id, StringComparison.OrdinalIgnoreCase) &&
                                           !mod.FilePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)))
                {
                    string disabled = CreateDisabledModPath(conflict.FilePath);
                    await transaction.BackupFileAsync(conflict.FilePath, cancellationToken).ConfigureAwait(false);
                    await transaction.BackupFileAsync(disabled, cancellationToken).ConfigureAwait(false);
                    File.Move(conflict.FilePath, disabled);
                }
            }
            File.Move(temporaryPath, targetPath, overwrite: true);
            return new ModDownloadResult(true, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static int GetDependencyMatchScore(
        CommunityResourceEntry entry,
        MinecraftMissingDependency dependency)
    {
        if (string.Equals(entry.Slug, dependency.ModId, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (string.Equals(entry.Title, dependency.Name, StringComparison.OrdinalIgnoreCase))
            return 1;
        return 2;
    }

    private static async Task<IReadOnlyList<string>> ReadRecentCrashLinesAsync(
        string gameDirectory,
        CancellationToken cancellationToken)
    {
        List<string> lines = [];
        foreach (string path in FindRecentCrashFiles(gameDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo file = new(path);
            if (file.Length > 8L * 1024L * 1024L)
                continue;
            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                useAsync: true);
            using StreamReader reader = new(stream, detectEncodingFromByteOrderMarks: true);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                lines.Add(line);
        }
        return lines;
    }

    private static string[] FindRecentCrashFiles(string gameDirectory)
    {
        List<string> paths = [];
        string latestLog = Path.Combine(gameDirectory, "logs", "latest.log");
        if (File.Exists(latestLog))
            paths.Add(latestLog);
        string debugLog = Path.Combine(gameDirectory, "logs", "debug.log");
        if (File.Exists(debugLog))
            paths.Add(debugLog);
        string crashDirectory = Path.Combine(gameDirectory, "crash-reports");
        if (Directory.Exists(crashDirectory))
        {
            string? latestCrash = Directory.EnumerateFiles(crashDirectory, "*.txt", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (latestCrash is not null)
                paths.Insert(0, latestCrash);
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task<string> BuildMinecraftAiDetailedContextAsync(
        RunningGameContext context,
        string gameDirectory,
        IReadOnlyList<string> crashLines,
        IReadOnlyList<MinecraftModMetadata> installedMods,
        IReadOnlyList<MinecraftAiContextScope> requestedScopes,
        int maximumLength,
        CancellationToken cancellationToken)
    {
        HashSet<MinecraftAiContextScope> scopes = requestedScopes.ToHashSet();
        if (scopes.Count == 0)
            return string.Empty;

        Dictionary<MinecraftAiContextScope, double> weights = new()
        {
            [MinecraftAiContextScope.Environment] = 1d,
            [MinecraftAiContextScope.Instance] = 4d,
            [MinecraftAiContextScope.CrashReports] = 4d,
            [MinecraftAiContextScope.RuntimeLogs] = 5d,
            [MinecraftAiContextScope.LaunchMethod] = 1.5d,
            [MinecraftAiContextScope.LoginMethod] = 1d
        };
        double totalWeight = scopes.Sum(scope => weights[scope]);
        int SectionBudget(MinecraftAiContextScope scope) => Math.Max(
            512,
            (int)((maximumLength - 512L) * weights[scope] / totalWeight));

        StringBuilder result = new();
        foreach (MinecraftAiContextScope scope in Enum.GetValues<MinecraftAiContextScope>())
        {
            if (!scopes.Contains(scope))
                continue;
            cancellationToken.ThrowIfCancellationRequested();
            int budget = SectionBudget(scope);
            string content = scope switch
            {
                MinecraftAiContextScope.Environment => BuildMinecraftAiEnvironmentContext(context, gameDirectory),
                MinecraftAiContextScope.Instance => await BuildMinecraftAiInstanceContextAsync(
                        context,
                        gameDirectory,
                        installedMods,
                        cancellationToken)
                    .ConfigureAwait(false),
                MinecraftAiContextScope.CrashReports => await BuildMinecraftAiCrashReportContextAsync(
                        context,
                        gameDirectory,
                        budget,
                        cancellationToken)
                    .ConfigureAwait(false),
                MinecraftAiContextScope.RuntimeLogs => await BuildMinecraftAiRuntimeLogContextAsync(
                        context,
                        gameDirectory,
                        crashLines,
                        budget,
                        cancellationToken)
                    .ConfigureAwait(false),
                MinecraftAiContextScope.LaunchMethod => BuildMinecraftAiLaunchMethodContext(context),
                MinecraftAiContextScope.LoginMethod => BuildMinecraftAiLoginMethodContext(context),
                _ => string.Empty
            };
            result.Append("\n[").Append(ToMinecraftAiScopeName(scope)).AppendLine("]")
                .AppendLine(MinecraftAiRepairAdvisor.BoundDetailedContext(content, budget));
        }

        string bounded = MinecraftAiRepairAdvisor.BoundDetailedContext(result.ToString().Trim(), maximumLength);
        DesktopFileLog.Info(
            "MinecraftRepairAI",
            $"已提供脱敏只读上下文：{string.Join(", ", scopes)}；字符数={bounded.Length}。");
        return bounded;
    }

#pragma warning disable CA1305 // Diagnostic text is serialized with explicit invariant values where applicable.
    private static string BuildMinecraftAiEnvironmentContext(RunningGameContext context, string gameDirectory)
    {
        using Process process = Process.GetCurrentProcess();
        StringBuilder value = new();
        value.AppendLine($"os={RuntimeInformation.OSDescription}")
            .AppendLine($"osArchitecture={RuntimeInformation.OSArchitecture}")
            .AppendLine($"processArchitecture={RuntimeInformation.ProcessArchitecture}")
            .AppendLine($"framework={RuntimeInformation.FrameworkDescription}")
            .AppendLine($"is64BitProcess={Environment.Is64BitProcess}")
            .AppendLine($"logicalProcessors={Environment.ProcessorCount}")
            .AppendLine($"launcherWorkingSetMiB={process.WorkingSet64 / 1024L / 1024L}")
            .AppendLine($"managedHeapMiB={GC.GetTotalMemory(false) / 1024L / 1024L}")
            .AppendLine($"culture={CultureInfo.CurrentCulture.Name}")
            .AppendLine($"uiCulture={CultureInfo.CurrentUICulture.Name}")
            .AppendLine($"timeZone={TimeZoneInfo.Local.Id}")
            .AppendLine("environmentVariables=not exposed because they may contain credentials");
        return RedactMinecraftAiContext(value.ToString(), context, gameDirectory);
    }

    private static async Task<string> BuildMinecraftAiInstanceContextAsync(
        RunningGameContext context,
        string gameDirectory,
        IReadOnlyList<MinecraftModMetadata> installedMods,
        CancellationToken cancellationToken)
    {
        InstanceMetadata metadata = await InstanceMetadataStore.LoadAsync(
                context.Instance.InstanceDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        MinecraftVersionJsonInfo version = MinecraftVersionJsonInspector.Read(context.Instance);
        StringBuilder value = new();
        value.AppendLine($"instanceName={context.Instance.Name}")
            .AppendLine($"minecraftVersion={version.MinecraftVersionId}")
            .AppendLine($"inheritsFrom={version.InheritsFrom ?? "none"}")
            .AppendLine($"loader={ResolveCommunityLoader(context.Instance, installedMods)}")
            .AppendLine($"description={metadata.Description}")
            .AppendLine($"customInfo={metadata.CustomInfo}")
            .AppendLine($"launchCount={metadata.LaunchCount}")
            .AppendLine($"modpackProjectId={metadata.ModpackProjectId}")
            .AppendLine($"modpackVersion={metadata.ModpackVersion}")
            .AppendLine($"instanceIsolation={metadata.InstanceIsolation}")
            .AppendLine($"disableAssetVerification={metadata.DisableAssetVerification}")
            .AppendLine($"ignoreJavaCompatibility={metadata.IgnoreJavaCompatibility}")
            .AppendLine($"renderer={metadata.Renderer}")
            .AppendLine($"javaSelectionMode={metadata.JavaSelectionMode}")
            .AppendLine($"memorySolution={metadata.MemorySolution}")
            .AppendLine($"customMemorySize={metadata.CustomMemorySize}")
            .AppendLine($"customJvmArgumentsConfigured={!string.IsNullOrWhiteSpace(metadata.JvmArguments)}")
            .AppendLine($"customGameArgumentsConfigured={!string.IsNullOrWhiteSpace(metadata.GameArguments)}")
            .AppendLine($"preLaunchCommandConfigured={!string.IsNullOrWhiteSpace(metadata.PreLaunchCommand)}")
            .AppendLine($"modsDirectoryFileCount={CountFilesSafely(Path.Combine(gameDirectory, "mods"))}")
            .AppendLine($"resourcePacksFileCount={CountFilesSafely(Path.Combine(gameDirectory, "resourcepacks"))}")
            .AppendLine($"shaderPacksFileCount={CountFilesSafely(Path.Combine(gameDirectory, "shaderpacks"))}")
            .AppendLine($"savesDirectoryCount={CountDirectoriesSafely(Path.Combine(gameDirectory, "saves"))}")
            .AppendLine("libraries:");
        foreach (string library in version.Libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            value.Append("- ").AppendLine(library);
        }
        value.AppendLine("installedModMetadata:");
        foreach (MinecraftModMetadata mod in installedMods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            value.Append("- file=").Append(Path.GetFileName(mod.FilePath))
                .Append("; id=").Append(mod.Id)
                .Append("; name=").Append(mod.Name)
                .Append("; version=").Append(mod.Version)
                .Append("; loader=").Append(mod.Loader)
                .Append("; dependencies=").AppendJoin(',', mod.Dependencies)
                .AppendLine();
        }
        return RedactMinecraftAiContext(value.ToString(), context, gameDirectory);
    }

    private static async Task<string> BuildMinecraftAiCrashReportContextAsync(
        RunningGameContext context,
        string gameDirectory,
        int budget,
        CancellationToken cancellationToken)
    {
        List<string> files = [];
        string crashDirectory = Path.Combine(gameDirectory, "crash-reports");
        try
        {
            if (Directory.Exists(crashDirectory))
            {
                files.AddRange(Directory.EnumerateFiles(crashDirectory, "*.txt", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Take(3));
            }
            files.AddRange(Directory.EnumerateFiles(gameDirectory, "hs_err_pid*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(2));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DesktopFileLog.Warn("MinecraftRepairAI", "枚举 Minecraft 崩溃报告失败。", ex);
        }
        return await ReadMinecraftAiDiagnosticFilesAsync(
                files,
                context,
                gameDirectory,
                budget,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<string> BuildMinecraftAiRuntimeLogContextAsync(
        RunningGameContext context,
        string gameDirectory,
        IReadOnlyList<string> crashLines,
        int budget,
        CancellationToken cancellationToken)
    {
        string[] files =
        [
            Path.Combine(gameDirectory, "logs", "latest.log"),
            Path.Combine(gameDirectory, "logs", "debug.log")
        ];
        string fileContent = await ReadMinecraftAiDiagnosticFilesAsync(
                files.Where(File.Exists),
                context,
                gameDirectory,
                Math.Max(512, budget * 3 / 4),
                cancellationToken)
            .ConfigureAwait(false);
        StringBuilder value = new(fileContent);
        if (crashLines.Count > 0)
        {
            value.AppendLine().AppendLine("--- captured launcher/runtime tail ---");
            foreach (string line in crashLines.TakeLast(160))
            {
                cancellationToken.ThrowIfCancellationRequested();
                value.AppendLine(RedactMinecraftAiContext(line, context, gameDirectory));
            }
        }
        return value.ToString();
    }

    private static string BuildMinecraftAiLaunchMethodContext(RunningGameContext context)
    {
        StringBuilder value = new();
        value.AppendLine($"launcherMode={(context.UsedExperimentalJvmHost ? "Jvm.NET lifecycle host" : "external Java process")}")
            .AppendLine($"javaExecutable={context.JavaExecutableName ?? "unknown"}")
            .AppendLine($"javaMajor={context.JavaMajorVersion?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}")
            .AppendLine($"maximumHeapMiB={context.MemoryMegabytes?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}")
            .AppendLine($"classpathEntryCount={context.ClasspathEntryCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}")
            .AppendLine($"vmArgumentCount={context.VmArgumentCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}")
            .AppendLine($"gameArgumentCount={context.GameArgumentCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}")
            .AppendLine($"launchTarget={(context.WorldName is not null ? "saved world" : context.ServerAddress is not null ? "multiplayer server" : "main menu")}")
            .AppendLine("rawArguments=not exposed because arguments may contain credentials");
        return value.ToString();
    }

    private static string BuildMinecraftAiLoginMethodContext(RunningGameContext context)
    {
        StringBuilder value = new();
        value.AppendLine($"loginMethod={context.LoginMethod ?? "unknown"}")
            .AppendLine($"authenticationServer={context.LoginServerHost ?? "official/default"}")
            .AppendLine($"identityBridge={(context.UsedExperimentalJvmHost ? "Jvm.NET local session bridge" : "traditional launcher authentication")}")
            .AppendLine("profileName=<redacted>")
            .AppendLine("uuid=<redacted>")
            .AppendLine("accessToken=<redacted>")
            .AppendLine("refreshToken=<redacted>");
        return value.ToString();
    }
#pragma warning restore CA1305

    private static async Task<string> ReadMinecraftAiDiagnosticFilesAsync(
        IEnumerable<string> paths,
        RunningGameContext context,
        string gameDirectory,
        int totalBudget,
        CancellationToken cancellationToken)
    {
        string[] existing = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (existing.Length == 0)
            return "no matching diagnostic files";
        int perFileBudget = Math.Max(512, totalBudget / existing.Length);
        StringBuilder result = new();
        foreach (string path in existing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo info = new(path);
            result.Append("--- ").Append(Path.GetFileName(path))
                .Append(" (").Append(info.Length).AppendLine(" bytes) ---");
            if (info.Length > 16L * 1024L * 1024L)
            {
                result.AppendLine("[file omitted because it exceeds 16 MiB]");
                continue;
            }
            try
            {
                await using FileStream stream = new(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    64 * 1024,
                    useAsync: true);
                using StreamReader reader = new(stream, detectEncodingFromByteOrderMarks: true);
                StringBuilder head = new();
                Queue<string> tail = new();
                int tailLength = 0;
                int headBudget = perFileBudget / 3;
                int tailBudget = perFileBudget - headBudget;
                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    string safe = RedactMinecraftAiContext(line, context, gameDirectory);
                    if (safe.Length > 1_200)
                        safe = safe[..1_200] + " [line truncated]";
                    if (head.Length < headBudget)
                    {
                        head.AppendLine(safe);
                        continue;
                    }
                    tail.Enqueue(safe);
                    tailLength += safe.Length + Environment.NewLine.Length;
                    while (tailLength > tailBudget && tail.TryDequeue(out string? removed))
                        tailLength -= removed.Length + Environment.NewLine.Length;
                }
                result.Append(head);
                if (tail.Count > 0)
                {
                    result.AppendLine("[middle of file omitted]");
                    foreach (string line in tail)
                        result.AppendLine(line);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Append("[unable to read: ").Append(ex.GetType().Name).AppendLine("]");
            }
        }
        return MinecraftAiRepairAdvisor.BoundDetailedContext(result.ToString(), totalBudget);
    }

    private static string RedactMinecraftAiContext(
        string? value,
        RunningGameContext context,
        string gameDirectory)
    {
        string result = PortableLog.Redact(value);
        List<(string Sensitive, string Replacement)> replacements =
        [
            (context.Instance.InstanceDirectory, "<instance-directory>"),
            (gameDirectory, "<game-directory>"),
            (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<user-home>")
        ];
        if (!string.IsNullOrWhiteSpace(context.ProfileUsername))
            replacements.Add((context.ProfileUsername, "<profile-name>"));
        if (!string.IsNullOrWhiteSpace(context.ProfileUuid))
        {
            replacements.Add((context.ProfileUuid, "<profile-uuid>"));
            replacements.Add((context.ProfileUuid.Replace("-", string.Empty, StringComparison.Ordinal), "<profile-uuid>"));
        }
        if (!string.IsNullOrWhiteSpace(context.JavaExecutablePathForRedaction))
            replacements.Add((context.JavaExecutablePathForRedaction, "<java-path>"));
        if (!string.IsNullOrWhiteSpace(context.WorldName))
            replacements.Add((context.WorldName, "<world-name>"));
        if (!string.IsNullOrWhiteSpace(context.ServerAddress))
            replacements.Add((context.ServerAddress, "<server-address>"));
        foreach ((string sensitive, string replacement) in replacements.DistinctBy(item => item.Sensitive,
                     StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(sensitive))
                result = result.Replace(sensitive, replacement, StringComparison.OrdinalIgnoreCase);
        }
        result = QuotedAbsolutePathPattern().Replace(result, "<local-path>");
        result = WindowsAbsolutePathPattern().Replace(result, "<local-path>");
        result = UnixAbsolutePathPattern().Replace(result, "<local-path>");
        return result;
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        "(?i)(?:\"[A-Z]:\\\\[^\"\\r\\n]+\"|'[A-Z]:\\\\[^'\\r\\n]+'|\"/(?:home|Users|tmp|var|opt|usr|mnt|media|run)/[^\"\\r\\n]+\"|'/(?:home|Users|tmp|var|opt|usr|mnt|media|run)/[^'\\r\\n]+')",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant |
        System.Text.RegularExpressions.RegexOptions.NonBacktracking)]
    private static partial System.Text.RegularExpressions.Regex QuotedAbsolutePathPattern();

    [System.Text.RegularExpressions.GeneratedRegex(
        "(?i)\\b[A-Z]:\\\\[^\\s\"',;|<>]+",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant |
        System.Text.RegularExpressions.RegexOptions.NonBacktracking)]
    private static partial System.Text.RegularExpressions.Regex WindowsAbsolutePathPattern();

    [System.Text.RegularExpressions.GeneratedRegex(
        "(?i)(?<![:/A-Z0-9_])/(?:home|Users|tmp|var|opt|usr|mnt|media|run)/[^\\s\"',;|<>]+",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex UnixAbsolutePathPattern();

    private static int CountFilesSafely(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).Count()
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    private static int CountDirectoriesSafely(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).Count()
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    private static string ToMinecraftAiScopeName(MinecraftAiContextScope scope) => scope switch
    {
        MinecraftAiContextScope.Environment => "environment",
        MinecraftAiContextScope.Instance => "instance",
        MinecraftAiContextScope.CrashReports => "crash_reports",
        MinecraftAiContextScope.RuntimeLogs => "runtime_logs",
        MinecraftAiContextScope.LaunchMethod => "launch_method",
        MinecraftAiContextScope.LoginMethod => "login_method",
        _ => scope.ToString()
    };

    private static string? ResolveLoginServerHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host
            : "custom authentication server";
    }

    internal static bool ShouldExecuteConventionalRepairDirectly(
        bool isFirstAttempt,
        bool automaticRepairEnabled,
        bool experimentalAiRepairEnabled) =>
        isFirstAttempt && automaticRepairEnabled && !experimentalAiRepairEnabled;

    private static string BuildRepairAttemptSummary(
        string source,
        string actions,
        MinecraftRepairExecutionResult result) =>
        $"来源={source}；动作={actions}；执行结果={result.Message}；" +
        $"实际修改文件={(result.MadeChanges ? "是" : "否")}；执行失败={(result.IsFailure ? "是" : "否")}。";

    private static string BuildFailedRepairFeedback(
        MinecraftRepairSession session,
        MinecraftLaunchFaultReport currentFault,
        int? processExitCode) =>
        FormatFailedRepairFeedback(
            session.LastRepairSummary,
            currentFault.Code,
            currentFault.Stage,
            processExitCode);

    internal static string FormatFailedRepairFeedback(
        string? previousRepairSummary,
        MinecraftLaunchFaultCode currentCode,
        string? currentStage,
        int? processExitCode)
    {
        string summary = string.IsNullOrWhiteSpace(previousRepairSummary)
            ? "上次修复内容未记录"
            : previousRepairSummary.Trim();
        return $"上次修复已执行，但修复后的重新启动仍然失败。上次修复：{summary}" +
               $" 本次失败：Code={currentCode}；Stage={currentStage ?? "Unknown"}；" +
               $"ExitCode={processExitCode?.ToString(CultureInfo.InvariantCulture) ?? "Unknown"}。" +
               "请结合本次新错误重新判断，并避免无依据重复上次修复。";
    }

    private sealed record MinecraftRepairExecutionResult(
        string Message,
        bool IsFailure,
        bool MadeChanges = false);

    private readonly record struct ModDownloadResult(bool Success, bool Changed);

    private sealed record RunningGameContext(
        LaunchInstanceInfo Instance,
        ILaunchHomeSurface LaunchPage,
        LauncherSettings Settings,
        Task<MinecraftLaunchFaultReport?>? FaultReport = null,
        string? NativesDirectory = null,
        string? WorldName = null,
        string? ServerAddress = null,
        MinecraftRepairSession? RepairSession = null,
        int? JavaMajorVersion = null,
        int? MemoryMegabytes = null,
        string? LoginMethod = null,
        string? LoginServerHost = null,
        string? ProfileUsername = null,
        string? ProfileUuid = null,
        bool UsedExperimentalJvmHost = false,
        string? JavaExecutableName = null,
        string? JavaExecutablePathForRedaction = null,
        int? ClasspathEntryCount = null,
        int? VmArgumentCount = null,
        int? GameArgumentCount = null,
        int? ProcessExitCode = null);

    private static int? TryReadMaximumHeapMegabytes(IEnumerable<string> arguments)
    {
        string? maximumHeap = arguments.FirstOrDefault(argument =>
            argument.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase));
        if (maximumHeap is null || maximumHeap.Length <= 4)
            return null;
        string value = maximumHeap[4..].Trim();
        long multiplier = 1;
        if (value.EndsWith('g') || value.EndsWith('G'))
        {
            multiplier = 1024;
            value = value[..^1];
        }
        else if (value.EndsWith('m') || value.EndsWith('M'))
        {
            value = value[..^1];
        }
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) &&
               parsed > 0 && parsed * multiplier <= int.MaxValue
            ? (int)(parsed * multiplier)
            : null;
    }

    private enum MinecraftRepairAttempt
    {
        None,
        ConventionalApplied,
        ModelApplied
    }

    private sealed class MinecraftRepairSession(LauncherSettings settings)
    {
        public LauncherSettings Settings { get; } = settings;

        public MinecraftRepairTransaction Transaction { get; } = new();

        public MinecraftRepairAttempt Attempt { get; set; }

        public string? LastModelAnalysis { get; set; }

        public string? LastRepairSummary { get; set; }
    }

    private Task<bool> ConfirmJavaDownloadAsync(string versionLabel, CancellationToken cancellationToken)
    {
        // Always Post so we never show+await a modal on the same UI stack frame (deadlock/freeze).
        TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
            tcs);

        Dispatcher.UIThread.Post(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
                registration.Dispose();
                return;
            }

            ShowConfirmDialog(
                "需要下载 Java",
                "启动该版本需要 Java " + versionLabel + "，但本机未找到兼容的 Java。\n\n是否由 PCL N 自动下载并安装官方运行时？",
                confirmed =>
                {
                    registration.Dispose();
                    tcs.TrySetResult(confirmed);
                },
                "下载并安装",
                "取消");
        });

        return tcs.Task;
    }

    private async Task<LoginProfileInfo> RefreshLaunchProfileAsync(
        LoginProfileInfo profile,
        CancellationToken cancellationToken)
    {
        if (profile.Kind != LaunchLoginProfileKind.Microsoft ||
            string.IsNullOrWhiteSpace(profile.RefreshToken))
        {
            return profile;
        }

        string clientId = MicrosoftMinecraftAuthService.ResolveClientId();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            // Local/debug builds may intentionally omit the OAuth client id. A
            // previously authenticated Minecraft access token remains usable
            // until its own expiry; refreshing is only mandatory afterwards.
            if (IsAccessTokenUsable(profile.AccessToken))
            {
                Dispatcher.UIThread.Post(
                    () => _launchRight?.AppendLog("未配置 Microsoft Client ID，使用档案中仍有效的访问令牌启动。"),
                    DispatcherPriority.Background);
                return profile;
            }

            throw new InvalidOperationException(
                "缺少 Microsoft 登录配置，无法刷新正版登录状态。请提供 PCL_MS_CLIENT_ID 后重试。");
        }

        MicrosoftMinecraftLoginResult refreshed = await _microsoftAuthService
            .RefreshAsync(clientId, profile.RefreshToken, cancellationToken)
            .ConfigureAwait(false);
        return profile with
        {
            Username = refreshed.Username,
            Uuid = refreshed.Uuid,
            AccessToken = refreshed.AccessToken,
            RefreshToken = refreshed.RefreshToken,
            SkinAddress = refreshed.SkinAddress ?? profile.SkinAddress,
            Info = refreshed.OwnsMinecraft ? "Microsoft 正版" : profile.Info
        };
    }

    private static bool IsAccessTokenUsable(string? accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            return false;

        string[] parts = accessToken.Split('.');
        if (parts.Length < 2)
            return true;
        try
        {
            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using JsonDocument document = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (!document.RootElement.TryGetProperty("exp", out JsonElement expiration) ||
                !expiration.TryGetInt64(out long seconds))
            {
                return true;
            }

            return DateTimeOffset.FromUnixTimeSeconds(seconds) > DateTimeOffset.UtcNow.AddMinutes(2d);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            return true;
        }
    }

    private async Task IncrementInstanceLaunchCountAsync(LaunchInstanceInfo instance)
    {
        try
        {
            InstanceMetadata metadata = await InstanceMetadataStore.UpdateAsync(
                    instance.InstanceDirectory,
                    current => current with { LaunchCount = Math.Max(0, current.LaunchCount) + 1 })
                .ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_instanceManagePage is not null &&
                    _managedInstance is not null &&
                    string.Equals(_managedInstance.InstanceDirectory, instance.InstanceDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    _instanceManagePage.SetInstance(instance);
                }

                _launchRight?.AppendLog($"这是 {instance.Name} 的第 {metadata.LaunchCount.ToString(CultureInfo.InvariantCulture)} 次启动。");
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(
                () => _launchRight?.AppendLog("记录启动次数失败：" + ex.Message),
                DispatcherPriority.Background);
        }
    }

    private static async Task<MinecraftProcessLaunchPlan> CreateLaunchPlanAsync(
        LaunchInstanceInfo instance,
        LoginProfileInfo profile,
        string javaExecutablePath,
        CancellationToken cancellationToken,
        string? worldName = null,
        InstanceMetadata? metadataOverride = null,
        string? serverAddress = null)
    {
        InstanceMetadata metadata = metadataOverride ??
            await InstanceMetadataStore.LoadAsync(instance.InstanceDirectory, cancellationToken).ConfigureAwait(false);
        // Never hit settings store synchronously on the launch path (disk IO hitch).
        LauncherSettings settings = await Task.Run(
                LauncherSettingsPageBinder.LoadSettings,
                cancellationToken)
            .ConfigureAwait(false);
        bool useJvmHost = settings.GetBooleanOption(
            LauncherSettingKeys.ExperimentalJvmLifecycleHost,
            LauncherSettingDefaults.GetBoolean(LauncherSettingKeys.ExperimentalJvmLifecycleHost.Value));
        int windowType = GetIntegerOption(settings, LauncherSettingKeys.LaunchArgumentWindowType, 1);
        (int width, int height) = GetWindowSize(settings);
        (string? authlibPath, string? authlibServer, string? authlibMetadata) =
            await ResolveAuthlibLaunchOptionsAsync(profile, useJvmHost, cancellationToken).ConfigureAwait(false);
        int javaMajorVersion = await ResolveJavaMajorVersionAsync(javaExecutablePath, cancellationToken)
            .ConfigureAwait(false);

        return await MinecraftProcessLaunchService.CreatePlanAsync(
            new MinecraftProcessLaunchRequest
            {
                VersionId = instance.Name,
                VersionJsonPath = instance.VersionJsonPath,
                InstanceDirectory = instance.InstanceDirectory,
                MinecraftRootDirectory = GetMinecraftRootFromInstance(instance),
                PlayerName = profile.Username,
                PlayerUuid = string.IsNullOrWhiteSpace(profile.Uuid) ? Guid.NewGuid().ToString("N") : profile.Uuid,
                AccessToken = string.IsNullOrWhiteSpace(profile.AccessToken) ? "0" : profile.AccessToken,
                JavaExecutablePath = javaExecutablePath,
                JavaMajorVersion = javaMajorVersion,
                MemoryMegabytes = ResolveLaunchMemoryMegabytes(instance, metadata, settings),
                Width = width,
                Height = height,
                Fullscreen = windowType == 0,
                IsolatedGameDirectory = metadata.InstanceIsolation,
                CustomJvmArguments = BuildInstanceJvmArguments(metadata, settings),
                CustomGameArguments = FirstNonEmpty(metadata.GameArguments, GetTextOption(settings, LauncherSettingKeys.LaunchAdvanceGame)),
                ClasspathHeadEntries = SplitClasspathHead(metadata.ClasspathHead),
                AuthlibInjectorPath = authlibPath,
                AuthlibServer = authlibServer,
                AuthlibPrefetchedMetadata = authlibMetadata,
                UseExperimentalJvmHost = useJvmHost,
                JvmHostIdentityMode = profile.Kind switch
                {
                    LaunchLoginProfileKind.ThirdParty => MinecraftJvmHostIdentityMode.ThirdParty,
                    LaunchLoginProfileKind.Offline => MinecraftJvmHostIdentityMode.Offline,
                    _ => MinecraftJvmHostIdentityMode.Official
                },
                OfflineSkinSource = profile.Kind == LaunchLoginProfileKind.Offline ? profile.SkinAddress : null,
                OfflineSkinSlim = profile.Kind == LaunchLoginProfileKind.Offline &&
                                  string.Equals(
                                      LoginProfileInfo.ResolveOfflineDefaultModel(profile.Uuid),
                                      "Alex",
                                      StringComparison.Ordinal),
                PreferredIpStack = GetPreferredIpStack(settings),
                Server = string.IsNullOrWhiteSpace(worldName)
                    ? FirstNonEmpty(serverAddress, metadata.ServerToEnter)
                    : null,
                ReleaseTime = TryReadReleaseTime(instance),
                HasOptiFine = HasOptiFine(instance),
                WorldName = worldName,
                LauncherName = "PCL-N",
                VersionType = FirstNonEmpty(
                    metadata.CustomInfo,
                    settings.GetTextOption("LaunchArgumentInfo", LauncherSettingDefaults.GetText("LaunchArgumentInfo"))) ?? "PCL-N"
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ResolveJavaMajorVersionAsync(
        string javaExecutablePath,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<PCL.Domain.Minecraft.Java.JavaRuntimeCandidate> candidates =
            await new FileSystemJavaLocator([javaExecutablePath]).FindAllAsync(cancellationToken)
                .ConfigureAwait(false);
        return candidates.Count > 0 ? candidates[0].Installation.MajorVersion : 17;
    }

    private static async Task<(string? Path, string? Server, string? Metadata)> ResolveAuthlibLaunchOptionsAsync(
        LoginProfileInfo profile,
        bool useJvmHost,
        CancellationToken cancellationToken)
    {
        if (profile.Kind != LaunchLoginProfileKind.ThirdParty || string.IsNullOrWhiteSpace(profile.AuthServer))
            return (null, null, null);

        AuthlibInjectorService service = new();
        string authServer = AuthlibInjectorService.NormalizeAuthServer(profile.AuthServer);
        string metadata = await service.GetServerMetadataAsync(authServer, cancellationToken)
            .ConfigureAwait(false);
        if (useJvmHost)
            return (null, authServer, metadata);

        string authlibPath = await service.EnsureAsync(GetAuthlibInjectorCachePath(), cancellationToken)
            .ConfigureAwait(false);
        return (authlibPath, authServer, metadata);
    }

    private static string GetAuthlibInjectorCachePath()
    {
        DefaultPlatformPathProvider paths = new();
        return Path.Combine(paths.ApplicationDataDirectory, "PCL-N", "authlib-injector.jar");
    }

    private static async Task RunPreLaunchCommandAsync(
        string command,
        bool waitForExit,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command))
            return;

        using Process? process = Process.Start(CreateShellStartInfo(command, workingDirectory));
        if (process is null)
            throw new InvalidOperationException("预启动命令未能启动。");

        if (!waitForExit)
            return;

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("预启动命令执行失败，退出码：" + process.ExitCode.ToString(CultureInfo.InvariantCulture));
    }

    private static ProcessStartInfo CreateShellStartInfo(string command, string workingDirectory)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (OperatingSystem.IsWindows())
            startInfo.ArgumentList.Add("/C");
        else
            startInfo.ArgumentList.Add("-lc");
        startInfo.ArgumentList.Add(command);
        return startInfo;
    }

    private static void ApplyProcessPriority(Process process, LauncherSettings settings)
    {
        try
        {
            process.PriorityClass = settings.GetIntegerOption(
                "LaunchArgumentPriority",
                LauncherSettingDefaults.GetInteger("LaunchArgumentPriority")) switch
            {
                0 => ProcessPriorityClass.AboveNormal,
                2 => ProcessPriorityClass.BelowNormal,
                3 => ProcessPriorityClass.High,
                4 => ProcessPriorityClass.RealTime,
                _ => ProcessPriorityClass.Normal
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }

    private void ApplyLauncherVisibility(Process process, LauncherSettings settings)
    {
        int visibility = settings.GetIntegerOption(
            "LaunchArgumentVisible",
            LauncherSettingDefaults.GetInteger("LaunchArgumentVisible"));
        switch (visibility)
        {
            case 0:
                // Keep the window alive while the game is running so a non-zero exit can still
                // surface the crash analyzer. A successful exit preserves the configured
                // "close launcher" behavior in RestoreAfterGameExitAsync.
                Hide();
                break;
            case 2:
                Hide();
                break;
            case 3:
                Hide();
                break;
            case 4:
                WindowState = WindowState.Minimized;
                break;
        }

        _ = RestoreAfterGameExitAsync(process, visibility);
    }

    private async Task RestoreAfterGameExitAsync(Process process, int visibility)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            bool gameCrashed = process.ExitCode != 0;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SetGameRunningExtras(null);
                UpdateBackgroundVideoPlayback();
                if (gameCrashed)
                {
                    if (!IsVisible)
                        Show();
                    if (WindowState == WindowState.Minimized)
                        WindowState = WindowState.Normal;
                    Activate();
                    return;
                }

                if (visibility is 0 or 2)
                {
                    Close();
                    return;
                }

                if (visibility == 3 && !IsVisible)
                    Show();
                if (visibility == 4 && WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;
                if (visibility is 3 or 4)
                    Activate();
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            await Dispatcher.UIThread.InvokeAsync(() => SetGameRunningExtras(null));
        }
        finally
        {
            process.Dispose();
        }
    }

    private static int ResolveLaunchMemoryMegabytes(
        LaunchInstanceInfo instance,
        InstanceMetadata metadata,
        LauncherSettings settings)
    {
        int memorySolution = metadata.MemorySolution;
        int customMemorySize = metadata.CustomMemorySize;
        if (memorySolution == 2)
        {
            memorySolution = GetIntegerOption(settings, LauncherSettingKeys.LaunchRamType, 0);
            customMemorySize = GetIntegerOption(settings, LauncherSettingKeys.LaunchRamCustom, 15);
        }

        return LaunchMemoryCalculator.ResolveMemoryMegabytes(
            new LaunchMemoryRequest
            {
                MemorySolution = memorySolution,
                CustomMemorySize = customMemorySize,
                MemoryInfo = new PCL.Platform.System.DefaultSystemInfoProvider().GetMemoryInfo(),
                Profile = GetMemoryProfile(instance, metadata),
                ModCount = CountModFiles(instance, metadata)
            });
    }

    private static LaunchMemoryProfile GetMemoryProfile(LaunchInstanceInfo instance, InstanceMetadata metadata)
    {
        if (CountModFiles(instance, metadata) > 0 || VersionJsonContains(instance, "fabric-loader", "forge", "neoforge", "quilt"))
            return LaunchMemoryProfile.Modded;
        return HasOptiFine(instance) ? LaunchMemoryProfile.OptiFine : LaunchMemoryProfile.Vanilla;
    }

    private static int CountModFiles(LaunchInstanceInfo instance, InstanceMetadata metadata)
    {
        HashSet<string> modPaths = new(StringComparer.OrdinalIgnoreCase);
        AddModFiles(modPaths, Path.Combine(instance.InstanceDirectory, "mods"));
        if (!metadata.InstanceIsolation)
            AddModFiles(modPaths, Path.Combine(GetMinecraftRootFromInstance(instance), "mods"));
        return modPaths.Count;
    }

    private static void AddModFiles(HashSet<string> modPaths, string modsDirectory)
    {
        if (!Directory.Exists(modsDirectory))
            return;

        foreach (string file in Directory.EnumerateFiles(modsDirectory, "*.jar", SearchOption.TopDirectoryOnly))
            modPaths.Add(file);
    }

    private static (int Width, int Height) GetWindowSize(LauncherSettings settings)
    {
        int width = GetTextOptionAsInt(settings, LauncherSettingKeys.LaunchArgumentWindowWidth, 854);
        int height = GetTextOptionAsInt(settings, LauncherSettingKeys.LaunchArgumentWindowHeight, 480);
        return (Math.Clamp(width, 1, 9999), Math.Clamp(height, 1, 9999));
    }

    private static int GetIntegerOption(LauncherSettings settings, SettingKey key, int fallback) =>
        settings.GetIntegerOption(key, fallback);

    private static string GetTextOption(LauncherSettings settings, SettingKey key) =>
        settings.GetTextOption(key);

    private static int GetTextOptionAsInt(LauncherSettings settings, SettingKey key, int fallback) =>
        int.TryParse(GetTextOption(settings, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;

    private static MinecraftJvmIpPreference GetPreferredIpStack(LauncherSettings settings) =>
        GetIntegerOption(settings, LauncherSettingKeys.LaunchPreferredIpStack, 1) switch
        {
            0 => MinecraftJvmIpPreference.PreferV4,
            2 => MinecraftJvmIpPreference.PreferV6,
            _ => MinecraftJvmIpPreference.SystemDefault
        };

    private static string[] SplitClasspathHead(string classpathHead)
    {
        if (string.IsNullOrWhiteSpace(classpathHead))
            return [];

        return classpathHead.Split(
                ["\r\n", "\n", Path.PathSeparator.ToString()],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static entry => !string.IsNullOrWhiteSpace(entry))
            .ToArray();
    }

    private static string BuildInstanceJvmArguments(InstanceMetadata metadata, LauncherSettings settings)
    {
        string arguments = FirstNonEmpty(
            metadata.JvmArguments,
            GetTextOption(settings, LauncherSettingKeys.LaunchAdvanceJvm)) ?? string.Empty;
        if (!metadata.UseProxy ||
            settings.GetIntegerOption("SystemHttpProxyType", LauncherSettingDefaults.GetInteger("SystemHttpProxyType")) != 2 ||
            !Uri.TryCreate(
                settings.GetTextOption("SystemHttpProxy", LauncherSettingDefaults.GetText("SystemHttpProxy")),
                UriKind.Absolute,
                out Uri? proxy))
        {
            return arguments;
        }

        string proxyArguments = $"-Dhttp.proxyHost={proxy.Host} -Dhttp.proxyPort={proxy.Port} " +
                                $"-Dhttps.proxyHost={proxy.Host} -Dhttps.proxyPort={proxy.Port}";
        return string.IsNullOrWhiteSpace(arguments) ? proxyArguments : arguments.Trim() + " " + proxyArguments;
    }

    private static string ResolvePreferredJavaExecutablePath(bool forceConsole = false)
    {
        bool forceConsoleJava = forceConsole || LauncherSettingDefaults.GetBoolean("LaunchAdvanceNoJavaw");
        try
        {
            LauncherSettings settings = LauncherSettingsPageBinder.LoadSettings();
            forceConsoleJava = forceConsole || settings.GetBooleanOption(
                "LaunchAdvanceNoJavaw",
                LauncherSettingDefaults.GetBoolean("LaunchAdvanceNoJavaw"));
            if (settings.TryGetTextOption(LauncherSettingKeys.LaunchSelectedJava, out string? selectedJava) &&
                !string.IsNullOrWhiteSpace(selectedJava) &&
                File.Exists(selectedJava))
            {
                if (OperatingSystem.IsWindows() && forceConsoleJava &&
                    string.Equals(Path.GetFileName(selectedJava), "javaw.exe", StringComparison.OrdinalIgnoreCase))
                {
                    string java = Path.Combine(Path.GetDirectoryName(selectedJava) ?? string.Empty, "java.exe");
                    if (File.Exists(java))
                        return java;
                }

                if (!forceConsoleJava && OperatingSystem.IsWindows() &&
                    string.Equals(Path.GetFileName(selectedJava), "java.exe", StringComparison.OrdinalIgnoreCase))
                {
                    string javaw = Path.Combine(Path.GetDirectoryName(selectedJava) ?? string.Empty, "javaw.exe");
                    if (File.Exists(javaw))
                        return javaw;
                }

                return selectedJava;
            }
        }
        catch (Exception)
        {
            // 启动路径读取失败时退回系统 PATH，避免设置文件损坏阻断启动。
        }

        return OperatingSystem.IsWindows() && !forceConsoleJava
            ? "javaw"
            : "java";
    }

    private void PromptRenameInstance(LaunchInstanceInfo instance)
    {
        ShowInputDialog(
            "重命名版本",
            "请输入新的版本名称。名称会同步到版本文件夹与 version.json。",
            instance.Name,
            "新的版本名称",
            result =>
            {
                if (string.IsNullOrWhiteSpace(result) || string.Equals(result, instance.Name, StringComparison.Ordinal))
                    return;
                RenameInstance(instance, result.Trim());
            });
    }

    private void PromptDeleteInstance(LaunchInstanceInfo instance)
    {
        ShowConfirmDialog(
            "删除版本",
            $"确定要删除“{instance.Name}”吗？\n\n该操作会删除版本文件夹：\n{instance.InstanceDirectory}",
            confirmed =>
            {
                if (confirmed)
                    DeleteInstance(instance);
            },
            "删除",
            "取消",
            isWarn: true);
    }

    private void PromptEditInstanceDescription(LaunchInstanceInfo instance)
    {
        _ = PromptEditInstanceDescriptionAsync(instance);
    }

    private async Task PromptEditInstanceDescriptionAsync(LaunchInstanceInfo instance)
    {
        InstanceMetadata metadata;
        try
        {
            metadata = await InstanceMetadataStore.LoadAsync(instance.InstanceDirectory).ConfigureAwait(true);
        }
        catch
        {
            metadata = new InstanceMetadata();
        }

        ShowInputDialog(
            "编辑版本描述",
            "这段描述会显示在版本卡片上，用来区分不同配置或整合包。",
            metadata.Description,
            "默认描述",
            result =>
            {
                if (result is null)
                    return;

                _ = SaveInstanceDescriptionAsync(instance, result);
            });
    }

    private async Task SaveInstanceDescriptionAsync(LaunchInstanceInfo instance, string description)
    {
        try
        {
            await InstanceMetadataStore.UpdateAsync(
                    instance.InstanceDirectory,
                    metadata => metadata with { Description = description.Trim() })
                .ConfigureAwait(true);
            _instanceManagePage?.SetInstance(instance);
            _launchRight?.AppendLog($"已更新 {instance.Name} 的版本描述。");
        }
        catch (Exception ex)
        {
            ShowTextDialog("保存失败", "未能保存版本描述。\n\n详细信息：" + ex.Message);
        }
    }

    private async Task ToggleInstanceStarAsync(LaunchInstanceInfo instance)
    {
        try
        {
            InstanceMetadata metadata = await InstanceMetadataStore.UpdateAsync(
                    instance.InstanceDirectory,
                    current => current with { IsStarred = !current.IsStarred })
                .ConfigureAwait(true);
            _instanceManagePage?.SetInstance(instance);
            _launchRight?.AppendLog(metadata.IsStarred
                ? $"已收藏版本 {instance.Name}。"
                : $"已取消收藏版本 {instance.Name}。");
        }
        catch (Exception ex)
        {
            ShowTextDialog("收藏失败", "未能更新收藏状态。\n\n详细信息：" + ex.Message);
        }
    }

    private async Task ExportLaunchScriptAsync(LaunchInstanceInfo instance)
    {
        LoginProfileInfo? profile = _loginProfiles.FirstOrDefault();
        if (profile is null)
        {
            SelectNavRoute(LaunchRoute, animate: true);
            _launchLeft?.PageChangeToLogin();
            ShowTextDialog("请选择账户档案", "导出启动脚本前需要先选择或创建一个账户档案。");
            return;
        }

        try
        {
            string defaultExtension = OperatingSystem.IsWindows() ? ".bat" : ".sh";
            string suggestedFileName = "启动 " + SanitizeFileName(instance.Name) + defaultExtension;
            string targetPath = await PickSaveFilePathAsync(
                    "导出启动脚本",
                    suggestedFileName,
                    OperatingSystem.IsWindows()
                        ? new FilePickerFileType("Windows 批处理") { Patterns = ["*.bat", "*.cmd"] }
                        : new FilePickerFileType("Shell 脚本") { Patterns = ["*.sh"] })
                .ConfigureAwait(true)
                ?? Path.Combine(GetDesktopOrBaseDirectory(), suggestedFileName);

            InstanceMetadata metadata = await InstanceMetadataStore.LoadAsync(instance.InstanceDirectory)
                .ConfigureAwait(true);
            LauncherSettings settings = LauncherSettingsPageBinder.LoadSettings();
            string javaPath = await MinecraftLaunchCoordinator.ResolveJavaExecutableAsync(
                    new MinecraftLaunchCoordinatorRequest
                    {
                        Instance = instance,
                        Profile = profile,
                        Metadata = metadata,
                        Settings = settings,
                        MinecraftRootDirectory = GetMinecraftRootFromInstance(instance),
                        Report = static _ => { },
                        RefreshProfileAsync = static (current, _) => Task.FromResult(current),
                        CreatePlanAsync = CreateLaunchPlanAsync,
                        RunPreLaunchCommandAsync = RunPreLaunchCommandAsync,
                        ApplyProcessPriority = ApplyProcessPriority
                    },
                    CancellationToken.None)
                .ConfigureAwait(true);
            MinecraftProcessLaunchPlan plan = await CreateLaunchPlanAsync(
                    instance,
                    profile,
                    javaPath,
                    CancellationToken.None,
                    metadataOverride: metadata)
                .ConfigureAwait(true);
            await MinecraftLaunchScriptService.SaveAsync(
                    new MinecraftLaunchScriptRequest
                    {
                        LaunchPlan = plan,
                        TargetPath = targetPath
                    })
                .ConfigureAwait(true);
            ShowTextDialog("导出完成", "启动脚本已保存到：\n" + targetPath);
        }
        catch (Exception ex)
        {
            ShowTextDialog("导出失败", "未能导出启动脚本。\n\n详细信息：" + ex.Message);
        }
    }

    private async Task TestLaunchFromInstancePageAsync(LaunchInstanceInfo instance)
    {
        SelectNavRoute(LaunchRoute, animate: true);
        if (_launchLeft is null)
            return;

        if (_launchLeft.Instances.Count > 0)
            _launchLeft.SetInstances(_launchLeft.Instances, instance);
        await StartMinecraftAsync(_launchLeft, instance).ConfigureAwait(true);
    }

    private async Task RepairInstanceFilesAsync(LaunchInstanceInfo instance)
    {
        string taskId = CreateTaskId("repair", instance.InstanceDirectory);
        using CancellationTokenSource cancellation = RegisterTrackedTask(taskId);
        string taskTitle = "修复 " + instance.Name;
        ActivateTaskManagerPage(animate: true);
        TrackTaskBegin(taskId, taskTitle, "准备检查版本文件");

        Progress<MinecraftInstallProgress> progress = new(update => TrackInstallProgress(taskId, taskTitle, update));
        try
        {
            await _minecraftInstallService.RepairAsync(
                    new MinecraftRepairRequest
                    {
                        VersionId = instance.Name,
                        VersionJsonPath = instance.VersionJsonPath,
                        MinecraftRootDirectory = GetMinecraftRootFromInstance(instance),
                        InstanceDirectory = instance.InstanceDirectory,
                        PreferOfficialSource = true
                    },
                    progress,
                    cancellation.Token)
                .ConfigureAwait(true);
            TrackTaskFinished(taskId, taskTitle, "文件检查完成");
            _launchRight?.AppendLog($"{instance.Name} 文件检查完成。");
        }
        catch (OperationCanceledException)
        {
            TrackTaskFailed(taskId, taskTitle, "修复已取消。", canceled: true);
        }
        catch (Exception ex)
        {
            TrackTaskFailed(taskId, taskTitle, ex.Message, canceled: false);
            ShowTextDialog("修复失败", "未能修复版本文件。\n\n详细信息：" + ex.Message);
        }
        finally
        {
            UnregisterTrackedTask(taskId, cancellation);
        }
    }

    private void PromptResetInstanceSettings(LaunchInstanceInfo instance)
    {
        ShowConfirmDialog(
            "初始化版本设置",
            $"确定要初始化“{instance.Name}”的本地设置吗？\n\n该操作不会删除游戏文件，只会清除 PCL N 保存的版本描述、收藏、分类和文件校验偏好。",
            confirmed =>
            {
                if (confirmed)
                    _ = ResetInstanceSettingsAsync(instance);
            },
            "初始化",
            "取消",
            isWarn: true);
    }

    private async Task ResetInstanceSettingsAsync(LaunchInstanceInfo instance)
    {
        try
        {
            await InstanceMetadataStore.SaveAsync(instance.InstanceDirectory, new InstanceMetadata())
                .ConfigureAwait(true);
            _instanceManagePage?.SetInstance(instance);
            _launchRight?.AppendLog($"已初始化 {instance.Name} 的版本设置。");
        }
        catch (Exception ex)
        {
            ShowTextDialog("初始化失败", "未能初始化版本设置。\n\n详细信息：" + ex.Message);
        }
    }

    private async Task PatchInstanceCoreAsync(LaunchInstanceInfo instance)
    {
        try
        {
            string? patchPath = await PickOpenFilePathAsync(
                    "选择要补全到核心的文件",
                    new FilePickerFileType("Java 压缩包") { Patterns = ["*.jar", "*.zip"] })
                .ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(patchPath))
                return;

            string targetJar = Path.Combine(instance.InstanceDirectory, instance.Name + ".jar");
            int count = await MinecraftJarPatchService.ApplyAsync(
                    new MinecraftJarPatchRequest
                    {
                        TargetJarPath = targetJar,
                        PatchArchivePath = patchPath
                    })
                .ConfigureAwait(true);
            await InstanceMetadataStore.UpdateAsync(
                    instance.InstanceDirectory,
                    metadata => metadata with { DisableAssetVerification = true })
                .ConfigureAwait(true);
            _instanceManagePage?.SetInstance(instance);
            ShowTextDialog("补全完成", $"已向核心文件写入 {count} 个文件。\n\n为避免补丁被校验覆盖，已自动关闭该版本的资源校验偏好。");
        }
        catch (Exception ex)
        {
            ShowTextDialog("补全失败", "未能补全核心文件。\n\n详细信息：" + ex.Message);
        }
    }

    private void RenameInstance(LaunchInstanceInfo instance, string newName)
    {
        try
        {
            if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                ShowTextDialog("名称不可用", "版本名称不能包含系统保留字符。");
                return;
            }

            string parent = Directory.GetParent(instance.InstanceDirectory)?.FullName
                            ?? throw new InvalidOperationException("无法确定版本目录。");
            string newDirectory = Path.Combine(parent, newName);
            if (Directory.Exists(newDirectory))
            {
                ShowTextDialog("名称已存在", "已经存在同名版本，请换一个名称。");
                return;
            }

            Directory.Move(instance.InstanceDirectory, newDirectory);
            RenameFileIfExists(Path.Combine(newDirectory, instance.Name + ".json"), Path.Combine(newDirectory, newName + ".json"));
            RenameFileIfExists(Path.Combine(newDirectory, instance.Name + ".jar"), Path.Combine(newDirectory, newName + ".jar"));
            UpdateVersionJsonId(Path.Combine(newDirectory, newName + ".json"), newName);
            _launchRight?.AppendLog($"已将版本 {instance.Name} 重命名为 {newName}。");
            _ = RefreshInstancesAfterManagementAsync(newDirectory);
        }
        catch (Exception ex)
        {
            ShowTextDialog("重命名失败", "未能重命名版本。\n\n详细信息：" + ex.Message);
        }
    }

    private void DeleteInstance(LaunchInstanceInfo instance)
    {
        try
        {
            Directory.Delete(instance.InstanceDirectory, recursive: true);
            _launchRight?.AppendLog($"已删除版本 {instance.Name}。");
            _ = RefreshInstancesAfterManagementAsync(null);
            SelectNavRoute(LaunchRoute, animate: true);
        }
        catch (Exception ex)
        {
            ShowTextDialog("删除失败", "未能删除版本。\n\n详细信息：" + ex.Message);
        }
    }

    private async Task RefreshInstancesAfterManagementAsync(string? selectedDirectory)
    {
        if (_launchLeft is null)
            return;

        await _launchLeft.RefreshInstancesAsync().ConfigureAwait(true);
        LaunchInstanceInfo? selected = string.IsNullOrWhiteSpace(selectedDirectory)
            ? _launchLeft.SelectedInstance
            : _launchLeft.Instances.FirstOrDefault(instance =>
                string.Equals(instance.InstanceDirectory, selectedDirectory, StringComparison.OrdinalIgnoreCase));
        if (selected is not null)
            _launchLeft.SetInstances(_launchLeft.Instances, selected);
        _instancesSelect.SetInstances(_launchLeft.Instances, selected);
        if (selected is not null)
            _instanceManagePage?.SetInstance(selected);
    }

    private static void RenameFileIfExists(string oldPath, string newPath)
    {
        if (File.Exists(oldPath))
            File.Move(oldPath, newPath, overwrite: true);
    }

    private static void UpdateVersionJsonId(string jsonPath, string newName)
    {
        if (!File.Exists(jsonPath))
            return;

        using FileStream stream = new(
            jsonPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 16 * 1024,
            useAsync: false);
        JsonNode? node = JsonNode.Parse(stream);
        if (node is not JsonObject json)
            return;

        json["id"] = newName;
        string tempPath = jsonPath + ".tmp";
        using (FileStream output = new(
                   tempPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.Read,
                   bufferSize: 16 * 1024,
                   useAsync: false))
        {
            using Utf8JsonWriter writer = new(output, new JsonWriterOptions { Indented = true });
            json.WriteTo(writer);
            writer.Flush();
        }

        File.Move(tempPath, jsonPath, overwrite: true);
    }

    private void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowTextDialog("无法打开文件夹", ex.Message);
        }
    }

    private void OpenExistingPath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
            {
                ShowTextDialog("无法打开", "目标文件不存在，可能已经被移动或删除。");
                return;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowTextDialog("无法打开", ex.Message);
        }
    }

    private async Task ExportInstanceZipAsync(LaunchInstanceInfo instance)
    {
        await ExportInstanceZipAsync(
                new InstanceExportPageRequest(
                    instance,
                    instance.Name,
                    "1.0.0",
                    [],
                    IncludeLauncherFiles: false,
                    IncludeLauncherCustom: false,
                    IncludeBundleFiles: false,
                    ModrinthUploadMode: false))
            .ConfigureAwait(true);
    }

    private async Task ExportInstanceZipAsync(InstanceExportPageRequest request)
    {
        try
        {
            LaunchInstanceInfo instance = request.Instance;
            string fileName = $"PCLN-{SanitizeFileName(request.PackageName)}-{SanitizeFileName(request.PackageVersion)}-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
            string targetPath = Path.Combine(GetDesktopOrBaseDirectory(), fileName);
            _launchRight?.AppendLog($"正在导出版本 {instance.Name}。");
            await InstanceExportService.ExportAsync(
                    new InstanceExportRequest
                    {
                        InstanceDirectory = instance.InstanceDirectory,
                        GameDirectory = GetMinecraftRootFromInstance(instance),
                        TargetArchivePath = targetPath,
                        Rules = request.Rules
                    })
                .ConfigureAwait(true);
            ShowTextDialog("导出完成", "版本已导出到：\n" + targetPath);
            _launchRight?.AppendLog($"版本已导出到 {targetPath}。");
        }
        catch (Exception ex)
        {
            ShowTextDialog("导出失败", "未能导出版本。\n\n详细信息：" + ex.Message);
        }
    }

    private async Task ExportInstanceRulesConfigAsync(IReadOnlyList<string> rules)
    {
        try
        {
            string targetPath = await PickSaveFilePathAsync(
                    "导出整合包规则配置",
                    $"PCLN-ExportRules-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                    new FilePickerFileType("Text")
                    {
                        Patterns = ["*.txt"]
                    })
                .ConfigureAwait(true)
                ?? Path.Combine(GetDesktopOrBaseDirectory(), $"PCLN-ExportRules-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

            await File.WriteAllLinesAsync(targetPath, rules).ConfigureAwait(true);
            ShowTextDialog("导出配置完成", "规则配置已导出到：\n" + targetPath);
        }
        catch (Exception ex)
        {
            ShowTextDialog("导出配置失败", "未能导出规则配置。\n\n详细信息：" + ex.Message);
        }
    }

    private async Task ImportInstanceRulesConfigAsync(PageInstanceExportRight page)
    {
        try
        {
            string? sourcePath = await PickOpenFilePathAsync(
                    "导入整合包规则配置",
                    new FilePickerFileType("Text")
                    {
                        Patterns = ["*.txt", "*.cfg"]
                    })
                .ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(sourcePath))
                return;

            string[] rules = await File.ReadAllLinesAsync(sourcePath).ConfigureAwait(true);
            page.ApplyRulesOverride(rules);
            ShowTextDialog("已导入配置", "导出内容将按配置文件中的规则生成。你可以点击“重置”恢复页面选项。");
        }
        catch (Exception ex)
        {
            ShowTextDialog("导入配置失败", "未能导入规则配置。\n\n详细信息：" + ex.Message);
        }
    }

    private async Task<string?> PickSaveFilePathAsync(
        string title,
        string suggestedFileName,
        FilePickerFileType fileType)
    {
        IStorageProvider? storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage?.CanSave != true)
            return null;

        IStorageFile? file = await storage.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = title,
                    SuggestedFileName = suggestedFileName,
                    FileTypeChoices = [fileType],
                    ShowOverwritePrompt = true
                })
            .ConfigureAwait(true);
        return file?.TryGetLocalPath();
    }

    private async Task<string?> PickOpenFilePathAsync(
        string title,
        FilePickerFileType fileType)
    {
        IStorageProvider? storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage?.CanOpen != true)
            return null;

        IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = title,
                    AllowMultiple = false,
                    FileTypeFilter = [fileType]
                })
            .ConfigureAwait(true);
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    private static string ResolveInstanceJavaExecutablePath(InstanceMetadata metadata, bool forceConsole = false)
    {
        if (metadata.JavaSelectionMode == 2 &&
            !string.IsNullOrWhiteSpace(metadata.SelectedJavaPath) &&
            File.Exists(metadata.SelectedJavaPath))
        {
            string selectedJava = metadata.SelectedJavaPath;
            if (OperatingSystem.IsWindows())
            {
                string directory = Path.GetDirectoryName(selectedJava) ?? string.Empty;
                if (forceConsole && string.Equals(Path.GetFileName(selectedJava), "javaw.exe", StringComparison.OrdinalIgnoreCase))
                {
                    string consoleJava = Path.Combine(directory, "java.exe");
                    if (File.Exists(consoleJava))
                        return consoleJava;
                }
                if (!forceConsole && string.Equals(Path.GetFileName(selectedJava), "java.exe", StringComparison.OrdinalIgnoreCase))
                {
                    string windowJava = Path.Combine(directory, "javaw.exe");
                    if (File.Exists(windowJava))
                        return windowJava;
                }
            }
            return selectedJava;
        }

        if (metadata.JavaSelectionMode == 1)
            return OperatingSystem.IsWindows() && !forceConsole ? "javaw" : "java";
        return ResolvePreferredJavaExecutablePath(forceConsole);
    }

    private async Task<string?> PickOpenFolderPathAsync(string title)
    {
        IStorageProvider? storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage?.CanPickFolder != true)
            return null;

        IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false
            });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    private static string GetDesktopOrBaseDirectory()
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return string.IsNullOrWhiteSpace(desktop) ? AppContext.BaseDirectory : desktop;
    }

    private static string SanitizeFileName(string name)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        string sanitized = new(name.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "Minecraft" : sanitized;
    }

    private string GetDefaultMinecraftRoot()
    {
        if (!string.IsNullOrWhiteSpace(_launchLeft?.MinecraftRootDirectory))
            return _launchLeft.MinecraftRootDirectory;

        IReadOnlyList<string> roots = LaunchInstanceDiscovery.GetCandidateRoots();
        foreach (string root in roots)
        {
            if (Directory.Exists(root))
                return root;
        }

        return roots.Count > 0 ? roots[0] : LaunchInstanceDiscovery.GetCurrentMinecraftRoot();
    }

    private static string GetMinecraftRootFromInstance(LaunchInstanceInfo instance)
    {
        DirectoryInfo versionDirectory = new(instance.InstanceDirectory);
        DirectoryInfo versionsDirectory = versionDirectory.Parent
            ?? throw new InvalidOperationException("无法确定 versions 目录。");
        return versionsDirectory.Parent?.FullName
               ?? throw new InvalidOperationException("无法确定 Minecraft 根目录。");
    }

    private static string ReadMinecraftVersionId(LaunchInstanceInfo instance)
    {
        try
        {
            using FileStream stream = File.OpenRead(instance.VersionJsonPath);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            string? inheritsFrom = TryReadJsonString(root, "inheritsFrom");
            if (!string.IsNullOrWhiteSpace(inheritsFrom))
                return inheritsFrom;

            string? id = TryReadJsonString(root, "id");
            if (!string.IsNullOrWhiteSpace(id))
                return id;
        }
        catch (Exception)
        {
        }

        return instance.Name;
    }

    private static DateTimeOffset? TryReadReleaseTime(LaunchInstanceInfo instance)
    {
        try
        {
            using FileStream stream = File.OpenRead(instance.VersionJsonPath);
            using JsonDocument document = JsonDocument.Parse(stream);
            string? releaseTime = TryReadJsonString(document.RootElement, "releaseTime");
            return DateTimeOffset.TryParse(
                releaseTime,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out DateTimeOffset value)
                ? value
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? TryReadJsonString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out JsonElement property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool HasOptiFine(LaunchInstanceInfo instance)
    {
        if (VersionJsonContains(instance, "optifine"))
            return true;

        try
        {
            return Directory.EnumerateFiles(instance.InstanceDirectory, "*", SearchOption.TopDirectoryOnly)
                .Any(static file => Path.GetFileName(file).Contains("optifine", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool VersionJsonContains(LaunchInstanceInfo instance, params string[] needles)
    {
        bool hasNeedle = false;
        int overlapLength = 0;
        foreach (string needle in needles)
        {
            if (string.IsNullOrWhiteSpace(needle))
                continue;

            hasNeedle = true;
            overlapLength = Math.Max(overlapLength, needle.Length - 1);
        }

        if (!hasNeedle)
            return false;

        try
        {
            char[] buffer = ArrayPool<char>.Shared.Rent(8 * 1024 + overlapLength);
            try
            {
                using StreamReader reader = new(
                    new FileStream(
                        instance.VersionJsonPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite,
                        bufferSize: 16 * 1024,
                        useAsync: false),
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true,
                    bufferSize: 8 * 1024,
                    leaveOpen: false);

                int carryLength = 0;
                while (true)
                {
                    int read = reader.ReadBlock(buffer, carryLength, buffer.Length - carryLength);
                    if (read == 0)
                        return false;

                    ReadOnlySpan<char> current = buffer.AsSpan(0, carryLength + read);
                    foreach (string needle in needles)
                    {
                        if (!string.IsNullOrWhiteSpace(needle) &&
                            current.Contains(needle, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }

                    carryLength = Math.Min(overlapLength, current.Length);
                    if (carryLength > 0)
                        current[^carryLength..].CopyTo(buffer);
                }
            }
            finally
            {
                ArrayPool<char>.Shared.Return(buffer);
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void ExtraDockViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_isDisposed)
            return;
        if (e.PropertyName is not (
            nameof(ExtraDockViewModel.ShouldShowGlassDock) or
            nameof(ExtraDockViewModel.UseGlassChrome) or
            nameof(ExtraDockViewModel.HasAnyVisibleButton)))
        {
            return;
        }

        // Headless tests may fire messenger updates after Close without Dispose.
        if (!IsLoaded)
            return;

        RefreshExtraDockChrome();
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;
        _isDisposed = true;
        _extraDockViewModel.PropertyChanged -= ExtraDockViewModel_PropertyChanged;
        if (_registeredPluginPageSurfaceId is { } pageSurface)
            DesktopHostUiComposition.Instance.UnregisterTarget(pageSurface);
        DesktopHostUiComposition.Instance.UnregisterSlot("pcl.navigation.main", "items.after-download");
        DesktopHostUiComposition.Instance.UnregisterTarget("pcl.navigation.main");
        DesktopHostUiComposition.Instance.UnregisterTarget("pcl.window.main");
        DesktopHostNotifications.Instance.Detach(OnPluginHostNotification);
        DesktopHostNotifications.Instance.DetachChoice(OnPluginHostChoiceAsync);
        DesktopHostBackgroundTasks.Instance.Detach();
        DesktopHost.Current.Navigation.Changed -= NavigationRegistryChanged;
        DesktopHostNavigation.Instance.Detach(NavigateToPluginRoute);
        LauncherSettingsPageBinder.SettingsChanged -= LauncherSettingsChanged;
        AvaloniaThemeManager.ThemeChanged -= ThemeChanged;
        AvaloniaLocalizationManager.LanguageChanged -= LocalizationChanged;
        _backgroundBitmap?.Dispose();
        _backgroundBitmap = null;
        _windowStateSubscription.Dispose();
        if (this.FindControl<MediaElement>("VideoBack") is { } video)
        {
            video.MediaFailed -= VideoFailed;
            video.Dispose();
        }
        _titleLogoBitmap?.Dispose();
        _titleLogoBitmap = null;
        _homepageLoadCancellation?.Cancel();
        _homepageLoadCancellation?.Dispose();
        _homepageLoadCancellation = null;
        DisposeTrackedTasks();
        _launchCancellation?.Cancel();
        _launchCancellation?.Dispose();
        _microsoftLoginCancellation?.Cancel();
        _microsoftLoginCancellation?.Dispose();
        (_launchLeft as IDisposable)?.Dispose();
        _launchRight?.Dispose();
        _communityRight?.Dispose();
        _communityDetail?.Dispose();
        _instancesSelect.RightPage?.Dispose();
        _setupRight?.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void AnimateMsgBackground(BlurBorder background, byte targetAlpha, Action? completed = null)
    {
        ModAnimation.AniStart(
        new List<ModAnimation.AniData>
        {
            ModAnimation.AaColor(
                background,
                Border.BackgroundProperty,
                Color.FromArgb(targetAlpha, 0, 0, 0),
                200,
                ease: new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak)),
            ModAnimation.AaCode(() =>
            {
                background.Background = new SolidColorBrush(Color.FromArgb(targetAlpha, 0, 0, 0));
                completed?.Invoke();
            }, after: true)
        }, "MyMsg Background");
    }

    private PageLoginMs CreateMicrosoftLoginPage(ILaunchHomeSurface launchPage)
    {
        PageLoginMs page = new();
        page.BackRequested += (_, _) => launchPage.RefreshPage(anim: true);
        page.PurchaseRequested += (_, _) => OpenExternalUrl(
            "https://www.xbox.com/zh-cn/games/store/minecraft-java-bedrock-edition-for-pc/9nxp44l49shj");
        page.WebsiteRequested += (_, _) => OpenExternalUrl("https://www.minecraft.net/zh-hans");
        page.LoginRequested += (_, _) => _ = StartMicrosoftLoginAsync(page, launchPage);
        return page;
    }

    private async Task StartMicrosoftLoginAsync(PageLoginMs page, ILaunchHomeSurface launchPage)
    {
        string clientId = MicrosoftMinecraftAuthService.ResolveClientId();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            page.FinishLogin();
            _launchRight?.AppendLog("缺少 Microsoft 登录配置：PCL_MS_CLIENT_ID。");
            ShowTextDialog(
                "Microsoft 登录配置缺失",
                "缺少 Microsoft 登录配置。请为启动器提供 PCL_MS_CLIENT_ID（Microsoft OAuth 公共客户端 ID）后重试。",
                "知道了");
            return;
        }

        _microsoftLoginCancellation?.Cancel();
        _microsoftLoginCancellation?.Dispose();
        _microsoftLoginCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _microsoftLoginCancellation.Token;
        MyMsgLogin? dialog = null;
        try
        {
            _launchRight?.AppendLog("正在申请 Microsoft 设备登录代码。");
            page.UpdateProgress(0.04d);
            MicrosoftDeviceCodeInfo deviceCode = await _microsoftAuthService
                .RequestDeviceCodeAsync(clientId, cancellationToken)
                .ConfigureAwait(true);
            page.UpdateProgress(0.08d);
            dialog = new MyMsgLogin
            {
                Title = "Microsoft 正版档案登录",
                Caption = FormatMicrosoftDeviceCodeCaption(deviceCode),
                UserCode = deviceCode.UserCode,
                Website = FirstNonEmpty(deviceCode.VerificationUriComplete, deviceCode.VerificationUri)
            };
            ShowLoginDialog(dialog, () => _microsoftLoginCancellation?.Cancel());
            await PrepareLoginDialogAsync(dialog).ConfigureAwait(true);

            Progress<double> progress = new(value => page.UpdateProgress(value));
            MicrosoftMinecraftLoginResult result = await _microsoftAuthService
                .CompleteDeviceLoginAsync(clientId, deviceCode, progress, cancellationToken)
                .ConfigureAwait(true);
            if (dialog.Parent is not null)
                dialog.CloseLikeWpf();

            LoginProfileInfo profile = new(
                result.Username,
                result.OwnsMinecraft ? "Microsoft 正版" : "Microsoft 账户",
                LaunchLoginProfileKind.Microsoft,
                result.Uuid,
                SvgIcon: "lucide/badge-check",
                SkinAddress: result.SkinAddress,
                AccessToken: result.AccessToken,
                RefreshToken: result.RefreshToken);
            AddOrUpdateLoginProfile(profile);
            _loginProfilePage?.SetProfiles(_loginProfiles, profile);
            _loginProfileSkinPage?.SetProfile(profile);
            launchPage.SetSelectedProfilePresent(true);
            launchPage.RefreshPage(anim: true);
            SaveProfilesInBackground("保存 Microsoft 正版档案");
            _launchRight?.AppendLog($"Microsoft 登录成功，已选中档案 {profile.Username}。");
            ShowTextDialog("登录成功", $"已添加并选中正版档案 {profile.Username}。", "知道了");
        }
        catch (OperationCanceledException)
        {
            _launchRight?.AppendLog("Microsoft 登录已取消。");
        }
        catch (Exception ex)
        {
            if (dialog?.Parent is not null)
                dialog.CloseLikeWpf();
            _launchRight?.AppendLog("Microsoft 登录失败：" + ex.Message);
            ShowTextDialog("Microsoft 登录失败", ex.Message, "知道了");
        }
        finally
        {
            page.FinishLogin();
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return string.Empty;
    }

    private static string FormatMicrosoftDeviceCodeCaption(MicrosoftDeviceCodeInfo deviceCode)
    {
        string website = FirstNonEmpty(deviceCode.VerificationUri, deviceCode.VerificationUriComplete);
        return string.IsNullOrWhiteSpace(website)
            ? $"请按浏览器页面提示登录 Microsoft 账户。\n\n授权码：{deviceCode.UserCode}"
            : $"请在浏览器中打开 {website}，并按页面提示登录 Microsoft 账户。\n\n授权码：{deviceCode.UserCode}";
    }

    private PageLoginAuth CreateAuthLoginPage(ILaunchHomeSurface launchPage)
    {
        PageLoginAuth page = new();
        page.BackRequested += (_, _) => launchPage.RefreshPage(anim: true);
        page.ValidationFailed += (_, message) => _launchRight?.AppendLog(message);
        page.RegisterLinkRequested += (_, isRegisterMode) => OpenAuthAccountPage(page.CurrentServer, isRegisterMode);
        page.LoginRequested += (_, request) => _ = StartThirdPartyAuthLoginAsync(page, request);
        return page;
    }

    private async Task StartThirdPartyAuthLoginAsync(PageLoginAuth page, AuthLoginRequest request)
    {
        _launchRight?.AppendLog($"正在连接第三方认证服务器：{request.Server}");
        page.UpdateProgress(0.12d);
        try
        {
            ThirdPartyAuthLoginResult result = await _thirdPartyAuthService
                .AuthenticateAsync(
                    new ThirdPartyAuthLoginRequest(request.Server, request.Username, request.Password))
                .ConfigureAwait(true);
            page.UpdateProgress(0.8d);
            string skinAddress = MySkin.ResolveSkinAddress(
                skinAddress: null,
                uuid: result.Uuid,
                authServer: result.AuthServer);
            LoginProfileInfo profile = new(
                result.Username,
                $"Authlib-Injector · {result.AuthServerDisplayName}",
                LaunchLoginProfileKind.ThirdParty,
                result.Uuid,
                SvgIcon: "lucide/key-round",
                SkinAddress: string.IsNullOrWhiteSpace(skinAddress) ? null : skinAddress,
                AuthServer: result.AuthServer,
                AccessToken: result.AccessToken);
            AddOrUpdateLoginProfile(profile);
            _loginProfilePage?.SetProfiles(_loginProfiles, profile);
            _loginProfileSkinPage?.SetProfile(profile);
            _launchLeft?.SetSelectedProfilePresent(true);
            _launchLeft?.RefreshPage(anim: true);
            SaveProfilesInBackground("保存第三方认证档案");
            _launchRight?.AppendLog($"第三方认证登录成功，已选中档案 {profile.Username}。");
            ShowTextDialog("登录成功", $"已添加并选中 {profile.Username}。", "知道了");
        }
        catch (Exception ex)
        {
            ShowTextDialog("第三方登录失败", ex.Message, "知道了");
            _launchRight?.AppendLog("第三方认证登录失败：" + ex.Message);
        }
        finally
        {
            page.FinishLogin();
        }
    }

    private void OpenAuthAccountPage(string server, bool isRegisterMode)
    {
        if (string.IsNullOrWhiteSpace(server))
        {
            ShowTextDialog("请先填写认证服务器", "填写认证服务器地址后，启动器才能打开对应的注册或找回密码页面。", "知道了");
            return;
        }

        try
        {
            string authServer = ThirdPartyAuthService.NormalizeYggdrasilServer(server);
            string root = authServer;
            const string apiSuffix = "/api/yggdrasil";
            if (root.EndsWith(apiSuffix, StringComparison.OrdinalIgnoreCase))
                root = root[..^apiSuffix.Length];
            OpenExternalUrl(root.TrimEnd('/') + (isRegisterMode ? "/auth/register" : "/auth/forgot"));
        }
        catch (Exception ex)
        {
            ShowTextDialog("认证服务器地址无效", ex.Message, "知道了");
        }
    }

    private PageLoginOffline CreateOfflineLoginPage(ILaunchHomeSurface launchPage)
    {
        PageLoginOffline page = new();
        page.BackRequested += (_, _) => launchPage.RefreshPage(anim: true);
        page.ValidationFailed += (_, message) => _launchRight?.AppendLog(message);
        page.ProfileCreateRequested += (_, request) =>
        {
            string info = string.IsNullOrWhiteSpace(request.SkinSourceUuid)
                ? "离线登录"
                : $"离线登录 · 借用 {request.SkinSourceName}";
            LoginProfileInfo profile = new(
                request.Username,
                info,
                LaunchLoginProfileKind.Offline,
                Uuid: request.Uuid,
                SvgIcon: "lucide/user");

            _loginProfiles.RemoveAll(existing =>
                existing.Kind == LaunchLoginProfileKind.Offline &&
                string.Equals(existing.Uuid, profile.Uuid, StringComparison.OrdinalIgnoreCase));
            _loginProfiles.Insert(0, profile);
            _loginProfilePage?.SetProfiles(_loginProfiles, profile);
            launchPage.SetSelectedProfilePresent(true);
            launchPage.RefreshPage(anim: true);
            SaveProfilesInBackground("保存离线账户档案");
            _launchRight?.AppendLog($"已创建并选中离线档案 {profile.Username}。");
        };
        return page;
    }

    private void AddOrUpdateLoginProfile(LoginProfileInfo profile)
    {
        int existingIndex = _loginProfiles.FindIndex(existing => IsSameProfile(existing, profile));
        if (existingIndex >= 0)
            _loginProfiles.RemoveAt(existingIndex);
        _loginProfiles.Insert(0, profile);
    }

    private void ReplaceLoginProfile(LoginProfileInfo original, LoginProfileInfo updated)
    {
        int existingIndex = _loginProfiles.FindIndex(existing => IsSameProfile(existing, original));
        if (existingIndex >= 0)
            _loginProfiles.RemoveAt(existingIndex);
        _loginProfiles.Insert(0, updated);
    }

    private async Task ImportProfilesAsync(PageLoginProfile page, ILaunchHomeSurface launchPage)
    {
        try
        {
            string? sourcePath = await PickOpenFilePathAsync(
                    "导入账户档案",
                    new FilePickerFileType("JSON")
                    {
                        Patterns = ["*.json"]
                    })
                .ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(sourcePath))
                return;

            using LaunchProfileStore store = new(sourcePath);
            LaunchProfileLoadResult result = await store.LoadAsync().ConfigureAwait(true);
            List<LoginProfileInfo> imported = result.Profiles.Profiles
                .Select(ToLoginProfileInfo)
                .ToList();
            int added = 0;
            int updated = 0;
            foreach (LoginProfileInfo profile in imported)
            {
                int existingIndex = _loginProfiles.FindIndex(existing => IsSameProfile(existing, profile));
                if (existingIndex >= 0)
                {
                    _loginProfiles[existingIndex] = profile;
                    updated++;
                }
                else
                {
                    _loginProfiles.Add(profile);
                    added++;
                }
            }

            page.SetProfiles(_loginProfiles, _loginProfiles.FirstOrDefault());
            launchPage.SetSelectedProfilePresent(_loginProfiles.Count > 0);
            SaveProfilesInBackground("导入账户档案");
            ShowTextDialog("导入完成", $"已导入 {added} 个新档案，更新 {updated} 个已有档案。");
        }
        catch (Exception ex)
        {
            ShowTextDialog("导入失败", "未能导入账户档案。\n\n详细信息：" + ex.Message);
        }
    }

    private async Task ExportProfilesAsync()
    {
        try
        {
            string? targetPath = await PickSaveFilePathAsync(
                    "导出账户档案",
                    $"PCLN-Profiles-{DateTime.Now:yyyyMMdd-HHmmss}.json",
                    new FilePickerFileType("JSON")
                    {
                        Patterns = ["*.json"]
                    })
                .ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(targetPath))
                return;

            using LaunchProfileStore store = new(targetPath);
            await store.SaveAsync(
                    new LaunchProfileSet
                    {
                        Profiles = _loginProfiles.Select(ToLaunchProfile).ToArray()
                    })
                .ConfigureAwait(true);
            ShowTextDialog("导出完成", "账户档案已导出到：\n" + targetPath);
        }
        catch (Exception ex)
        {
            ShowTextDialog("导出失败", "未能导出账户档案。\n\n详细信息：" + ex.Message);
        }
    }

    private async Task LoadProfilesAsync()
    {
        try
        {
            using LaunchProfileStore store = CreateLaunchProfileStore();
            LaunchProfileLoadResult result = await store.LoadAsync().ConfigureAwait(false);
            List<LoginProfileInfo> profiles = result.Profiles.Profiles
                .Select(ToLoginProfileInfo)
                .ToList();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _loginProfiles.Clear();
                _loginProfiles.AddRange(profiles);
                _loginProfilePage?.SetProfiles(_loginProfiles);
                _launchLeft?.SetSelectedProfilePresent(_loginProfiles.Count > 0);
                if (result.WasRecovered)
                    _launchRight?.AppendLog($"账户档案配置已重置，损坏文件已备份到：{result.BackupPath}");
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                _launchRight?.AppendLog("读取账户档案失败：" + ex.Message));
        }
    }

    private static bool IsSameProfile(LoginProfileInfo left, LoginProfileInfo right)
    {
        if (!string.IsNullOrWhiteSpace(left.Uuid) && !string.IsNullOrWhiteSpace(right.Uuid))
            return string.Equals(left.Uuid, right.Uuid, StringComparison.OrdinalIgnoreCase);

        return left.Kind == right.Kind &&
               string.Equals(left.Username, right.Username, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.AuthServer, right.AuthServer, StringComparison.OrdinalIgnoreCase);
    }

    private void SaveProfilesInBackground(string action)
    {
        LaunchProfileSet snapshot = new()
        {
            Profiles = _loginProfiles.Select(ToLaunchProfile).ToArray()
        };
        _ = Task.Run(async () =>
        {
            try
            {
                using LaunchProfileStore store = CreateLaunchProfileStore();
                await store.SaveAsync(snapshot).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    _launchRight?.AppendLog(action + "失败：" + ex.Message));
            }
        });
    }

    private static LaunchProfileStore CreateLaunchProfileStore() =>
        new(CreateLaunchProfilePath());

    private static string CreateLaunchProfilePath()
    {
        string? overridePath = Environment.GetEnvironmentVariable("PCLN_LAUNCH_PROFILES_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
            return overridePath;

        DefaultPlatformPathProvider paths = new();
        return Path.Combine(paths.ApplicationDataDirectory, "PCL-N", "launch-profiles.json");
    }

    private static LoginProfileInfo ToLoginProfileInfo(LaunchProfile profile) =>
        new(
            profile.Username,
            profile.Info,
            profile.Kind switch
            {
                LaunchProfileKind.Microsoft => LaunchLoginProfileKind.Microsoft,
                LaunchProfileKind.ThirdParty => LaunchLoginProfileKind.ThirdParty,
                _ => LaunchLoginProfileKind.Offline
            },
            profile.Uuid,
            profile.Logo,
            profile.SvgIcon,
            profile.SkinAddress,
            profile.AuthServer,
            profile.AccessToken,
            profile.RefreshToken);

    private static LaunchProfile ToLaunchProfile(LoginProfileInfo profile) =>
        new()
        {
            Username = profile.Username,
            Info = profile.Info,
            Kind = profile.Kind switch
            {
                LaunchLoginProfileKind.Microsoft => LaunchProfileKind.Microsoft,
                LaunchLoginProfileKind.ThirdParty => LaunchProfileKind.ThirdParty,
                _ => LaunchProfileKind.Offline
            },
            Uuid = profile.Uuid,
            Logo = profile.Logo,
            SvgIcon = profile.SvgIcon,
            SkinAddress = profile.SkinAddress,
            AuthServer = profile.AuthServer,
            AccessToken = profile.AccessToken,
            RefreshToken = profile.RefreshToken
        };

    private static string? NormalizeAuthServerUrl(string authServer)
    {
        if (string.IsNullOrWhiteSpace(authServer))
            return null;

        string trimmed = authServer.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal))
            trimmed = "https://" + trimmed;

        return Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.ToString()
            : null;
    }

    private static bool TryCreateHttpUri(string value, out Uri? uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return true;
        }

        uri = null;
        return false;
    }

    private void OpenExternalUrl(string url)
    {
        try
        {
            _externalUrlOpener(url);
        }
        catch (Exception ex)
        {
            _launchRight?.AppendLog("无法打开浏览器：" + ex.Message);
        }
    }

    private static void OpenExternalUrlCore(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private DesktopMainPage CreatePlaceholderMainPage(string pageTitle) =>
        new(null, CreateLoadingPlaceholder(pageTitle));

    private static DesktopMainPage CreateLoadingMainPage(string pageTitle) =>
        new(null, CreateLoadingPlaceholder(pageTitle));

    private static Grid CreateLoadingPlaceholder(string pageTitle) =>
        new()
        {
            Children =
            {
                new MyLoading
                {
                    Name = "LoadMain",
                    Width = 220d,
                    Height = 120d,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Text = $"正在加载{pageTitle}页面"
                }
            }
        };

    private static Grid CreateTextPlaceholder(string pageTitle, string message) =>
        new()
        {
            Children =
            {
                new StackPanel
                {
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Spacing = 12d,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = pageTitle,
                            FontSize = 19d,
                            FontWeight = FontWeight.Bold,
                            Foreground = new SolidColorBrush(Color.Parse("#343d4a"))
                        },
                        new TextBlock
                        {
                            Text = message,
                            TextWrapping = TextWrapping.Wrap,
                            MaxWidth = 420d,
                            Foreground = new SolidColorBrush(Color.Parse("#1370f3"))
                        }
                    }
                }
            }
        };

    private static NavigationPageDescriptor[] CreateNavigationPageMap(
        INavigationRegistry navigation)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        return navigation.Pages
            .Where(static page => page.Region == PageRegion.Main)
            .ToArray();
    }

    private void BuildMainNavigationItems()
    {
        if (this.FindControl<Panel>("PanTitleSelect") is not { } panel)
            return;

        panel.Children.Clear();
        for (int pageIndex = 0; pageIndex < _navigationPages.Length; pageIndex++)
        {
            NavigationPageDescriptor descriptor = _navigationPages[pageIndex];
            MyListItem item = new()
            {
                Name = $"BtnTitleSelect{pageIndex.ToString(CultureInfo.InvariantCulture)}",
                Title = descriptor.Title,
                Tag = descriptor.Route,
                Margin = pageIndex == 0 ? new Thickness(1d, 10d, 1d, 0d) : new Thickness(1d, 0d, 1d, 0d),
                FontSize = 12d,
                Type = MyListItem.CheckType.RadioBox,
                LogoScale = 0.8d,
                SvgIcon = string.IsNullOrWhiteSpace(descriptor.Icon) ? "lucide/circle" : descriptor.Icon
            };
            item.Click += BtnNavItem_Click;
            panel.Children.Add(item);
        }
    }

    private void LauncherSettingsChanged(LauncherSettings settings)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyRuntimeSettings(settings));
            return;
        }

        ApplyRuntimeSettings(settings);
    }

    private void ThemeChanged()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ThemeChanged, DispatcherPriority.Background);
            return;
        }

        ApplyFormBackground(AvaloniaThemeManager.CurrentSettings);
    }

    private void LocalizationChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RefreshNavigationText);
            return;
        }

        RefreshNavigationText();
    }

    private void ApplyRuntimeSettings(LauncherSettings settings)
    {
        DesktopFileLog.ConfigureLevel(DesktopFileLog.LevelFromSetting(settings.GetIntegerOption(
            "SystemLogLevel",
            LauncherSettingDefaults.GetInteger("SystemLogLevel"))));
        _targetWindowOpacity = Math.Clamp(
            0.4d + settings.GetIntegerOption(
                "UiLauncherTransparent",
                LauncherSettingDefaults.GetInteger("UiLauncherTransparent")) / 1000d,
            0.4d,
            1d);
        if (_isMainWindowOpened)
            Opacity = _targetWindowOpacity;

        CanResize = !settings.GetBooleanOption(
            "UiLockWindowSize",
            LauncherSettingDefaults.GetBoolean("UiLockWindowSize"));
        ModAnimation.Configure(
            settings.GetIntegerOption("UiAniFPS", LauncherSettingDefaults.GetInteger("UiAniFPS")) + 1,
            settings.GetIntegerOption("SystemDebugAnim", LauncherSettingDefaults.GetInteger("SystemDebugAnim")));
        // Keep per-pixel alpha for the rounded transparent margin and shadow. Native
        // acrylic is intentionally not requested because it tints the whole borderless
        // surface; None remains a fallback for compositors without alpha support.
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None];
        ApplyFormBackground(settings);
        ApplyTitleAppearance(settings);
        ApplyBackgroundAppearance(settings);
        ApplyNetworkProxy(settings);
        ApplyExperimentalChrome(IsExperimentalHomepageUiEnabled(settings));
        _launchRight?.SetMaximumLogLines(ResolveMaximumLogLines(settings));
        ApplyLaunchPageSettings(settings);
        ApplyHomepageSettings(settings);
    }

    private void ApplyFormBackground(LauncherSettings settings)
    {
        if (this.FindControl<Grid>("PanForm") is not { } form)
            return;

        bool colorful = settings.GetBooleanOption(
            "UiBackgroundColorful",
            LauncherSettingDefaults.GetBoolean("UiBackgroundColorful"));
        // Prefer live theme manager state so ColorMode.System tracks OS preference.
        bool isDarkMode = AvaloniaThemeManager.IsDarkMode;
        ColorTheme theme = isDarkMode ? settings.DarkColor : settings.LightColor;
        string customColor = settings.GetTextOption(
            isDarkMode ? "UiCustomDarkColor" : "UiCustomLightColor",
            isDarkMode ? "#6F8CFF" : "#3D7DFF");
        Color? accentColor = AvaloniaThemeManager.CurrentTheme == ColorTheme.SystemAccent
            ? Avalonia.Application.Current?.PlatformSettings?.GetColorValues().AccentColor1
            : null;
        IReadOnlyDictionary<string, Color> palette = ThemeColorPalette.Create(
            isDarkMode,
            theme,
            accentColor,
            customColor);
        if (!colorful)
        {
            form.Background = new SolidColorBrush(palette["ColorBrushBackground"]);
            return;
        }

        Color first = palette["ColorObject6"];
        Color middle = palette["ColorObject7"];
        Color last = palette["ColorObject8"];
        form.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.9d, 0d, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.1d, 1d, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(first, 0d),
                new GradientStop(middle, 0.4d),
                new GradientStop(last, 1d)
            }
        };
    }

    private void ApplyLaunchPageSettings(LauncherSettings settings)
    {
        _launchLeft?.ConfigureLaunchingHint(settings.GetBooleanOption(
            "UiShowLaunchingHint",
            LauncherSettingDefaults.GetBoolean("UiShowLaunchingHint")));
        _launchRight?.ConfigureDebugLog(settings.GetBooleanOption(
            "SystemDebugMode",
            LauncherSettingDefaults.GetBoolean("SystemDebugMode")));
        if (this.FindControl<StackPanel>("PanHint") is { } hints)
        {
            bool alignRight = settings.GetBooleanOption(
                "UiHintAlignRight",
                LauncherSettingDefaults.GetBoolean("UiHintAlignRight"));
            // WPF: left = Margin 25,0,0,18; right = leave room for PanExtraButtons (~70)
            // Content column only — leave room for PanExtraButtons when right-aligned.
            hints.HorizontalAlignment = alignRight
                ? Avalonia.Layout.HorizontalAlignment.Right
                : Avalonia.Layout.HorizontalAlignment.Left;
            hints.Margin = alignRight
                ? new Thickness(0d, 0d, 70d, 18d)
                : new Thickness(12d, 0d, 0d, 18d);
        }
    }

    private void ApplyTitleAppearance(LauncherSettings settings)
    {
        Avalonia.Controls.Shapes.Path? defaultLogo = this.FindControl<Avalonia.Controls.Shapes.Path>("ShapeTitleLogo");
        Avalonia.Controls.Shapes.Path? hmclWordmark = this.FindControl<Avalonia.Controls.Shapes.Path>("ShapeHMCLTitleLogo");
        MyImage? hmclLogo = this.FindControl<MyImage>("ImageHMCLTitleLogo");
        MyImage? customLogo = this.FindControl<MyImage>("ImageTitleLogo");
        TextBlock? customText = this.FindControl<TextBlock>("LabTitleLogo");
        Grid? titleMain = this.FindControl<Grid>("PanTitleMain");
        Grid? titleLeft = this.FindControl<Grid>("PanTitleLeft");
        if (defaultLogo is null || customLogo is null || customText is null)
            return;

        bool useHmclBranding = AvaloniaThemeManager.CurrentTheme == ColorTheme.HmclBlue;
        int titleType = settings.GetIntegerOption("UiLogoType", LauncherSettingDefaults.GetInteger("UiLogoType"));
        string logoPath = settings.GetTextOption(
            LauncherSettingKeys.UiCustomLogoPath,
            Path.Combine(LauncherSettingsPageBinder.CreateDataDirectory(), "Logo.png"));
        bool hasCustomImage = titleType == 3 && File.Exists(logoPath);
        defaultLogo.IsVisible = !useHmclBranding && (titleType == 1 || titleType == 3 && !hasCustomImage);
        if (hmclWordmark is not null)
            hmclWordmark.IsVisible = useHmclBranding;
        if (hmclLogo is not null)
            hmclLogo.IsVisible = useHmclBranding;
        customText.IsVisible = !useHmclBranding && titleType == 2;
        customLogo.IsVisible = !useHmclBranding && hasCustomImage;
        customText.Text = settings.GetTextOption("UiLogoText", LauncherSettingDefaults.GetText("UiLogoText"));
        if (string.IsNullOrWhiteSpace(customText.Text))
            customText.Text = "PCL N";

        if (hasCustomImage)
        {
            try
            {
                string logoStamp = GetFileStamp(logoPath);
                if (!string.Equals(_titleLogoFile, logoStamp, StringComparison.Ordinal))
                {
                    _titleLogoBitmap?.Dispose();
                    _titleLogoBitmap = new Bitmap(logoPath);
                    _titleLogoFile = logoStamp;
                }
                customLogo.Source = _titleLogoBitmap;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                customLogo.IsVisible = false;
                defaultLogo.IsVisible = true;
            }
        }
        else
        {
            customLogo.Source = null;
            _titleLogoBitmap?.Dispose();
            _titleLogoBitmap = null;
            _titleLogoFile = null;
        }

        if (titleMain is not null)
        {
            titleMain.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            // WPF UiLogoLeft collapses the left star when logo type is None + left-align.
            // For any visible logo/text/image, keep content on the left (column 0) so the
            // title is not optically centered under the chrome buttons.
            bool alignLeft = settings.GetBooleanOption(
                "UiLogoLeft",
                LauncherSettingDefaults.GetBoolean("UiLogoLeft"));
            bool collapseLeadingStar = alignLeft && titleType == 0;
            if (titleMain.ColumnDefinitions.Count >= 3)
            {
                titleMain.ColumnDefinitions[0].Width = collapseLeadingStar
                    ? new GridLength(0d)
                    : new GridLength(1d, GridUnitType.Star);
                titleMain.ColumnDefinitions[1].Width = GridLength.Auto;
                titleMain.ColumnDefinitions[2].Width = new GridLength(1d, GridUnitType.Star);
            }
        }

        if (titleLeft is not null)
        {
            titleLeft.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            // Visible titles (default logo / text / image) always sit on the left.
            // Only logo type None uses the centered Auto column when left-align is off.
            Grid.SetColumn(titleLeft, titleType == 0 ? 1 : 0);
        }
    }

    private void ApplyBackgroundAppearance(LauncherSettings settings)
    {
        Image? image = this.FindControl<Image>("ImageBack");
        MediaElement? video = this.FindControl<MediaElement>("VideoBack");
        if (image is null || video is null)
            return;

        string directory = Path.Combine(LauncherSettingsPageBinder.CreateDataDirectory(), "Backgrounds");
        string[] files = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(IsSupportedBackgroundFile)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
        StringComparer pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        string refreshToken = settings.GetTextOption("UiBackgroundRefreshToken", string.Empty);
        bool refreshRequested = !string.Equals(_backgroundRefreshToken, refreshToken, StringComparison.Ordinal);
        _backgroundRefreshToken = refreshToken;
        string? file = !refreshRequested && _backgroundFile is not null && files.Contains(_backgroundFile, pathComparer)
            ? _backgroundFile
            : files.Length == 0 ? null : files[Random.Shared.Next(files.Length)];
        _backgroundFile = file;
        string? backgroundStamp = file is null ? null : GetFileStamp(file);
        bool isVideo = file is not null && IsVideoBackgroundFile(file);
        if (!string.Equals(_backgroundStamp, backgroundStamp, StringComparison.Ordinal))
        {
            _backgroundBitmap?.Dispose();
            _backgroundBitmap = null;
            _backgroundStamp = backgroundStamp;
            video.Close();
            if (file is not null && isVideo)
            {
                video.Source = new Uri(file, UriKind.Absolute);
            }
            else if (file is not null)
            {
                try
                {
                    _backgroundBitmap = new Bitmap(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    _backgroundStamp = null;
                }
            }
            image.Source = _backgroundBitmap;
        }

        image.IsVisible = _backgroundBitmap is not null;
        video.IsVisible = isVideo && video.Source is not null;
        double opacity = Math.Clamp(
            settings.GetIntegerOption("UiBackgroundOpacity", LauncherSettingDefaults.GetInteger("UiBackgroundOpacity")) / 1000d,
            0d,
            1d);
        image.Opacity = opacity;
        video.Opacity = opacity;
        int blurRadius = settings.GetIntegerOption("UiBackgroundBlur", LauncherSettingDefaults.GetInteger("UiBackgroundBlur"));
        image.Effect = blurRadius > 0 ? new BlurEffect { Radius = blurRadius } : null;
        video.Effect = blurRadius > 0 ? new BlurEffect { Radius = blurRadius } : null;
        int backgroundSuit = settings.GetIntegerOption("UiBackgroundSuit", LauncherSettingDefaults.GetInteger("UiBackgroundSuit"));
        ApplyBackgroundSuit(image, backgroundSuit);
        ApplyBackgroundSuit(video, backgroundSuit);
        UpdateBackgroundVideoPlayback(settings);
    }

    private static bool IsSupportedBackgroundFile(string path) =>
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
        IsVideoBackgroundFile(path);

    private static bool IsVideoBackgroundFile(string path) =>
        path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase);

    private void UpdateBackgroundVideoPlayback(LauncherSettings? settings = null)
    {
        if (this.FindControl<MediaElement>("VideoBack") is not { Source: not null, IsVisible: true } video)
            return;

        settings ??= LauncherSettingsPageBinder.LoadSettings();
        bool pauseForGame = _gameSessionStore.IsRunning && settings.GetBooleanOption(
            "UiAutoPauseVideo",
            LauncherSettingDefaults.GetBoolean("UiAutoPauseVideo"));
        if (WindowState == WindowState.Minimized || pauseForGame)
            video.Pause();
        else if (!video.IsPlaying)
            video.Play();
    }

    private static void ApplyBackgroundSuit(Image image, int mode)
    {
        image.Stretch = mode switch
        {
            2 => Stretch.Uniform,
            3 => Stretch.Fill,
            >= 1 => Stretch.None,
            _ => Stretch.UniformToFill
        };
        image.HorizontalAlignment = mode switch
        {
            5 or 7 => Avalonia.Layout.HorizontalAlignment.Left,
            6 or 8 => Avalonia.Layout.HorizontalAlignment.Right,
            _ => Avalonia.Layout.HorizontalAlignment.Center
        };
        image.VerticalAlignment = mode switch
        {
            5 or 6 => Avalonia.Layout.VerticalAlignment.Top,
            7 or 8 => Avalonia.Layout.VerticalAlignment.Bottom,
            _ => Avalonia.Layout.VerticalAlignment.Center
        };
    }

    private static int ResolveMaximumLogLines(LauncherSettings settings)
    {
        int value = settings.GetIntegerOption("SystemMaxLog", LauncherSettingDefaults.GetInteger("SystemMaxLog"));
        return value switch
        {
            <= 5 => value * 10 + 50,
            <= 13 => value * 50 - 150,
            <= 28 => value * 100 - 800,
            _ => int.MaxValue
        };
    }

    private static void ApplyNetworkProxy(LauncherSettings settings)
    {
        int proxyType = settings.GetIntegerOption(
            "SystemHttpProxyType",
            LauncherSettingDefaults.GetInteger("SystemHttpProxyType"));
        if (proxyType == 1)
        {
            HttpClient.DefaultProxy = SystemDefaultProxy;
            return;
        }

        if (proxyType != 2)
        {
            HttpClient.DefaultProxy = new WebProxy();
            return;
        }

        string address = settings.GetTextOption("SystemHttpProxy", LauncherSettingDefaults.GetText("SystemHttpProxy"));
        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? proxyAddress))
            return;

        WebProxy proxy = new(proxyAddress);
        string username = settings.GetTextOption(
            "SystemHttpProxyCustomUsername",
            LauncherSettingDefaults.GetText("SystemHttpProxyCustomUsername"));
        if (!string.IsNullOrWhiteSpace(username))
        {
            proxy.Credentials = new NetworkCredential(
                username,
                settings.GetTextOption(
                    "SystemHttpProxyCustomPassword",
                    LauncherSettingDefaults.GetText("SystemHttpProxyCustomPassword")));
        }
        HttpClient.DefaultProxy = proxy;
    }

    private void ApplyHomepageSettings(LauncherSettings settings)
    {
        if (_launchRight is null)
            return;

        int mode = settings.GetIntegerOption("UiCustomType", LauncherSettingDefaults.GetInteger("UiCustomType"));
        string networkAddress = settings.GetTextOption("UiCustomNet", LauncherSettingDefaults.GetText("UiCustomNet"));
        int preset = settings.GetIntegerOption("UiCustomPreset", LauncherSettingDefaults.GetInteger("UiCustomPreset"));
        string signature = $"{mode.ToString(CultureInfo.InvariantCulture)}|{preset.ToString(CultureInfo.InvariantCulture)}|{networkAddress}";
        if (mode != 1 && string.Equals(_homepageSignature, signature, StringComparison.Ordinal))
            return;

        _homepageSignature = signature;
        _homepageLoadCancellation?.Cancel();
        _homepageLoadCancellation?.Dispose();
        _homepageLoadCancellation = null;

        switch (mode)
        {
            case 1:
                LoadLocalHomepage();
                break;
            case 2 when Uri.TryCreate(networkAddress, UriKind.Absolute, out Uri? address):
                _homepageLoadCancellation = new CancellationTokenSource();
                _ = LoadNetworkHomepageAsync(address, _homepageLoadCancellation.Token);
                break;
            case 3:
                _launchRight.ClearCustomContent();
                break;
            default:
                _launchRight.ClearCustomContent();
                break;
        }
    }

    private void LoadLocalHomepage()
    {
        string directory = Path.Combine(LauncherSettingsPageBinder.CreateDataDirectory(), "CustomHomepage");
        string? file = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(static path => path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
                                      path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                                      path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()
            : null;
        if (file is null)
        {
            _launchRight?.LoadTextContent("自定义主页目录中没有 .txt、.md 或 .xaml 文件。");
            return;
        }

        try
        {
            _launchRight?.LoadTextContent(File.ReadAllText(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _launchRight?.LoadTextContent("读取自定义主页失败：" + ex.Message);
        }
    }

    private async Task LoadNetworkHomepageAsync(Uri address, CancellationToken cancellationToken)
    {
        try
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(15) };
            string content = await client.GetStringAsync(address, cancellationToken).ConfigureAwait(false);
            if (content.Length > 1024 * 1024)
                content = content[..(1024 * 1024)];
            await Dispatcher.UIThread.InvokeAsync(() => _launchRight?.LoadTextContent(content));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                _launchRight?.LoadTextContent("下载自定义主页失败：" + ex.Message));
        }
    }

    private static string GetFileStamp(string path)
    {
        FileInfo file = new(path);
        return string.Concat(
            file.FullName,
            "|",
            file.Length.ToString(CultureInfo.InvariantCulture),
            "|",
            file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
    }

    private void RefreshNavigationText()
    {
        foreach (MyListItem item in GetNavItems())
        {
            if (!TryGetNavRoute(item, out NavigationRouteId route))
                continue;
            item.Title = route.Value switch
            {
                DesktopNavigationRegistry.LaunchRouteValue => AvaloniaLocalizationManager.GetText("Main.TopTitle.Launch", "启动"),
                DesktopNavigationRegistry.DownloadRouteValue => AvaloniaLocalizationManager.GetText("Main.TopTitle.Download", "安装"),
                DesktopNavigationRegistry.CommunityRouteValue => AvaloniaLocalizationManager.GetText("Main.TopTitle.Community", "资源"),
                DesktopNavigationRegistry.SettingsRouteValue => AvaloniaLocalizationManager.GetText("Main.TopTitle.Settings", "设置"),
                _ => item.Title
            };
        }
    }

    private void BeginPageChangeAnimation(NavigationRouteId route)
    {
        _pendingNavRoute = route;
        if (this.FindControl<Control>("PanMainRight") is not { } right)
        {
            ApplyPagePlaceholder(route);
            return;
        }

        ModAnimation.AniStart(
            new List<ModAnimation.AniData>
            {
                ModAnimation.AaOpacity(right, -right.Opacity, MotionTokens.NavCrossfadeOutMs),
                ModAnimation.AaCode(() =>
                {
                    ApplyPagePlaceholder(route);
                    right.Opacity = 0d;
                }, after: true),
                ModAnimation.AaOpacity(right, 1d, MotionTokens.NavCrossfadeInMs),
                // Always force full opacity when the sequence finishes — guards against
                // mid-animation AniStop / nested page swaps leaving the right pane gray.
                ModAnimation.AaCode(() => right.Opacity = 1d, after: true)
            },
            "FrmMain PageChangeRight");
    }

    private IEnumerable<MyListItem> GetNavItems()
    {
        if (this.FindControl<Panel>("PanTitleSelect") is not { } panel)
            yield break;

        foreach (Control child in panel.Children)
        {
            if (child is MyListItem item)
                yield return item;
        }
    }

    private static bool TryGetNavRoute(MyListItem item, out NavigationRouteId route)
    {
        route = default;
        return item.Tag switch
        {
            NavigationRouteId value => SetRoute(value, out route),
            string text when !string.IsNullOrWhiteSpace(text) => SetRoute(NavigationRouteId.Parse(text), out route),
            _ => false
        };
    }

    private static bool SetRoute(NavigationRouteId value, out NavigationRouteId route)
    {
        route = value;
        return true;
    }

    private void CaptureShowAnimationTransforms()
    {
        if (Content is not Control root)
            return;

        _showAnimationRoot = root;
        if (root.RenderTransform is not TransformGroup group)
            return;

        foreach (ITransform transform in group.Children)
        {
            _showAnimationRotate ??= transform as RotateTransform;
            _showAnimationTranslate ??= transform as TranslateTransform;
        }
    }

    private void StartShowAnimation()
    {
        if (_showAnimationStarted)
            return;

        _showAnimationStarted = true;
        ModAnimation.AniStart(
            new List<ModAnimation.AniData>
            {
                ModAnimation.AaOpacity(this, _targetWindowOpacity - Opacity, 250, 100),
                ModAnimation.AaDouble(
                    value =>
                    {
                        if (_showAnimationTranslate is not null)
                            _showAnimationTranslate.Y += value;
                    },
                    -(_showAnimationTranslate?.Y ?? 0d),
                    600,
                    100,
                    new ModAnimation.AniEaseOutBack(ModAnimation.AniEasePower.Weak)),
                ModAnimation.AaDouble(
                    value =>
                    {
                        if (_showAnimationRotate is not null)
                            _showAnimationRotate.Angle += value;
                    },
                    -(_showAnimationRotate?.Angle ?? 0d),
                    500,
                    100,
                    new ModAnimation.AniEaseOutBack(ModAnimation.AniEasePower.Weak)),
                ModAnimation.AaCode(() =>
                {
                    if (_showAnimationRoot is not null)
                        _showAnimationRoot.RenderTransform = null;
                }, after: true)
            },
            "FrmMain Load");
    }

    private static double EaseOutCubic(double progress)
    {
        double inverse = 1d - progress;
        return 1d - inverse * inverse * inverse;
    }



}

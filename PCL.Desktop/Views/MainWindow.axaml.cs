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
using PCL.Application.Hosting;
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
using PCL.Desktop.Controls.Motion;
using PCL.Desktop.Diagnostics;
using PCL.Desktop.Telemetry;
using PCL.Desktop.Features.Community;
using PCL.Desktop.Hosting;
using PCL.Desktop.Legal;
using PCL.Desktop.Localization;
using PCL.Desktop.Messaging;
using PCL.Desktop.Session;
using PCL.Desktop.Shell;
using PCL.Desktop.Theme;
using PCL.Desktop.Platform;
using PCL.Desktop.Features.Downloads.Views;
using PCL.Desktop.Features.Downloads;
using PCL.Desktop.Features.Instances;
using PCL.Desktop.Features.Instances.Views;
using PCL.Desktop.Features.Launching;
using PCL.Desktop.Features.Launching.Views;
using PCL.Desktop.Features.Settings;
using PCL.Desktop.Features.Tasks;
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
    private double _navExpandedWidth = 200d;
    private readonly List<IDisposable> navMotionScopes = [];
    private int navMotionGeneration;
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
    private readonly LaunchHomeSurface _launchHomeSurface;
    private readonly StartMinecraftUseCase _startMinecraft;
    private readonly DownloadFeatureSurface _downloadSurface;
    private readonly SettingsFeatureSurface _settingsSurface;
    private readonly CommunityFeatureSurface _communitySurface;
    private readonly TaskManagerSurface _taskManagerSurface;
    private readonly InstancesManageSurface _instancesManage;
    private readonly LaunchLoginSurface _launchLoginSurface;
    private bool _isDisposed;
    private PageDownloadLeft? _downloadLeft;
    private PageDownloadInstall? _downloadInstallPage;
    private PageCommunityLeft? _communityLeft;
    private PageCommunityRight? _communityRight;
    private PageCommunityDetail? _communityDetail;
    private PageCommunityFavoritesRight? _communityFavoritesRight;
    private (CommunityResourceCategory Category, string Directory)? _communityDownloadTarget;
    private readonly CommunityFavoritesStore _communityFavorites;
    private PageSpeedLeft? _speedLeft;
    private PageSpeedRight? _speedRight;
    private LaunchInstanceInfo? _managedInstance;
    private bool _isTitleSubPageVisible;
    private Action? _titleInnerBackAction;
    private readonly UiUpdateCoalescer _taskUiCoalescer;
    private MyScrollViewer? _backButtonScrollViewer;
    private CancellationTokenSource? _launchCancellation;
    private CancellationTokenSource? _microsoftLoginCancellation;
    private CancellationTokenSource? _littleSkinLoginCancellation;
    private readonly MinecraftVanillaInstallService _minecraftInstallService = new();
    private readonly MinecraftLaunchCoordinator _launchCoordinator;
    private readonly MinecraftAiRepairAdvisor _minecraftAiRepairAdvisor = new();
    private readonly ThirdPartyAuthService _thirdPartyAuthService = new();
    private readonly IMicrosoftMinecraftAuthService _microsoftAuthService;
    private readonly ILittleSkinOAuthService _littleSkinOAuthService;
    private readonly IMinecraftCapeService _minecraftCapeService;
    private readonly IMinecraftSkinService _minecraftSkinService;
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
    private readonly IDisposable _windowStateSubscription;
    private string? _registeredPageSurfaceId;

    private const double NavCollapsedWidth = 50d;
    private const int NavAnimDuration = 200;

    private static readonly NavigationRouteId LaunchRoute = DesktopNavigationRegistry.LaunchRoute;
    private static readonly NavigationRouteId DownloadRoute = DesktopNavigationRegistry.DownloadRoute;
    private static readonly NavigationRouteId CommunityRoute = DesktopNavigationRegistry.CommunityRoute;
    private static readonly NavigationRouteId SettingsRoute = DesktopNavigationRegistry.SettingsRoute;

    public MainWindow()
        : this(new MicrosoftMinecraftAuthService(), littleSkinOAuthService: new LittleSkinOAuthService())
    {
    }

    public MainWindow(
        IMicrosoftMinecraftAuthService microsoftAuthService,
        Action<string>? externalUrlOpener = null,
        Func<string, Task>? clipboardWriter = null,
        ILittleSkinOAuthService? littleSkinOAuthService = null,
        IMinecraftCapeService? minecraftCapeService = null,
        IMinecraftSkinService? minecraftSkinService = null)
    {
        _microsoftAuthService = microsoftAuthService ?? throw new ArgumentNullException(nameof(microsoftAuthService));
        _littleSkinOAuthService = littleSkinOAuthService ?? new LittleSkinOAuthService();
        _minecraftCapeService = minecraftCapeService ?? new MinecraftCapeService();
        _minecraftSkinService = minecraftSkinService ?? new MinecraftSkinService();
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
        _launchHomeSurface = DesktopCompositionRoot.GetRequiredService<LaunchHomeSurface>();
        _startMinecraft = DesktopCompositionRoot.GetRequiredService<StartMinecraftUseCase>();
        _downloadSurface = DesktopCompositionRoot.GetRequiredService<DownloadFeatureSurface>();
        _settingsSurface = DesktopCompositionRoot.GetRequiredService<SettingsFeatureSurface>();
        _communitySurface = DesktopCompositionRoot.GetRequiredService<CommunityFeatureSurface>();
        _communityFavorites = DesktopCompositionRoot.GetRequiredService<CommunityFavoritesStore>();
        _taskManagerSurface = DesktopCompositionRoot.GetRequiredService<TaskManagerSurface>();
        _instancesManage = DesktopCompositionRoot.GetRequiredService<InstancesManageSurface>();
        _launchLoginSurface = DesktopCompositionRoot.GetRequiredService<LaunchLoginSurface>();
        _taskUiCoalescer = new UiUpdateCoalescer(
            () =>
            {
                UpdateTaskManagerViews();
                RefreshTaskManagerButton();
            },
            intervalMs: 50);
        BindStartMinecraftUseCase();
        _extraDockViewModel.PropertyChanged += ExtraDockViewModel_PropertyChanged;
        AvaloniaXamlLoader.Load(this);
        DesktopRenderBootstrap.ApplyCompositorHints(this);
        if (this.FindControl<Control>("PanBack") is { } panBack)
            DesktopRenderBootstrap.ApplyCompositorHints(panBack);
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
        DesktopHostNotifications.Instance.Attach(OnHostNotification);
        DesktopHostNotifications.Instance.AttachChoice(OnHostChoiceAsync);
        DesktopHostBackgroundTasks.Instance.Attach(BeginHostBackgroundTask);
        DesktopHost.Current.Navigation.Changed += NavigationRegistryChanged;
        DesktopHostNavigation.Instance.Attach(NavigateToHostRoute);
        _ = LoadProfilesAsync();
        SelectNavRoute(LaunchRoute, animate: false);
    }

    private void OnHostNotification(string message, bool critical) =>
        ShowHint(message, critical);

    private Task<int> OnHostChoiceAsync(
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

    private void NavigateToHostRoute(string route)
    {
        if (!string.IsNullOrWhiteSpace(route))
            SelectNavRoute(NavigationRouteId.Parse(route), animate: true);
    }

    private void RefreshTitleButtonsBeforeFirstFrame()
    {
        foreach (string name in new[] { "BtnTitleClose", "BtnTitleMax", "BtnTitleMin", "BtnTitleHelp" })
            this.FindControl<MyIconButton>(name)?.RefreshAnim();
    }

    private void ApplyMacOsChromeIfNeeded()
    {
        if (!MacOsWindowChrome.IsActivePlatform)
            return;

        MacOsWindowChrome.Apply(this);
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

    private async void FormMain_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (!_profileSavesDrainedForClose && HasPendingProfileSaves())
        {
            e.Cancel = true;
            if (_profileSaveDrainRequested)
                return;

            _profileSaveDrainRequested = true;
            DesktopFileLog.Info("AccountProfile", "关闭前等待账户档案保存队列完成。");
            try
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
                await DrainProfileSaveQueueAsync(timeout.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                DesktopFileLog.Warn("AccountProfile", "关闭前等待账户档案保存超时；将继续关闭启动器。");
            }
            catch (Exception ex)
            {
                DesktopFileLog.Warn("AccountProfile", "关闭前等待账户档案保存失败；将继续关闭启动器。", ex);
            }
            finally
            {
                _profileSavesDrainedForClose = true;
                _profileSaveDrainRequested = false;
            }

            Close();
            return;
        }

        DesktopFileLog.Info("Window", "主窗口正在关闭。");
        LauncherSettingsPageBinder.SettingsChanged -= LauncherSettingsChanged;
        AvaloniaThemeManager.ThemeChanged -= ThemeChanged;
        AvaloniaLocalizationManager.LanguageChanged -= LocalizationChanged;
        CancellationTokenSource[] trackedCancellations = _taskCancellations.Values.ToArray();
        CancellationTokenSource? launchCancellation = _launchCancellation;
        MediaElement? backgroundVideo = this.FindControl<MediaElement>("VideoBack");

        // Cancellation callbacks and native media/model shutdown may block. Do
        // not run them inside Avalonia's synchronous Closing dispatch.
        UnhandledExceptionGuard.Observe(
            Task.Run(() =>
            {
                foreach (CancellationTokenSource cancellation in trackedCancellations)
                {
                    try { cancellation.Cancel(); }
                    catch (ObjectDisposedException) { }
                }

                try { launchCancellation?.Cancel(); }
                catch (ObjectDisposedException) { }

                _minecraftAiRepairAdvisor.StopLocalServer();
                backgroundVideo?.Stop();
            }),
            "MainWindow.CloseCleanup");
        DesktopFileLog.Info("Window", "主窗口关闭清理已转入后台；关闭事件可以立即返回。");
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

        if (route.Equals(CommunityRoute.Value))
            _communityDownloadTarget = null;
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

        double targetWidth = _isNavExpanded ? _navExpandedWidth : NavCollapsedWidth;
        BeginNavLayoutTransition(navLayer);
        SetNavWidth(navLayer, targetWidth);
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
        // On macOS the bundle's CFBundleIconFile is the application identity.
        // Setting a per-window source icon would replace the framed .icns with
        // the raw cross-platform artwork in the Dock and app switcher.
        if (OperatingSystem.IsMacOS())
            return;

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

    private void BeginNavLayoutTransition(Control navLayer)
    {
        ClearNavLayoutTransition();
        int generation = ++navMotionGeneration;
        if (!ControlVisualHelpers.ShouldAnimate(navLayer))
            return;

        TimeSpan duration = TimeSpan.FromMilliseconds(NavAnimDuration);
        AddNavMotionScope(CompositionMotion.EnableLayoutTransition(
            navLayer,
            duration,
            animateOffset: false,
            animateSize: true));

        if (this.FindControl<Control>("PanMain") is { } main)
        {
            AddNavMotionScope(CompositionMotion.EnableLayoutTransition(
                main,
                duration,
                animateOffset: true,
                animateSize: true));
        }

        if (this.FindControl<Control>("PanHint") is { } hint)
        {
            AddNavMotionScope(CompositionMotion.EnableLayoutTransition(
                hint,
                duration,
                animateOffset: true,
                animateSize: false));
        }

        UnhandledExceptionGuard.Observe(
            CompleteNavLayoutTransition(generation),
            "MainWindow.NavigationLayoutTransition");
    }

    private void AddNavMotionScope(IDisposable? scope)
    {
        if (scope is not null)
            navMotionScopes.Add(scope);
    }

    private async Task CompleteNavLayoutTransition(int generation)
    {
        await Task.Delay(NavAnimDuration + 50).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (generation == navMotionGeneration)
                ClearNavLayoutTransition();
        });
    }

    private void ClearNavLayoutTransition()
    {
        foreach (IDisposable scope in navMotionScopes)
            scope.Dispose();
        navMotionScopes.Clear();
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
        if (!route.Equals(CommunityRoute.Value))
            _communityDownloadTarget = null;

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
        LauncherTelemetry.CaptureEvent(
            "page_opened",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["page"] = TelemetryDataPolicy.NormalizeName(route.Value)
            });

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
        TelemetryOperation pageLoad = LauncherTelemetry.StartOperation(
            "page.load." + TelemetryDataPolicy.NormalizeName(descriptor.Route.Value),
            "page.load");
        PageCreateContext context = new(descriptor.Route.Value, DesktopHost.Current.Services, _desktopPageContext);
        ValueTask<DesktopMainPage> createTask;
        try
        {
            createTask = _pageAdapter.CreateMainPageAsync(descriptor.Provider, context, CancellationToken.None);
        }
        catch (Exception ex)
        {
            pageLoad.Fail(ex);
            LauncherTelemetry.CaptureException(ex, "page.load");
            ApplyPageCreationError(descriptor.Title, ex);
            return;
        }

        if (createTask.IsCompletedSuccessfully)
        {
            ApplyRegisteredMainPage(createTask.Result);
            pageLoad.Complete();
            return;
        }

        ApplyRegisteredMainPage(CreateLoadingMainPage(descriptor.Title));
        _ = CompleteRegisteredPageAsync(createTask.AsTask(), requestId, descriptor.Title, pageLoad);
    }

    private async Task CompleteRegisteredPageAsync(
        Task<DesktopMainPage> createTask,
        int requestId,
        string pageTitle,
        TelemetryOperation pageLoad)
    {
        try
        {
            DesktopMainPage page = await createTask.ConfigureAwait(true);
            if (requestId != _registeredPageRequestId)
            {
                pageLoad.Cancel();
                return;
            }

            ApplyRegisteredMainPage(page);
            pageLoad.Complete();
        }
        catch (Exception ex)
        {
            pageLoad.Fail(ex);
            LauncherTelemetry.CaptureException(ex, "page.load");
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

        RegisterCurrentPageSurface(page.Right);

        if (page.Title is { Length: > 0 } title)
            EnterTitleSubPage(title);
        else
            ExitTitleSubPage();

        RefreshBackToTopBinding();
        page.Activated?.Invoke();
        rightHost.Opacity = 1d;
    }

    private void RegisterCurrentPageSurface(Control page)
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
        if (_registeredPageSurfaceId is { } previous &&
            !string.Equals(previous, surfaceId, StringComparison.OrdinalIgnoreCase))
        {
            DesktopHostUiComposition.Instance.UnregisterTarget(previous);
        }
        if (surfaceId is null)
            return;
        _registeredPageSurfaceId = surfaceId;
        DesktopHostUiComposition.Instance.RegisterTarget(surfaceId, page);
    }

    private DesktopMainPage CreateLaunchMainPage()
    {
        LauncherSettings launchSettings = LauncherSettingsPageBinder.LoadSettings();
        bool experimental = _launchHomeProfile.UseExperimentalFullPageHome() ||
                            IsExperimentalHomepageUiEnabled(launchSettings);
        ApplyExperimentalChrome(experimental);

        _launchHomeSurface.WireOnce(this, CreateLaunchHomeBindings(launchSettings));
        DesktopMainPage page = _launchHomeSurface.CreateMainPage(launchSettings, experimental);
        SyncLaunchFieldsFromSurface();
        return page;
    }

    private void SyncLaunchFieldsFromSurface()
    {
        _launchLeft = _launchHomeSurface.Home;
        _launchRight = _launchHomeSurface.ClassicRight;
        _launchHomeExperimental = _launchHomeSurface.ExperimentalHome;
        _useExperimentalLaunchHome = _launchHomeSurface.UseExperimental;
    }

    private LaunchHomeBindings CreateLaunchHomeBindings(LauncherSettings launchSettings) =>
        new()
        {
            NavigateDownload = () => SelectNavRoute(DownloadRoute, animate: true),
            NavigateInstanceSelect = ApplyInstanceSelectPage,
            ManageInstance = instance => ApplyInstanceManagePage(instance),
            CancelLaunch = () =>
            {
                _launchCancellation?.Cancel();
                HandleStatusMessage("已取消启动。");
            },
            StatusMessage = HandleStatusMessage,
            OpenLoginPage = ApplyLaunchLoginPage,
            StartMinecraft = request => _startMinecraft.ExecuteAsync(request),
            ActivateShortcut = ActivateLaunchShortcutAsync,
            HideCommunityHint = PromptHideCommunityHint,
            ApplyLaunchPageSettings = () => ApplyLaunchPageSettings(launchSettings),
            ApplyHomepageSettings = () => ApplyHomepageSettings(launchSettings),
            ResolveMaximumLogLines = () => ResolveMaximumLogLines(launchSettings),
            EnsureFoldersLoaded = EnsureMinecraftFoldersLoaded,
            SelectedMinecraftRoot = () => _folderStore.SelectedRoot,
            PreferredInstanceDirectory = LoadPreferredInstanceDirectory,
            ShowLaunchingHint = () =>
            {
                LauncherSettings settings = LauncherSettingsPageBinder.LoadSettings();
                return settings.GetBooleanOption(
                    "UiShowLaunchingHint",
                    LauncherSettingDefaults.GetBoolean("UiShowLaunchingHint"));
            }
        };

    private void EnsureExperimentalLaunchHome(LauncherSettings launchSettings)
    {
        // Keep for call sites that pre-warm experimental home before navigation.
        bool experimental = _launchHomeProfile.UseExperimentalFullPageHome() ||
                            IsExperimentalHomepageUiEnabled(launchSettings);
        _launchHomeSurface.WireOnce(this, CreateLaunchHomeBindings(launchSettings));
        _ = _launchHomeSurface.CreateMainPage(launchSettings, experimental);
        SyncLaunchFieldsFromSurface();
    }

    private void WireLaunchHomeSurface(PageLaunchLeft page)
    {
        // Legacy entry points (e.g. CreateLaunchLeftPage) still wire through surface bindings.
        LaunchHomeBindings bindings = CreateLaunchHomeBindings(LauncherSettingsPageBinder.LoadSettings());
        page.DownloadRequested += (_, _) => bindings.NavigateDownload();
        page.InstanceSelectRequested += (_, _) => bindings.NavigateInstanceSelect();
        page.InstanceSettingsRequested += (_, _) =>
        {
            if (page.SelectedInstance is not null)
                bindings.ManageInstance(page.SelectedInstance);
        };
        page.CancelLaunchRequested += (_, _) => bindings.CancelLaunch();
        page.StatusMessage += (_, message) => bindings.StatusMessage(message);
        page.LoginPageRequested += (_, type) => bindings.OpenLoginPage(page, type);
        page.LaunchRequested += (_, instance) =>
            _ = bindings.StartMinecraft(new StartMinecraftRequest(page, instance));
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

        StartMinecraftRequest request = pin.Kind == LaunchShortcutKind.Server
            ? new StartMinecraftRequest(launchHome, instance, ServerAddress: pin.Target)
            : new StartMinecraftRequest(launchHome, instance, WorldName: pin.Target);
        await _startMinecraft.ExecuteAsync(request).ConfigureAwait(true);
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
            HostShellLifecycleEvents.PublishReady();
            return;
        }

        StartShowAnimation();
        // The coordinator performs synchronous settings/lock work before its
        // first incomplete await. Keep that work off the UI thread so the
        // initial opacity animation and Window.Show() cannot be starved.
        UnhandledExceptionGuard.Observe(
            Task.Run(() => LauncherUpdateCoordinator.Current.StartAutomaticUpdateOnceAsync()),
            "LauncherUpdateCoordinator.AutomaticStartup");
        // First-run chain: community welcome → special build notice (no EULA gate).
        Dispatcher.UIThread.Post(MaybeShowFirstRunDialogs, DispatcherPriority.Background);
        DesktopFileLog.Info("Window", "主窗口首帧任务已排队；显现动画与后台更新检查均已启动。");
    }

    private void MaybeShowFirstRunDialogs()
    {
        // Headless unit tests / automation never click dialogs; the chain would hang the UI dispatcher.
        if (ShouldSuppressStartupDialogs())
            return;

        try
        {
            LauncherSettings settings = LauncherSettingsPageBinder.LoadSettings();
            // Legal acceptance (PCL-N-Edition terms + privacy) must pass before other first-run notices.
            MaybeShowLegalAcceptance(
                settings,
                () => MaybeShowCommunityWelcome(
                    settings,
                    () => MaybeShowSpecialVersionNotice(OnStartupNoticesCompleted)));
        }
        catch (Exception ex)
        {
            DesktopFileLog.Warn("FirstRun", "首次运行引导加载失败，将继续显示特殊版本提示。", ex);
            MaybeShowSpecialVersionNotice(OnStartupNoticesCompleted);
        }
    }

    private void MaybeShowLegalAcceptance(LauncherSettings settings, Action completed)
    {
        string accepted = settings.GetTextOption(
            EmbeddedLegalDocuments.SettingsKeyAcceptedVersion,
            string.Empty);
        if (string.Equals(accepted, EmbeddedLegalDocuments.DocumentVersion, StringComparison.Ordinal))
        {
            completed();
            return;
        }

        string title = AvaloniaLocalizationManager.GetText(
            "Legal.FirstRun.Title",
            "用户协议与隐私保护");
        string body;
        try
        {
            body = EmbeddedLegalDocuments.BuildFirstRunAcceptanceMarkdown();
        }
        catch (Exception ex)
        {
            DesktopFileLog.Warn("FirstRun", "无法加载嵌入的法律文档。", ex);
            body =
                "无法加载嵌入的《用户服务协议》与《隐私保护协议》。\n\n" +
                "请从官方渠道重新获取安装包。若继续使用，表示你确认已另行阅读并同意相关协议。";
        }

        string accept = AvaloniaLocalizationManager.GetText("Legal.FirstRun.Accept", "我已阅读并同意");
        string decline = AvaloniaLocalizationManager.GetText("Legal.FirstRun.Decline", "不同意并退出");

        ShowMarkdownDialog(
            title,
            body,
            result =>
            {
                if (result != 1)
                {
                    Close();
                    return;
                }

                LauncherSettingsPageBinder.UpdateSettings(current =>
                {
                    current.SetTextOption(
                        EmbeddedLegalDocuments.SettingsKeyAcceptedVersion,
                        EmbeddedLegalDocuments.DocumentVersion);
                    return current;
                });
                completed();
            },
            accept,
            decline,
            isWarn: true);
    }

    private void OnStartupNoticesCompleted()
    {
        HostShellLifecycleEvents.PublishReady();
        _ = MaybeShowLauncherAnnouncementsAsync();
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
                LauncherSettingsPageBinder.UpdateSettings(current =>
                {
                    current.SetTextOption("UiCommunityNoticeVersion", currentVersion);
                    return current;
                });
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
            ShowLauncherAnnouncement(announcements, 0, seen);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            DesktopFileLog.Warn("Announcement", "启动器公告获取失败；不会阻塞启动。", exception);
        }
    }

    private void ShowLauncherAnnouncement(
        IReadOnlyList<LauncherAnnouncement> announcements,
        int index,
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
                    string serializedSeen = string.Join('\n', seen.TakeLast(200));
                    LauncherSettingsPageBinder.UpdateSettings(current =>
                    {
                        current.SetTextOption("SystemAnnouncementSeen", serializedSeen);
                        return current;
                    });
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
                ShowLauncherAnnouncement(announcements, index + 1, seen);
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
        ApplyExperimentalChrome(IsExperimentalHomepageUiEnabled());
        _downloadSurface.Configure(
            this,
            CreateDownloadInstallPage,
            (_, args) => ApplyDownloadRightPage(args.Page));
        DesktopMainPage page = _downloadSurface.CreateMainPage();
        _downloadLeft = _downloadSurface.Left;
        return page;
    }

    private DesktopMainPage CreateCommunityMainPage()
    {
        WireCommunitySurface();
        DesktopMainPage page = _communitySurface.CreateMainPage();
        SyncCommunityFieldsFromSurface();
        return page;
    }

    private void WireCommunitySurface()
    {
        _communitySurface.WireOnce(this, new CommunityFeatureBindings
        {
            Favorites = _communityFavorites,
            ApplyRightPage = ApplyCommunityRightPage,
            OpenDetailAsync = OpenCommunityDetailAsync,
            DownloadAsync = DownloadCommunityResourceAsync,
            CategoryChanged = category =>
            {
                if (_communityDownloadTarget is { } target && target.Category != category)
                    _communityDownloadTarget = null;
            },
            CloseDetail = CloseCommunityDetail,
            OpenUrl = url =>
            {
                if (!string.IsNullOrWhiteSpace(url))
                    OpenExternalUrl(url);
            },
            ShowMessage = (title, message) => ShowTextDialog(title, message, "知道了"),
            PromptInput = request => ShowInputDialog(
                request.Title,
                request.Caption,
                request.Content,
                request.HintText,
                request.Complete,
                maxLength: request.MaxLength),
            Confirm = request => ShowConfirmDialog(
                request.Title,
                request.Caption,
                request.Complete,
                request.PrimaryButton,
                isWarn: request.IsWarning)
        });
    }

    private void SyncCommunityFieldsFromSurface()
    {
        _communityLeft = _communitySurface.Left;
        _communityRight = _communitySurface.Right;
        _communityDetail = _communitySurface.Detail;
        _communityFavoritesRight = _communitySurface.FavoritesRight;
    }

    private PageCommunityLeft CreateCommunityLeftPage(PageCommunityRight rightPage)
    {
        // Legacy path: ensure surface pages, ignore pre-created right (surface owns pairing).
        _ = rightPage;
        WireCommunitySurface();
        PageCommunityLeft left = _communitySurface.EnsureLeft();
        SyncCommunityFieldsFromSurface();
        return left;
    }

    private PageCommunityRight CreateCommunityRightPage()
    {
        WireCommunitySurface();
        PageCommunityRight right = _communitySurface.EnsureRight();
        SyncCommunityFieldsFromSurface();
        return right;
    }

    private PageCommunityDetail CreateCommunityDetailPage()
    {
        WireCommunitySurface();
        PageCommunityDetail detail = _communitySurface.EnsureDetail();
        SyncCommunityFieldsFromSurface();
        return detail;
    }

    private PageCommunityFavoritesRight CreateCommunityFavoritesRightPage()
    {
        WireCommunitySurface();
        PageCommunityFavoritesRight favorites = _communitySurface.EnsureFavorites();
        SyncCommunityFieldsFromSurface();
        return favorites;
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
        WireCommunitySurface();
        PageCommunityDetail detail = _communitySurface.EnsureDetail();
        _ = _communitySurface.EnsureRight();
        _ = _communitySurface.EnsureLeft();
        SyncCommunityFieldsFromSurface();

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
        if (!ReferenceEquals(oldRight, detail))
        {
            oldRight?.PageOnExit();
            rightHost.Child = detail;
            RefreshBackToTopBinding();
            detail.PageOnEnter();
        }

        EnterTitleSubPage(entry.Title);
        _titleInnerBackAction = CloseCommunityDetail;
        await detail.ShowAsync(entry, category, options).ConfigureAwait(true);
    }

    private void CloseCommunityDetail()
    {
        _titleInnerBackAction = null;
        WireCommunitySurface();
        PageCommunityLeft left = _communitySurface.EnsureLeft();
        MyPageRight target = _communitySurface.ResolveListRight();
        SyncCommunityFieldsFromSurface();

        if (this.FindControl<Border>("PanMainLeft") is { } leftHost)
        {
            if (!ReferenceEquals(leftHost.Child, left))
            {
                leftHost.Child = left;
                left.TriggerShowAnimation();
            }
        }

        if (this.FindControl<Border>("PanMainRight") is { } rightHost)
        {
            MyPageRight? oldRight = rightHost.Child as MyPageRight;
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

    private Task DownloadCommunityResourceAsync(CommunityResourceDownloadRequest request) =>
        CommunityDownloadOrchestrator.RunAsync(
            request,
            new CommunityDownloadHost
            {
                GetSelectedInstance = () => _launchLeft?.SelectedInstance ?? _managedInstance,
                GetTargetDirectoryOverride = category =>
                    _communityDownloadTarget is { } target && target.Category == category
                        ? target.Directory
                        : null,
                CloseDetailIfOpen = () =>
                {
                    if (this.FindControl<Border>("PanMainRight")?.Child is PageCommunityDetail)
                        CloseCommunityDetail();
                },
                CreateTaskId = projectId => CreateTaskId("community", projectId),
                RegisterTrackedTask = RegisterTrackedTask,
                UnregisterTrackedTask = UnregisterTrackedTask,
                TrackTaskBegin = TrackTaskBegin,
                TrackTaskProgress = TrackTaskProgress,
                TrackTaskFinished = TrackTaskFinished,
                TrackTaskFailed = TrackTaskFailed,
                AppendLog = message => _launchRight?.AppendLog(message),
                ShowHint = ShowHint,
                TruncateHint = message => TruncateHint(message),
                PickSaveAsPathAsync = async (title, suggestedFileName) =>
                {
                    IStorageProvider? storage = StorageProvider;
                    if (storage is null)
                    {
                        ShowHint("另存为失败：无法打开保存对话框", critical: true);
                        return null;
                    }

                    IStorageFile? target = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
                    {
                        Title = "另存为 — " + title,
                        SuggestedFileName = suggestedFileName,
                        FileTypeChoices =
                        [
                            new FilePickerFileType("资源文件")
                            {
                                Patterns =
                                [
                                    "*" + (Path.GetExtension(suggestedFileName) is { Length: > 0 } ext
                                        ? ext
                                        : ".*")
                                ]
                            }
                        ]
                    }).ConfigureAwait(true);
                    return target?.Path.LocalPath;
                }
            });

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

    private void WireTaskManagerSurface()
    {
        _taskManagerSurface.WireOnce(this, new TaskManagerBindings
        {
            CancelTask = CancelTrackedTask,
            DismissTask = RemoveTask
        });
    }

    private PageSpeedRight CreateTaskManagerRightPage()
    {
        WireTaskManagerSurface();
        PageSpeedRight page = _taskManagerSurface.EnsureRight();
        _speedRight = page;
        _speedLeft = _taskManagerSurface.EnsureLeft();
        return page;
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

    /// <summary>
    /// Frosted dock chrome only when experimental UI is on <em>and</em> ExtraDock reports a visible FAB.
    /// Uses VM <see cref="ExtraDockViewModel.HasAnyVisibleButton"/> only — never control Height/IsVisible
    /// during hide animations (that left an empty glass “bubble” in the corner).
    /// </summary>
    private void RefreshExtraDockChrome()
    {
        if (this.FindControl<Border>("PanExtraDock") is not { } dock)
            return;

        bool experimental = _extraDockViewModel.UseGlassChrome || _experimentalChromeApplied;
        bool showChrome = experimental && _extraDockViewModel.HasAnyVisibleButton;

        if (showChrome)
        {
            bool dark = AvaloniaThemeManager.IsDarkMode;
            dock.Padding = new Thickness(6d);
            dock.CornerRadius = new CornerRadius(26d);
            dock.Background = new SolidColorBrush(Color.Parse(dark ? "#D929292D" : "#CCF5F5F7"));
            dock.BorderBrush = new SolidColorBrush(Color.Parse(dark ? "#38FFFFFF" : "#33FFFFFF"));
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
            // No chrome and no intentional FABs: do not intercept clicks on an empty corner.
            dock.IsHitTestVisible = !experimental || _extraDockViewModel.HasAnyVisibleButton;
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
        SyncLaunchFieldsFromSurface();
        bool experimental = _instancesSelect.IsFullPageLayout;
        ApplyExperimentalChrome(experimental);

        ILaunchHomeSurface? launchHome = _launchLeft ?? _launchHomeSurface.Home;
        // Prefer the live launch root so the folder list highlights the folder in use.
        if (launchHome?.MinecraftRootDirectory is { Length: > 0 } liveRoot)
        {
            string? normalizedLive = NormalizeDirectoryPath(liveRoot);
            if (normalizedLive is not null && _folderStore.ContainsRoot(normalizedLive))
                _folderStore.SetSelectedRootWithoutPersist(normalizedLive);
        }

        _instancesSelect.WireOnce(this, CreateInstancesSelectBindings());
        _instancesSelect.Apply(
            leftHost,
            rightHost,
            launchHome?.Instances ?? [],
            launchHome?.SelectedInstance);

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
        MinecraftFolderInfo folder = AddOrGetMinecraftFolder(root, "当前");
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

        WireInstancesManageSurface();
        bool experimental =
            _shellViewModel.Profile.Manage == InstanceManageLayout.FullPageSidebar ||
            IsExperimentalHomepageUiEnabled();

        if (experimental)
        {
            ApplyExperimentalInstanceManagePage(instance, subPage, leftHost, rightHost);
            return;
        }

        (PageInstanceLeft left, MyPageRight rightPage, InstancePageSubType normalized) =
            _instancesManage.Prepare(instance, subPage);
        _managedInstance = _instancesManage.ManagedInstance;

        if (!ReferenceEquals(leftHost.Child, left))
        {
            if (leftHost.Child is MyPageLeft oldLeft)
                oldLeft.TriggerHideAnimation();
            leftHost.Child = left;
            left.TriggerShowAnimation();
        }

        EnterTitleSubPage($"版本设置 - {instance.Name}");

        MyPageRight? oldRight = rightHost.Child as MyPageRight;
        if (ReferenceEquals(oldRight, rightPage))
            return;

        oldRight?.PageOnExit();
        rightHost.Child = rightPage;
        RefreshBackToTopBinding();
        rightPage.PageOnEnter();
        _ = normalized;
    }

    private void ApplyExperimentalInstanceManagePage(
        LaunchInstanceInfo instance,
        InstancePageSubType subPage,
        Border leftHost,
        Border rightHost)
    {
        ApplyExperimentalChrome(true);

        (PageInstanceManageExperimental shell, InstancePageSubType normalized) =
            _instancesManage.PrepareExperimental(instance, subPage);
        _managedInstance = _instancesManage.ManagedInstance;

        // Full-page: clear classic left rail (appearance / select pattern).
        if (leftHost.Child is MyPageLeft oldLeft)
            oldLeft.TriggerHideAnimation();
        leftHost.Child = null;

        EnterTitleSubPage($"版本设置 - {instance.Name}");

        MyPageRight? oldRight = rightHost.Child as MyPageRight;
        if (!ReferenceEquals(oldRight, shell))
        {
            oldRight?.PageOnExit();
            rightHost.Child = shell;
            rightHost.Opacity = 1d;
            shell.PageOnEnter();
        }

        RefreshBackToTopBinding();
        _ = normalized;
    }

    private void WireInstancesManageSurface()
    {
        _instancesManage.WireOnce(this, new InstancesManageBindings
        {
            SelectSubPage = ApplyInstanceManagePage,
            RefreshInstancesAsync = path => RefreshInstancesAfterManagementAsync(path),
            ResetSettings = PromptResetInstanceSettings,
            OpenPath = OpenFolder,
            OpenExistingPath = OpenExistingPath,
            StatusMessage = HandleStatusMessage,
            ShowHint = message => ShowHint(message),
            RenameInstance = PromptRenameInstance,
            DeleteInstance = PromptDeleteInstance,
            EditDescription = PromptEditInstanceDescription,
            ToggleStarAsync = ToggleInstanceStarAsync,
            ExportLaunchScriptAsync = ExportLaunchScriptAsync,
            TestLaunchAsync = TestLaunchFromInstancePageAsync,
            RepairFilesAsync = RepairInstanceFilesAsync,
            PatchCoreAsync = PatchInstanceCoreAsync,
            OpenGlobalSettings = () => SelectNavRoute(SettingsRoute, animate: true),
            ShowMessage = (title, message, primary) => ShowTextDialog(title, message, primary ?? "确定"),
            Confirm = (title, message, complete, primary, secondary, isWarn) =>
                ShowConfirmDialog(
                    title,
                    message,
                    complete,
                    primary ?? "确定",
                    secondary ?? "取消",
                    isWarn),
            CreateAuthProfile = authServer =>
            {
                SelectNavRoute(LaunchRoute, animate: true);
                _launchLeft ??= CreateLaunchLeftPage();
                ApplyLaunchLoginPage(_launchLeft, PageLaunchLeft.LaunchLoginPageType.Auth);
                _launchLoginSurface.AuthPage?.SetServer(authServer);
            },
            ExportZipAsync = ExportInstanceZipAsync,
            ImportExportConfigAsync = ImportInstanceRulesConfigAsync,
            ExportExportConfigAsync = ExportInstanceRulesConfigAsync,
            OpenDownloadInstallAsync = OpenDownloadInstallForInstanceAsync,
            ShowSaveDetailsAsync = ShowInstanceSaveDetailsAsync,
            QuickPlayWorld = worldName =>
            {
                if (_managedInstance is not null && _launchLeft is not null)
                    _ = StartMinecraftAsync(_launchLeft, _managedInstance, worldName);
            },
            NavigateDownload = () => SelectNavRoute(DownloadRoute, animate: true),
            NavigateInstanceSelect = () =>
            {
                SelectNavRoute(LaunchRoute, animate: true);
                ApplyInstanceSelectPage();
            },
            OpenCommunityForResource = OpenCommunityForResourcePage,
            OpenCommunityDetailAsync = OpenCommunityResourceDetailAsync,
            OpenCommunityDataPacks = OpenCommunityDataPacks,
            ShowDatapacks = ShowInstanceDatapacks,
            AddServer = PromptAddServer,
            ConnectServer = server =>
            {
                if (_managedInstance is { } instance && _launchLeft is { } launchPage)
                    _ = StartMinecraftAsync(launchPage, instance, serverAddress: server.Address);
            },
            EditServer = (page, server) =>
            {
                if (_managedInstance is { } instance)
                    PromptEditServer(instance, page, server);
            },
            RemoveServer = (page, server) =>
            {
                if (_managedInstance is { } instance)
                    PromptRemoveServer(instance, page, server);
            },
            RemoveServers = (page, servers) =>
            {
                if (_managedInstance is { } instance)
                    PromptRemoveServers(instance, page, servers);
            }
        });
    }

    private async Task OpenDownloadInstallForInstanceAsync(InstanceInstallModifyRequest request)
    {
        LaunchInstanceInfo instance = request.Instance;
        string versionId = string.IsNullOrWhiteSpace(request.MinecraftVersionId)
            ? MinecraftLaunchPlanFactory.ReadMinecraftVersionId(instance)
            : request.MinecraftVersionId;
        string minecraftRoot = MinecraftLaunchPlanFactory.GetMinecraftRootFromInstance(instance);
        PageDownloadInstall installPage = ActivateDownloadInstallPage(animate: true);
        if (request.ApplySelection)
        {
            bool started = await installPage.ApplyExistingInstallSelection(
                    versionId,
                    instance.Name,
                    minecraftRoot,
                    request.LoaderKind,
                    request.LoaderVersion,
                    request.CurrentOptiFineVersion)
                .ConfigureAwait(true);
            if (!started)
            {
                ShowTextDialog(
                    "无法应用组件更改",
                    $"未能在版本清单中找到 Minecraft {versionId}，组件更改尚未执行。");
            }
            return;
        }

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

        PageSpeedRight rightPage = CreateTaskManagerRightPage();
        PageSpeedLeft leftPage = _speedLeft!;
        UpdateTaskManagerViews();

        if (!ReferenceEquals(leftHost.Child, leftPage))
        {
            if (leftHost.Child is MyPageLeft oldLeft)
                oldLeft.TriggerHideAnimation();
            leftHost.Child = leftPage;
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
        leftPage.TriggerShowAnimation();
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
            _instancesManage.Left is { } manageLeft)
        {
            LaunchInstanceInfo instance = _managedInstance;
            InstancePageSubType subPage = manageLeft.PageId;
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
        // Lifecycle edges flush immediately so the task row appears without waiting for coalesce.
        _taskUiCoalescer.FlushNow();
        NotifyTaskManagerButton(ribble: true);
    }

    private void TrackTaskProgress(string taskId, string title, double progress, string detail)
    {
        // Model always updates; view refresh is coalesced (~20 Hz) per Avalonia performance guide.
        if (PortableLog.IsEnabled(PortableLogLevel.RealTime))
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
        _taskUiCoalescer.Request();
    }

    private void TrackInstallProgress(string taskId, string title, MinecraftInstallProgress progress)
    {
        string stage = string.IsNullOrWhiteSpace(progress.Stage) ? "正在处理下载任务" : progress.Stage;
        if (PortableLog.IsEnabled(PortableLogLevel.RealTime))
        {
            DesktopFileLog.RealTime(
                "Task",
                $"安装任务进度；Id={taskId}；Title={title}；Stage={stage}；Progress={progress.Progress:P1}；" +
                $"Files={progress.CompletedFiles}/{progress.TotalFiles}；Threads={progress.ActiveThreads}/{progress.ThreadLimit}；Speed={progress.SpeedBytesPerSecond}B/s。");
        }
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
        _taskUiCoalescer.Request();
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
        _taskUiCoalescer.FlushNow();
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
        _taskUiCoalescer.FlushNow();
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

    private void DisposeTrackedTasks()
    {
        foreach (CancellationTokenSource cancellation in _taskCancellations.Values)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        _taskCancellations.Clear();
    }

    private async Task ShowInstanceSaveDetailsAsync(string saveFolder)
    {
        if (_managedInstance is null ||
            this.FindControl<Border>("PanMainRight") is not { } rightHost)
        {
            return;
        }

        WireInstancesManageSurface();
        PageInstanceSavesInfoRight page = _instancesManage.EnsureSavesInfoPage();
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

        WireInstancesManageSurface();
        PageInstanceResourceRight page = _instancesManage.EnsureDatapackPage();
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

        _communityDownloadTarget = null;
        SelectNavRoute(CommunityRoute, animate: true);
        if (_communityLeft is not null && _communityLeft.TrySelectCategory(category))
            _ = _communityRight?.SetCategoryAsync(category);
    }

    private async Task OpenCommunityResourceDetailAsync(
        CommunityResourceEntry entry,
        CommunityResourceCategory category,
        CommunitySearchOptions options)
    {
        _communityDownloadTarget = null;
        SelectNavRoute(CommunityRoute, animate: false);
        await OpenCommunityDetailAsync(entry, category, options).ConfigureAwait(true);
    }

    private void OpenCommunityDataPacks(string targetDirectory)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory))
            return;

        _communityDownloadTarget = (
            CommunityResourceCategory.DataPack,
            Path.GetFullPath(targetDirectory));
        SelectNavRoute(CommunityRoute, animate: true);
        if (_communityLeft?.TrySelectCategory(CommunityResourceCategory.DataPack) == true)
            _ = _communityRight?.SetCategoryAsync(CommunityResourceCategory.DataPack);
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

    private void PromptRemoveServers(
        LaunchInstanceInfo instance,
        PageInstanceServerRight page,
        IReadOnlyList<MinecraftServerEntry> servers)
    {
        if (servers.Count == 0)
            return;

        ShowConfirmDialog(
            "批量移除服务器",
            $"确定要从列表中移除选中的 {servers.Count} 个服务器吗？",
            confirmed =>
            {
                if (confirmed)
                    _ = RemoveServersAsync(instance, page, servers);
            },
            "移除",
            "取消",
            isWarn: true);
    }

    private async Task RemoveServersAsync(
        LaunchInstanceInfo instance,
        PageInstanceServerRight page,
        IReadOnlyList<MinecraftServerEntry> servers)
    {
        int removed = 0;
        try
        {
            string gameDir = await InstanceGameDirectory.ResolveAsync(instance).ConfigureAwait(true);
            removed = await MinecraftServerListService
                .RemoveManyAsync(gameDir, servers)
                .ConfigureAwait(true);

            page.Reload();
            _launchRight?.AppendLog($"已移除 {removed} 个服务器。");
            if (removed < servers.Count)
            {
                ShowTextDialog(
                    "部分服务器未移除",
                    $"已移除 {removed} 个服务器，另有 {servers.Count - removed} 个条目已不存在。\n请刷新列表后重试。");
            }
        }
        catch (Exception ex)
        {
            page.Reload();
            ShowTextDialog(
                "批量移除失败",
                $"已移除 {removed} 个服务器，随后操作失败。\n\n详细信息：{ex.Message}");
        }
    }

    private DesktopMainPage CreateSettingsMainPage()
    {
        _settingsSurface.WireOnce(this, new SettingsFeatureBindings
        {
            EnsureRightHostOpaque = () =>
            {
                if (this.FindControl<Border>("PanMainRight") is { } rightHost)
                    rightHost.Opacity = 1d;
            },
            TryGetLiveRightPage = () =>
                this.FindControl<Border>("PanMainRight")?.Child as MyPageRight,
            WirePage = WireSetupPage,
            ApplyRightPage = ApplySetupRightPage,
            Confirm = (title, message, complete, primary, secondary, isWarn) =>
                ShowConfirmDialog(
                    title,
                    message,
                    complete,
                    primary ?? "确定",
                    secondary ?? "取消",
                    isWarn)
        });
        DesktopMainPage page = _settingsSurface.CreateMainPage();
        _setupLeft = _settingsSurface.Left;
        _setupRight = _settingsSurface.Right;
        return page;
    }

    private PageSetupLeft CreateSetupLeftPage()
    {
        // Legacy path: ensure surface is wired then return its left page.
        _ = CreateSettingsMainPage();
        return _setupLeft!;
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
        int downloadThreadLimit = Math.Clamp(
            settings.GetIntegerOption(LauncherSettingKeys.ToolDownloadThread, 63) + 1,
            1,
            256);
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
                        JavaExecutablePath = MinecraftLaunchPlanFactory.ResolvePreferredJavaExecutablePath(forceConsole: true)
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

    private void BindStartMinecraftUseCase()
    {
        _startMinecraft.Bind(
            new StartMinecraftHost
            {
                ResolveProfile = () =>
                    _launchLoginSurface.ProfileSkinPage?.Profile ??
                    _launchLoginSurface.ProfilePage?.SelectedProfile ??
                    _loginProfiles.FirstOrDefault(),
                AcquireLaunchCancellation = repairSession =>
                {
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

                    return _launchCancellation.Token;
                },
                GetMinecraftRoot = MinecraftLaunchPlanFactory.GetMinecraftRootFromInstance,
                WaitForUiPaintAsync = async () =>
                {
                    // Yield until after layout + animation frames so the launching pane is visible
                    // before any disk/network work (WPF ModLaunch similarly paints first).
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                    await Task.Yield();
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                    await Task.Delay(32).ConfigureAwait(false);
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                },
                PostUi = action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background),
                InvokeUiAsync = action => Dispatcher.UIThread.InvokeAsync(action).GetTask(),
                AppendLog = message => _launchRight?.AppendLog(message),
                ShowNoProfileDialog = () =>
                    ShowTextDialog("请选择账户档案", "启动游戏前需要先选择或创建一个账户档案。"),
                ShowLaunchFailedDialog = message =>
                    ShowTextDialog("启动失败", "未能启动游戏。\n\n详细信息：" + message),
                LoadSettingsAsync = cancellationToken => Task.Run(
                    LauncherSettingsPageBinder.LoadSettings,
                    cancellationToken),
                RefreshProfileAsync = (profile, status, token) =>
                    RefreshLaunchProfileAsync(profile, token, status),
                CreatePlanAsync = MinecraftLaunchPlanFactory.CreateAsync,
                RunPreLaunchCommandAsync = MinecraftLaunchPlanFactory.RunPreLaunchCommandAsync,
                ApplyProcessPriority = MinecraftLaunchPlanFactory.ApplyProcessPriority,
                ConfirmJavaDownloadAsync = ConfirmJavaDownloadAsync,
                StopRepairServerAsync = () => _minecraftAiRepairAdvisor.StopLocalServerAsync(),
                OnSucceededAsync = OnStartMinecraftSucceededAsync,
                OnFailedAsync = OnStartMinecraftFailedAsync,
                IncrementLaunchCountAsync = IncrementInstanceLaunchCountAsync
            },
            _launchCoordinator);
    }

    private Task StartMinecraftAsync(
        ILaunchHomeSurface launchPage,
        LaunchInstanceInfo instance,
        string? worldName = null,
        string? serverAddress = null,
        MinecraftRepairSession? repairSession = null) =>
        _startMinecraft.ExecuteAsync(
            new StartMinecraftRequest(
                launchPage,
                instance,
                WorldName: worldName,
                ServerAddress: serverAddress),
            repairSession);

    private async Task OnStartMinecraftSucceededAsync(StartMinecraftSucceededArgs args)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!ReferenceEquals(args.Result.Profile, args.OriginalProfile) &&
                args.Result.Profile.Kind is
                    LaunchLoginProfileKind.Microsoft or
                    LaunchLoginProfileKind.LittleSkin or
                    LaunchLoginProfileKind.NCloud)
            {
                AddOrUpdateLoginProfile(args.Result.Profile);
                _launchLoginSurface.ProfilePage?.SetProfiles(_loginProfiles, args.Result.Profile);
                _launchLoginSurface.ProfileSkinPage?.SetProfile(args.Result.Profile);
                SaveProfilesInBackground(
                    args.Result.Profile.Kind == LaunchLoginProfileKind.LittleSkin
                        ? "刷新 LittleSkin OAuth 档案"
                        : args.Result.Profile.Kind == LaunchLoginProfileKind.NCloud
                            ? "刷新 N Cloud 在线档案"
                            : "刷新 Microsoft 正版档案");
            }

            Process process = args.Result.Process;
            SetGameRunningExtras(
                process,
                new RunningGameContext(
                    args.Instance,
                    args.Home,
                    args.Settings,
                    args.Result.FaultReport,
                    args.Result.Plan.NativesDirectory,
                    args.WorldName,
                    args.ServerAddress,
                    JavaMajorVersion: args.Result.Plan.JvmHostRequest?.JavaMajorVersion,
                    MemoryMegabytes: TryReadMaximumHeapMegabytes(args.Result.Plan.JvmHostRequest is { } hostRequest
                        ? hostRequest.VmArguments
                        : args.Result.Plan.StartInfo.ArgumentList),
                    LoginMethod: MinecraftLaunchCoordinator.FormatLoginMethod(args.Result.Profile),
                    LoginServerHost: ResolveLoginServerHost(args.Result.Profile.AuthServer),
                    ProfileUsername: args.Result.Profile.Username,
                    ProfileUuid: args.Result.Profile.Uuid,
                    UsedExperimentalJvmHost: args.Result.Plan.JvmHostRequest is not null,
                    JavaExecutableName: Path.GetFileName(args.Result.Plan.JvmHostRequest?.JavaExecutablePath ??
                                                         args.Result.Plan.StartInfo.FileName),
                    JavaExecutablePathForRedaction: args.Result.Plan.JvmHostRequest?.JavaExecutablePath ??
                                                    args.Result.Plan.StartInfo.FileName,
                    ClasspathEntryCount: args.Result.Plan.ClasspathEntries.Count,
                    VmArgumentCount: args.Result.Plan.JvmHostRequest?.VmArguments.Length ??
                                     args.Result.Plan.StartInfo.ArgumentList.Count(argument =>
                                         argument.StartsWith('-')),
                    GameArgumentCount: args.Result.Plan.JvmHostRequest?.GameArguments.Length));
            UpdateBackgroundVideoPlayback(args.Settings);
            _launchRight?.AppendLog(!string.IsNullOrWhiteSpace(args.WorldName)
                ? $"{args.Instance.Name} 已启动，正在进入存档 {args.WorldName}。"
                : !string.IsNullOrWhiteSpace(args.ServerAddress)
                    ? $"{args.Instance.Name} 已启动，正在连接服务器 {args.ServerAddress}。"
                    : $"{args.Instance.Name} 已启动。");

            if (args.Settings.GetIntegerOption(
                    "LaunchArgumentVisible",
                    LauncherSettingDefaults.GetInteger("LaunchArgumentVisible")) != 0)
            {
                args.Home.PageChangeToLogin();
            }

            ApplyLauncherVisibility(process, args.Settings);
        });
    }

    private async Task OnStartMinecraftFailedAsync(StartMinecraftFailedArgs args)
    {
        LauncherSettings? repairSettings = args.RuntimeSettings ?? args.RepairSession?.Settings;
        if (repairSettings is not null)
        {
            MinecraftLaunchFaultReport failureReport = args.Exception is MinecraftLaunchFailureException launchFailure &&
                                                       launchFailure.FaultReport is { } structuredFailure
                ? structuredFailure
                : MinecraftLaunchFaultAnalyzer.Analyze(args.Exception, "LaunchCoordinator");
            await Dispatcher.UIThread.InvokeAsync(() =>
                _launchRight?.AppendLog("启动失败，错误处理器正在分析：" + args.Exception.Message));
            await TryRepairMissingDependenciesAsync(
                    new RunningGameContext(
                        args.Instance,
                        args.Home,
                        repairSettings,
                        Task.FromResult<MinecraftLaunchFaultReport?>(failureReport),
                        WorldName: args.WorldName,
                        ServerAddress: args.ServerAddress,
                        RepairSession: args.RepairSession,
                        LoginMethod: MinecraftLaunchCoordinator.FormatLoginMethod(args.Profile),
                        LoginServerHost: ResolveLoginServerHost(args.Profile.AuthServer),
                        ProfileUsername: args.Profile.Username,
                        ProfileUuid: args.Profile.Uuid,
                        UsedExperimentalJvmHost: repairSettings.GetBooleanOption(
                            LauncherSettingKeys.ExperimentalJvmLifecycleHost,
                            LauncherSettingDefaults.GetBoolean(
                                LauncherSettingKeys.ExperimentalJvmLifecycleHost.Value))))
                .ConfigureAwait(false);
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (args.Home.IsLaunchInProgress)
                args.Home.PageChangeToLogin();
            ShowTextDialog("启动失败", "未能启动游戏。\n\n详细信息：" + args.Exception.Message);
            _launchRight?.AppendLog("启动失败：" + args.Exception.Message);
        });
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

    /// <summary>
    /// Login-stage entry: validates/refreshes the selected profile before any launch plan is built.
    /// </summary>
    private async Task<LoginProfileInfo> RefreshLaunchProfileAsync(
        LoginProfileInfo profile,
        CancellationToken cancellationToken,
        Action<string>? status = null)
    {
        void Report(string message) => status?.Invoke(message);

        if (profile.Kind == LaunchLoginProfileKind.Offline)
        {
            Report("离线档案，跳过在线验证。");
            return profile;
        }

        if (profile.Kind == LaunchLoginProfileKind.ThirdParty)
            return await RefreshThirdPartyLaunchProfileAsync(profile, cancellationToken, status)
                .ConfigureAwait(false);

        if (profile.Kind == LaunchLoginProfileKind.LittleSkin)
            return await RefreshLittleSkinLaunchProfileAsync(profile, cancellationToken, status)
                .ConfigureAwait(false);

        if (profile.Kind == LaunchLoginProfileKind.NCloud)
        {
            Report("正在刷新 N Cloud 会话…");
            IHostOnlineMinecraftAccountProvider? provider =
                HostOnlineMinecraftAccountProvider.Current;
            if (provider?.IsAuthenticated != true)
            {
                throw new InvalidOperationException(
                    "N Cloud 档案需要已登录的在线服务账户。请在设置中重新连接账户。");
            }

            HostOnlineMinecraftSession session = await provider
                .CreateSessionAsync(cancellationToken)
                .ConfigureAwait(false);
            Report("N Cloud 会话已刷新。");
            return profile with
            {
                Username = session.Username,
                Uuid = session.Uuid,
                AccessToken = session.AccessToken,
                ClientToken = session.ClientToken,
                AuthServer = session.AuthServer,
                SkinAddress = session.SkinAddress ?? profile.SkinAddress
            };
        }

        if (profile.Kind != LaunchLoginProfileKind.Microsoft ||
            string.IsNullOrWhiteSpace(profile.RefreshToken))
        {
            Report("Microsoft 档案无需刷新（无 refresh token 或非微软账户）。");
            return profile;
        }

        string clientId = MicrosoftMinecraftAuthService.ResolveClientId();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            // Local/debug builds may intentionally omit the OAuth client id. A
            // previously authenticated Minecraft access token remains usable
            // until its own expiry; refreshing is only mandatory afterwards.
            if (MinecraftLaunchPlanFactory.IsAccessTokenUsable(profile.AccessToken))
            {
                Report("未配置 Microsoft Client ID，使用档案中仍有效的访问令牌。");
                return profile;
            }

            throw new InvalidOperationException(
                "缺少 Microsoft 登录配置，无法刷新正版登录状态。请提供 PCL_MS_CLIENT_ID 后重试。");
        }

        Report("正在刷新 Microsoft 访问令牌…");
        MicrosoftMinecraftLoginResult refreshed = await _microsoftAuthService
            .RefreshAsync(clientId, profile.RefreshToken, cancellationToken)
            .ConfigureAwait(false);
        Report("Microsoft 访问令牌已刷新。");
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

    /// <summary>
    /// Login stage: validate → Yggdrasil refresh → encrypted password re-auth.
    /// Must finish before launch arguments are generated.
    /// </summary>
    private async Task<LoginProfileInfo> RefreshThirdPartyLaunchProfileAsync(
        LoginProfileInfo profile,
        CancellationToken cancellationToken,
        Action<string>? status = null)
    {
        void Report(string message) => status?.Invoke(message);

        if (string.IsNullOrWhiteSpace(profile.AuthServer))
        {
            throw new InvalidOperationException(
                "第三方档案缺少认证服务器地址。请重新登录该账户。");
        }

        Report("正在读取加密凭据并校验会话…");
        ThirdPartyStoredCredential? stored = await ThirdPartyCredentialStore
            .TryReadAsync(profile.AuthServer, profile.Uuid, cancellationToken)
            .ConfigureAwait(false);
        string clientToken = !string.IsNullOrWhiteSpace(profile.ClientToken)
            ? profile.ClientToken
            : stored?.ClientToken ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(profile.AccessToken) &&
            ThirdPartyAuthService.IsJwtAccessTokenUnexpired(profile.AccessToken))
        {
            Report("正在向认证服务器 validate 访问令牌…");
            bool stillValid = await _thirdPartyAuthService
                .ValidateAsync(
                    profile.AuthServer,
                    profile.AccessToken,
                    string.IsNullOrWhiteSpace(clientToken) ? null : clientToken,
                    cancellationToken)
                .ConfigureAwait(false);
            if (stillValid)
            {
                Report("访问令牌有效，无需刷新。");
                return profile;
            }

            Report("validate 未通过，准备 refresh…");
        }
        else
        {
            PortableLog.Warn(
                "ThirdPartyAuth",
                "第三方访问令牌已过期或格式异常，尝试 refresh / 重登。");
            Report("访问令牌已过期或无效，准备 refresh…");
        }

        // 1) Yggdrasil refresh (works while accessToken is still accepted for refresh).
        if (!string.IsNullOrWhiteSpace(profile.AccessToken))
        {
            try
            {
                Report("正在 refresh 访问令牌…");
                ThirdPartyAuthLoginResult refreshed = await _thirdPartyAuthService
                    .RefreshAsync(
                        profile.AuthServer,
                        profile.AccessToken,
                        string.IsNullOrWhiteSpace(clientToken) ? null : clientToken,
                        cancellationToken)
                    .ConfigureAwait(false);
                return await PersistRefreshedThirdPartyProfileAsync(
                        profile,
                        refreshed,
                        stored,
                        "访问令牌已自动刷新。",
                        status)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                PortableLog.Warn(ex, "ThirdPartyAuth", "Yggdrasil refresh 失败，尝试加密凭据重登。");
                Report("refresh 失败，尝试加密凭据静默重登…");
            }
        }

        // 2) Silent re-auth with DPAPI/Keychain-stored password.
        if (stored is not null)
        {
            try
            {
                Report("正在使用加密保存的密码重新认证…");
                ThirdPartyAuthLoginResult reauthed = await _thirdPartyAuthService
                    .AuthenticateAsync(
                        new ThirdPartyAuthLoginRequest(
                            profile.AuthServer,
                            stored.LoginUsername,
                            stored.Password,
                            string.IsNullOrWhiteSpace(clientToken) ? stored.ClientToken : clientToken),
                        cancellationToken)
                    .ConfigureAwait(false);

                await ThirdPartyCredentialStore.SaveAsync(
                        reauthed.AuthServer,
                        reauthed.Uuid,
                        stored.LoginUsername,
                        stored.Password,
                        reauthed.ClientToken,
                        cancellationToken)
                    .ConfigureAwait(false);

                return await PersistRefreshedThirdPartyProfileAsync(
                        profile,
                        reauthed,
                        stored,
                        "已使用加密凭据重新登录。",
                        status)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                PortableLog.Error(ex, "ThirdPartyAuth", "加密凭据重登失败。");
                throw new InvalidOperationException(
                    "登录阶段：第三方访问令牌已失效，使用本机加密凭据重新登录也失败。" +
                    Environment.NewLine +
                    "请打开「账户」重新登录该认证服务器（密码可能已更改）。" +
                    Environment.NewLine +
                    "详情：" + ex.Message,
                    ex);
            }
        }

        throw new InvalidOperationException(
            "登录阶段：第三方访问令牌已失效，且本机没有可用于自动刷新的加密凭据。" +
            Environment.NewLine +
            "请打开「账户」重新登录该认证服务器。登录成功后密码会加密保存在本机，之后可在登录阶段自动刷新。");
    }

    private async Task<LoginProfileInfo> PersistRefreshedThirdPartyProfileAsync(
        LoginProfileInfo profile,
        ThirdPartyAuthLoginResult refreshed,
        ThirdPartyStoredCredential? stored,
        string logMessage,
        Action<string>? status = null)
    {
        LoginProfileInfo updated = profile with
        {
            Username = refreshed.Username,
            Uuid = refreshed.Uuid,
            AccessToken = refreshed.AccessToken,
            ClientToken = string.IsNullOrWhiteSpace(refreshed.ClientToken)
                ? profile.ClientToken
                : refreshed.ClientToken,
            RefreshToken = string.IsNullOrWhiteSpace(refreshed.RefreshToken)
                ? profile.RefreshToken
                : refreshed.RefreshToken,
            AuthServer = refreshed.AuthServer,
            SkinAddress = MySkin.ResolveSkinAddress(
                skinAddress: null,
                uuid: refreshed.Uuid,
                authServer: refreshed.AuthServer)
        };

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            AddOrUpdateLoginProfile(updated);
            _launchLoginSurface.ProfilePage?.SetProfiles(_loginProfiles, updated);
            SaveProfilesInBackground("刷新第三方访问令牌");
            _launchRight?.AppendLog(logMessage);
        });
        status?.Invoke(logMessage);

        // If we re-authed under a new UUID (rare), migrate vault key.
        if (stored is not null &&
            !string.Equals(NormalizeUuidHex(stored.ProfileUuid), NormalizeUuidHex(updated.Uuid), StringComparison.OrdinalIgnoreCase))
        {
            await ThirdPartyCredentialStore.DeleteAsync(stored.AuthServer, stored.ProfileUuid)
                .ConfigureAwait(false);
            await ThirdPartyCredentialStore.SaveAsync(
                    updated.AuthServer,
                    updated.Uuid,
                    stored.LoginUsername,
                    stored.Password,
                    updated.ClientToken)
                .ConfigureAwait(false);
        }

        return updated;
    }

    private static string NormalizeUuidHex(string uuid) =>
        new string(uuid.Where(static ch => ch is not ('-' or ' ')).ToArray()).ToLowerInvariant();

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
                if (_instancesManage.ManagePage is not null &&
                    _managedInstance is not null &&
                    string.Equals(_managedInstance.InstanceDirectory, instance.InstanceDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    _instancesManage.ManagePage.SetInstance(instance);
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
            _instancesManage.ManagePage?.SetInstance(instance);
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
            _instancesManage.ManagePage?.SetInstance(instance);
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
            string suggestedFileName = "启动 " + DesktopPathHelpers.SanitizeFileName(instance.Name) + defaultExtension;
            string targetPath = await PickSaveFilePathAsync(
                    "导出启动脚本",
                    suggestedFileName,
                    OperatingSystem.IsWindows()
                        ? new FilePickerFileType("Windows 批处理") { Patterns = ["*.bat", "*.cmd"] }
                        : new FilePickerFileType("Shell 脚本") { Patterns = ["*.sh"] })
                .ConfigureAwait(true)
                ?? Path.Combine(DesktopPathHelpers.GetDesktopOrBaseDirectory(), suggestedFileName);

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
                        MinecraftRootDirectory = MinecraftLaunchPlanFactory.GetMinecraftRootFromInstance(instance),
                        Report = static _ => { },
                        RefreshProfileAsync = static (current, _, _) => Task.FromResult(current),
                        CreatePlanAsync = MinecraftLaunchPlanFactory.CreateAsync,
                        RunPreLaunchCommandAsync = MinecraftLaunchPlanFactory.RunPreLaunchCommandAsync,
                        ApplyProcessPriority = MinecraftLaunchPlanFactory.ApplyProcessPriority
                    },
                    CancellationToken.None)
                .ConfigureAwait(true);
            MinecraftProcessLaunchPlan plan = await MinecraftLaunchPlanFactory.CreateAsync(
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
                        MinecraftRootDirectory = MinecraftLaunchPlanFactory.GetMinecraftRootFromInstance(instance),
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
            _instancesManage.ManagePage?.SetInstance(instance);
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
            _instancesManage.ManagePage?.SetInstance(instance);
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
            _instancesManage.ManagePage?.SetInstance(selected);
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
            string extension = request.IncludeLauncherFiles ? ".zip" : ".mrpack";
            string fileName = $"PCLN-{DesktopPathHelpers.SanitizeFileName(request.PackageName)}-{DesktopPathHelpers.SanitizeFileName(request.PackageVersion)}-{DateTime.Now:yyyyMMdd-HHmmss}{extension}";
            string targetPath = Path.Combine(DesktopPathHelpers.GetDesktopOrBaseDirectory(), fileName);
            _launchRight?.AppendLog($"正在导出版本 {instance.Name}。");
            string gameDirectory = await InstanceGameDirectory.ResolveAsync(instance).ConfigureAwait(true);
            MinecraftVersionJsonInfo versionInfo = MinecraftVersionJsonInspector.Read(instance);
            Dictionary<string, string> dependencies = BuildExportDependencies(versionInfo);
            InstanceMetadata metadata = await InstanceMetadataStore.LoadAsync(instance.InstanceDirectory)
                .ConfigureAwait(true);
            using CompositeCommunityResourceCatalog catalog = new();
            await InstanceExportService.ExportAsync(
                    new InstanceExportRequest
                    {
                        InstanceDirectory = instance.InstanceDirectory,
                        GameDirectory = gameDirectory,
                        TargetArchivePath = targetPath,
                        Rules = request.Rules,
                        PackageName = request.PackageName,
                        PackageVersion = request.PackageVersion,
                        Summary = !string.IsNullOrWhiteSpace(metadata.Description)
                            ? metadata.Description
                            : metadata.CustomInfo,
                        Dependencies = dependencies,
                        IncludeLauncherFiles = request.IncludeLauncherFiles,
                        IncludeLauncherCustom = request.IncludeLauncherCustom,
                        IncludeBundleFiles = request.IncludeBundleFiles,
                        ModrinthUploadMode = request.ModrinthUploadMode,
                        LauncherExecutablePath = Environment.ProcessPath,
                        LauncherDataDirectory = Path.Combine(AppContext.BaseDirectory, "PCL"),
                        ResolveHostedFilesAsync = (files, cancellationToken) =>
                            ResolveExportHostedFilesAsync(catalog, files, cancellationToken)
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

    private static Dictionary<string, string> BuildExportDependencies(MinecraftVersionJsonInfo versionInfo)
    {
        Dictionary<string, string> dependencies = new(StringComparer.OrdinalIgnoreCase)
        {
            ["minecraft"] = versionInfo.MinecraftVersionId
        };
        string? neoForge = MinecraftLoaderLibraryDetector.DetectVersion(
            versionInfo.LoaderEntries,
            "net.neoforged:neoforge:",
            "net.neoforge:forge:");
        string? forge = neoForge is null
            ? MinecraftLoaderLibraryDetector.DetectVersion(versionInfo.LoaderEntries, "net.minecraftforge:forge:")
            : null;
        string? fabric = MinecraftLoaderLibraryDetector.DetectVersion(
            versionInfo.LoaderEntries,
            "net.fabricmc:fabric-loader:");
        string? quilt = MinecraftLoaderLibraryDetector.DetectVersion(
            versionInfo.LoaderEntries,
            "org.quiltmc:quilt-loader:");
        if (!string.IsNullOrWhiteSpace(neoForge))
            dependencies["neoforge"] = neoForge;
        if (!string.IsNullOrWhiteSpace(forge))
            dependencies["forge"] = forge;
        if (!string.IsNullOrWhiteSpace(fabric))
            dependencies["fabric-loader"] = fabric;
        if (!string.IsNullOrWhiteSpace(quilt))
            dependencies["quilt-loader"] = quilt;
        return dependencies;
    }

    private static async Task<IReadOnlyDictionary<string, InstanceExportHostedFile>> ResolveExportHostedFilesAsync(
        CompositeCommunityResourceCatalog catalog,
        IReadOnlyList<InstanceExportFile> files,
        CancellationToken cancellationToken)
    {
        using SemaphoreSlim gate = new(3, 3);
        Task<KeyValuePair<string, InstanceExportHostedFile>?>[] tasks = files.Select(async file =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                CommunityResourceFileMatches matches = await catalog.LookupFilesAsync(
                        file.Sha1,
                        file.ModrinthOnly ? null : file.CurseForgeFingerprint,
                        file.ModrinthOnly,
                        cancellationToken)
                    .ConfigureAwait(false);
                List<string> urls = [];
                if (matches.Modrinth?.CurrentFile is { } modrinthFile)
                    urls.AddRange(modrinthFile.CandidateUrls);
                if (!file.ModrinthOnly && matches.CurseForge?.CurrentFile is { } curseForgeFile)
                    urls.AddRange(curseForgeFile.CandidateUrls);
                string[] distinctUrls = urls
                    .Where(static url => !string.IsNullOrWhiteSpace(url))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return distinctUrls.Length == 0
                    ? (KeyValuePair<string, InstanceExportHostedFile>?)null
                    : KeyValuePair.Create(
                        file.RelativePath,
                        new InstanceExportHostedFile(distinctUrls));
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        KeyValuePair<string, InstanceExportHostedFile>?[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results
            .Where(static result => result.HasValue)
            .Select(static result => result.GetValueOrDefault())
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
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
                ?? Path.Combine(DesktopPathHelpers.GetDesktopOrBaseDirectory(), $"PCLN-ExportRules-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

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
        if (_registeredPageSurfaceId is { } pageSurface)
            DesktopHostUiComposition.Instance.UnregisterTarget(pageSurface);
        DesktopHostUiComposition.Instance.UnregisterSlot("pcl.navigation.main", "items.after-download");
        DesktopHostUiComposition.Instance.UnregisterTarget("pcl.navigation.main");
        DesktopHostUiComposition.Instance.UnregisterTarget("pcl.window.main");
        DesktopHostNotifications.Instance.Detach(OnHostNotification);
        DesktopHostNotifications.Instance.DetachChoice(OnHostChoiceAsync);
        DesktopHostBackgroundTasks.Instance.Detach();
        DesktopHost.Current.Navigation.Changed -= NavigationRegistryChanged;
        DesktopHostNavigation.Instance.Detach(NavigateToHostRoute);
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
        _taskUiCoalescer.Dispose();
        ClearNavLayoutTransition();
        DisposeTrackedTasks();
        _launchCancellation?.Cancel();
        _launchCancellation?.Dispose();
        _microsoftLoginCancellation?.Cancel();
        _microsoftLoginCancellation?.Dispose();
        _appearanceLoadCancellation?.Cancel();
        _appearanceLoadCancellation?.Dispose();
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
        // Experimental glass dialogs use a slightly stronger, longer-settling scrim (Apple-style focus dim).
        bool experimental = ExperimentalMsgChrome.IsEnabled;
        byte alpha = targetAlpha == 0
            ? (byte)0
            : experimental
                ? ExperimentalMsgChrome.ScrimAlpha
                : targetAlpha;
        int duration = experimental ? 320 : 200;
        ModAnimation.AniEase ease = experimental
            ? new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Middle)
            : new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak);
        ModAnimation.AniStart(
        new List<ModAnimation.AniData>
        {
            ModAnimation.AaColor(
                background,
                Border.BackgroundProperty,
                Color.FromArgb(alpha, 0, 0, 0),
                duration,
                ease: ease),
            ModAnimation.AaCode(() =>
            {
                background.Background = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));
                completed?.Invoke();
            }, after: true)
        }, "MyMsg Background");
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
        foreach (string name in ExtraButtonNames)
        {
            if (this.FindControl<MyExtraButton>(name) is { } extra)
                extra.RefreshColor();
        }
        RefreshExtraDockChrome();
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
        LauncherTelemetry.ApplySettings(settings);
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
        // Keep per-pixel alpha for the rounded transparent margin and shadow.
        // Cocoa supports Transparent but not the other material levels; do not
        // negotiate down to an opaque None surface there. The explicit
        // transparent fallback also avoids Avalonia's default white fallback.
        TransparencyBackgroundFallback = Brushes.Transparent;
        TransparencyLevelHint = OperatingSystem.IsMacOS()
            ? [WindowTransparencyLevel.Transparent]
            : [WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None];
        ApplyFormBackground(settings);
        ApplyTitleAppearance(settings);
        ApplyBackgroundAppearance(settings);
        ApplyNetworkProxy(settings);
        ExperimentalUiProfile profile = _shellViewModel.RefreshProfile(settings);
        ApplyExperimentalChrome(profile.HomepageUi);
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
        if (mode == 2)
            mode = 0; // Remote homepages were removed; legacy settings fall back to blank.
        int preset = settings.GetIntegerOption("UiCustomPreset", LauncherSettingDefaults.GetInteger("UiCustomPreset"));
        string signature = $"{mode.ToString(CultureInfo.InvariantCulture)}|{preset.ToString(CultureInfo.InvariantCulture)}";
        if (mode != 1 && string.Equals(_homepageSignature, signature, StringComparison.Ordinal))
            return;

        _homepageSignature = signature;

        switch (mode)
        {
            case 1:
                LoadLocalHomepage();
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

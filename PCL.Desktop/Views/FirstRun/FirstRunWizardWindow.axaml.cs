// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PCL.Application.Settings;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Features.Settings.Views;
using PCL.Desktop.Hosting.PluginSidecar;
using PCL.Desktop.Legal;
using PCL.Desktop.Localization;
using PCL.Desktop.Paths;

namespace PCL.Desktop.Views.FirstRun;

/// <summary>
/// Out-of-box experience (OOBE) after first install:
/// welcome → terms → privacy → data paths → online (plugin) → telemetry → finish + restart.
/// </summary>
public sealed partial class FirstRunWizardWindow : Window
{
    /// <summary>Legacy key kept for users who finished an earlier first-run build.</summary>
    public const string SettingsKeyCompletedVersionLegacy = "UiFirstRunWizardVersion";

    /// <summary>Primary OOBE completion marker.</summary>
    public const string SettingsKeyCompletedVersion = "UiOobeVersion";

    public const string WizardVersion = "1";

    /// <summary>Plugin data-chain page injected for OOBE online setup.</summary>
    public const string PluginOobeOnlinePageId = "pcl.oobe.online";

    private const double SplashSize = 136d;
    private const double IconSize = 112d;
    private const double TargetWidth = 860d;
    private const double TargetHeight = 520d;
    private const double BubbleMargin = 12d;
    private const double BubbleEndWidth = TargetWidth - BubbleMargin * 2d;
    private const double BubbleEndHeight = TargetHeight - BubbleMargin * 2d;
    private const double SurfaceCorner = 12d;

    private readonly Stopwatch _clock = new();
    private DispatcherTimer? _timer;
    private Phase _phase = Phase.Idle;
    private double _phaseDurationMs;
    private PixelPoint _centerScreen;
    private bool _introStarted;
    private bool _splashPrepared;
    private OobeStep _step = OobeStep.Welcome;
    private int _legalPageIndex;
    private bool _finishLayoutApplied;
    private bool _completing;

    private Border? _panBubble;
    private Image? _heroIcon;
    private Image? _finishIcon;
    private StackPanel? _welcomePanel;
    private StackPanel? _finishPanel;
    private TranslateTransform? _iconTranslate;
    private TranslateTransform? _finishIconTranslate;
    private Grid? _pageWelcome;
    private Grid? _pageLegal;
    private Grid? _pageData;
    private Grid? _pageOnline;
    private Grid? _pageTelemetry;
    private Grid? _pageFinish;
    private MyMarkdownViewer? _labLegalMarkdown;
    private MyScrollViewer? _panLegalScroll;
    private MyTextBox? _txtDataPath;
    private MyTextBox? _txtCachePath;
    private ContentControl? _hostOnlinePlugin;
    private MyButton? _btnStart;
    private MyButton? _btnLegalDisagree;
    private MyButton? _btnLegalPrev;
    private MyButton? _btnLegalNext;
    private MyButton? _btnFinish;

    private string _termsMarkdown = string.Empty;
    private string _privacyMarkdown = string.Empty;
    private Control? _onlineFallback;

    /// <summary>Raised when OOBE finishes successfully (caller should restart host).</summary>
    public event EventHandler? Completed;

    private enum Phase
    {
        Idle,
        ExpandBubble,
        SettleIcon,
        RevealWelcome,
        FadeWelcomeOut,
        Done
    }

    private enum OobeStep
    {
        Welcome,
        Terms,
        Privacy,
        DataPaths,
        Online,
        Telemetry,
        Finish
    }

    public FirstRunWizardWindow()
    {
        AvaloniaXamlLoader.Load(this);
        WireControls();

        Width = TargetWidth;
        Height = TargetHeight;
        MinWidth = TargetWidth;
        MinHeight = TargetHeight;

        ApplyBubbleFrame(SplashSize, SplashSize, SplashSize / 2d, shadowProgress: 0d);
        ApplyLocalizedCopy();
        LoadLegalDocuments();
        SeedPathFields();

        Opened += OnOpened;
    }

    public static bool NeedsWizard(LauncherSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string oobe = settings.GetTextOption(SettingsKeyCompletedVersion, string.Empty);
        if (string.Equals(oobe, WizardVersion, StringComparison.Ordinal))
            return false;

        string legacy = settings.GetTextOption(SettingsKeyCompletedVersionLegacy, string.Empty);
        return !string.Equals(legacy, WizardVersion, StringComparison.Ordinal);
    }

    public static void MarkCompleted()
    {
        LauncherSettingsPageBinder.UpdateSettings(current =>
        {
            current.SetTextOption(SettingsKeyCompletedVersion, WizardVersion);
            current.SetTextOption(SettingsKeyCompletedVersionLegacy, WizardVersion);
            return current;
        });
    }

    public void PrepareFromSplash(PixelRect splashBounds)
    {
        _splashPrepared = true;
        _centerScreen = new PixelPoint(
            splashBounds.X + splashBounds.Width / 2,
            splashBounds.Y + splashBounds.Height / 2);
        WindowStartupLocation = WindowStartupLocation.Manual;
        PlaceWindowAtCenter(_centerScreen);
        ApplyBubbleFrame(SplashSize, SplashSize, SplashSize / 2d, shadowProgress: 0d);
        TryStartIntro();
    }

    public void PrepareCentered()
    {
        Width = TargetWidth;
        Height = TargetHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ApplyBubbleFrame(SplashSize, SplashSize, SplashSize / 2d, shadowProgress: 0d);
    }

    public void StartIntroAnimation()
    {
        if (!_splashPrepared)
        {
            _centerScreen = new PixelPoint(
                Position.X + (int)(TargetWidth * RenderScaling / 2d),
                Position.Y + (int)(TargetHeight * RenderScaling / 2d));
        }

        TryStartIntro();
    }

    private void WireControls()
    {
        _panBubble = this.FindControl<Border>("PanBubble");
        _pageWelcome = this.FindControl<Grid>("PageWelcome");
        _pageLegal = this.FindControl<Grid>("PageLegal");
        _pageData = this.FindControl<Grid>("PageData");
        _pageOnline = this.FindControl<Grid>("PageOnline");
        _pageTelemetry = this.FindControl<Grid>("PageTelemetry");
        _pageFinish = this.FindControl<Grid>("PageFinish");
        _heroIcon = this.FindControl<Image>("HeroIcon");
        _finishIcon = this.FindControl<Image>("FinishIcon");
        _welcomePanel = this.FindControl<StackPanel>("WelcomePanel");
        _finishPanel = this.FindControl<StackPanel>("FinishPanel");
        _labLegalMarkdown = this.FindControl<MyMarkdownViewer>("LabLegalMarkdown");
        _panLegalScroll = this.FindControl<MyScrollViewer>("PanLegalScroll");
        _txtDataPath = this.FindControl<MyTextBox>("TxtDataPath");
        _txtCachePath = this.FindControl<MyTextBox>("TxtCachePath");
        _hostOnlinePlugin = this.FindControl<ContentControl>("HostOnlinePlugin");
        _btnStart = this.FindControl<MyButton>("BtnStartSetup");
        _btnLegalDisagree = this.FindControl<MyButton>("BtnLegalDisagree");
        _btnLegalPrev = this.FindControl<MyButton>("BtnLegalPrev");
        _btnLegalNext = this.FindControl<MyButton>("BtnLegalNext");
        _btnFinish = this.FindControl<MyButton>("BtnFinish");

        if (_heroIcon?.RenderTransform is TranslateTransform tt)
            _iconTranslate = tt;
        else if (_heroIcon is not null)
        {
            _iconTranslate = new TranslateTransform();
            _heroIcon.RenderTransform = _iconTranslate;
        }

        if (_finishIcon?.RenderTransform is TranslateTransform ft)
            _finishIconTranslate = ft;
        else if (_finishIcon is not null)
        {
            _finishIconTranslate = new TranslateTransform();
            _finishIcon.RenderTransform = _finishIconTranslate;
        }
    }

    private void ApplyLocalizedCopy()
    {
        if (_btnStart is not null)
            _btnStart.Text = AvaloniaLocalizationManager.GetText("Oobe.Welcome.Start", "开始配置");
        if (this.FindControl<TextBlock>("LabWelcome") is { } lab)
            lab.Text = AvaloniaLocalizationManager.GetText("Oobe.Welcome.Title", "欢迎使用 PCL N Edition");
        if (this.FindControl<TextBlock>("LabDataTitle") is { } dataTitle)
            dataTitle.Text = AvaloniaLocalizationManager.GetText("Oobe.Data.Title", "启动器数据配置");
        if (this.FindControl<TextBlock>("LabOnlineTitle") is { } onlineTitle)
            onlineTitle.Text = AvaloniaLocalizationManager.GetText("Oobe.Online.Title", "在线服务配置");
        if (this.FindControl<TextBlock>("LabTelemetryTitle") is { } telTitle)
            telTitle.Text = AvaloniaLocalizationManager.GetText("Oobe.Telemetry.Title", "遥测与数据收集");
        if (this.FindControl<TextBlock>("LabTelemetryBody") is { } telBody)
            telBody.Text = AvaloniaLocalizationManager.GetText("Oobe.Telemetry.Empty", "暂无遥测内容");
        if (this.FindControl<TextBlock>("LabFinishTitle") is { } finish)
            finish.Text = AvaloniaLocalizationManager.GetText("Oobe.Finish.Title", "感谢您选择 PCL N Edition！");
        if (_btnFinish is not null)
            _btnFinish.Text = AvaloniaLocalizationManager.GetText("Oobe.Finish.Complete", "完成配置");
    }

    private void LoadLegalDocuments()
    {
        try
        {
            _termsMarkdown = EmbeddedLegalDocuments.LoadTermsMarkdown();
            _privacyMarkdown = EmbeddedLegalDocuments.LoadPrivacyMarkdown();
        }
        catch (Exception)
        {
            _termsMarkdown = "无法加载嵌入的《用户服务协议》。";
            _privacyMarkdown = "无法加载嵌入的《隐私保护协议》。";
        }
    }

    private void SeedPathFields()
    {
        if (_txtDataPath is not null)
            _txtDataPath.Text = LauncherPathLayout.ResolveDataDirectory();
        if (_txtCachePath is not null)
            _txtCachePath.Text = LauncherPathLayout.ResolveCacheDirectory();
    }

    private void PlaceWindowAtCenter(PixelPoint centerScreen)
    {
        double scale = RenderScaling > 0 ? RenderScaling : 1d;
        int pw = Math.Max(1, (int)Math.Round(TargetWidth * scale));
        int ph = Math.Max(1, (int)Math.Round(TargetHeight * scale));
        Position = new PixelPoint(centerScreen.X - pw / 2, centerScreen.Y - ph / 2);
    }

    private void ApplyBubbleFrame(double width, double height, double cornerRadius, double shadowProgress)
    {
        if (_panBubble is null)
            return;

        _panBubble.Width = width;
        _panBubble.Height = height;
        _panBubble.CornerRadius = new CornerRadius(cornerRadius);

        double p = Math.Clamp(shadowProgress, 0d, 1d);
        _panBubble.BoxShadow = new BoxShadows(new BoxShadow
        {
            Blur = Lerp(0d, 22d, p),
            OffsetY = Lerp(0d, 10d, p),
            Color = Color.FromArgb((byte)Math.Clamp((int)(Lerp(0d, 0.34d, p) * 255d), 0, 255), 0, 0, 0)
        });
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (_splashPrepared)
        {
            TryStartIntro();
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_introStarted)
                return;
            _centerScreen = new PixelPoint(
                Position.X + (int)(TargetWidth * RenderScaling / 2d),
                Position.Y + (int)(TargetHeight * RenderScaling / 2d));
            TryStartIntro();
        }, DispatcherPriority.Loaded);
    }

    private void TryStartIntro()
    {
        if (_introStarted)
            return;

        _introStarted = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = TargetWidth;
        Height = TargetHeight;
        ApplyBubbleFrame(SplashSize, SplashSize, SplashSize / 2d, shadowProgress: 0d);
        BeginPhase(Phase.ExpandBubble, durationMs: 780);
    }

    private void BeginPhase(Phase phase, double durationMs)
    {
        _phase = phase;
        _phaseDurationMs = Math.Max(1d, durationMs);
        _clock.Restart();

        if (phase == Phase.RevealWelcome && _welcomePanel is not null)
        {
            _welcomePanel.IsHitTestVisible = false;
            _welcomePanel.Opacity = 0;
            _welcomePanel.Margin = new Thickness(TargetWidth * 0.52, 0, 48, 0);
        }

        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        if (!_timer.IsEnabled)
            _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        double t = Math.Clamp(_clock.Elapsed.TotalMilliseconds / _phaseDurationMs, 0d, 1d);
        double eased = EaseOutCubic(t);

        switch (_phase)
        {
            case Phase.ExpandBubble:
                TickExpandBubble(eased);
                if (t >= 1d)
                {
                    ApplyBubbleFrame(BubbleEndWidth, BubbleEndHeight, SurfaceCorner, shadowProgress: 1d);
                    BeginPhase(Phase.SettleIcon, durationMs: 140);
                }
                break;
            case Phase.SettleIcon:
                if (t >= 1d)
                    BeginPhase(Phase.RevealWelcome, durationMs: 460);
                break;
            case Phase.RevealWelcome:
                TickRevealWelcome(eased);
                if (t >= 1d)
                {
                    _timer?.Stop();
                    _phase = Phase.Done;
                    if (_welcomePanel is not null)
                    {
                        _welcomePanel.Opacity = 1;
                        _welcomePanel.IsHitTestVisible = true;
                    }
                }
                break;
            case Phase.FadeWelcomeOut:
                TickFadeWelcomeOut(eased);
                if (t >= 1d)
                {
                    _timer?.Stop();
                    _phase = Phase.Done;
                    ShowStep(OobeStep.Terms, animate: false);
                }
                break;
        }
    }

    private void TickExpandBubble(double eased)
    {
        double w = Lerp(SplashSize, BubbleEndWidth, eased);
        double h = Lerp(SplashSize, BubbleEndHeight, eased);
        double circleCorner = Math.Min(w, h) / 2d;
        double corner = Lerp(circleCorner, SurfaceCorner, Math.Pow(eased, 1.6d));
        ApplyBubbleFrame(w, h, corner, shadowProgress: eased);
    }

    private void TickRevealWelcome(double eased)
    {
        if (_iconTranslate is null || _welcomePanel is null)
            return;

        double targetX = -Math.Min(BubbleEndWidth * 0.22, 170);
        _iconTranslate.X = Lerp(0, targetX, eased);
        _welcomePanel.Opacity = eased;
        double panelLeft = BubbleEndWidth * 0.5 + targetX + IconSize * 0.45;
        _welcomePanel.Margin = new Thickness(Math.Max(panelLeft, BubbleEndWidth * 0.42), 0, 48, 0);
    }

    private void TickFadeWelcomeOut(double eased)
    {
        double opacity = 1d - eased;
        if (_pageWelcome is not null)
            _pageWelcome.Opacity = opacity;
        if (_heroIcon is not null)
            _heroIcon.Opacity = opacity;
        if (_welcomePanel is not null)
            _welcomePanel.Opacity = opacity;
    }

    private void ShowStep(OobeStep step, bool animate)
    {
        _step = step;
        SetPageVisible(_pageWelcome, false);
        SetPageVisible(_pageLegal, step is OobeStep.Terms or OobeStep.Privacy);
        SetPageVisible(_pageData, step == OobeStep.DataPaths);
        SetPageVisible(_pageOnline, step == OobeStep.Online);
        SetPageVisible(_pageTelemetry, step == OobeStep.Telemetry);
        SetPageVisible(_pageFinish, step == OobeStep.Finish);

        switch (step)
        {
            case OobeStep.Terms:
                ShowLegalPage(0);
                break;
            case OobeStep.Privacy:
                ShowLegalPage(1);
                break;
            case OobeStep.DataPaths:
                SeedPathFieldsIfEmpty();
                break;
            case OobeStep.Online:
                EnsureOnlinePluginContent();
                break;
            case OobeStep.Finish:
                ApplyFinishLayout();
                break;
        }

        if (animate)
        {
            Grid? active = step switch
            {
                OobeStep.Terms or OobeStep.Privacy => _pageLegal,
                OobeStep.DataPaths => _pageData,
                OobeStep.Online => _pageOnline,
                OobeStep.Telemetry => _pageTelemetry,
                OobeStep.Finish => _pageFinish,
                _ => null
            };
            if (active is not null)
            {
                active.Opacity = 0.55;
                Dispatcher.UIThread.Post(() => active.Opacity = 1, DispatcherPriority.Render);
            }
        }
    }

    private static void SetPageVisible(Grid? page, bool visible)
    {
        if (page is null)
            return;
        page.IsVisible = visible;
        page.Opacity = visible ? 1 : 0;
        page.IsHitTestVisible = visible;
    }

    private void SeedPathFieldsIfEmpty()
    {
        if (_txtDataPath is not null && string.IsNullOrWhiteSpace(_txtDataPath.Text))
            _txtDataPath.Text = LauncherPathLayout.ResolveDataDirectory();
        if (_txtCachePath is not null && string.IsNullOrWhiteSpace(_txtCachePath.Text))
            _txtCachePath.Text = LauncherPathLayout.ResolveCacheDirectory();
    }

    private void ShowLegalPage(int index)
    {
        _legalPageIndex = index is 0 or 1 ? index : 0;
        if (_labLegalMarkdown is not null)
            _labLegalMarkdown.Markdown = _legalPageIndex == 0 ? _termsMarkdown : _privacyMarkdown;
        _panLegalScroll?.ScrollToHome();

        if (_legalPageIndex == 0)
        {
            if (_btnLegalDisagree is not null)
            {
                _btnLegalDisagree.Text = AvaloniaLocalizationManager.GetText("Oobe.Legal.Disagree", "不同意");
                _btnLegalDisagree.ColorType = MyButton.ColorState.Red;
            }

            if (_btnLegalPrev is not null)
            {
                _btnLegalPrev.Text = AvaloniaLocalizationManager.GetText("Oobe.Nav.Previous", "上一页");
                _btnLegalPrev.IsEnabled = false;
            }

            if (_btnLegalNext is not null)
            {
                _btnLegalNext.Text = AvaloniaLocalizationManager.GetText("Oobe.Nav.Next", "下一页");
                _btnLegalNext.ColorType = MyButton.ColorState.Highlight;
            }
        }
        else
        {
            if (_btnLegalDisagree is not null)
            {
                _btnLegalDisagree.Text = AvaloniaLocalizationManager.GetText(
                    "Oobe.Legal.DisagreeExit",
                    "不同意并退出");
                _btnLegalDisagree.ColorType = MyButton.ColorState.Red;
            }

            if (_btnLegalPrev is not null)
            {
                _btnLegalPrev.Text = AvaloniaLocalizationManager.GetText("Oobe.Nav.Previous", "上一页");
                _btnLegalPrev.IsEnabled = true;
            }

            if (_btnLegalNext is not null)
            {
                _btnLegalNext.Text = AvaloniaLocalizationManager.GetText("Oobe.Legal.Agree", "同意条款");
                _btnLegalNext.ColorType = MyButton.ColorState.Highlight;
            }
        }
    }

    private void ApplyFinishLayout()
    {
        if (_finishLayoutApplied)
            return;
        _finishLayoutApplied = true;

        double targetX = -Math.Min(BubbleEndWidth * 0.22, 170);
        if (_finishIconTranslate is not null)
            _finishIconTranslate.X = targetX;
        if (_finishPanel is not null)
        {
            double panelLeft = BubbleEndWidth * 0.5 + targetX + IconSize * 0.45;
            _finishPanel.Margin = new Thickness(Math.Max(panelLeft, BubbleEndWidth * 0.42), 0, 48, 0);
        }
    }

    private void EnsureOnlinePluginContent()
    {
        // Plugin actively provides OOBE online page via sidecar data-chain.
        _ = Task.Run(async () =>
        {
            try
            {
                if (!PluginSidecarSupervisor.Instance.IsAvailable)
                    await PluginSidecarSupervisor.Instance.TryStartAsync().ConfigureAwait(false);
            }
            catch
            {
                // Host will show fallback.
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_hostOnlinePlugin is null || _step != OobeStep.Online)
                    return;

                if (PluginSidecarSupervisor.Instance.IsAvailable)
                {
                    if (_hostOnlinePlugin.Content is PageSetupRemoteDataChain)
                        return;
                    _hostOnlinePlugin.Content = new PageSetupRemoteDataChain(PluginOobeOnlinePageId)
                    {
                        Margin = new Thickness(0)
                    };
                    return;
                }

                _onlineFallback ??= CreateOnlineFallback();
                _hostOnlinePlugin.Content = _onlineFallback;
            });
        });
    }

    private static StackPanel CreateOnlineFallback()
    {
        return new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(0, 12, 0, 0),
            Children =
            {
                new MyHint
                {
                    Text = AvaloniaLocalizationManager.GetText(
                        "Oobe.Online.Unavailable",
                        "插件侧车未运行，暂时无法登录在线账户。可跳过本页，稍后在「设置 → 在线」中连接。"),
                    Theme = MyHint.Themes.Yellow
                },
                new TextBlock
                {
                    Text = AvaloniaLocalizationManager.GetText(
                        "Oobe.Online.Unavailable.Detail",
                        "在线服务由 Plugin 提供；打包完整发行版后，此页将显示登录与云同步入口。"),
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#FF1C1C1E"))
                }
            }
        };
    }

    private void BtnStartSetup_Click(object? sender, EventArgs e)
    {
        if (_phase is Phase.ExpandBubble or Phase.SettleIcon or Phase.RevealWelcome or Phase.FadeWelcomeOut)
            return;

        if (_welcomePanel is not null)
            _welcomePanel.IsHitTestVisible = false;
        if (_btnStart is not null)
            _btnStart.IsEnabled = false;

        // Warm sidecar early so page 5 is ready.
        _ = PluginSidecarSupervisor.Instance.TryStartAsync();
        BeginPhase(Phase.FadeWelcomeOut, durationMs: 320);
    }

    private void BtnLegalDisagree_Click(object? sender, EventArgs e) => ShutdownHost();

    private void BtnLegalPrev_Click(object? sender, EventArgs e)
    {
        if (_step == OobeStep.Privacy)
            ShowStep(OobeStep.Terms, animate: true);
    }

    private void BtnLegalNext_Click(object? sender, EventArgs e)
    {
        if (_step == OobeStep.Terms)
        {
            ShowStep(OobeStep.Privacy, animate: true);
            return;
        }

        // Privacy accepted → record legal, continue OOBE (do not finish yet).
        LauncherSettingsPageBinder.UpdateSettings(current =>
        {
            current.SetTextOption(
                EmbeddedLegalDocuments.SettingsKeyAcceptedVersion,
                EmbeddedLegalDocuments.DocumentVersion);
            return current;
        });
        ShowStep(OobeStep.DataPaths, animate: true);
    }

    private async void BtnBrowseData_Click(object? sender, EventArgs e)
    {
        string? path = await PickFolderAsync(
            AvaloniaLocalizationManager.GetText("Oobe.Data.BrowseData", "选择启动器数据位置")).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(path) && _txtDataPath is not null)
            _txtDataPath.Text = path;
    }

    private async void BtnBrowseCache_Click(object? sender, EventArgs e)
    {
        string? path = await PickFolderAsync(
            AvaloniaLocalizationManager.GetText("Oobe.Data.BrowseCache", "选择启动器缓存位置")).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(path) && _txtCachePath is not null)
            _txtCachePath.Text = path;
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        IStorageProvider? storage = StorageProvider;
        if (storage?.CanPickFolder != true)
            return null;

        IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false
            }).ConfigureAwait(true);
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    private void BtnDataPrev_Click(object? sender, EventArgs e) => ShowStep(OobeStep.Privacy, animate: true);

    private void BtnDataNext_Click(object? sender, EventArgs e) => ShowStep(OobeStep.Online, animate: true);

    private void BtnOnlinePrev_Click(object? sender, EventArgs e) => ShowStep(OobeStep.DataPaths, animate: true);

    private void BtnOnlineNext_Click(object? sender, EventArgs e) => ShowStep(OobeStep.Telemetry, animate: true);

    private void BtnTelemetryPrev_Click(object? sender, EventArgs e) => ShowStep(OobeStep.Online, animate: true);

    private void BtnTelemetryNext_Click(object? sender, EventArgs e) => ShowStep(OobeStep.Finish, animate: true);

    private void BtnFinish_Click(object? sender, EventArgs e)
    {
        if (_completing)
            return;
        _completing = true;
        if (_btnFinish is not null)
            _btnFinish.IsEnabled = false;

        try
        {
            string dataPath = _txtDataPath?.Text?.Trim() ?? LauncherPathLayout.GetDefaultDataDirectory();
            string cachePath = _txtCachePath?.Text?.Trim() ?? LauncherPathLayout.GetDefaultCacheDirectory();

            // Persist path overrides + migrate existing config into the chosen roots.
            LauncherPathLayout.ApplyAndMigrate(dataPath, cachePath);

            LauncherSettingsPageBinder.UpdateSettings(current =>
            {
                current.SetTextOption(
                    EmbeddedLegalDocuments.SettingsKeyAcceptedVersion,
                    EmbeddedLegalDocuments.DocumentVersion);
                current.SetTextOption(SettingsKeyCompletedVersion, WizardVersion);
                current.SetTextOption(SettingsKeyCompletedVersionLegacy, WizardVersion);
                current.SetTextOption("OobeDataDirectory", LauncherPathLayout.ResolveDataDirectory());
                current.SetTextOption("OobeCacheDirectory", LauncherPathLayout.ResolveCacheDirectory());
                return current;
            });

            Completed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _completing = false;
            if (_btnFinish is not null)
                _btnFinish.IsEnabled = true;
            PortableLogFinishError(ex);
        }
    }

    private static void PortableLogFinishError(Exception ex)
    {
        try
        {
            PCL.Core.Logging.PortableLog.Warn("OOBE", "完成配置失败：" + ex.Message);
        }
        catch
        {
            // ignore
        }
    }

    private void ShutdownHost()
    {
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown(0);
                return;
            }
        }
        catch (Exception)
        {
            // fall through
        }

        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _timer?.Stop();
        _timer = null;
        base.OnClosing(e);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static double EaseOutCubic(double t)
    {
        double u = 1d - t;
        return 1d - u * u * u;
    }
}

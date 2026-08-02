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
using PCL.Desktop.Theme;
using PCL.Desktop.Telemetry;

namespace PCL.Desktop.Views.FirstRun;

/// <summary>
/// Configurable OOBE shell. Step lists come from <see cref="OobeConfiguration"/> /
/// <c>pcln-oobe.json</c>: full install flow vs short post-update flow.
/// </summary>
public sealed partial class FirstRunWizardWindow : Window
{
    // Compatibility aliases for callers/tests.
    public const string SettingsKeyCompletedVersion = OobeConfiguration.SettingsKeyCompletedVersion;
    public const string SettingsKeyCompletedVersionLegacy = OobeConfiguration.SettingsKeyCompletedVersionLegacy;
    public const string WizardVersion = OobeConfiguration.DefaultContentVersion;
    public const string PluginOobeOnlinePageId = "pcl.oobe.online";

    private const double SplashSize = 136d;
    private const double IconSize = 112d;
    private const double TargetWidth = 860d;
    private const double TargetHeight = 520d;
    private const double BubbleMargin = 12d;
    private const double BubbleEndWidth = TargetWidth - BubbleMargin * 2d;
    private const double BubbleEndHeight = TargetHeight - BubbleMargin * 2d;
    private const double SurfaceCorner = 12d;
    private const string StepTransitionAnimationKey = "OOBE Step Transition";

    private readonly OobeRunPlan _plan;
    private readonly Stopwatch _clock = new();
    private DispatcherTimer? _timer;
    private Phase _phase = Phase.Idle;
    private double _phaseDurationMs;
    private PixelPoint _centerScreen;
    private bool _introStarted;
    private bool _splashPrepared;
    private int _stepIndex;
    private OobeStepId _step = OobeStepId.Welcome;
    private bool _finishLayoutApplied;
    private bool _completing;
    private bool _visitedDataPaths;

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
    private MyCheckBox? _checkExperienceProgram;

    private string _termsMarkdown = string.Empty;
    private string _privacyMarkdown = string.Empty;
    private StackPanel? _onlineFallback;

    /// <summary>Raised when OOBE finishes successfully.</summary>
    public event EventHandler? Completed;

    /// <summary>
    /// Raised after config paths are applied mid-wizard; host should relaunch with
    /// <see cref="RestartArguments"/> so the sidecar can extract into the new data root.
    /// </summary>
    public event EventHandler? PathRestartRequested;

    /// <summary>
    /// Raised once intro expand has begun (or skipped). Host should dismiss splash only after this
    /// so the logo handoff does not flicker.
    /// </summary>
    public event EventHandler? IntroStarted;

    public OobeRunPlan Plan => _plan;

    public bool HasIntroStarted => _introStarted;

    public bool ShouldRestartAfterComplete => _plan.RestartAfterComplete;

    /// <summary>CLI args for the next process when <see cref="PathRestartRequested"/> fires.</summary>
    public IReadOnlyList<string> RestartArguments { get; private set; } = [];

    private enum Phase
    {
        Idle,
        ExpandBubble,
        SettleIcon,
        RevealWelcome,
        FadeWelcomeOut,
        Done
    }

    public FirstRunWizardWindow()
        : this(OobeConfiguration.CreateRunPlan(LauncherSettingsPageBinder.LoadSettings()))
    {
    }

    public FirstRunWizardWindow(OobeRunPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Steps.Count == 0)
            throw new ArgumentException("OOBE plan must contain at least one step.", nameof(plan));

        _plan = plan;
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
        SeedTelemetryChoice();
        ApplyWelcomeCopyForPlan();

        Opened += OnOpened;
    }

    public static bool NeedsWizard(LauncherSettings settings) =>
        OobeConfiguration.ShouldRun(settings);

    public static void MarkCompleted() =>
        OobeConfiguration.MarkCompleted(OobeConfiguration.Current.ContentVersion);

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
        _checkExperienceProgram = this.FindControl<MyCheckBox>("CheckExperienceProgram");

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
        if (this.FindControl<TextBlock>("LabDataTitle") is { } dataTitle)
            dataTitle.Text = AvaloniaLocalizationManager.GetText("Oobe.Data.Title", "启动器数据配置");
        if (this.FindControl<TextBlock>("LabOnlineTitle") is { } onlineTitle)
            onlineTitle.Text = AvaloniaLocalizationManager.GetText("Oobe.Online.Title", "在线服务配置");
        if (this.FindControl<TextBlock>("LabTelemetryTitle") is { } telTitle)
            telTitle.Text = AvaloniaLocalizationManager.GetText("Oobe.Telemetry.Title", "遥测与数据收集");
        if (this.FindControl<TextBlock>("LabFinishTitle") is { } finish)
            finish.Text = AvaloniaLocalizationManager.GetText("Oobe.Finish.Title", "感谢您选择 PCL N Edition！");
        if (_btnFinish is not null)
            _btnFinish.Text = AvaloniaLocalizationManager.GetText("Oobe.Finish.Complete", "完成配置");
    }

    private void ApplyWelcomeCopyForPlan()
    {
        bool update = _plan.Kind == OobeRunKind.Update;
        bool resume = _plan.Kind == OobeRunKind.Resume;
        if (this.FindControl<TextBlock>("LabWelcome") is { } lab)
        {
            lab.Text = resume
                ? AvaloniaLocalizationManager.GetText(
                    "Oobe.Welcome.ResumeTitle",
                    "配置目录已就绪")
                : update
                    ? AvaloniaLocalizationManager.GetText("Oobe.Welcome.UpdateTitle", "PCL N Edition 已更新")
                    : AvaloniaLocalizationManager.GetText("Oobe.Welcome.Title", "欢迎使用 PCL N Edition");
        }

        if (this.FindControl<TextBlock>("LabWelcomeDetail") is { } detail && resume)
        {
            detail.Text = AvaloniaLocalizationManager.GetText(
                "Oobe.Welcome.ResumeDetail",
                "插件已连接到你的配置目录。下一步将进行在线服务配置。");
        }

        if (_btnStart is not null)
        {
            _btnStart.Text = resume || update
                ? AvaloniaLocalizationManager.GetText("Oobe.Welcome.Continue", "继续")
                : AvaloniaLocalizationManager.GetText("Oobe.Welcome.Start", "开始配置");
        }
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

    private void SeedTelemetryChoice()
    {
        if (_checkExperienceProgram is null)
            return;
        LauncherSettings settings = LauncherSettingsPageBinder.LoadSettings();
        _checkExperienceProgram.Checked = settings.GetBooleanOption(
            LauncherTelemetry.ExperienceSettingKey,
            LauncherSettingDefaults.GetBoolean(LauncherTelemetry.ExperienceSettingKey));
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
        // If plan has no Welcome, jump straight into first content step after expand.
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
        // Keep bubble at splash size for the first frames so splash can release without a gap.
        Width = TargetWidth;
        Height = TargetHeight;
        ApplyBubbleFrame(SplashSize, SplashSize, SplashSize / 2d, shadowProgress: 0d);

        try
        {
            IntroStarted?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // host dismiss must not break intro
        }

        // Plans that skip Welcome still expand chrome, then land on first step.
        if (!_plan.Steps.Contains(OobeStepId.Welcome))
        {
            ApplyBubbleFrame(BubbleEndWidth, BubbleEndHeight, SurfaceCorner, shadowProgress: 1d);
            ShowStepAt(0, animate: false);
            return;
        }

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

                    _stepIndex = Math.Max(0, IndexOfStep(OobeStepId.Welcome));
                    _step = OobeStepId.Welcome;
                }
                break;
            case Phase.FadeWelcomeOut:
                TickFadeWelcomeOut(eased);
                if (t >= 1d)
                {
                    _timer?.Stop();
                    _phase = Phase.Done;
                    GoToNextStep(animate: true);
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

    private int IndexOfStep(OobeStepId id)
    {
        for (int i = 0; i < _plan.Steps.Count; i++)
        {
            if (_plan.Steps[i] == id)
                return i;
        }

        return -1;
    }

    private void GoToNextStep(bool animate)
    {
        int next = _stepIndex + 1;
        if (next >= _plan.Steps.Count)
        {
            // Safety: land on finish if present.
            int finish = IndexOfStep(OobeStepId.Finish);
            ShowStepAt(finish >= 0 ? finish : _plan.Steps.Count - 1, animate);
            return;
        }

        ShowStepAt(next, animate);
    }

    private void GoToPreviousStep(bool animate)
    {
        int prev = _stepIndex - 1;
        while (prev >= 0 && _plan.Steps[prev] == OobeStepId.Welcome)
            prev--; // do not re-enter splash welcome via back

        if (prev < 0)
            return;
        ShowStepAt(prev, animate);
    }

    private void ShowStepAt(int index, bool animate)
    {
        index = Math.Clamp(index, 0, _plan.Steps.Count - 1);
        int previousIndex = _stepIndex;
        Grid? outgoing = GetPageForStep(_step);
        _stepIndex = index;
        OobeStepId step = _plan.Steps[index];
        _step = step;

        switch (step)
        {
            case OobeStepId.Terms:
                ConfigureLegalPage(isPrivacy: false);
                break;
            case OobeStepId.Privacy:
                ConfigureLegalPage(isPrivacy: true);
                break;
            case OobeStepId.DataPaths:
                _visitedDataPaths = true;
                SeedPathFieldsIfEmpty();
                ConfigureNavButtons(
                    this.FindControl<MyButton>("BtnDataPrev"),
                    this.FindControl<MyButton>("BtnDataNext"),
                    canPrev: CanGoPrevious());
                break;
            case OobeStepId.Online:
                EnsureOnlinePluginContent();
                ConfigureNavButtons(
                    this.FindControl<MyButton>("BtnOnlinePrev"),
                    this.FindControl<MyButton>("BtnOnlineNext"),
                    canPrev: CanGoPrevious());
                break;
            case OobeStepId.Telemetry:
                ConfigureNavButtons(
                    this.FindControl<MyButton>("BtnTelemetryPrev"),
                    this.FindControl<MyButton>("BtnTelemetryNext"),
                    canPrev: CanGoPrevious());
                break;
            case OobeStepId.Finish:
                ApplyFinishLayout();
                break;
        }

        Grid? incoming = GetPageForStep(step);
        if (animate && incoming is not null && ControlVisualHelpers.ShouldAnimate(this))
            AnimateStepTransition(outgoing, incoming, index >= previousIndex ? 1d : -1d);
        else
            CompleteStepTransition(outgoing, incoming);
    }

    private bool CanGoPrevious()
    {
        for (int i = _stepIndex - 1; i >= 0; i--)
        {
            if (_plan.Steps[i] != OobeStepId.Welcome)
                return true;
        }

        return false;
    }

    private static void ConfigureNavButtons(MyButton? prev, MyButton? next, bool canPrev)
    {
        if (prev is not null)
        {
            prev.Text = AvaloniaLocalizationManager.GetText("Oobe.Nav.Previous", "上一页");
            prev.IsEnabled = canPrev;
        }

        if (next is not null)
            next.Text = AvaloniaLocalizationManager.GetText("Oobe.Nav.Next", "下一页");
    }

    private Grid? GetPageForStep(OobeStepId step) =>
        step switch
        {
            OobeStepId.Terms or OobeStepId.Privacy => _pageLegal,
            OobeStepId.DataPaths => _pageData,
            OobeStepId.Online => _pageOnline,
            OobeStepId.Telemetry => _pageTelemetry,
            OobeStepId.Finish => _pageFinish,
            OobeStepId.Welcome => _pageWelcome,
            _ => null
        };

    private IEnumerable<Grid> GetStepPages()
    {
        Grid?[] pages = [_pageWelcome, _pageLegal, _pageData, _pageOnline, _pageTelemetry, _pageFinish];
        foreach (Grid? page in pages)
        {
            if (page is not null)
                yield return page;
        }
    }

    private void AnimateStepTransition(Grid? outgoing, Grid incoming, double direction)
    {
        ModAnimation.AniStop(StepTransitionAnimationKey);
        foreach (Grid page in GetStepPages())
        {
            if (!ReferenceEquals(page, outgoing) && !ReferenceEquals(page, incoming))
                SetPageRestState(page, visible: false);
        }

        if (ReferenceEquals(outgoing, incoming))
        {
            incoming.IsVisible = true;
            incoming.IsHitTestVisible = false;
            incoming.Opacity = 0.38d;
            TranslateTransform sameTransform = EnsurePageTranslate(incoming);
            sameTransform.X = direction * MotionTokens.OobeStepOffsetX * 0.6d;
            ModAnimation.AniStart(
                new List<ModAnimation.AniData>
                {
                    ModAnimation.AaOpacity(
                        incoming,
                        1d - incoming.Opacity,
                        MotionTokens.OobeStepEnterMs,
                        ease: new ModAnimation.AniEaseOutFluent()),
                    ModAnimation.AaTranslateX(
                        incoming,
                        -sameTransform.X,
                        MotionTokens.OobeStepEnterMs,
                        ease: new ModAnimation.AniEaseOutFluent()),
                    ModAnimation.AaCode(
                        () => CompleteStepTransition(outgoing, incoming),
                        after: true)
                },
                StepTransitionAnimationKey);
            return;
        }

        incoming.IsVisible = true;
        incoming.IsHitTestVisible = false;
        incoming.Opacity = 0d;
        TranslateTransform incomingTransform = EnsurePageTranslate(incoming);
        incomingTransform.X = direction * MotionTokens.OobeStepOffsetX;

        List<ModAnimation.AniData> animations =
        [
            ModAnimation.AaOpacity(
                incoming,
                1d,
                MotionTokens.OobeStepEnterMs,
                ease: new ModAnimation.AniEaseOutFluent()),
            ModAnimation.AaTranslateX(
                incoming,
                -incomingTransform.X,
                MotionTokens.OobeStepEnterMs,
                ease: new ModAnimation.AniEaseOutFluent())
        ];

        if (outgoing is not null)
        {
            outgoing.IsVisible = true;
            outgoing.IsHitTestVisible = false;
            TranslateTransform outgoingTransform = EnsurePageTranslate(outgoing);
            double outgoingTarget = -direction * MotionTokens.OobeStepOffsetX * 0.5d;
            animations.Add(ModAnimation.AaOpacity(
                outgoing,
                -outgoing.Opacity,
                MotionTokens.OobeStepExitMs,
                ease: new ModAnimation.AniEaseOutFluent()));
            animations.Add(ModAnimation.AaTranslateX(
                outgoing,
                outgoingTarget - outgoingTransform.X,
                MotionTokens.OobeStepExitMs,
                ease: new ModAnimation.AniEaseOutFluent()));
        }

        animations.Add(ModAnimation.AaCode(
            () => CompleteStepTransition(outgoing, incoming),
            after: true));
        ModAnimation.AniStart(animations, StepTransitionAnimationKey);
    }

    private void CompleteStepTransition(Grid? outgoing, Grid? incoming)
    {
        ModAnimation.AniStop(StepTransitionAnimationKey);
        foreach (Grid page in GetStepPages())
            SetPageRestState(page, ReferenceEquals(page, incoming));
    }

    private static TranslateTransform EnsurePageTranslate(Grid page)
    {
        if (page.RenderTransform is TranslateTransform translate)
            return translate;

        translate = new TranslateTransform();
        page.RenderTransform = translate;
        return translate;
    }

    private static void SetPageRestState(Grid page, bool visible)
    {
        page.IsVisible = visible;
        page.Opacity = visible ? 1d : 0d;
        page.IsHitTestVisible = visible;
        EnsurePageTranslate(page).X = 0d;
    }

    private void SeedPathFieldsIfEmpty()
    {
        if (_txtDataPath is not null && string.IsNullOrWhiteSpace(_txtDataPath.Text))
            _txtDataPath.Text = LauncherPathLayout.ResolveDataDirectory();
        if (_txtCachePath is not null && string.IsNullOrWhiteSpace(_txtCachePath.Text))
            _txtCachePath.Text = LauncherPathLayout.ResolveCacheDirectory();
    }

    private void ConfigureLegalPage(bool isPrivacy)
    {
        if (_labLegalMarkdown is not null)
            _labLegalMarkdown.Markdown = isPrivacy ? _privacyMarkdown : _termsMarkdown;
        _panLegalScroll?.ScrollToHome();

        if (_btnLegalDisagree is not null)
        {
            _btnLegalDisagree.Text = isPrivacy
                ? AvaloniaLocalizationManager.GetText("Oobe.Legal.DisagreeExit", "不同意并退出")
                : AvaloniaLocalizationManager.GetText("Oobe.Legal.Disagree", "不同意");
            _btnLegalDisagree.ColorType = MyButton.ColorState.Red;
        }

        if (_btnLegalPrev is not null)
        {
            _btnLegalPrev.Text = AvaloniaLocalizationManager.GetText("Oobe.Nav.Previous", "上一页");
            _btnLegalPrev.IsEnabled = CanGoPrevious();
        }

        if (_btnLegalNext is not null)
        {
            bool lastLegal = isPrivacy || IndexOfStep(OobeStepId.Privacy) < 0;
            // "同意条款" only when this is the last legal step before non-legal content / finish.
            bool nextIsNonLegal = true;
            if (_stepIndex + 1 < _plan.Steps.Count)
            {
                OobeStepId next = _plan.Steps[_stepIndex + 1];
                nextIsNonLegal = next is not (OobeStepId.Terms or OobeStepId.Privacy);
            }

            _btnLegalNext.Text = lastLegal && nextIsNonLegal
                ? AvaloniaLocalizationManager.GetText("Oobe.Legal.Agree", "同意条款")
                : AvaloniaLocalizationManager.GetText("Oobe.Nav.Next", "下一页");
            _btnLegalNext.ColorType = MyButton.ColorState.Highlight;
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
                if (_hostOnlinePlugin is null || _step != OobeStepId.Online)
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

        if (_plan.Steps.Contains(OobeStepId.Online))
            _ = PluginSidecarSupervisor.Instance.TryStartAsync();

        BeginPhase(Phase.FadeWelcomeOut, durationMs: 320);
    }

    private void BtnLegalDisagree_Click(object? sender, EventArgs e) => ShutdownHost();

    private void BtnLegalPrev_Click(object? sender, EventArgs e) => GoToPreviousStep(animate: true);

    private void BtnLegalNext_Click(object? sender, EventArgs e)
    {
        if (_step is OobeStepId.Terms or OobeStepId.Privacy)
        {
            // Accept legal when leaving the last legal step in the plan.
            bool moreLegal = false;
            for (int i = _stepIndex + 1; i < _plan.Steps.Count; i++)
            {
                if (_plan.Steps[i] is OobeStepId.Terms or OobeStepId.Privacy)
                {
                    moreLegal = true;
                    break;
                }
            }

            if (!moreLegal)
            {
                LauncherSettingsPageBinder.UpdateSettings(current =>
                {
                    current.SetTextOption(
                        EmbeddedLegalDocuments.SettingsKeyAcceptedVersion,
                        EmbeddedLegalDocuments.DocumentVersion);
                    return current;
                });
            }
        }

        GoToNextStep(animate: true);
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

    private void BtnDataPrev_Click(object? sender, EventArgs e) => GoToPreviousStep(animate: true);

    private void BtnDataNext_Click(object? sender, EventArgs e)
    {
        // Apply config dirs now, then restart with --oobe-resume so the next process
        // extracts the embedded plugin into the chosen data directory before Welcome → Online.
        try
        {
            string dataPath = _txtDataPath?.Text?.Trim() ?? LauncherPathLayout.GetDefaultDataDirectory();
            string cachePath = _txtCachePath?.Text?.Trim() ?? LauncherPathLayout.GetDefaultCacheDirectory();
            LauncherPathLayout.ApplyAndMigrate(dataPath, cachePath);
            _visitedDataPaths = true;

            LauncherSettingsPageBinder.UpdateSettings(current =>
            {
                current.SetTextOption("OobeDataDirectory", LauncherPathLayout.ResolveDataDirectory());
                current.SetTextOption("OobeCacheDirectory", LauncherPathLayout.ResolveCacheDirectory());
                return current;
            });

            OobeConfiguration.WriteResumeMarker();
            RestartArguments = [OobeConfiguration.ResumeArgument];
            PathRestartRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            PCL.Core.Logging.PortableLog.Warn("OOBE", "应用配置目录并重启失败：" + ex.Message);
            // Fall back to in-process next step so the user is not stuck.
            GoToNextStep(animate: true);
        }
    }

    private void BtnOnlinePrev_Click(object? sender, EventArgs e) => GoToPreviousStep(animate: true);

    private void BtnOnlineNext_Click(object? sender, EventArgs e) => GoToNextStep(animate: true);

    private void BtnTelemetryPrev_Click(object? sender, EventArgs e) => GoToPreviousStep(animate: true);

    private void BtnTelemetryNext_Click(object? sender, EventArgs e)
    {
        PersistTelemetryChoice();
        GoToNextStep(animate: true);
    }

    private void PersistTelemetryChoice()
    {
        bool enabled = _checkExperienceProgram?.Checked == true;
        LauncherSettingsPageBinder.UpdateSettings(current =>
        {
            current.SetBooleanOption(LauncherTelemetry.ExperienceSettingKey, enabled);
            if (!enabled)
                current.RemoveTextOption(LauncherTelemetry.AnonymousIdSettingKey);
            return current;
        });
    }

    private void BtnFinish_Click(object? sender, EventArgs e)
    {
        if (_completing)
            return;
        _completing = true;
        if (_btnFinish is not null)
            _btnFinish.IsEnabled = false;

        try
        {
            // Paths may already be applied at DataPaths → restart; re-apply only if still needed.
            if (_visitedDataPaths && _plan.Steps.Contains(OobeStepId.DataPaths) &&
                _plan.Kind != OobeRunKind.Resume)
            {
                string dataPath = _txtDataPath?.Text?.Trim() ?? LauncherPathLayout.GetDefaultDataDirectory();
                string cachePath = _txtCachePath?.Text?.Trim() ?? LauncherPathLayout.GetDefaultCacheDirectory();
                LauncherPathLayout.ApplyAndMigrate(dataPath, cachePath);
            }

            OobeConfiguration.MarkCompleted(_plan.ContentVersion);
            PersistTelemetryChoice();
            LauncherSettingsPageBinder.UpdateSettings(current =>
            {
                if (_plan.Steps.Contains(OobeStepId.Terms) || _plan.Steps.Contains(OobeStepId.Privacy))
                {
                    current.SetTextOption(
                        EmbeddedLegalDocuments.SettingsKeyAcceptedVersion,
                        EmbeddedLegalDocuments.DocumentVersion);
                }

                current.SetTextOption("OobeDataDirectory", LauncherPathLayout.ResolveDataDirectory());
                current.SetTextOption("OobeCacheDirectory", LauncherPathLayout.ResolveCacheDirectory());
                current.SetTextOption("OobeLastRunKind", _plan.Kind.ToString());
                return current;
            });

            Completed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _completing = false;
            if (_btnFinish is not null)
                _btnFinish.IsEnabled = true;
            try
            {
                PCL.Core.Logging.PortableLog.Warn("OOBE", "完成配置失败：" + ex.Message);
            }
            catch
            {
                // ignore
            }
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

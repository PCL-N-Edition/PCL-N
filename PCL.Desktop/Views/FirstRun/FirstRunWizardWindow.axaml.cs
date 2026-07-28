// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using PCL.Application.Settings;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Features.Settings.Views;
using PCL.Desktop.Legal;
using PCL.Desktop.Localization;

namespace PCL.Desktop.Views.FirstRun;

/// <summary>
/// First-run wizard:
/// page 1 welcome (fixed-size circular reveal + icon slide),
/// page 2 terms, page 3 privacy.
/// </summary>
public sealed partial class FirstRunWizardWindow : Window
{
    public const string SettingsKeyCompletedVersion = "UiFirstRunWizardVersion";
    public const string WizardVersion = "1";

    private const double SplashSize = 136d;
    private const double IconSize = 112d;
    private const double TargetWidth = 860d;
    private const double TargetHeight = 520d;
    private const double SurfaceCorner = 12d;

    private readonly Stopwatch _clock = new();
    private DispatcherTimer? _timer;
    private Phase _phase = Phase.Idle;
    private double _phaseDurationMs;
    private PixelPoint _centerScreen;
    private bool _introStarted;
    private bool _splashPrepared;
    private int _legalPageIndex; // 0 = terms, 1 = privacy

    private EllipseGeometry? _revealClip;
    private Image? _heroIcon;
    private StackPanel? _welcomePanel;
    private Grid? _pageWelcome;
    private Grid? _pageLegal;
    private Border? _panSurface;
    private TranslateTransform? _iconTranslate;
    private MyMarkdownViewer? _labLegalMarkdown;
    private MyScrollViewer? _panLegalScroll;
    private MyButton? _btnStart;
    private MyButton? _btnDisagree;
    private MyButton? _btnPrev;
    private MyButton? _btnNext;

    private string _termsMarkdown = string.Empty;
    private string _privacyMarkdown = string.Empty;

    public event EventHandler? Completed;

    private enum Phase
    {
        Idle,
        ExpandClip,
        SettleIcon,
        RevealWelcome,
        FadeWelcomeOut,
        Done
    }

    public FirstRunWizardWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _panSurface = this.FindControl<Border>("PanSurface");
        _pageWelcome = this.FindControl<Grid>("PageWelcome");
        _pageLegal = this.FindControl<Grid>("PageLegal");
        _heroIcon = this.FindControl<Image>("HeroIcon");
        _welcomePanel = this.FindControl<StackPanel>("WelcomePanel");
        _labLegalMarkdown = this.FindControl<MyMarkdownViewer>("LabLegalMarkdown");
        _panLegalScroll = this.FindControl<MyScrollViewer>("PanLegalScroll");
        _btnStart = this.FindControl<MyButton>("BtnStartSetup");
        _btnDisagree = this.FindControl<MyButton>("BtnLegalDisagree");
        _btnPrev = this.FindControl<MyButton>("BtnLegalPrev");
        _btnNext = this.FindControl<MyButton>("BtnLegalNext");

        // Fixed window size from construction — never animate Width/Height/Position each frame.
        Width = TargetWidth;
        Height = TargetHeight;
        MinWidth = TargetWidth;
        MinHeight = TargetHeight;

        _revealClip = new EllipseGeometry
        {
            Center = new Point(TargetWidth / 2d, TargetHeight / 2d),
            RadiusX = SplashSize / 2d,
            RadiusY = SplashSize / 2d
        };
        if (_panSurface is not null)
            _panSurface.Clip = _revealClip;

        if (_heroIcon?.RenderTransform is TranslateTransform tt)
            _iconTranslate = tt;
        else if (_heroIcon is not null)
        {
            _iconTranslate = new TranslateTransform();
            _heroIcon.RenderTransform = _iconTranslate;
        }

        ApplyLocalizedCopy();
        LoadLegalDocuments();

        Opened += OnOpened;
    }

    public static bool NeedsWizard(LauncherSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string seen = settings.GetTextOption(SettingsKeyCompletedVersion, string.Empty);
        return !string.Equals(seen, WizardVersion, StringComparison.Ordinal);
    }

    public static void MarkCompleted()
    {
        LauncherSettingsPageBinder.UpdateSettings(current =>
        {
            current.SetTextOption(SettingsKeyCompletedVersion, WizardVersion);
            return current;
        });
    }

    /// <summary>Center on splash icon (screen pixels), keep full window size, start clip expand.</summary>
    public void PrepareFromSplash(PixelRect splashBounds)
    {
        _splashPrepared = true;
        _centerScreen = new PixelPoint(
            splashBounds.X + splashBounds.Width / 2,
            splashBounds.Y + splashBounds.Height / 2);
        WindowStartupLocation = WindowStartupLocation.Manual;
        PlaceWindowAtCenter(_centerScreen);
        ResetRevealClip();
        TryStartIntro();
    }

    public void PrepareCentered()
    {
        Width = TargetWidth;
        Height = TargetHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResetRevealClip();
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

    private void ApplyLocalizedCopy()
    {
        if (_btnStart is not null)
            _btnStart.Text = AvaloniaLocalizationManager.GetText("FirstRun.Page1.StartSetup", "开始配置");
        if (this.FindControl<TextBlock>("LabWelcome") is { } lab)
        {
            lab.Text = AvaloniaLocalizationManager.GetText(
                "FirstRun.Page1.WelcomeTitle",
                "欢迎使用 PCL N Edition");
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
            _termsMarkdown =
                "无法加载嵌入的《用户服务协议》。\n\n请从官方渠道重新获取安装包。";
            _privacyMarkdown =
                "无法加载嵌入的《隐私保护协议》。\n\n请从官方渠道重新获取安装包。";
        }
    }

    private void ResetRevealClip()
    {
        if (_revealClip is null || _panSurface is null)
            return;

        _revealClip.Center = new Point(TargetWidth / 2d, TargetHeight / 2d);
        _revealClip.RadiusX = SplashSize / 2d;
        _revealClip.RadiusY = SplashSize / 2d;
        _panSurface.Clip = _revealClip;
        _panSurface.CornerRadius = new CornerRadius(SurfaceCorner);
    }

    private void PlaceWindowAtCenter(PixelPoint centerScreen)
    {
        // Single placement — never touch Position during animation frames.
        double scale = RenderScaling > 0 ? RenderScaling : 1d;
        int pw = Math.Max(1, (int)Math.Round(TargetWidth * scale));
        int ph = Math.Max(1, (int)Math.Round(TargetHeight * scale));
        Position = new PixelPoint(centerScreen.X - pw / 2, centerScreen.Y - ph / 2);
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
        // Ensure we stay at full size; only clip grows.
        Width = TargetWidth;
        Height = TargetHeight;
        ResetRevealClip();
        BeginPhase(Phase.ExpandClip, durationMs: 720);
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
            case Phase.ExpandClip:
                TickExpandClip(eased);
                if (t >= 1d)
                {
                    if (_panSurface is not null)
                        _panSurface.Clip = null;
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
                    FinishFadeToLegal();
                }
                break;
        }
    }

    private void TickExpandClip(double eased)
    {
        if (_revealClip is null)
            return;

        // Cover the full surface (corner-to-corner) so clip can be removed cleanly.
        double startR = SplashSize / 2d;
        double endR = Math.Sqrt((TargetWidth * 0.5d) * (TargetWidth * 0.5d) +
                                (TargetHeight * 0.5d) * (TargetHeight * 0.5d)) * 1.05d;
        double radius = Lerp(startR, endR, eased);
        _revealClip.Center = new Point(TargetWidth / 2d, TargetHeight / 2d);
        _revealClip.RadiusX = radius;
        _revealClip.RadiusY = radius;
    }

    private void TickRevealWelcome(double eased)
    {
        if (_iconTranslate is null || _welcomePanel is null)
            return;

        double targetX = -Math.Min(TargetWidth * 0.22, 170);
        _iconTranslate.X = Lerp(0, targetX, eased);
        _welcomePanel.Opacity = eased;

        double panelLeft = TargetWidth * 0.5 + targetX + IconSize * 0.45;
        _welcomePanel.Margin = new Thickness(Math.Max(panelLeft, TargetWidth * 0.42), 0, 48, 0);
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

    private void FinishFadeToLegal()
    {
        if (_pageWelcome is not null)
        {
            _pageWelcome.IsVisible = false;
            _pageWelcome.IsHitTestVisible = false;
            _pageWelcome.Opacity = 0;
        }

        if (_pageLegal is not null)
        {
            _pageLegal.IsVisible = true;
            _pageLegal.Opacity = 1;
            _pageLegal.IsHitTestVisible = true;
        }

        _legalPageIndex = 0;
        ShowLegalPage(0, animateContent: false);
    }

    private void BtnStartSetup_Click(object? sender, EventArgs e)
    {
        if (_phase is Phase.ExpandClip or Phase.SettleIcon or Phase.RevealWelcome or Phase.FadeWelcomeOut)
            return;

        if (_welcomePanel is not null)
            _welcomePanel.IsHitTestVisible = false;
        if (_btnStart is not null)
            _btnStart.IsEnabled = false;

        BeginPhase(Phase.FadeWelcomeOut, durationMs: 320);
    }

    private void ShowLegalPage(int index, bool animateContent)
    {
        _legalPageIndex = index is 0 or 1 ? index : 0;
        string markdown = _legalPageIndex == 0 ? _termsMarkdown : _privacyMarkdown;

        if (_labLegalMarkdown is not null)
            _labLegalMarkdown.Markdown = markdown;

        _panLegalScroll?.ScrollToHome();

        // Page 2 (terms): 不同意 / 上一页(disabled) / 下一页
        // Page 3 (privacy): 不同意并退出 / 上一页 / 同意条款
        if (_legalPageIndex == 0)
        {
            if (_btnDisagree is not null)
            {
                _btnDisagree.Text = AvaloniaLocalizationManager.GetText("FirstRun.Legal.Disagree", "不同意");
                _btnDisagree.ColorType = MyButton.ColorState.Red;
            }

            if (_btnPrev is not null)
            {
                _btnPrev.Text = AvaloniaLocalizationManager.GetText("FirstRun.Legal.Previous", "上一页");
                _btnPrev.IsEnabled = false;
            }

            if (_btnNext is not null)
            {
                _btnNext.Text = AvaloniaLocalizationManager.GetText("FirstRun.Legal.Next", "下一页");
                _btnNext.ColorType = MyButton.ColorState.Highlight;
            }
        }
        else
        {
            if (_btnDisagree is not null)
            {
                _btnDisagree.Text = AvaloniaLocalizationManager.GetText(
                    "FirstRun.Legal.DisagreeExit",
                    "不同意并退出");
                _btnDisagree.ColorType = MyButton.ColorState.Red;
            }

            if (_btnPrev is not null)
            {
                _btnPrev.Text = AvaloniaLocalizationManager.GetText("FirstRun.Legal.Previous", "上一页");
                _btnPrev.IsEnabled = true;
            }

            if (_btnNext is not null)
            {
                _btnNext.Text = AvaloniaLocalizationManager.GetText("FirstRun.Legal.Agree", "同意条款");
                _btnNext.ColorType = MyButton.ColorState.Highlight;
            }
        }

        if (animateContent && _pageLegal is not null)
        {
            // Soft content swap: only middle opacity, chrome stays put.
            _pageLegal.Opacity = 0.55;
            Dispatcher.UIThread.Post(() =>
            {
                if (_pageLegal is not null)
                    _pageLegal.Opacity = 1;
            }, DispatcherPriority.Render);
        }
    }

    private void BtnLegalDisagree_Click(object? sender, EventArgs e)
    {
        // Decline either agreement → exit without completing wizard / legal accept.
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

    private void BtnLegalPrev_Click(object? sender, EventArgs e)
    {
        if (_legalPageIndex <= 0)
            return;
        ShowLegalPage(0, animateContent: true);
    }

    private void BtnLegalNext_Click(object? sender, EventArgs e)
    {
        if (_legalPageIndex == 0)
        {
            ShowLegalPage(1, animateContent: true);
            return;
        }

        AcceptLegalAndComplete();
    }

    private void AcceptLegalAndComplete()
    {
        LauncherSettingsPageBinder.UpdateSettings(current =>
        {
            current.SetTextOption(
                EmbeddedLegalDocuments.SettingsKeyAcceptedVersion,
                EmbeddedLegalDocuments.DocumentVersion);
            current.SetTextOption(SettingsKeyCompletedVersion, WizardVersion);
            return current;
        });

        Completed?.Invoke(this, EventArgs.Empty);
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

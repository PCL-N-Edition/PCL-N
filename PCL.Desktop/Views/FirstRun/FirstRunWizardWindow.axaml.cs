// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using PCL.Application.Settings;
using PCL.Desktop.Features.Settings.Views;
using PCL.Desktop.Localization;

namespace PCL.Desktop.Views.FirstRun;

/// <summary>
/// Multi-step first-run wizard. Page 1: splash handoff → circular expand → icon slide + welcome.
/// </summary>
public sealed partial class FirstRunWizardWindow : Window
{
    public const string SettingsKeyCompletedVersion = "UiFirstRunWizardVersion";
    public const string WizardVersion = "1";

    private const double SplashSize = 136d;
    private const double IconSize = 112d;
    private const double TargetWidth = 860d;
    private const double TargetHeight = 520d;
    private const double TargetCorner = 12d;

    private readonly Stopwatch _clock = new();
    private DispatcherTimer? _timer;
    private Phase _phase = Phase.Idle;
    private double _phaseDurationMs;
    private PixelPoint _centerScreen;
    private double _startWidth;
    private double _startHeight;
    private double _endWidth;
    private double _endHeight;

    private EllipseGeometry? _revealClip;
    private Image? _heroIcon;
    private StackPanel? _welcomePanel;
    private Border? _panShadow;
    private Border? _panSurface;
    private TranslateTransform? _iconTranslate;
    private Button? _btnStart;

    public event EventHandler? Completed;

    private enum Phase
    {
        Idle,
        Expand,
        SettleIcon,
        RevealWelcome,
        Done
    }

    public FirstRunWizardWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _panShadow = this.FindControl<Border>("PanShadow");
        _panSurface = this.FindControl<Border>("PanSurface");
        _heroIcon = this.FindControl<Image>("HeroIcon");
        _welcomePanel = this.FindControl<StackPanel>("WelcomePanel");
        _btnStart = this.FindControl<Button>("BtnStartSetup");

        // EllipseGeometry is not a Control — create/bind clip in code for animation.
        _revealClip = new EllipseGeometry(new Rect(0, 0, SplashSize, SplashSize));
        if (_panSurface is not null)
            _panSurface.Clip = _revealClip;

        if (_heroIcon?.RenderTransform is TranslateTransform tt)
            _iconTranslate = tt;
        else if (_heroIcon is not null)
        {
            _iconTranslate = new TranslateTransform();
            _heroIcon.RenderTransform = _iconTranslate;
        }

        if (_btnStart is not null)
            _btnStart.Content = AvaloniaLocalizationManager.GetText("FirstRun.Page1.StartSetup", "开始配置");
        if (this.FindControl<TextBlock>("LabWelcome") is { } lab)
            lab.Text = AvaloniaLocalizationManager.GetText(
                "FirstRun.Page1.WelcomeTitle",
                "欢迎使用 PCL N Edition");

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

    /// <summary>Place the window at the splash icon rect (screen pixels) before show.</summary>
    public void PrepareFromSplash(PixelRect splashBounds)
    {
        _centerScreen = new PixelPoint(
            splashBounds.X + splashBounds.Width / 2,
            splashBounds.Y + splashBounds.Height / 2);
        Width = SplashSize;
        Height = SplashSize;
        Position = new PixelPoint(
            _centerScreen.X - (int)(SplashSize / 2),
            _centerScreen.Y - (int)(SplashSize / 2));
    }

    public void PrepareCentered()
    {
        Width = SplashSize;
        Height = SplashSize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        // Resolve center after first layout if not prepared from splash.
        if (_centerScreen.X == 0 && _centerScreen.Y == 0)
        {
            _centerScreen = new PixelPoint(
                Position.X + (int)(Math.Max(Bounds.Width, SplashSize) * RenderScaling / 2),
                Position.Y + (int)(Math.Max(Bounds.Height, SplashSize) * RenderScaling / 2));
        }

        ApplyFrame(SplashSize, SplashSize, cornerRadius: SplashSize / 2, clipRadius: SplashSize / 2);
        BeginPhase(Phase.Expand, durationMs: 720);
    }

    private void BeginPhase(Phase phase, double durationMs)
    {
        _phase = phase;
        _phaseDurationMs = Math.Max(1d, durationMs);
        _clock.Restart();

        switch (phase)
        {
            case Phase.Expand:
                _startWidth = Width;
                _startHeight = Height;
                _endWidth = TargetWidth;
                _endHeight = TargetHeight;
                break;
            case Phase.SettleIcon:
                // Brief hold so icon reads as “in-window” before sliding.
                break;
            case Phase.RevealWelcome:
                LayoutWelcomeTargets();
                if (_welcomePanel is not null)
                {
                    _welcomePanel.IsHitTestVisible = false;
                    _welcomePanel.Opacity = 0;
                }
                break;
        }

        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        double t = Math.Clamp(_clock.Elapsed.TotalMilliseconds / _phaseDurationMs, 0d, 1d);
        double eased = EaseOutCubic(t);

        switch (_phase)
        {
            case Phase.Expand:
                TickExpand(eased);
                if (t >= 1d)
                    BeginPhase(Phase.SettleIcon, durationMs: 180);
                break;
            case Phase.SettleIcon:
                // Icon already fills the former splash role; no transform yet.
                if (t >= 1d)
                    BeginPhase(Phase.RevealWelcome, durationMs: 520);
                break;
            case Phase.RevealWelcome:
                TickRevealWelcome(eased);
                if (t >= 1d)
                {
                    _timer?.Stop();
                    _phase = Phase.Done;
                    if (_welcomePanel is not null)
                        _welcomePanel.IsHitTestVisible = true;
                }
                break;
        }
    }

    private void TickExpand(double eased)
    {
        double w = Lerp(_startWidth, _endWidth, eased);
        double h = Lerp(_startHeight, _endHeight, eased);
        double corner = Lerp(SplashSize / 2, TargetCorner, eased);
        // Circular reveal: radius grows from icon circle to cover the whole window.
        double startR = SplashSize / 2;
        double endR = Math.Sqrt((w * 0.5) * (w * 0.5) + (h * 0.5) * (h * 0.5)) * 1.02;
        double radius = Lerp(startR, endR, eased);
        ApplyFrame(w, h, corner, radius);

        // Keep window center fixed on the original icon center.
        int pw = (int)Math.Round(w * RenderScaling);
        int ph = (int)Math.Round(h * RenderScaling);
        Position = new PixelPoint(_centerScreen.X - pw / 2, _centerScreen.Y - ph / 2);

        // Soft shadow appears as we leave pure icon mode.
        if (_panShadow is not null)
        {
            double shadow = Lerp(0, 18, eased);
            double alpha = Lerp(0, 0.28, eased);
            _panShadow.BoxShadow = new BoxShadows(new BoxShadow
            {
                Blur = shadow,
                OffsetY = shadow * 0.35,
                Color = Color.FromArgb((byte)(alpha * 255), 0, 0, 0)
            });
            _panShadow.Margin = new Thickness(Lerp(0, 10, eased));
        }
    }

    private void TickRevealWelcome(double eased)
    {
        if (_iconTranslate is null || _heroIcon is null || _welcomePanel is null)
            return;

        // Slide icon from center toward left third.
        double targetX = -Math.Min(Bounds.Width * 0.22, 180);
        _iconTranslate.X = Lerp(0, targetX, eased);
        _welcomePanel.Opacity = eased;

        // Welcome panel sits to the right of the icon.
        double panelLeft = Bounds.Width * 0.5 + targetX * eased + IconSize * 0.55;
        _welcomePanel.Margin = new Thickness(Math.Max(panelLeft, Bounds.Width * 0.42), 0, 40, 0);
    }

    private void LayoutWelcomeTargets()
    {
        // Initial margin before slide; TickRevealWelcome will animate.
        if (_welcomePanel is not null)
            _welcomePanel.Margin = new Thickness(Bounds.Width * 0.55, 0, 40, 0);
    }

    private void ApplyFrame(double width, double height, double cornerRadius, double clipRadius)
    {
        Width = width;
        Height = height;

        if (_panSurface is not null)
            _panSurface.CornerRadius = new CornerRadius(cornerRadius);
        if (_panShadow is not null)
            _panShadow.CornerRadius = new CornerRadius(cornerRadius);

        if (_revealClip is not null)
        {
            double cx = width / 2;
            double cy = height / 2;
            _revealClip.Center = new Point(cx, cy);
            _revealClip.RadiusX = clipRadius;
            _revealClip.RadiusY = clipRadius;
        }
    }

    private void BtnStartSetup_Click(object? sender, RoutedEventArgs e)
    {
        // Page 1 complete — later pages will chain from here.
        // For now finish the wizard shell so MainWindow can continue (legal / normal UI).
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

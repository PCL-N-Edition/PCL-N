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
    private double _startWidth = SplashSize;
    private double _startHeight = SplashSize;
    private double _endWidth = TargetWidth;
    private double _endHeight = TargetHeight;
    private bool _introStarted;
    private bool _splashPrepared;

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

        // Prefer Center/Radius properties — Rect-based geometry does not animate reliably.
        _revealClip = new EllipseGeometry
        {
            Center = new Point(SplashSize / 2, SplashSize / 2),
            RadiusX = SplashSize / 2,
            RadiusY = SplashSize / 2
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

        if (_btnStart is not null)
            _btnStart.Content = AvaloniaLocalizationManager.GetText("FirstRun.Page1.StartSetup", "开始配置");
        if (this.FindControl<TextBlock>("LabWelcome") is { } lab)
            lab.Text = AvaloniaLocalizationManager.GetText(
                "FirstRun.Page1.WelcomeTitle",
                "欢迎使用 PCL N Edition");

        // Do NOT start expand in Opened alone — App may call PrepareFromSplash afterward and
        // would reset size mid-animation (user would only see a stuck circle).
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

    /// <summary>Align to splash icon rect (screen pixels), then start intro animation.</summary>
    public void PrepareFromSplash(PixelRect splashBounds)
    {
        _splashPrepared = true;
        _centerScreen = new PixelPoint(
            splashBounds.X + splashBounds.Width / 2,
            splashBounds.Y + splashBounds.Height / 2);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = SplashSize;
        Height = SplashSize;
        // splashBounds is already in pixels.
        Position = new PixelPoint(
            _centerScreen.X - (int)(SplashSize * RenderScaling / 2d),
            _centerScreen.Y - (int)(SplashSize * RenderScaling / 2d));
        ApplyFrame(SplashSize, SplashSize, SplashSize / 2, SplashSize / 2);
        TryStartIntro();
    }

    public void PrepareCentered()
    {
        Width = SplashSize;
        Height = SplashSize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ApplyFrame(SplashSize, SplashSize, SplashSize / 2, SplashSize / 2);
    }

    /// <summary>Call after splash handoff (or when no splash) to begin expand animation.</summary>
    public void StartIntroAnimation()
    {
        if (!_splashPrepared)
        {
            // Capture center from current window placement (CenterScreen).
            _centerScreen = new PixelPoint(
                Position.X + (int)(SplashSize * RenderScaling / 2d),
                Position.Y + (int)(SplashSize * RenderScaling / 2d));
        }

        TryStartIntro();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        // If splash path will call PrepareFromSplash soon, wait for StartIntroAnimation().
        // Without splash, start after first layout tick so Position is valid.
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
                Position.X + (int)(Math.Max(Bounds.Width, SplashSize) * RenderScaling / 2d),
                Position.Y + (int)(Math.Max(Bounds.Height, SplashSize) * RenderScaling / 2d));
            TryStartIntro();
        }, DispatcherPriority.Loaded);
    }

    private void TryStartIntro()
    {
        if (_introStarted)
            return;
        if (!IsVisible && !IsLoaded)
            return;

        _introStarted = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ApplyFrame(SplashSize, SplashSize, SplashSize / 2, SplashSize / 2);
        BeginPhase(Phase.Expand, durationMs: 780);
    }

    private void BeginPhase(Phase phase, double durationMs)
    {
        _phase = phase;
        _phaseDurationMs = Math.Max(1d, durationMs);
        _clock.Restart();

        switch (phase)
        {
            case Phase.Expand:
                _startWidth = SplashSize;
                _startHeight = SplashSize;
                _endWidth = TargetWidth;
                _endHeight = TargetHeight;
                break;
            case Phase.RevealWelcome:
                if (_welcomePanel is not null)
                {
                    _welcomePanel.IsHitTestVisible = false;
                    _welcomePanel.Opacity = 0;
                    _welcomePanel.Margin = new Thickness(TargetWidth * 0.52, 0, 48, 0);
                }
                break;
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
            case Phase.Expand:
                TickExpand(eased);
                if (t >= 1d)
                {
                    // Drop elliptical clip so content is a normal rounded rectangle.
                    if (_panSurface is not null)
                        _panSurface.Clip = null;
                    ApplyFrame(TargetWidth, TargetHeight, TargetCorner, clipRadius: 0);
                    BeginPhase(Phase.SettleIcon, durationMs: 160);
                }
                break;
            case Phase.SettleIcon:
                if (t >= 1d)
                    BeginPhase(Phase.RevealWelcome, durationMs: 480);
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
        }
    }

    private void TickExpand(double eased)
    {
        double w = Lerp(_startWidth, _endWidth, eased);
        double h = Lerp(_startHeight, _endHeight, eased);
        double corner = Lerp(SplashSize / 2, TargetCorner, eased);
        double startR = SplashSize / 2;
        double endR = Math.Sqrt((TargetWidth * 0.5) * (TargetWidth * 0.5) + (TargetHeight * 0.5) * (TargetHeight * 0.5)) * 1.05;
        double radius = Lerp(startR, endR, eased);
        ApplyFrame(w, h, corner, radius);

        int pw = Math.Max(1, (int)Math.Round(w * RenderScaling));
        int ph = Math.Max(1, (int)Math.Round(h * RenderScaling));
        Position = new PixelPoint(_centerScreen.X - pw / 2, _centerScreen.Y - ph / 2);

        if (_panShadow is not null)
        {
            double shadow = Lerp(0, 16, eased);
            byte a = (byte)Math.Clamp((int)(Lerp(0, 0.32, eased) * 255), 0, 255);
            _panShadow.BoxShadow = new BoxShadows(new BoxShadow
            {
                Blur = shadow,
                OffsetY = shadow * 0.4,
                Color = Color.FromArgb(a, 0, 0, 0)
            });
            _panShadow.Margin = new Thickness(Lerp(0, 12, eased));
            // Outer host should not keep circular clip once expanding.
            _panShadow.ClipToBounds = false;
        }

        if (_panSurface is not null)
            _panSurface.ClipToBounds = false;
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

    private void ApplyFrame(double width, double height, double cornerRadius, double clipRadius)
    {
        // Force layout size (MinWidth/MinHeight match XAML floor).
        Width = width;
        Height = height;
        MinWidth = Math.Min(SplashSize, width);
        MinHeight = Math.Min(SplashSize, height);

        if (_panSurface is not null)
            _panSurface.CornerRadius = new CornerRadius(cornerRadius);
        if (_panShadow is not null)
            _panShadow.CornerRadius = new CornerRadius(cornerRadius);

        if (_revealClip is not null && clipRadius > 0 && _panSurface?.Clip is not null)
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

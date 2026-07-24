// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using System.Runtime.CompilerServices;
using PCL.Application.Settings;
using PCL.Desktop.Composition;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Features.Settings.Views;

namespace PCL.Desktop.Shell;

/// <summary>
/// Apple-inspired experimental chrome for in-window message dialogs (glass card, soft scrim,
/// scale+fade motion). Active only when <see cref="ChromeStyle.Glass"/> is enabled.
/// </summary>
internal static class ExperimentalMsgChrome
{
    private const double OpenScaleFrom = 0.94d;
    private const double OpenTranslateY = 14d;
    private const double CloseScaleTo = 0.97d;
    private const double CloseTranslateY = 10d;
    private static readonly ConditionalWeakTable<Control, ShellSnapshot> ClassicShells = new();

    public static bool IsEnabled
    {
        get
        {
            try
            {
                if (DesktopCompositionRoot.IsInitialized)
                {
                    ExperimentalUiProfile profile = DesktopCompositionRoot
                        .GetRequiredService<ExperimentalUiProfileSource>()
                        .Current;
                    return profile.Chrome == ChromeStyle.Glass;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
            }

            try
            {
                LauncherSettings settings = LauncherSettingsPageBinder.LoadSettings();
                return settings.GetBooleanOption(
                    LauncherSettingKeys.ExperimentalHomepageUi,
                    LauncherSettingDefaults.GetBoolean(LauncherSettingKeys.ExperimentalHomepageUi.Value));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                           NotSupportedException)
            {
                return false;
            }
        }
    }

    /// <summary>Scrim peak alpha for the modal backdrop (classic uses ~90).</summary>
    public static byte ScrimAlpha => 108;

    public static void ApplyShell(
        Control root,
        Border? border,
        TextBlock? title,
        Rectangle? line,
        Panel? buttons)
    {
        if (!IsEnabled)
            return;

        _ = ClassicShells.GetValue(
            root,
            _ => ShellSnapshot.Capture(root, border, title, line, buttons));
        root.RenderTransformOrigin = new RelativePoint(0.5d, 0.5d, RelativeUnit.Relative);
        root.Margin = new Thickness(28d);

        if (border is not null)
        {
            border.CornerRadius = new CornerRadius(22d);
            border.BoxShadow = BoxShadows.Parse("0 18 48 0 #2E000000, 0 2 10 0 #14000000");
            border.BorderThickness = new Thickness(1d);
            border.BorderBrush = ResolveHairlineBrush(root);
            border.Background = ResolveGlassFill(root);
            border.ClipToBounds = false;
        }

        if (title is not null)
        {
            title.FontSize = 17d;
            title.FontWeight = FontWeight.SemiBold;
            title.LetterSpacing = -0.24d;
            title.LineHeight = 22d;
            title.HorizontalAlignment = HorizontalAlignment.Center;
            title.TextAlignment = TextAlignment.Center;
            title.Margin = new Thickness(20d, 6d, 20d, 10d);
        }

        if (line is not null)
        {
            // Apple alerts avoid a heavy accent bar; keep a hairline for structure.
            line.Height = 1d;
            line.Opacity = 0.14d;
            line.Fill = ResolveHairlineBrush(root);
            line.Margin = new Thickness(16d, 0d, 16d, 0d);
        }

        if (buttons is not null)
        {
            buttons.HorizontalAlignment = HorizontalAlignment.Stretch;
            buttons.Margin = new Thickness(16d, 6d, 16d, 4d);
            foreach (MyButton button in EnumerateButtons(buttons))
            {
                button.MinHeight = 36d;
                button.MinWidth = 92d;
                button.Margin = button.Name == "BtnLeft"
                    ? new Thickness(0d)
                    : new Thickness(8d, 0d, 0d, 0d);
                button.Padding = new Thickness(14d, 0d, 14d, 0d);
                button.UseExperimentalStyle = true;
            }
        }
    }

    /// <summary>Restores every property changed by <see cref="ApplyShell"/>.</summary>
    public static void RestoreShell(
        Control root,
        Border? border,
        TextBlock? title,
        Rectangle? line,
        Panel? buttons)
    {
        if (!ClassicShells.TryGetValue(root, out ShellSnapshot? snapshot))
            return;

        snapshot.Restore(root, border, title, line, buttons);
    }

    public static (ScaleTransform Scale, TranslateTransform Translate) PrepareOpenTransforms(Control root)
    {
        ScaleTransform scale = new(OpenScaleFrom, OpenScaleFrom);
        TranslateTransform translate = new(0d, OpenTranslateY);
        root.RenderTransformOrigin = new RelativePoint(0.5d, 0.5d, RelativeUnit.Relative);
        root.RenderTransform = new TransformGroup
        {
            Children = { scale, translate }
        };
        return (scale, translate);
    }

    public static void RunShowAnimation(
        Control root,
        ScaleTransform scale,
        TranslateTransform translate,
        int uuid,
        Action onCompleted)
    {
        root.Opacity = 0d;
        // Critically damped, response ~0.35–0.4s — no bounce (Apple default for menus/alerts).
        ModAnimation.AniEase ease = new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Middle);
        ModAnimation.AniStart(
            new List<ModAnimation.AniData>
            {
                ModAnimation.AaOpacity(root, 1d, 240, 0, ease),
                ModAnimation.AaDouble(delta => scale.ScaleX += delta, 1d - OpenScaleFrom, 380, 0, ease),
                ModAnimation.AaDouble(delta => scale.ScaleY += delta, 1d - OpenScaleFrom, 380, 0, ease),
                ModAnimation.AaDouble(delta => translate.Y += delta, -OpenTranslateY, 380, 0, ease),
                ModAnimation.AaCode(onCompleted, after: true)
            },
            $"MyMsgBox Experimental {uuid}");
    }

    public static void RunCloseAnimation(
        Control root,
        ScaleTransform scale,
        TranslateTransform translate,
        int uuid,
        Action onCompleted)
    {
        double scaleDeltaX = CloseScaleTo - scale.ScaleX;
        double scaleDeltaY = CloseScaleTo - scale.ScaleY;
        double translateDelta = CloseTranslateY - translate.Y;
        ModAnimation.AniEase ease = new ModAnimation.AniEaseInFluent(ModAnimation.AniEasePower.Weak);
        ModAnimation.AniStart(
            new List<ModAnimation.AniData>
            {
                ModAnimation.AaOpacity(root, -root.Opacity, 180, 0, ease),
                ModAnimation.AaDouble(delta => scale.ScaleX += delta, scaleDeltaX, 220, 0, ease),
                ModAnimation.AaDouble(delta => scale.ScaleY += delta, scaleDeltaY, 220, 0, ease),
                ModAnimation.AaDouble(delta => translate.Y += delta, translateDelta, 220, 0, ease),
                ModAnimation.AaCode(onCompleted, after: true)
            },
            $"MyMsgBox Experimental {uuid}");
    }

    public static bool TryGetScaleTranslate(Control root, out ScaleTransform? scale, out TranslateTransform? translate)
    {
        scale = null;
        translate = null;
        if (root.RenderTransform is ScaleTransform onlyScale)
        {
            scale = onlyScale;
            return true;
        }

        if (root.RenderTransform is not TransformGroup group)
            return false;

        foreach (ITransform child in group.Children)
        {
            scale ??= child as ScaleTransform;
            translate ??= child as TranslateTransform;
        }

        return scale is not null || translate is not null;
    }

    private static SolidColorBrush ResolveGlassFill(Control root)
    {
        bool dark = IsDarkTheme(root);
        // Materialize glass: high-opacity frost so text stays legible without real backdrop-filter.
        return new SolidColorBrush(dark
            ? Color.FromArgb(236, 36, 36, 38)
            : Color.FromArgb(242, 255, 255, 255));
    }

    private static SolidColorBrush ResolveHairlineBrush(Control root)
    {
        bool dark = IsDarkTheme(root);
        return new SolidColorBrush(dark
            ? Color.FromArgb(48, 255, 255, 255)
            : Color.FromArgb(40, 0, 0, 0));
    }

    private static bool IsDarkTheme(Control root) =>
        root.ActualThemeVariant == ThemeVariant.Dark ||
        global::Avalonia.Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    private static IEnumerable<MyButton> EnumerateButtons(Control root)
    {
        if (root is MyButton button)
            yield return button;

        if (root is Panel panel)
        {
            foreach (Control child in panel.Children)
            {
                foreach (MyButton descendant in EnumerateButtons(child))
                    yield return descendant;
            }
        }
        else if (root is ContentControl { Content: Control content })
        {
            foreach (MyButton descendant in EnumerateButtons(content))
                yield return descendant;
        }
    }

    private sealed class ShellSnapshot
    {
        private readonly RelativePoint _renderTransformOrigin;
        private readonly ITransform? _renderTransform;
        private readonly Thickness _rootMargin;
        private readonly BorderSnapshot? _border;
        private readonly TitleSnapshot? _title;
        private readonly LineSnapshot? _line;
        private readonly PanelSnapshot? _panel;
        private readonly Dictionary<MyButton, ButtonSnapshot> _buttons;

        private ShellSnapshot(
            Control root,
            Border? border,
            TextBlock? title,
            Rectangle? line,
            Panel? panel)
        {
            _renderTransformOrigin = root.RenderTransformOrigin;
            _renderTransform = root.RenderTransform;
            _rootMargin = root.Margin;
            _border = border is null ? null : new BorderSnapshot(border);
            _title = title is null ? null : new TitleSnapshot(title);
            _line = line is null ? null : new LineSnapshot(line);
            _panel = panel is null ? null : new PanelSnapshot(panel);
            _buttons = panel is null
                ? []
                : EnumerateButtons(panel).ToDictionary(static button => button, static button => new ButtonSnapshot(button));
        }

        public static ShellSnapshot Capture(
            Control root,
            Border? border,
            TextBlock? title,
            Rectangle? line,
            Panel? panel) =>
            new(root, border, title, line, panel);

        public void Restore(
            Control root,
            Border? border,
            TextBlock? title,
            Rectangle? line,
            Panel? panel)
        {
            root.RenderTransformOrigin = _renderTransformOrigin;
            root.RenderTransform = _renderTransform;
            root.Margin = _rootMargin;
            if (border is not null)
                _border?.Restore(border);
            if (title is not null)
                _title?.Restore(title);
            if (line is not null)
                _line?.Restore(line);
            if (panel is not null)
                _panel?.Restore(panel);
            foreach ((MyButton button, ButtonSnapshot snapshot) in _buttons)
                snapshot.Restore(button);
        }
    }

    private sealed record BorderSnapshot(
        CornerRadius CornerRadius,
        BoxShadows BoxShadow,
        Thickness BorderThickness,
        IBrush? BorderBrush,
        IBrush? Background,
        bool ClipToBounds)
    {
        public BorderSnapshot(Border border)
            : this(
                border.CornerRadius,
                border.BoxShadow,
                border.BorderThickness,
                border.BorderBrush,
                border.Background,
                border.ClipToBounds)
        {
        }

        public void Restore(Border border)
        {
            border.CornerRadius = CornerRadius;
            border.BoxShadow = BoxShadow;
            border.BorderThickness = BorderThickness;
            border.BorderBrush = BorderBrush;
            border.Background = Background;
            border.ClipToBounds = ClipToBounds;
        }
    }

    private sealed record TitleSnapshot(
        double FontSize,
        FontWeight FontWeight,
        double LetterSpacing,
        double LineHeight,
        HorizontalAlignment HorizontalAlignment,
        TextAlignment TextAlignment,
        Thickness Margin)
    {
        public TitleSnapshot(TextBlock title)
            : this(
                title.FontSize,
                title.FontWeight,
                title.LetterSpacing,
                title.LineHeight,
                title.HorizontalAlignment,
                title.TextAlignment,
                title.Margin)
        {
        }

        public void Restore(TextBlock title)
        {
            title.FontSize = FontSize;
            title.FontWeight = FontWeight;
            title.LetterSpacing = LetterSpacing;
            title.LineHeight = LineHeight;
            title.HorizontalAlignment = HorizontalAlignment;
            title.TextAlignment = TextAlignment;
            title.Margin = Margin;
        }
    }

    private sealed record LineSnapshot(
        double Height,
        double Opacity,
        IBrush? Fill,
        Thickness Margin)
    {
        public LineSnapshot(Rectangle line)
            : this(line.Height, line.Opacity, line.Fill, line.Margin)
        {
        }

        public void Restore(Rectangle line)
        {
            line.Height = Height;
            line.Opacity = Opacity;
            line.Fill = Fill;
            line.Margin = Margin;
        }
    }

    private sealed record PanelSnapshot(HorizontalAlignment HorizontalAlignment, Thickness Margin)
    {
        public PanelSnapshot(Panel panel)
            : this(panel.HorizontalAlignment, panel.Margin)
        {
        }

        public void Restore(Panel panel)
        {
            panel.HorizontalAlignment = HorizontalAlignment;
            panel.Margin = Margin;
        }
    }

    private sealed record ButtonSnapshot(
        double MinHeight,
        double MinWidth,
        Thickness Margin,
        Thickness Padding,
        bool UseExperimentalStyle)
    {
        public ButtonSnapshot(MyButton button)
            : this(
                button.MinHeight,
                button.MinWidth,
                button.Margin,
                button.Padding,
                button.UseExperimentalStyle)
        {
        }

        public void Restore(MyButton button)
        {
            button.MinHeight = MinHeight;
            button.MinWidth = MinWidth;
            button.Margin = Margin;
            button.Padding = Padding;
            button.UseExperimentalStyle = UseExperimentalStyle;
        }
    }
}

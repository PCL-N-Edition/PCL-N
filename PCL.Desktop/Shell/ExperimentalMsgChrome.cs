// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
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
            buttons.Margin = new Thickness(16d, 4d, 16d, 4d);
            foreach (Control child in buttons.Children)
            {
                if (child is not MyButton button)
                    continue;
                button.MinHeight = 36d;
                button.MinWidth = 92d;
                button.Margin = new Thickness(8d, 0d, 0d, 0d);
                button.Padding = new Thickness(14d, 0d, 14d, 0d);
            }
        }
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
}

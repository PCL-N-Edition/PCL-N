// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Localization;

namespace PCL.Desktop.Features.Launching.Views;

public sealed record SkinAppearanceCard(
    string Title,
    string Subtitle,
    string SkinAddress,
    string? CapeAddress,
    bool IsSlim,
    bool CanApply = true);

public sealed record SkinAppearancePageModel(
    LoginProfileInfo Profile,
    SkinAppearanceCard Current,
    IReadOnlyList<SkinAppearanceCard> Skins,
    IReadOnlyList<SkinAppearanceCard> Capes);

public partial class PageSkinAppearanceRight : MyPageRight
{
    private sealed record AppearanceCardVisual(
        Border Container,
        Grid Layout,
        MinecraftPlayerPreview Preview,
        StackPanel Details,
        TextBlock Title,
        TextBlock Subtitle,
        MyButton? ApplyButton,
        bool CanApply);

    private SkinAppearancePageModel? _model;
    private readonly List<AppearanceCardVisual> _skinCards = [];
    private readonly List<AppearanceCardVisual> _capeCards = [];

    public PageSkinAppearanceRight()
    {
        AvaloniaXamlLoader.Load(this);
        WireTrackViewport(this.FindControl<ScrollViewer>("PanSkinScroll"), isCapeTrack: false);
        WireTrackViewport(this.FindControl<ScrollViewer>("PanCapeScroll"), isCapeTrack: true);
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        ApplyResponsiveLayout();
    }

    public event EventHandler? LocalSkinRequested;

    public event EventHandler? SkinLibraryRequested;

    public event EventHandler<SkinAppearanceCard>? SkinSelected;

    public event EventHandler<SkinAppearanceCard>? CapeSelected;

    public void SetModel(SkinAppearancePageModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;
        if (this.FindControl<MinecraftPlayerPreview>("CurrentPlayer") is { } current)
        {
            current.SkinAddress = model.Current.SkinAddress;
            current.CapeAddress = model.Current.CapeAddress ?? string.Empty;
            current.IsSlim = model.Current.IsSlim;
        }

        if (this.FindControl<TextBlock>("LabProfileName") is { } name)
            name.Text = model.Profile.Username;
        if (this.FindControl<TextBlock>("LabProfileType") is { } type)
            type.Text = model.Profile.DisplayInfo;

        PopulateTrack(
            "PanSkins",
            "PanSkinsEmpty",
            "LabSkinCount",
            model.Skins,
            selected => SkinSelected?.Invoke(this, selected),
            isCapeTrack: false);
        PopulateTrack(
            "PanCapes",
            "PanCapesEmpty",
            "LabCapeCount",
            model.Capes,
            selected => CapeSelected?.Invoke(this, selected),
            isCapeTrack: true);
    }

    private void PopulateTrack(
        string panelName,
        string emptyName,
        string countName,
        IReadOnlyList<SkinAppearanceCard> cards,
        Action<SkinAppearanceCard> select,
        bool isCapeTrack)
    {
        if (this.FindControl<StackPanel>(panelName) is not { } panel)
            return;

        panel.Children.Clear();
        List<AppearanceCardVisual> visuals = isCapeTrack ? _capeCards : _skinCards;
        visuals.Clear();
        foreach (SkinAppearanceCard card in cards)
        {
            AppearanceCardVisual visual = CreateAppearanceCard(card, select, isCapeTrack);
            visuals.Add(visual);
            panel.Children.Add(visual.Container);
        }

        if (this.FindControl<Control>(emptyName) is { } empty)
            empty.IsVisible = cards.Count == 0;
        if (this.FindControl<TextBlock>(countName) is { } count)
        {
            count.Text = FormatResource(
                "Appearance.ItemCount",
                "{0} 项",
                cards.Count);
        }

        ApplyTrackMetrics(isCapeTrack);
    }

    private static AppearanceCardVisual CreateAppearanceCard(
        SkinAppearanceCard card,
        Action<SkinAppearanceCard> select,
        bool isCapeTrack)
    {
        MinecraftPlayerPreview preview = new()
        {
            Width = 76,
            Height = 152,
            SkinAddress = card.SkinAddress,
            CapeAddress = card.CapeAddress ?? string.Empty,
            IsSlim = card.IsSlim,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        TextBlock title = new()
        {
            Text = card.Title,
            MaxWidth = 148,
            FontSize = 13.5,
            FontWeight = FontWeight.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        TextBlock subtitle = new()
        {
            Text = card.Subtitle,
            MaxWidth = 148,
            FontSize = 10.5,
            Opacity = 0.66,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        StackPanel details = new()
        {
            Spacing = 3,
            Children =
            {
                title,
                subtitle
            }
        };
        MyButton? apply = null;
        if (card.CanApply)
        {
            apply = new MyButton
            {
                Height = 30,
                Margin = new Thickness(0, 5, 0, 0),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 0),
                Text = ResourceText(
                    isCapeTrack ? "Appearance.Action.UseCape" : "Appearance.Action.UseSkin",
                    isCapeTrack ? "使用披风" : "使用皮肤"),
                UseExperimentalStyle = true
            };
            apply.Click += (_, _) => select(card);
            details.Children.Add(apply);
        }

        Grid layout = new()
        {
            Children =
            {
                preview,
                details
            }
        };
        Border border = new()
        {
            Width = 176,
            Height = 228,
            Margin = new Thickness(0, 0, 0, 2),
            CornerRadius = new CornerRadius(18),
            Background = ResolveBrush("ColorBrushWhite", "#f7ffffff"),
            BorderBrush = ResolveBrush("ColorBrushGray6", "#22000000"),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = layout
        };
        border.PointerEntered += (_, _) =>
            border.BorderBrush = ResolveBrush("ColorBrush3", "#1370f3");
        border.PointerExited += (_, _) =>
            border.BorderBrush = ResolveBrush("ColorBrushGray6", "#22000000");
        return new AppearanceCardVisual(
            border,
            layout,
            preview,
            details,
            title,
            subtitle,
            apply,
            card.CanApply);
    }

    private void ApplyResponsiveLayout()
    {
        if (this.FindControl<Grid>("PanRoot") is not { } root)
            return;

        double width = Bounds.Width;
        double height = Bounds.Height;
        bool compactHeight = height > 0d && height <= 560d;
        double rail = width >= 1380d ? 326d : width <= 940d ? 268d : 300d;
        root.ColumnDefinitions = new ColumnDefinitions($"{rail},*");
        if (this.FindControl<Grid>("PanProfileRail") is { } profileRail)
        {
            profileRail.Margin = compactHeight
                ? new Thickness(18d, 14d, 18d, 16d)
                : new Thickness(22d, 18d, 22d, 22d);
        }

        if (this.FindControl<Grid>("PanContent") is { } content)
        {
            double edge = width >= 1500d ? 38d : width <= 980d ? 18d : 26d;
            content.Margin = compactHeight
                ? new Thickness(edge, 14d, edge, 16d)
                : new Thickness(edge, 20d, edge, 24d);
        }

        if (this.FindControl<Grid>("PanCapeHeader") is { } capeHeader)
            capeHeader.Margin = new Thickness(2d, compactHeight ? 12d : 18d, 2d, 10d);

        ApplyTrackMetrics(isCapeTrack: false);
        ApplyTrackMetrics(isCapeTrack: true);
    }

    private void ApplyTrackMetrics(bool isCapeTrack)
    {
        string viewportName = isCapeTrack ? "PanCapeScroll" : "PanSkinScroll";
        double viewportHeight = this.FindControl<ScrollViewer>(viewportName)?.Bounds.Height ?? 0d;
        if (viewportHeight <= 1d)
        {
            double reservedHeight = Bounds.Height <= 560d ? 116d : 144d;
            viewportHeight = Math.Max(112d, (Bounds.Height - reservedHeight) / 2d);
        }

        List<AppearanceCardVisual> visuals = isCapeTrack ? _capeCards : _skinCards;
        foreach (AppearanceCardVisual visual in visuals)
            ApplyCardMetrics(visual, CalculateCardMetrics(viewportHeight, visual.CanApply));
    }

    internal static AppearanceCardMetrics CalculateCardMetrics(
        double viewportHeight,
        bool canApply)
    {
        if (!double.IsFinite(viewportHeight) || viewportHeight <= 0d)
            viewportHeight = 228d;

        bool horizontal = viewportHeight < 190d;
        if (horizontal)
        {
            double cardHeight = Math.Clamp(viewportHeight - 10d, 112d, 176d);
            double previewHeight = Math.Clamp(cardHeight - 18d, 72d, 150d);
            double previewWidth = previewHeight / 2d;
            double cardWidth = Math.Clamp(previewWidth + 116d, 164d, 216d);
            return new AppearanceCardMetrics(
                cardWidth,
                cardHeight,
                previewWidth,
                previewHeight,
                IsHorizontal: true);
        }

        double verticalCardHeight = Math.Clamp(viewportHeight - 10d, 180d, 270d);
        double reserved = canApply ? 90d : 56d;
        double verticalPreviewHeight = Math.Clamp(
            verticalCardHeight - reserved,
            72d,
            180d);
        double verticalPreviewWidth = verticalPreviewHeight / 2d;
        double verticalCardWidth = Math.Clamp(
            verticalPreviewWidth + 88d,
            150d,
            190d);
        return new AppearanceCardMetrics(
            verticalCardWidth,
            verticalCardHeight,
            verticalPreviewWidth,
            verticalPreviewHeight,
            IsHorizontal: false);
    }

    private static void ApplyCardMetrics(
        AppearanceCardVisual visual,
        AppearanceCardMetrics metrics)
    {
        visual.Container.Width = metrics.CardWidth;
        visual.Container.Height = metrics.CardHeight;
        visual.Preview.Width = metrics.PreviewWidth;
        visual.Preview.Height = metrics.PreviewHeight;

        if (metrics.IsHorizontal)
        {
            visual.Layout.Margin = new Thickness(10d, 8d);
            visual.Layout.ColumnDefinitions = new ColumnDefinitions("Auto,10,*");
            visual.Layout.RowDefinitions = new RowDefinitions("*");
            Grid.SetColumn(visual.Preview, 0);
            Grid.SetRow(visual.Preview, 0);
            Grid.SetColumn(visual.Details, 2);
            Grid.SetRow(visual.Details, 0);
            visual.Details.VerticalAlignment = VerticalAlignment.Center;
            visual.Details.HorizontalAlignment = HorizontalAlignment.Stretch;
            visual.Title.MaxWidth = Math.Max(72d, metrics.CardWidth - metrics.PreviewWidth - 42d);
            visual.Subtitle.MaxWidth = visual.Title.MaxWidth;
            visual.Title.TextAlignment = TextAlignment.Left;
            visual.Subtitle.TextAlignment = TextAlignment.Left;
            if (visual.ApplyButton is { } horizontalButton)
                horizontalButton.Height = 28d;
            return;
        }

        visual.Layout.Margin = new Thickness(14d, 10d, 14d, 12d);
        visual.Layout.ColumnDefinitions = new ColumnDefinitions("*");
        visual.Layout.RowDefinitions = new RowDefinitions("*,Auto");
        Grid.SetColumn(visual.Preview, 0);
        Grid.SetRow(visual.Preview, 0);
        Grid.SetColumn(visual.Details, 0);
        Grid.SetRow(visual.Details, 1);
        visual.Details.VerticalAlignment = VerticalAlignment.Bottom;
        visual.Details.HorizontalAlignment = HorizontalAlignment.Stretch;
        visual.Title.MaxWidth = Math.Max(96d, metrics.CardWidth - 28d);
        visual.Subtitle.MaxWidth = visual.Title.MaxWidth;
        visual.Title.TextAlignment = TextAlignment.Center;
        visual.Subtitle.TextAlignment = TextAlignment.Center;
        if (visual.ApplyButton is { } verticalButton)
            verticalButton.Height = 30d;
    }

    private void WireTrackViewport(ScrollViewer? viewer, bool isCapeTrack)
    {
        if (viewer is null)
            return;

        WireHorizontalWheel(viewer);
        viewer.SizeChanged += (_, _) => ApplyTrackMetrics(isCapeTrack);
    }

    private static void WireHorizontalWheel(ScrollViewer? viewer)
    {
        if (viewer is null)
            return;
        viewer.PointerWheelChanged += (_, e) =>
        {
            double delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y)
                ? -e.Delta.X
                : -e.Delta.Y;
            double maximum = Math.Max(0d, viewer.Extent.Width - viewer.Viewport.Width);
            if (maximum <= 0d || Math.Abs(delta) < 0.001d)
                return;

            viewer.Offset = new Vector(
                Math.Clamp(viewer.Offset.X + delta * 64d, 0d, maximum),
                viewer.Offset.Y);
            e.Handled = true;
        };
    }

    private void BtnLocalSkin_Click(object? sender, EventArgs e) =>
        LocalSkinRequested?.Invoke(this, EventArgs.Empty);

    private void BtnSkinLibrary_Click(object? sender, EventArgs e) =>
        SkinLibraryRequested?.Invoke(this, EventArgs.Empty);

    private static string ResourceText(string key, string fallback) =>
        AvaloniaLocalizationManager.GetText(key, fallback);

    private static string FormatResource(string key, string fallback, params object[] arguments) =>
        string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            ResourceText(key, fallback),
            arguments);

    private static IBrush ResolveBrush(string key, string fallback)
    {
        if (global::Avalonia.Application.Current?.TryFindResource(key, null, out object? resource) == true &&
            resource is IBrush brush)
        {
            return brush;
        }

        return Brush.Parse(fallback);
    }
}

internal readonly record struct AppearanceCardMetrics(
    double CardWidth,
    double CardHeight,
    double PreviewWidth,
    double PreviewHeight,
    bool IsHorizontal);

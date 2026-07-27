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
    private SkinAppearancePageModel? _model;

    public PageSkinAppearanceRight()
    {
        AvaloniaXamlLoader.Load(this);
        WireHorizontalWheel(this.FindControl<ScrollViewer>("PanSkinScroll"));
        WireHorizontalWheel(this.FindControl<ScrollViewer>("PanCapeScroll"));
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
        foreach (SkinAppearanceCard card in cards)
            panel.Children.Add(CreateAppearanceCard(card, select, isCapeTrack));

        if (this.FindControl<Control>(emptyName) is { } empty)
            empty.IsVisible = cards.Count == 0;
        if (this.FindControl<TextBlock>(countName) is { } count)
        {
            count.Text = FormatResource(
                "Appearance.ItemCount",
                "{0} 项",
                cards.Count);
        }
    }

    private static Border CreateAppearanceCard(
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
        StackPanel content = new()
        {
            Margin = new Thickness(14, 10, 14, 12),
            Spacing = 3,
            Children =
            {
                preview,
                title,
                subtitle
            }
        };
        if (card.CanApply)
        {
            MyButton apply = new()
            {
                Height = 30,
                Margin = new Thickness(2, 6, 2, 0),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 0),
                Text = ResourceText(
                    isCapeTrack ? "Appearance.Action.UseCape" : "Appearance.Action.UseSkin",
                    isCapeTrack ? "使用披风" : "使用皮肤"),
                UseExperimentalStyle = true
            };
            apply.Click += (_, _) => select(card);
            content.Children.Add(apply);
        }

        Border border = new()
        {
            Width = 176,
            MinHeight = 228,
            Margin = new Thickness(0, 0, 0, 2),
            CornerRadius = new CornerRadius(18),
            Background = ResolveBrush("ColorBrushWhite", "#f7ffffff"),
            BorderBrush = ResolveBrush("ColorBrushGray6", "#22000000"),
            BorderThickness = new Thickness(1),
            Child = content
        };
        border.PointerEntered += (_, _) =>
            border.BorderBrush = ResolveBrush("ColorBrush3", "#1370f3");
        border.PointerExited += (_, _) =>
            border.BorderBrush = ResolveBrush("ColorBrushGray6", "#22000000");
        return border;
    }

    private void ApplyResponsiveLayout()
    {
        if (this.FindControl<Grid>("PanRoot") is not { } root)
            return;

        double width = Bounds.Width;
        double rail = width >= 1380d ? 326d : width <= 940d ? 268d : 300d;
        root.ColumnDefinitions = new ColumnDefinitions($"{rail},*");
        if (this.FindControl<Grid>("PanContent") is { } content)
        {
            double edge = width >= 1500d ? 38d : width <= 980d ? 18d : 26d;
            content.Margin = new Thickness(edge, 20d, edge, 24d);
        }
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

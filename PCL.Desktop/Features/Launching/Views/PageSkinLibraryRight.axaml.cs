// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Features.Launching.Appearance;
using PCL.Desktop.Localization;

namespace PCL.Desktop.Features.Launching.Views;

public partial class PageSkinLibraryRight : MyPageRight
{
    private IReadOnlyList<ISkinSiteCatalog> _catalogs = [];
    private ISkinSiteCatalog? _selectedCatalog;
    private CancellationTokenSource? _loadCancellation;
    private int _currentPage = 1;
    private bool _hasPreviousPage;
    private bool _hasNextPage;
    private bool _isLoading;

    public PageSkinLibraryRight()
    {
        AvaloniaXamlLoader.Load(this);
        PanScroll = this.FindControl<MyScrollViewer>("PanBack");
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        ActualThemeVariantChanged += (_, _) => RenderSiteRail();
        PageExit += CancelPendingLoad;
        ApplyResponsiveLayout();
        UpdatePager();
    }

    public event EventHandler<SkinSiteItem>? SkinSelected;

    public event EventHandler<Uri>? OpenUrlRequested;

    public void SetCatalogs(
        IReadOnlyList<ISkinSiteCatalog> catalogs,
        string? selectedId = null)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        _catalogs = catalogs;
        _selectedCatalog = catalogs.FirstOrDefault(catalog =>
                               string.Equals(
                                   catalog.Descriptor.Id,
                                   selectedId,
                                   StringComparison.OrdinalIgnoreCase))
                           ?? (catalogs.Count > 0 ? catalogs[0] : null);
        _currentPage = 1;
        RenderSiteRail();
        _ = ReloadAsync(force: false);
    }

    private async Task ReloadAsync(bool force)
    {
        ISkinSiteCatalog? catalog = _selectedCatalog;
        if (catalog is null)
        {
            ShowEmpty(
                ResourceText("Appearance.Library.Empty.Title", "没有皮肤站"),
                ResourceText("Appearance.Library.Empty.NoSite", "尚未配置可用的皮肤站。"));
            return;
        }

        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _loadCancellation.Token;
        SetLoading(true);
        if (this.FindControl<TextBlock>("LabSiteName") is { } siteName)
            siteName.Text = catalog.Descriptor.DisplayName;
        if (this.FindControl<TextBlock>("LabSiteStatus") is { } status)
            status.Text = ResourceText("Appearance.Library.Loading", "正在读取皮肤站 API…");

        try
        {
            // Catalog implementations own cache policy. Re-selecting a site remains
            // instant while an explicit refresh reconstructs the built-in adapter.
            if (force && catalog is LittleSkinCatalog)
            {
                int index = _catalogs.ToList().IndexOf(catalog);
                LittleSkinCatalog replacement = new();
                _catalogs = _catalogs
                    .Select((item, itemIndex) => itemIndex == index ? replacement : item)
                    .ToArray();
                catalog = replacement;
                _selectedCatalog = replacement;
            }

            SkinSitePage page = await catalog
                .GetPageAsync(_currentPage, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                    return;
                RenderPage(page);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                    return;
                ShowEmpty(
                    ResourceText("Appearance.Library.Error.Title", "皮肤站暂时不可用"),
                    FormatResource(
                        "Appearance.Library.Error.Message",
                        "无法读取 API：{0}",
                        exception.Message));
                if (this.FindControl<TextBlock>("LabSiteStatus") is { } status)
                    status.Text = ResourceText("Appearance.Library.Error.Status", "API 请求失败");
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!cancellationToken.IsCancellationRequested)
                    SetLoading(false);
            });
        }
    }

    private void RenderPage(SkinSitePage page)
    {
        _currentPage = page.Page;
        _hasPreviousPage = page.HasPreviousPage;
        _hasNextPage = page.HasNextPage;
        if (this.FindControl<TextBlock>("LabSiteName") is { } name)
            name.Text = page.SiteName;
        if (this.FindControl<TextBlock>("LabSiteStatus") is { } status)
        {
            string version = string.IsNullOrWhiteSpace(page.ServerVersion)
                ? string.Empty
                : " · Blessing Skin " + page.ServerVersion;
            status.Text = FormatResource(
                "Appearance.Library.Status",
                "第 {0} 页 · {1} 项{2}",
                page.Page,
                page.Items.Count,
                version);
        }

        if (this.FindControl<WrapPanel>("PanItems") is { } panel)
        {
            panel.Children.Clear();
            foreach (SkinSiteItem item in page.Items)
                panel.Children.Add(CreateSkinCard(item));
        }

        if (this.FindControl<Control>("PanEmpty") is { } empty)
            empty.IsVisible = page.Items.Count == 0;
        if (this.FindControl<Control>("PanBack") is { } content)
            content.IsVisible = page.Items.Count > 0;
        UpdatePager();
        PanScroll?.ScrollToHome();
    }

    private Border CreateSkinCard(SkinSiteItem item)
    {
        MinecraftPlayerPreview preview = new()
        {
            Width = 84,
            Height = 168,
            SkinAddress = item.SkinAddress,
            IsSlim = string.Equals(item.Model, "alex", StringComparison.OrdinalIgnoreCase),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        TextBlock title = new()
        {
            Text = item.Name,
            MaxWidth = 174,
            FontSize = 13.5,
            FontWeight = FontWeight.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        TextBlock uploader = new()
        {
            Text = FormatResource(
                "Appearance.Library.By",
                "由 {0} 上传",
                string.IsNullOrWhiteSpace(item.Uploader)
                    ? ResourceText("Appearance.Library.UnknownUploader", "未知用户")
                    : item.Uploader),
            MaxWidth = 174,
            FontSize = 10.5,
            Opacity = 0.65,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        TextBlock metadata = new()
        {
            Text = FormatResource(
                "Appearance.Library.Metadata",
                "♥ {0} · {1}{2}",
                item.Likes,
                string.Equals(item.Model, "alex", StringComparison.OrdinalIgnoreCase)
                    ? "Slim"
                    : "Classic",
                item.IsHighDefinition ? " · HD" : string.Empty),
            FontSize = 10.5,
            Opacity = 0.62,
            TextAlignment = TextAlignment.Center
        };
        MyButton useButton = new()
        {
            Height = 31,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 0),
            Margin = new Thickness(0, 6, 0, 0),
            UseExperimentalStyle = true,
            ColorType = MyButton.ColorState.Highlight,
            Text = ResourceText("Appearance.Action.UseSkin", "使用皮肤")
        };
        useButton.Click += (_, _) => SkinSelected?.Invoke(this, item);
        MyButton detailsButton = new()
        {
            Height = 31,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 0),
            UseExperimentalStyle = true,
            Text = ResourceText("Appearance.Library.Details", "查看详情")
        };
        detailsButton.Click += (_, _) => OpenUrlRequested?.Invoke(this, item.DetailsUri);

        StackPanel content = new()
        {
            Margin = new Thickness(14, 11, 14, 13),
            Spacing = 3,
            Children =
            {
                preview,
                title,
                uploader,
                metadata,
                useButton,
                detailsButton
            }
        };
        return new Border
        {
            Width = 208,
            Margin = new Thickness(6),
            CornerRadius = new CornerRadius(18),
            Background = ResolveBrush("ColorBrushWhite", "#f7ffffff"),
            BorderBrush = ResolveBrush("ColorBrushGray6", "#22000000"),
            BorderThickness = new Thickness(1),
            Child = content
        };
    }

    private void RenderSiteRail()
    {
        if (this.FindControl<StackPanel>("PanSites") is not { } panel)
            return;
        panel.Children.Clear();
        panel.Children.Add(new TextBlock
        {
            Text = ResourceText("Appearance.Library.Sites", "皮肤站"),
            Margin = new Thickness(8, 2, 8, 7),
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            LetterSpacing = 0.35,
            Opacity = 0.55
        });
        foreach (ISkinSiteCatalog catalog in _catalogs)
            panel.Children.Add(CreateSiteRow(catalog));
    }

    private Border CreateSiteRow(ISkinSiteCatalog catalog)
    {
        bool selected = ReferenceEquals(catalog, _selectedCatalog);
        Grid grid = new()
        {
            ColumnDefinitions = new ColumnDefinitions("3,9,*")
        };
        Border indicator = new()
        {
            Width = 3,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 2),
            Background = ResolveBrush("ColorBrush3", "#1370f3"),
            Opacity = selected ? 1d : 0d
        };
        grid.Children.Add(indicator);
        StackPanel text = new()
        {
            Spacing = 2,
            Children =
            {
                new TextBlock
                {
                    Text = catalog.Descriptor.DisplayName,
                    FontSize = 13.5,
                    FontWeight = selected ? FontWeight.SemiBold : FontWeight.Medium,
                    Foreground = selected
                        ? ResolveBrush("ColorBrush2", "#0b5bcb")
                        : ResolveBrush("ColorBrush1", "#343d4a")
                },
                new TextBlock
                {
                    Text = catalog.Descriptor.BaseUri.Host,
                    FontSize = 10.5,
                    Opacity = 0.62,
                    Foreground = ResolveBrush("ColorBrushGray2", "#737373")
                }
            }
        };
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);

        Border row = new()
        {
            Margin = new Thickness(0, 2),
            Padding = new Thickness(10, 9),
            CornerRadius = new CornerRadius(10),
            Background = selected
                ? ResolveBrush("ColorBrushBg1", "#bee0eafd")
                : Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = grid
        };
        row.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
                return;
            e.Handled = true;
            _selectedCatalog = catalog;
            _currentPage = 1;
            RenderSiteRail();
            _ = ReloadAsync(force: false);
        };
        return row;
    }

    private void SetLoading(bool loading)
    {
        _isLoading = loading;
        if (this.FindControl<Control>("PanLoading") is { } panel)
            panel.IsVisible = loading;
        if (loading)
        {
            if (this.FindControl<Control>("PanEmpty") is { } empty)
                empty.IsVisible = false;
            if (this.FindControl<Control>("PanBack") is { } content)
                content.IsVisible = false;
        }
        UpdatePager();
    }

    private void ShowEmpty(string title, string message)
    {
        if (this.FindControl<TextBlock>("LabEmptyTitle") is { } titleBlock)
            titleBlock.Text = title;
        if (this.FindControl<TextBlock>("LabEmptyMessage") is { } messageBlock)
            messageBlock.Text = message;
        if (this.FindControl<Control>("PanEmpty") is { } empty)
            empty.IsVisible = true;
        if (this.FindControl<Control>("PanBack") is { } content)
            content.IsVisible = false;
        _hasPreviousPage = false;
        _hasNextPage = false;
        UpdatePager();
    }

    private void UpdatePager()
    {
        if (this.FindControl<MyButton>("BtnPrevious") is { } previous)
            previous.IsEnabled = _hasPreviousPage && !_isLoading;
        if (this.FindControl<MyButton>("BtnNext") is { } next)
            next.IsEnabled = _hasNextPage && !_isLoading;
    }

    private void ApplyResponsiveLayout()
    {
        if (this.FindControl<Grid>("PanRoot") is not { } root)
            return;
        double width = Bounds.Width;
        double rail = width >= 1500d ? 294d : width <= 960d ? 238d : 268d;
        root.ColumnDefinitions = new ColumnDefinitions($"{rail},*");
    }

    private void BtnRefresh_Click(object? sender, EventArgs e) =>
        _ = ReloadAsync(force: true);

    private void BtnDocs_Click(object? sender, EventArgs e)
    {
        if (_selectedCatalog is { } catalog)
            OpenUrlRequested?.Invoke(this, catalog.Descriptor.DocumentationUri);
    }

    private void BtnOpenSite_Click(object? sender, EventArgs e)
    {
        if (_selectedCatalog is { } catalog)
            OpenUrlRequested?.Invoke(this, catalog.Descriptor.BaseUri);
    }

    private void BtnPrevious_Click(object? sender, EventArgs e)
    {
        if (!_hasPreviousPage)
            return;
        _currentPage = Math.Max(1, _currentPage - 1);
        _ = ReloadAsync(force: false);
    }

    private void BtnNext_Click(object? sender, EventArgs e)
    {
        if (!_hasNextPage)
            return;
        _currentPage++;
        _ = ReloadAsync(force: false);
    }

    public override void Dispose()
    {
        CancelPendingLoad();
        PageExit -= CancelPendingLoad;
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private void CancelPendingLoad()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
    }

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

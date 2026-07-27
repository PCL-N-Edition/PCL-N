// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Features.Launching.Views;

namespace PCL.Desktop.Features.Instances.Views;

/// <summary>
/// Experimental full-page version-settings shell: frosted sidebar + content host.
/// Rehosts classic right sub-pages so metadata/actions stay identical.
/// </summary>
public partial class PageInstanceManageExperimental : MyPageRight
{
    private const double SidebarBaseWidth = 200d;
    private const double SidebarMaxExtra = 20d;

    private LaunchInstanceInfo? _instance;
    private bool _isModable;
    private bool _hasShaderSupport;
    private bool _hasSchematicSupport;
    private MyPageRight? _currentContent;

    public PageInstanceManageExperimental()
    {
        AvaloniaXamlLoader.Load(this);
        InitializeRegisteredPageTags();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        AttachedToVisualTree += (_, _) => ApplyResponsiveLayout();
        PageEnter += () =>
        {
            _currentContent?.PageOnEnter();
            ExperimentalControlChrome.ApplyDeferred(_currentContent, enabled: true);
        };
        PageExit += () => _currentContent?.PageOnExit();
    }

    public event EventHandler<InstancePageSubType>? PageChanged;

    public event EventHandler<InstancePageSubType>? RefreshRequested;

    public event EventHandler? ResetRequested;

    public InstancePageSubType PageId { get; private set; } = InstancePageSubType.Overall;

    public MyPageRight? CurrentContent => _currentContent;

    public void SetInstance(LaunchInstanceInfo instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        _instance = instance;
        _isModable = InstanceDisplayHelper.IsModable(instance);
        _hasShaderSupport = _isModable && InstanceDisplayHelper.HasShaderSupport(instance);
        _hasSchematicSupport = _isModable && InstanceDisplayHelper.HasSchematicSupport(instance);
        if (this.FindControl<TextBlock>("LabInstanceName") is { } nameLabel)
            nameLabel.Text = instance.Name;
        RefreshResourceNavVisibility();
    }

    public InstancePageSubType NormalizePage(InstancePageSubType page)
    {
        // No mod-loader: hide the whole mods entry (do not show a disabled stub).
        if ((page is InstancePageSubType.Mods or InstancePageSubType.ModsDisabled) && !_isModable)
            return InstancePageSubType.Overall;
        if (page == InstancePageSubType.Shaders && !_hasShaderSupport)
            return InstancePageSubType.Overall;
        if (page == InstancePageSubType.Schematics && !_hasSchematicSupport)
            return InstancePageSubType.Overall;
        if (page == InstancePageSubType.ModsDisabled && _isModable)
            return InstancePageSubType.Mods;
        return page;
    }

    public void SelectPage(InstancePageSubType page)
    {
        page = NormalizePage(page);
        PageId = page;
        foreach (MyListItem item in GetItems())
        {
            if (TryGetPage(item, out InstancePageSubType itemPage))
                item.SetChecked(itemPage == page, user: false, animate: false);
        }
    }

    public void SetContent(MyPageRight page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (ReferenceEquals(_currentContent, page))
        {
            ExperimentalControlChrome.ApplyDeferred(page, enabled: true);
            return;
        }

        if (_currentContent is not null)
            _currentContent.PageOnExit();

        _currentContent = page;
        page.Background = Brushes.Transparent;
        page.ClipToBounds = true;
        page.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        page.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;

        if (this.FindControl<ContentControl>("PanContent") is { } host)
            host.Content = page;

        // Nested pages scroll themselves; keep shell scroll null so back-to-top binds to child.
        PanScroll = page.PanScroll;
        ExperimentalControlChrome.ApplyDeferred(page, enabled: true);
        page.PageOnEnter();
    }

    private void ApplyResponsiveLayout()
    {
        if (this.FindControl<Grid>("PanRoot") is not { } root)
            return;

        double width = Bounds.Width;
        if (width <= 0)
            return;

        // Keep sidebar compact so the content column keeps room for SharedSize rows.
        double progress = Math.Clamp((width - 1200d) / 600d, 0d, 1d);
        double sidebar = SidebarBaseWidth + SidebarMaxExtra * progress;
        root.ColumnDefinitions = new ColumnDefinitions($"{sidebar:0.#},*");
    }

    private void PageCheck(object senderRaw, RouteEventArgs e)
    {
        if (senderRaw is MyListItem item && TryGetPage(item, out InstancePageSubType page))
            PageChange(page);
    }

    private void PageChange(InstancePageSubType page)
    {
        page = NormalizePage(page);
        if (PageId == page)
            return;

        PageId = page;
        PageChanged?.Invoke(this, page);
    }

    private void Refresh_Click(object? sender, EventArgs e)
    {
        if (sender is MyIconButton button && TryNormalizePageTag(button.Tag, out InstancePageSubType page))
            RefreshRequested?.Invoke(this, page);
    }

    private void Reset_Click(object? sender, EventArgs e)
    {
        ResetRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshResourceNavVisibility()
    {
        if (this.FindControl<MyListItem>("ItemMod") is { } itemMod)
            itemMod.IsVisible = _isModable;

        // Disabled stub is never shown in experimental manage UI.
        if (this.FindControl<MyListItem>("ItemModDisabled") is { } itemModDisabled)
            itemModDisabled.IsVisible = false;

        if (this.FindControl<MyListItem>("ItemShader") is { } itemShader)
            itemShader.IsVisible = _hasShaderSupport;

        if (this.FindControl<MyListItem>("ItemSchematic") is { } itemSchematic)
            itemSchematic.IsVisible = _hasSchematicSupport;
    }

    private IEnumerable<MyListItem> GetItems()
    {
        if (this.FindControl<Panel>("PanNav") is not { } panel)
            yield break;

        foreach (Control child in panel.Children)
        {
            if (child is MyListItem item)
                yield return item;
        }
    }

    private void InitializeRegisteredPageTags()
    {
        foreach (MyListItem item in GetItems())
        {
            if (TryNormalizePageTag(item.Tag, out InstancePageSubType page))
                item.Tag = page;

            foreach (MyIconButton button in item.Buttons)
            {
                if (TryNormalizePageTag(button.Tag, out InstancePageSubType buttonPage))
                    button.Tag = buttonPage;
            }
        }
    }

    private static bool TryGetPage(MyListItem item, out InstancePageSubType page) =>
        TryNormalizePageTag(item.Tag, out page);

    private static bool TryNormalizePageTag(object? tag, out InstancePageSubType page)
    {
        page = InstancePageSubType.Overall;
        return tag switch
        {
            int value when InstancePageRegistry.IsDefined((InstancePageSubType)value) =>
                SetPage((InstancePageSubType)value, out page),
            InstancePageSubType value when InstancePageRegistry.IsDefined(value) =>
                SetPage(value, out page),
            string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) &&
                             InstancePageRegistry.IsDefined((InstancePageSubType)value) =>
                SetPage((InstancePageSubType)value, out page),
            _ => false
        };
    }

    private static bool SetPage(InstancePageSubType value, out InstancePageSubType page)
    {
        page = value;
        return true;
    }
}

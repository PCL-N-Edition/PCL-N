// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PCL.Core.Logging;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Hosting;
using PCL.Desktop.Hosting.PluginSidecar;

namespace PCL.Desktop.Features.Settings.Views;

/// <summary>
/// Generic host settings page: body is fully supplied by sidecar UI data-chain (no plugin UI code here).
/// </summary>
internal sealed class PageSetupRemoteDataChain : MyPageRight, IRefreshableSettingsPage
{
    private readonly string _pageId;
    private readonly StackPanel _panMain;
    private readonly TextBlock _status;
    private readonly Dictionary<string, TextBox> _fields = new(StringComparer.OrdinalIgnoreCase);

    public PageSetupRemoteDataChain(string pageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        _pageId = pageId;
        _status = new TextBlock
        {
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            Text = "正在从插件侧车加载页面…"
        };
        _panMain = new StackPanel
        {
            Margin = new Thickness(25, 25, 25, 10),
            Spacing = 12,
            Children = { _status }
        };
        MyScrollViewer scroll = new()
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = _panMain
        };
        PanScroll = scroll;
        Content = scroll;
        RefreshPage();
    }

    public void RefreshPage() => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        if (!PluginSidecarSupervisor.Instance.IsAvailable)
        {
            bool started = await PluginSidecarSupervisor.Instance.TryStartAsync().ConfigureAwait(true);
            if (!started)
            {
                _status.Text = "插件侧车未运行，无法加载远程页面。";
                return;
            }
        }

        try
        {
            PluginSidecarClient client = PluginSidecarSupervisor.Instance.Client
                ?? throw new InvalidOperationException("sidecar client null");
            PluginSidecarResult page = await client.UiGetPageAsync(_pageId).ConfigureAwait(true);
            if (!page.Ok || page.Root is null)
            {
                _status.Text = page.Message ?? "页面为空。";
                return;
            }

            ApplyRoot(page.Root);
        }
        catch (Exception ex)
        {
            _status.Text = "加载失败：" + ex.Message;
            _panMain.Children.Clear();
            _panMain.Children.Add(_status);
        }
    }

    private void ApplyRoot(PluginUiNodeDto root)
    {
        _fields.Clear();
        _panMain.Children.Clear();
        _panMain.Children.Add(RenderNode(root));
    }

    private Control RenderNode(PluginUiNodeDto node)
    {
        string kind = node.Kind ?? "stack";
        return kind.ToLowerInvariant() switch
        {
            "card" => RenderCard(node),
            "text" => new TextBlock
            {
                Text = node.Text ?? "",
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                IsEnabled = node.Enabled
            },
            "muted" => new TextBlock
            {
                Text = node.Text ?? "",
                FontSize = 12,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap
            },
            "hint" => new MyHint
            {
                Text = node.Text ?? "",
                Theme = MyHint.Themes.Yellow
            },
            "button" => RenderButton(node),
            "checkbox" => RenderCheckBox(node),
            "textbox" => RenderTextBox(node),
            "list" or "stack" => RenderStack(node),
            "row" => RenderRow(node),
            _ => new TextBlock { Text = $"未知节点: {kind}", Opacity = 0.6 }
        };
    }

    private MyCard RenderCard(PluginUiNodeDto node)
    {
        MyCard card = new()
        {
            Title = node.Title ?? "",
            Margin = new Thickness(0, 0, 0, 12)
        };
        StackPanel content = new()
        {
            Margin = new Thickness(25, 40, 25, 20),
            Spacing = 10
        };
        foreach (PluginUiNodeDto child in node.Children ?? [])
            content.Children.Add(RenderNode(child));
        card.Children.Add(content);
        return card;
    }

    private StackPanel RenderStack(PluginUiNodeDto node)
    {
        StackPanel panel = new() { Spacing = 8 };
        if (!string.IsNullOrWhiteSpace(node.Title))
        {
            panel.Children.Add(new TextBlock
            {
                Text = node.Title,
                FontWeight = FontWeight.SemiBold,
                FontSize = 13
            });
        }

        foreach (PluginUiNodeDto child in node.Children ?? [])
            panel.Children.Add(RenderNode(child));
        return panel;
    }

    private Border RenderRow(PluginUiNodeDto node)
    {
        Grid grid = new();
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        StackPanel left = new() { Spacing = 2 };
        if (!string.IsNullOrWhiteSpace(node.Title))
        {
            left.Children.Add(new TextBlock
            {
                Text = node.Title,
                FontWeight = FontWeight.SemiBold,
                FontSize = 14
            });
        }

        if (!string.IsNullOrWhiteSpace(node.Text))
        {
            left.Children.Add(new TextBlock
            {
                Text = node.Text,
                FontSize = 12,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap
            });
        }

        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        WrapPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        foreach (PluginUiNodeDto child in node.Children ?? [])
        {
            if (string.Equals(child.Kind, "button", StringComparison.OrdinalIgnoreCase))
                actions.Children.Add(RenderButton(child));
        }

        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);

        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10),
            Child = grid
        };
    }

    private MyButton RenderButton(PluginUiNodeDto node)
    {
        MyButton button = new()
        {
            Text = node.Text ?? node.Title ?? "操作",
            MinWidth = 80,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 6),
            IsEnabled = node.Enabled
        };
        string? actionId = node.ActionId;
        string? meta = node.Meta;
        string? valueField = node.ValueField;
        if (!string.IsNullOrWhiteSpace(actionId))
        {
            button.Click += async (_, _) =>
            {
                string? value = ResolveFieldValue(valueField);
                await InvokeAsync(actionId!, pluginId: meta, value: value).ConfigureAwait(true);
            };
        }

        return button;
    }

    private MyCheckBox RenderCheckBox(PluginUiNodeDto node)
    {
        bool isChecked = node.Checked == true;
        // Host-owned settings: sidecar cannot read launcher prefs; resolve by known field id.
        if (string.Equals(node.Id, "host.SystemDebugMode", StringComparison.Ordinal))
            isChecked = DesktopHostDeveloperDiagnostics.Instance.IsEnabled;

        MyCheckBox box = new()
        {
            Text = node.Text ?? node.Title ?? "",
            Checked = isChecked,
            Height = 22,
            IsEnabled = node.Enabled
        };
        string? actionId = node.ActionId;
        string? meta = node.Meta;
        if (!string.IsNullOrWhiteSpace(actionId))
        {
            box.Change += async (_, _) =>
            {
                await InvokeAsync(actionId!, pluginId: meta, boolValue: box.Checked == true)
                    .ConfigureAwait(true);
            };
        }

        return box;
    }

    private StackPanel RenderTextBox(PluginUiNodeDto node)
    {
        StackPanel panel = new() { Spacing = 4 };
        if (!string.IsNullOrWhiteSpace(node.Title))
        {
            panel.Children.Add(new TextBlock
            {
                Text = node.Title,
                FontSize = 13,
                FontWeight = FontWeight.SemiBold
            });
        }

        TextBox box = new()
        {
            Text = node.Text ?? "",
            PlaceholderText = node.Placeholder ?? "",
            MinWidth = 280,
            MinHeight = 32,
            IsEnabled = node.Enabled,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        if (!string.IsNullOrWhiteSpace(node.Id))
            _fields[node.Id!] = box;

        panel.Children.Add(box);
        return panel;
    }

    private string? ResolveFieldValue(string? fieldId)
    {
        if (string.IsNullOrWhiteSpace(fieldId))
            return null;
        return _fields.TryGetValue(fieldId, out TextBox? box) ? box.Text : null;
    }

    private async Task InvokeAsync(
        string actionId,
        string? pluginId = null,
        bool? boolValue = null,
        string? packagePath = null,
        string? value = null)
    {
        if (!PluginSidecarSupervisor.Instance.IsAvailable)
        {
            _status.Text = "侧车未连接。";
            return;
        }

        try
        {
            PluginSidecarClient client = PluginSidecarSupervisor.Instance.Client!;
            PluginSidecarResult result = await client.UiInvokeActionAsync(
                    _pageId,
                    actionId,
                    value: value,
                    boolValue: boolValue,
                    packagePath: packagePath,
                    pluginId: pluginId)
                .ConfigureAwait(true);

            // Host-side pick file/folder then re-invoke (data-chain handoff).
            if (result.PickFolder)
            {
                string? path = await PickFolderAsync(result.PickFolderTitle).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(path))
                    return;
                result = await client.UiInvokeActionAsync(
                        _pageId,
                        actionId,
                        value: value,
                        packagePath: path,
                        pluginId: pluginId)
                    .ConfigureAwait(true);
            }
            else if (result.PickFilePatterns is { Length: > 0 })
            {
                string? path = await PickFileAsync(result.PickFileTitle, result.PickFilePatterns).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(path))
                    return;
                result = await client.UiInvokeActionAsync(
                        _pageId,
                        actionId,
                        value: value,
                        packagePath: path,
                        pluginId: pluginId)
                    .ConfigureAwait(true);
            }

            if (!string.IsNullOrWhiteSpace(result.OpenUrl) &&
                Uri.TryCreate(result.OpenUrl, UriKind.Absolute, out Uri? uri))
            {
                await DesktopHostUriLauncher.Instance.OpenAsync(uri).ConfigureAwait(true);
            }

            if (!string.IsNullOrWhiteSpace(result.Message))
                DesktopHostNotifications.Instance.ShowInformation(result.Message!);

            // Sidecar may request host-only settings (e.g. SystemDebugMode diagnostics).
            if (!string.IsNullOrWhiteSpace(result.HostBooleanKey) && result.HostBooleanValue is { } hostBool)
            {
                if (string.Equals(result.HostBooleanKey, "SystemDebugMode", StringComparison.Ordinal))
                    DesktopHostDeveloperDiagnostics.Instance.SetEnabled(hostBool);
                else
                    PortableLog.Warn("PluginSidecar", "Unknown hostBooleanKey: " + result.HostBooleanKey);
            }

            // Developer toggles may add Safety / UI Patch / Compatibility pages.
            if (result.RefreshNavigation)
            {
                try
                {
                    await PluginSidecarUiInjector.InjectAsync(DesktopHost.Current).ConfigureAwait(true);
                }
                catch (Exception injectEx)
                {
                    PortableLog.Warn("PluginSidecar", "Navigation reinject failed: " + injectEx.Message);
                }
            }

            // Prefer inline root from action (e.g. local market scan) over full page reload.
            if (result.Root is not null)
            {
                ApplyRoot(result.Root);
                return;
            }

            if (result.RefreshPage || result.Ok)
                await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            DesktopHostNotifications.Instance.ShowWarning(ex.Message);
        }
    }

    private async Task<string?> PickFileAsync(string? title, string[] patterns)
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is null)
            return null;

        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = title ?? "选择文件",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Package")
                    {
                        Patterns = patterns
                    }
                ]
            }).ConfigureAwait(true);
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    private async Task<string?> PickFolderAsync(string? title)
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is null)
            return null;

        IReadOnlyList<IStorageFolder> folders = await top.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = title ?? "选择目录",
                AllowMultiple = false
            }).ConfigureAwait(true);
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }
}

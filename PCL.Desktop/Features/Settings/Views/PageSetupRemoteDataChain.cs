// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PCL.Application.Hosting.RuntimeExtensions;
using PCL.Core.Logging;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Diagnostics;
using PCL.Desktop.Hosting;
using PCL.Desktop.Hosting.PluginSidecar;

namespace PCL.Desktop.Features.Settings.Views;

/// <summary>
/// Generic host settings page: body from sidecar UI data-chain.
/// Visual language matches classic PCL setup pages (MyCard / MyTextBox / MyButton) — not experimental UI.
/// </summary>
internal sealed class PageSetupRemoteDataChain : MyPageRight, IRefreshableSettingsPage
{
    private static readonly IBrush RowBorderBrush =
        new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));

    private readonly string _pageId;
    private readonly StackPanel _panMain;
    private readonly MyLoading _loading;
    private readonly Dictionary<string, Func<string?>> _fields = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _liveRefreshCancellation;
    private long _revision;
    private int _listAnimSeq;

    public PageSetupRemoteDataChain(string pageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        _pageId = pageId;
        _loading = new MyLoading
        {
            Text = "正在加载",
            Margin = new Thickness(20, 20, 20, 17),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _panMain = new StackPanel
        {
            Margin = new Thickness(25, 25, 25, 10),
            Spacing = 0
        };
        MyScrollViewer scroll = new()
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = _panMain
        };
        PanScroll = scroll;
        Content = scroll;
        AttachedToVisualTree += (_, _) => StartLiveRefresh();
        DetachedFromVisualTree += (_, _) => StopLiveRefresh();

        // Reuse a root already fetched during this session; otherwise load on demand.
        if (PluginUiPageCache.TryGetRoot(_pageId, out PluginUiNodeDto? cached) && cached is not null)
        {
            ApplyRoot(cached);
            return;
        }

        if (PluginUiPageCache.TryGetFailure(_pageId, out string? failure) &&
            !string.IsNullOrWhiteSpace(failure))
        {
            ShowPageMessage(failure!, warn: true);
            return;
        }

        _panMain.Children.Add(_loading);
        RefreshPage();
    }

    public void RefreshPage() => _ = RefreshAsync(forceNetwork: true);

    private async Task RefreshAsync(bool forceNetwork = false)
    {
        if (!forceNetwork &&
            PluginUiPageCache.TryGetRoot(_pageId, out PluginUiNodeDto? cached) &&
            cached is not null)
        {
            ApplyRoot(cached);
            return;
        }

        if (!PluginSidecarSupervisor.Instance.IsAvailable)
        {
            bool started = await PluginSidecarSupervisor.Instance.TryStartAsync().ConfigureAwait(true);
            if (!started)
            {
                ShowPageMessage(
                    "插件侧车未运行，无法配置插件。\n\n" +
                    "常见原因：发行包未附带 PCL.Plugin.Sidecar（应在启动器目录的 sidecar\\ 下），" +
                    "或侧车启动失败；若下载的是 NoRuntime 版本，请先安装 .NET 10 Runtime。" +
                    "\n" +
                    "开发构建请先编译 PCL.Plugin.Sidecar，并确认日志中有 “Starting sidecar”。\n\n" +
                    "若刚关闭过启动器却无法刷新：请在任务管理器结束残留的 PCL-N-Edition / PCL.Plugin.Sidecar 后再开。",
                    warn: true);
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
                ShowPageMessage(page.Message ?? "页面为空。", warn: true);
                return;
            }

            PluginUiPageCache.SetRoot(_pageId, page.Root);
            _revision = page.Revision;
            ApplyRoot(page.Root);
        }
        catch (Exception ex)
        {
            // Broken pipe after id desync: one reconnect attempt before surfacing the error.
            if (ex.Message.Contains("传输异常", StringComparison.Ordinal) ||
                ex.Message.Contains("连接已损坏", StringComparison.Ordinal) ||
                ex.Message.Contains("expected", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    PluginUiPageCache.Invalidate(_pageId);
                    bool restarted = await PluginSidecarSupervisor.Instance.TryStartAsync().ConfigureAwait(true);
                    if (restarted && PluginSidecarSupervisor.Instance.Client is { } retry)
                    {
                        PluginSidecarResult page = await retry.UiGetPageAsync(_pageId).ConfigureAwait(true);
                        if (page.Ok && page.Root is not null)
                        {
                            PluginUiPageCache.SetRoot(_pageId, page.Root);
                            _revision = page.Revision;
                            ApplyRoot(page.Root);
                            return;
                        }
                    }
                }
                catch (Exception retryEx)
                {
                    ShowPageMessage("加载失败（重连后仍失败）：" + retryEx.Message, warn: true);
                    return;
                }
            }

            ShowPageMessage("加载失败：" + ex.Message, warn: true);
        }
    }

    private void ShowPageMessage(string text, bool warn)
    {
        _fields.Clear();
        _panMain.Children.Clear();
        _panMain.Children.Add(new MyHint
        {
            Text = text,
            Theme = warn ? MyHint.Themes.Yellow : MyHint.Themes.Blue,
            Margin = new Thickness(0, 0, 0, 12)
        });
    }

    private void ApplyRoot(PluginUiNodeDto root)
    {
        _fields.Clear();
        _panMain.Children.Clear();
        _panMain.Children.Add(RenderNode(root));
    }

    private void StartLiveRefresh()
    {
        if (_liveRefreshCancellation is not null)
            return;
        _liveRefreshCancellation = new CancellationTokenSource();
        UnhandledExceptionGuard.Observe(
            PollLiveContentAsync(_liveRefreshCancellation.Token),
            $"PluginUi.LiveRefresh.{_pageId}");
    }

    private void StopLiveRefresh()
    {
        CancellationTokenSource? cancellation = _liveRefreshCancellation;
        _liveRefreshCancellation = null;
        if (cancellation is null)
            return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private async Task PollLiveContentAsync(CancellationToken cancellationToken)
    {
        // Dynamic plugin pages can update from background room/session events. Poll only
        // while this page is attached; revision avoids rebuilding an unchanged visual tree.
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(750));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!PluginSidecarSupervisor.Instance.IsAvailable ||
                PluginSidecarSupervisor.Instance.Client is not { } client)
            {
                continue;
            }

            PluginSidecarResult page;
            try
            {
                page = await client.UiGetPageAsync(_pageId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                continue;
            }

            if (!page.Ok || page.Root is null || page.Revision == 0 || page.Revision == _revision)
                continue;

            _revision = page.Revision;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                PluginUiPageCache.SetRoot(_pageId, page.Root);
                ApplyRoot(page.Root);
            }, DispatcherPriority.Background, cancellationToken);

            if (!string.IsNullOrWhiteSpace(page.NavigateRoute))
                await DesktopHostNavigation.Instance.NavigateAsync(page.NavigateRoute, cancellationToken)
                    .ConfigureAwait(false);
        }
    }

    private Control RenderNode(PluginUiNodeDto node)
    {
        string kind = node.Kind ?? "stack";
        Control control = kind.ToLowerInvariant() switch
        {
            "card" => RenderCard(node),
            "toolbar" => RenderToolbar(node),
            "text" => RenderText(node),
            "muted" => CreateMuted(node.Text ?? ""),
            "hint" => new MyHint
            {
                Text = node.Text ?? "",
                Theme = MyHint.Themes.Yellow,
                Margin = new Thickness(0, 2, 0, 6)
            },
            "button" => RenderButton(node),
            "checkbox" => RenderCheckBox(node),
            "textbox" => RenderTextBox(node),
            "select" => RenderSelect(node),
            "list" => RenderList(node),
            "stack" => RenderStack(node),
            "row" => RenderRow(node),
            "settingsgroup" => RenderSettingsGroup(node),
            "settingscell" => RenderSettingsCell(node),
            "markdown" => new MyMarkdownViewer { Markdown = node.Text ?? "" },
            "progress" => RenderProgress(node),
            "slider" => RenderSlider(node),
            "spacer" => new Border { MinHeight = Math.Max(0, node.Number ?? 8) },
            _ => CreateMuted($"未知节点: {kind}")
        };
        ApplyCommon(control, node);
        return control;
    }

    private static TextBlock RenderText(PluginUiNodeDto node)
    {
        (double size, FontWeight weight, double opacity) = node.Style?.ToLowerInvariant() switch
        {
            "caption" => (12, FontWeight.Normal, 0.68),
            "subtitle" => (16, FontWeight.SemiBold, 0.92),
            "title" => (20, FontWeight.SemiBold, 1),
            "heading" => (26, FontWeight.Bold, 1),
            _ => (14, FontWeight.Normal, 0.9)
        };
        return new TextBlock
        {
            Text = node.Text ?? "",
            FontSize = size,
            FontWeight = weight,
            Opacity = opacity,
            TextWrapping = TextWrapping.Wrap
        };
    }

    private MyCard RenderCard(PluginUiNodeDto node)
    {
        MyCard card = new()
        {
            Title = node.Title ?? "",
            Margin = new Thickness(0, 0, 0, 15)
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
        StackPanel panel = new()
        {
            Spacing = node.Spacing ?? 0,
            Orientation = string.Equals(node.Orientation, "horizontal", StringComparison.OrdinalIgnoreCase)
                ? Orientation.Horizontal
                : Orientation.Vertical
        };
        if (!string.IsNullOrWhiteSpace(node.Title))
            panel.Children.Add(CreateSectionTitle(node.Title!));

        foreach (PluginUiNodeDto child in node.Children ?? [])
            panel.Children.Add(RenderNode(child));
        return panel;
    }

    private StackPanel RenderList(PluginUiNodeDto node)
    {
        StackPanel panel = new() { Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        if (!string.IsNullOrWhiteSpace(node.Title))
            panel.Children.Add(CreateSectionTitle(node.Title!));

        foreach (PluginUiNodeDto child in node.Children ?? [])
            panel.Children.Add(RenderNode(child));

        string key = "remote-data-chain-" + _pageId + "-" + Interlocked.Increment(ref _listAnimSeq);
        ControlVisualHelpers.AnimateListEntrance(panel, key);
        return panel;
    }

    private WrapPanel RenderToolbar(PluginUiNodeDto node)
    {
        WrapPanel bar = new()
        {
            Orientation = Orientation.Horizontal,
            ItemHeight = 36,
            Margin = new Thickness(0, 0, 0, 4)
        };
        foreach (PluginUiNodeDto child in node.Children ?? [])
        {
            Control control = RenderNode(child);
            // Inline controls in toolbar shouldn't stretch full card width.
            if (control is StackPanel stack)
                stack.HorizontalAlignment = HorizontalAlignment.Left;
            bar.Children.Add(control);
        }

        return bar;
    }

    /// <summary>iOS Settings–style grouped section (inset rounded list).</summary>
    private StackPanel RenderSettingsGroup(PluginUiNodeDto node)
    {
        StackPanel outer = new()
        {
            Spacing = 6,
            Margin = new Thickness(0, 0, 0, 16)
        };
        if (!string.IsNullOrWhiteSpace(node.Title))
        {
            outer.Children.Add(new TextBlock
            {
                Text = node.Title.ToUpperInvariant(),
                FontSize = 12,
                Opacity = 0.55,
                Margin = new Thickness(16, 0, 16, 0),
                FontWeight = FontWeight.SemiBold
            });
        }

        StackPanel cells = new() { Spacing = 0 };
        PluginUiNodeDto[] children = node.Children ?? [];
        for (int i = 0; i < children.Length; i++)
        {
            cells.Children.Add(RenderNode(children[i]));
            if (i < children.Length - 1)
            {
                cells.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(16, 0, 0, 0),
                    Background = new SolidColorBrush(Color.FromArgb(36, 128, 128, 128)),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                });
            }
        }

        outer.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(18, 128, 128, 128)),
            CornerRadius = new CornerRadius(12),
            BorderBrush = new SolidColorBrush(Color.FromArgb(28, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0, 2),
            Child = cells,
            ClipToBounds = true
        });
        return outer;
    }

    /// <summary>iOS Settings–style row: title/subtitle + optional trailing switch.</summary>
    private Border RenderSettingsCell(PluginUiNodeDto node)
    {
        Grid grid = new()
        {
            MinHeight = 44,
            Margin = new Thickness(16, 8, 12, 8)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        StackPanel left = new()
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (!string.IsNullOrWhiteSpace(node.Title))
        {
            left.Children.Add(new TextBlock
            {
                Text = node.Title,
                FontSize = 15,
                TextWrapping = TextWrapping.Wrap
            });
        }

        if (!string.IsNullOrWhiteSpace(node.Text))
        {
            left.Children.Add(new TextBlock
            {
                Text = node.Text,
                FontSize = 12,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap
            });
        }

        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        bool hasToggle = node.Checked.HasValue ||
                         string.Equals(node.ActionId, "permission.toggle", StringComparison.Ordinal) ||
                         string.Equals(node.ActionId, "cloud.setSection", StringComparison.Ordinal) ||
                         (node.ActionId?.StartsWith("safety.", StringComparison.Ordinal) ?? false) ||
                         (node.ActionId?.StartsWith("developer.", StringComparison.Ordinal) ?? false);

        if (hasToggle && !string.IsNullOrWhiteSpace(node.ActionId))
        {
            bool isChecked = node.Checked == true;
            if (string.Equals(node.Id, "host.SystemDebugMode", StringComparison.Ordinal))
                isChecked = DesktopHostDeveloperDiagnostics.Instance.IsEnabled;

            MyCheckBox box = new()
            {
                Checked = isChecked,
                Height = 22,
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = node.Enabled,
                // Empty label — title is on the left like iOS.
                Text = ""
            };
            string actionId = node.ActionId!;
            string? meta = node.Meta;
            box.Change += async (_, _) =>
            {
                await InvokeAsync(actionId, pluginId: meta, boolValue: box.Checked == true)
                    .ConfigureAwait(true);
            };
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
        }
        else if (node.Children is { Length: > 0 })
        {
            WrapPanel trailing = new()
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            foreach (PluginUiNodeDto child in node.Children)
            {
                if (string.Equals(child.Kind, "button", StringComparison.OrdinalIgnoreCase))
                    trailing.Children.Add(RenderButton(child, inRow: true));
            }

            Grid.SetColumn(trailing, 1);
            grid.Children.Add(trailing);
        }

        return new Border
        {
            Background = Brushes.Transparent,
            Child = grid,
            IsEnabled = node.Enabled
        };
    }

    private Border RenderRow(PluginUiNodeDto node)
    {
        Grid grid = new();
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        StackPanel left = new()
        {
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center
        };
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
            // Multi-line detail: first line metadata, rest as secondary lines.
            string[] lines = node.Text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                left.Children.Add(new TextBlock
                {
                    Text = lines[i],
                    FontSize = i == 0 ? 12 : 12,
                    Opacity = 0.75,
                    TextWrapping = TextWrapping.Wrap
                });
            }
        }

        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        WrapPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            ItemHeight = 35,
            Margin = new Thickness(12, 0, 0, 0)
        };
        foreach (PluginUiNodeDto child in node.Children ?? [])
        {
            if (string.Equals(child.Kind, "button", StringComparison.OrdinalIgnoreCase))
                actions.Children.Add(RenderButton(child, inRow: true));
        }

        if (actions.Children.Count > 0)
        {
            Grid.SetColumn(actions, 1);
            grid.Children.Add(actions);
        }

        return new Border
        {
            BorderBrush = RowBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10),
            Child = grid
        };
    }

    private MyButton RenderButton(PluginUiNodeDto node, bool inRow = false)
    {
        string label = node.Text ?? node.Title ?? "操作";
        MyButton button = new()
        {
            Text = label,
            MinWidth = inRow ? 72 : 90,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 6),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = node.Enabled,
            ColorType = ResolveButtonColor(node.ActionId, label, node.Style)
        };
        string? actionId = node.ActionId;
        string? meta = node.Meta;
        string? valueField = node.ValueField;
        string? metaField = node.MetaField;
        if (!string.IsNullOrWhiteSpace(actionId))
        {
            button.Click += async (_, _) =>
            {
                string? value = ResolveFieldValue(valueField);
                string? pluginId = ResolveFieldValue(metaField) ?? meta;
                await InvokeAsync(actionId!, pluginId: pluginId, value: value).ConfigureAwait(true);
            };
        }

        return button;
    }

    private static MyButton.ColorState ResolveButtonColor(string? actionId, string label, string? style = null)
    {
        if (string.Equals(style, "primary", StringComparison.OrdinalIgnoreCase))
            return MyButton.ColorState.Highlight;
        if (string.Equals(style, "danger", StringComparison.OrdinalIgnoreCase))
            return MyButton.ColorState.Red;
        if (string.Equals(style, "subtle", StringComparison.OrdinalIgnoreCase))
            return MyButton.ColorState.Gray;
        if (actionId is "catalog.uninstall" || label.Contains("卸载", StringComparison.Ordinal))
            return MyButton.ColorState.Red;
        if (actionId is "catalog.disable" || label is "禁用")
            return MyButton.ColorState.Gray;
        if (actionId is "market.installRemote" or "catalog.install" or "market.installListing"
            or "developer.verify" or "online.connect"
            || label is "获取" or "安装" or "更新" or "验证订单并启用" or "连接 PCL N 在线服务账户")
            return MyButton.ColorState.Highlight;
        return MyButton.ColorState.Normal;
    }

    private MyCheckBox RenderCheckBox(PluginUiNodeDto node)
    {
        bool isChecked = node.Checked == true;
        if (string.Equals(node.Id, "host.SystemDebugMode", StringComparison.Ordinal))
            isChecked = DesktopHostDeveloperDiagnostics.Instance.IsEnabled;

        MyCheckBox box = new()
        {
            Text = node.Text ?? node.Title ?? "",
            Checked = isChecked,
            Height = 22,
            Margin = new Thickness(0, 2, 0, 2),
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
        StackPanel panel = new()
        {
            Spacing = 4,
            Margin = new Thickness(0, 0, 12, 6),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        if (!string.IsNullOrWhiteSpace(node.Title))
            panel.Children.Add(CreateFieldLabel(node.Title!));

        MyTextBox box = new()
        {
            Text = node.Text ?? "",
            HintText = node.Placeholder ?? "",
            MinWidth = 220,
            Height = 32,
            MaxLength = node.Multiline ? 8000 : 200,
            AcceptsReturn = node.Multiline,
            TextWrapping = node.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            PasswordChar = node.Password ? '*' : '\0',
            IsEnabled = node.Enabled,
            UseExperimentalStyle = false
        };
        if (!string.IsNullOrWhiteSpace(node.Id))
            _fields[node.Id!] = () => box.Text;

        if (!string.IsNullOrWhiteSpace(node.ActionId) && !string.IsNullOrWhiteSpace(node.Meta))
        {
            string actionId = node.ActionId!;
            string elementId = node.Meta!;
            CancellationTokenSource? debounce = null;
            box.PropertyChanged += (_, args) =>
            {
                if (args.Property != TextBox.TextProperty)
                    return;
                debounce?.Cancel();
                debounce?.Dispose();
                debounce = new CancellationTokenSource();
                CancellationToken token = debounce.Token;
                _ = SendTextChangeAsync(actionId, elementId, box.Text ?? "", token);
            };
        }

        panel.Children.Add(box);
        return panel;
    }

    private StackPanel RenderSelect(PluginUiNodeDto node)
    {
        StackPanel panel = new()
        {
            Spacing = 4,
            Margin = new Thickness(0, 0, 12, 6),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        if (!string.IsNullOrWhiteSpace(node.Title))
            panel.Children.Add(CreateFieldLabel(node.Title!));

        ComboBox combo = new()
        {
            MinWidth = 150,
            Height = 32,
            IsEnabled = node.Enabled,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        int selectedIndex = 0;
        PluginUiOptionDto[] options = node.Options ?? [];
        for (int i = 0; i < options.Length; i++)
        {
            PluginUiOptionDto opt = options[i];
            combo.Items.Add(new SelectOptionItem(opt.Value ?? "", opt.Label ?? opt.Value ?? ""));
            if (string.Equals(opt.Value, node.Selected, StringComparison.OrdinalIgnoreCase))
                selectedIndex = i;
        }

        if (!string.IsNullOrWhiteSpace(node.Id))
        {
            _fields[node.Id!] = () =>
                combo.SelectedItem is SelectOptionItem item ? item.Value : node.Selected;
        }

        string? actionId = node.ActionId;
        string? valueField = node.ValueField;
        string? metaField = node.MetaField;
        string? staticMeta = node.Meta;
        bool selectionReady = false;
        if (!string.IsNullOrWhiteSpace(actionId))
        {
            combo.SelectionChanged += async (_, _) =>
            {
                if (!selectionReady)
                    return;

                string? value = ResolveFieldValue(valueField);
                string? pluginId = ResolveFieldValue(metaField) ?? staticMeta;
                if (string.Equals(actionId, "pclui.value", StringComparison.Ordinal) &&
                    combo.SelectedItem is SelectOptionItem selectedItem)
                {
                    value = selectedItem.Value;
                }
                if (!string.IsNullOrWhiteSpace(node.Id) &&
                    string.Equals(metaField, node.Id, StringComparison.OrdinalIgnoreCase) &&
                    combo.SelectedItem is SelectOptionItem current)
                {
                    pluginId = current.Value;
                }

                await InvokeAsync(actionId!, pluginId: pluginId, value: value).ConfigureAwait(true);
            };
        }

        if (combo.Items.Count > 0)
            combo.SelectedIndex = selectedIndex;
        selectionReady = true;

        panel.Children.Add(combo);
        return panel;
    }

    private static StackPanel RenderProgress(PluginUiNodeDto node)
    {
        StackPanel panel = new() { Spacing = 5 };
        if (!string.IsNullOrWhiteSpace(node.Text))
            panel.Children.Add(CreateFieldLabel(node.Text!));
        panel.Children.Add(new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Value = Math.Clamp(node.Number ?? 0, 0, 1),
            Height = 5
        });
        return panel;
    }

    private StackPanel RenderSlider(PluginUiNodeDto node)
    {
        int minimum = (int)Math.Round(node.Minimum ?? 0);
        int maximum = Math.Max(minimum + 1, (int)Math.Round(node.Maximum ?? 100));
        MySlider slider = new()
        {
            MaxValue = maximum - minimum,
            Value = Math.Clamp((int)Math.Round(node.Number ?? minimum), minimum, maximum) - minimum,
            ValueByKey = (uint)Math.Max(1, (int)Math.Round(node.Step ?? 1)),
            MinWidth = 140,
            IsEnabled = node.Enabled
        };
        if (!string.IsNullOrWhiteSpace(node.ActionId) && !string.IsNullOrWhiteSpace(node.Meta))
        {
            string actionId = node.ActionId!;
            string elementId = node.Meta!;
            slider.Change += async (_, user) =>
            {
                if (!user)
                    return;
                await InvokeAsync(
                        actionId,
                        pluginId: elementId,
                        value: (slider.Value + minimum).ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ConfigureAwait(true);
            };
        }

        StackPanel panel = new() { Spacing = 5 };
        if (!string.IsNullOrWhiteSpace(node.Title))
            panel.Children.Add(CreateFieldLabel(node.Title!));
        panel.Children.Add(slider);
        return panel;
    }

    private static void ApplyCommon(Control control, PluginUiNodeDto node)
    {
        control.IsVisible = node.Visible;
        control.IsEnabled = node.Enabled;
        if (node.Margin is { Length: >= 4 } margin)
            control.Margin = new Thickness(margin[0], margin[1], margin[2], margin[3]);
        if (node.Width is { } width)
            control.Width = width;
        if (node.MinWidth is { } minWidth)
            control.MinWidth = minWidth;
        if (node.MaxWidth is { } maxWidth)
            control.MaxWidth = maxWidth;
    }

    private async Task SendTextChangeAsync(
        string actionId,
        string elementId,
        string value,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(80, cancellationToken).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(
                () => InvokeAsync(actionId, pluginId: elementId, value: value),
                DispatcherPriority.Background,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke superseded this value.
        }
    }

    private static TextBlock CreateSectionTitle(string text) =>
        new()
        {
            Text = text,
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
            Margin = new Thickness(0, 8, 0, 4)
        };

    private static TextBlock CreateFieldLabel(string text) =>
        new()
        {
            Text = text,
            FontSize = 12,
            Opacity = 0.8
        };

    private static TextBlock CreateBodyText(string text, bool enabled = true) =>
        new()
        {
            Text = text,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            IsEnabled = enabled,
            Margin = new Thickness(0, 0, 0, 2)
        };

    private static TextBlock CreateMuted(string text, double fontSize = 12) =>
        new()
        {
            Text = text,
            FontSize = fontSize,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 2)
        };

    private string? ResolveFieldValue(string? fieldId)
    {
        if (string.IsNullOrWhiteSpace(fieldId))
            return null;
        return _fields.TryGetValue(fieldId, out Func<string?>? getter) ? getter() : null;
    }

    private static bool TracksHostTask(string actionId) =>
        actionId is "market.installRemote" or "catalog.install" or "market.installListing";

    private static bool QuietSuccessToast(string actionId) =>
        actionId is "page.refresh" or "catalog.refresh" or "market.searchOnline"
            or "safety.setPluginSafe" or "safety.setUiSafe" or "safety.setIsolation"
            or "developer.setMode" or "developer.setAllowUnsigned" or "developer.showSafety"
            or "developer.showUiPatches" or "developer.showCompatibility"
            or "developer.setDiagnostics" or "cloud.setSection";

    private async Task InvokeAsync(
        string actionId,
        string? pluginId = null,
        bool? boolValue = null,
        string? packagePath = null,
        string? value = null)
    {
        if (!PluginSidecarSupervisor.Instance.IsAvailable)
        {
            DesktopHostNotifications.Instance.ShowWarning("插件侧车未连接。");
            return;
        }

        IHostBackgroundTask? hostTask = null;
        try
        {
            if (TracksHostTask(actionId))
            {
                string title = actionId switch
                {
                    "market.installRemote" => "获取插件 " + ExtractPluginName(pluginId),
                    "market.installListing" => "安装本地市场插件",
                    _ => "安装插件包"
                };
                hostTask = DesktopHostBackgroundTasks.Instance.Begin(title, openTaskManager: true);
                hostTask.Report(new HostBackgroundTaskProgress("准备", "正在连接插件侧车…", 0.01));
            }

            IProgress<PluginSidecarProgress>? progress = hostTask is null
                ? null
                : new Progress<PluginSidecarProgress>(p =>
                {
                    hostTask.Report(new HostBackgroundTaskProgress(
                        p.Stage,
                        p.Detail ?? "",
                        p.Progress,
                        p.CompletedFiles,
                        p.TotalFiles,
                        p.SpeedBytesPerSecond));
                });

            PluginSidecarClient client = PluginSidecarSupervisor.Instance.Client!;
            PluginSidecarResult result = await client.UiInvokeActionAsync(
                    _pageId,
                    actionId,
                    value: value,
                    boolValue: boolValue,
                    packagePath: packagePath,
                    pluginId: pluginId,
                    progress: progress)
                .ConfigureAwait(true);

            if (result.ConfirmRequired)
            {
                hostTask?.Report(new HostBackgroundTaskProgress(
                    "等待确认",
                    "请在弹窗中确认必要权限…",
                    0.95));

                bool accepted = await DesktopHostNotifications.Instance.ConfirmAsync(
                        result.ConfirmTitle ?? "确认",
                        result.ConfirmBody ?? result.Message ?? "是否继续？",
                        result.ConfirmPrimary ?? "确定",
                        result.ConfirmSecondary ?? "取消",
                        isWarn: true)
                    .ConfigureAwait(true);
                if (!accepted)
                {
                    hostTask?.Fail("已取消", canceled: true);
                    // Still refresh so install-without-consent (pending) is visible in 已安装.
                    if (!string.IsNullOrWhiteSpace(result.FollowUpPluginId) ||
                        !string.IsNullOrWhiteSpace(result.Message))
                    {
                        PluginUiPageCache.Invalidate(_pageId);
                        DesktopHostNotifications.Instance.ShowInformation(
                            result.Message is { Length: > 0 } msg
                                ? msg + "（已跳过权限确认，可稍后在「已安装」中启用。）"
                                : "已取消权限确认。插件可能已下载，可在「已安装」中查看。");
                        await RefreshAsync(forceNetwork: true).ConfigureAwait(true);
                    }
                    else
                    {
                        DesktopHostNotifications.Instance.ShowInformation("已取消操作。");
                    }

                    return;
                }

                // Re-invoke with boolValue=true so sidecar treats this as user-confirmed consent.
                // Install→approve uses FollowUpPluginId (no package path) so we do not re-install.
                string? followPluginId = !string.IsNullOrWhiteSpace(result.FollowUpPluginId)
                    ? result.FollowUpPluginId
                    : pluginId;
                string? followPackage = string.IsNullOrWhiteSpace(result.FollowUpPluginId)
                    ? packagePath
                    : null;
                result = await client.UiInvokeActionAsync(
                        _pageId,
                        actionId,
                        value: value,
                        boolValue: true,
                        packagePath: followPackage,
                        pluginId: followPluginId,
                        progress: progress)
                    .ConfigureAwait(true);
            }
            else if (result.PickFolder)
            {
                string? path = await PickFolderAsync(result.PickFolderTitle).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(path))
                {
                    hostTask?.Fail("已取消选择目录", canceled: true);
                    return;
                }

                result = await client.UiInvokeActionAsync(
                        _pageId,
                        actionId,
                        value: value,
                        packagePath: path,
                        pluginId: pluginId,
                        progress: progress)
                    .ConfigureAwait(true);
            }
            else if (result.PickFilePatterns is { Length: > 0 })
            {
                string? path = await PickFileAsync(result.PickFileTitle, result.PickFilePatterns).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(path))
                {
                    hostTask?.Fail("已取消选择文件", canceled: true);
                    return;
                }

                result = await client.UiInvokeActionAsync(
                        _pageId,
                        actionId,
                        value: value,
                        packagePath: path,
                        pluginId: pluginId,
                        progress: progress)
                    .ConfigureAwait(true);
            }

            if (!string.IsNullOrWhiteSpace(result.OpenUrl) &&
                Uri.TryCreate(result.OpenUrl, UriKind.Absolute, out Uri? uri))
            {
                await DesktopHostUriLauncher.Instance.OpenAsync(uri).ConfigureAwait(true);
            }

            if (!string.IsNullOrWhiteSpace(result.NavigateRoute))
            {
                await DesktopHostNavigation.Instance.NavigateAsync(result.NavigateRoute)
                    .ConfigureAwait(true);
            }

            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                if (!result.Ok)
                    DesktopHostNotifications.Instance.ShowWarning(result.Message!);
                else if (!QuietSuccessToast(actionId))
                    DesktopHostNotifications.Instance.ShowInformation(result.Message!);
            }

            if (!string.IsNullOrWhiteSpace(result.HostBooleanKey) && result.HostBooleanValue is { } hostBool)
            {
                if (string.Equals(result.HostBooleanKey, "SystemDebugMode", StringComparison.Ordinal))
                    DesktopHostDeveloperDiagnostics.Instance.SetEnabled(hostBool);
                else
                    PortableLog.Warn("PluginSidecar", "Unknown hostBooleanKey: " + result.HostBooleanKey);
            }

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

            if (hostTask is not null)
            {
                if (result.Ok)
                    hostTask.Complete(result.Message ?? "完成");
                else
                    hostTask.Fail(result.Message ?? "失败");
            }

            if (result.Root is not null)
            {
                _revision = result.Revision;
                PluginUiPageCache.SetRoot(_pageId, result.Root);
                ApplyRoot(result.Root);
                return;
            }

            if (result.RefreshPage ||
                (result.Ok && !actionId.StartsWith("pclui.", StringComparison.Ordinal)))
            {
                PluginUiPageCache.Invalidate(_pageId);
                await RefreshAsync(forceNetwork: true).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            hostTask?.Fail(ex.Message);
            DesktopHostNotifications.Instance.ShowWarning(ex.Message);
        }
        finally
        {
            hostTask?.Dispose();
        }
    }

    private static string ExtractPluginName(string? pluginIdMeta)
    {
        if (string.IsNullOrWhiteSpace(pluginIdMeta))
            return "";
        int tab = pluginIdMeta.IndexOf('\t');
        return tab < 0 ? pluginIdMeta : pluginIdMeta[..tab];
    }

    private sealed record SelectOptionItem(string Value, string Label)
    {
        public override string ToString() => Label;
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

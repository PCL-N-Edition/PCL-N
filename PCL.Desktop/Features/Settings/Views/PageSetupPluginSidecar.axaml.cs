// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Hosting.PluginSidecar;

namespace PCL.Desktop.Features.Settings.Views;

/// <summary>
/// Host-owned settings page that drives the plugin sidecar over IPC (no sidecar UI window).
/// </summary>
public partial class PageSetupPluginSidecar : MyPageRight, IRefreshableSettingsPage
{
    private readonly TextBlock _statusText;
    private readonly StackPanel _pluginList;

    public PageSetupPluginSidecar()
    {
        AvaloniaXamlLoader.Load(this);
        PanScroll = this.FindControl<MyScrollViewer>("PanBack");
        _statusText = this.FindControl<TextBlock>("StatusText")
            ?? throw new InvalidOperationException("StatusText missing.");
        _pluginList = this.FindControl<StackPanel>("PluginList")
            ?? throw new InvalidOperationException("PluginList missing.");
        MyButton refresh = this.FindControl<MyButton>("RefreshButton")
            ?? throw new InvalidOperationException("RefreshButton missing.");
        MyButton start = this.FindControl<MyButton>("StartButton")
            ?? throw new InvalidOperationException("StartButton missing.");
        MyButton install = this.FindControl<MyButton>("InstallButton")
            ?? throw new InvalidOperationException("InstallButton missing.");

        refresh.Click += (_, _) => RefreshPage();
        start.Click += async (_, _) =>
        {
            _statusText.Text = "正在启动侧车…";
            bool ok = await PluginSidecarSupervisor.Instance.TryStartAsync().ConfigureAwait(true);
            _statusText.Text = ok ? "侧车已启动。" : "侧车启动失败（未找到可执行文件或握手失败）。";
            RefreshPage();
        };
        install.Click += async (_, _) => await InstallPnpAsync().ConfigureAwait(true);
        RefreshPage();
    }

    public void RefreshPage() => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        if (!PluginSidecarSupervisor.Instance.IsAvailable)
        {
            string? path = PluginSidecarPaths.ResolveExecutable();
            _statusText.Text = path is null
                ? "侧车未运行：未找到 PCL.Plugin.Sidecar。请构建侧车或设置 PCL_PLUGIN_SIDECAR_PATH。"
                : $"侧车未连接。已找到：{path}";
            _pluginList.Children.Clear();
            _pluginList.Children.Add(new TextBlock
            {
                Text = "连接侧车后将在此列出已安装插件。",
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        try
        {
            PluginSidecarClient client = PluginSidecarSupervisor.Instance.Client
                ?? throw new InvalidOperationException("client null");
            PluginSidecarResult ping = await client.PingAsync().ConfigureAwait(true);
            PluginSidecarResult catalog = await client.ListCatalogAsync().ConfigureAwait(true);
            _statusText.Text =
                $"侧车在线 · {catalog.Message ?? "ok"} · ping={(ping.Ok ? "ok" : "fail")} · protocol v2";

            _pluginList.Children.Clear();
            PluginSidecarCatalogEntry[] plugins = catalog.Plugins ?? [];
            if (plugins.Length == 0)
            {
                _pluginList.Children.Add(new TextBlock
                {
                    Text = "侧车目录中尚无已安装的 .pnp 插件。",
                    Opacity = 0.75,
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            foreach (PluginSidecarCatalogEntry entry in plugins)
                _pluginList.Children.Add(CreatePluginRow(entry));
        }
        catch (Exception ex)
        {
            _statusText.Text = "刷新失败：" + ex.Message;
        }
    }

    private Border CreatePluginRow(PluginSidecarCatalogEntry entry)
    {
        Border row = new()
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(6),
            Padding = new Avalonia.Thickness(12, 10)
        };

        Grid grid = new();
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        StackPanel text = new()
        {
            Spacing = 2,
            Children =
            {
                new TextBlock
                {
                    Text = entry.Name,
                    FontWeight = FontWeight.SemiBold,
                    FontSize = 14
                },
                new TextBlock
                {
                    Text = $"{entry.PluginId} · v{entry.Version ?? "—"} · {(entry.Enabled ? "已启用" : "已禁用")}",
                    FontSize = 12,
                    Opacity = 0.75,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        WrapPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(12, 0, 0, 0)
        };

        string pluginId = entry.PluginId;
        bool nextEnabled = !entry.Enabled;
        MyButton toggle = new()
        {
            Text = entry.Enabled ? "禁用" : "启用",
            MinWidth = 72,
            Height = 32,
            Margin = new Avalonia.Thickness(0, 0, 8, 0)
        };
        toggle.Click += async (_, _) =>
        {
            toggle.IsEnabled = false;
            try
            {
                PluginSidecarClient client = PluginSidecarSupervisor.Instance.Client
                    ?? throw new InvalidOperationException("client null");
                PluginSidecarResult result = await client.SetEnabledAsync(pluginId, nextEnabled).ConfigureAwait(true);
                _statusText.Text = result.Message ?? "已更新启用状态。";
            }
            catch (Exception ex)
            {
                _statusText.Text = "操作失败：" + ex.Message;
            }
            finally
            {
                RefreshPage();
            }
        };
        actions.Children.Add(toggle);

        MyButton uninstall = new()
        {
            Text = "卸载",
            MinWidth = 72,
            Height = 32
        };
        uninstall.Click += async (_, _) =>
        {
            uninstall.IsEnabled = false;
            try
            {
                PluginSidecarClient client = PluginSidecarSupervisor.Instance.Client
                    ?? throw new InvalidOperationException("client null");
                PluginSidecarResult result = await client.UninstallAsync(pluginId).ConfigureAwait(true);
                _statusText.Text = result.Message ?? "已卸载。";
            }
            catch (Exception ex)
            {
                _statusText.Text = "卸载失败：" + ex.Message;
            }
            finally
            {
                RefreshPage();
            }
        };
        actions.Children.Add(uninstall);

        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);
        row.Child = grid;
        return row;
    }

    private async Task InstallPnpAsync()
    {
        if (!PluginSidecarSupervisor.Instance.IsAvailable)
        {
            bool started = await PluginSidecarSupervisor.Instance.TryStartAsync().ConfigureAwait(true);
            if (!started)
            {
                _statusText.Text = "无法启动侧车，安装取消。";
                return;
            }
        }

        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is null)
        {
            _statusText.Text = "无法打开文件选择器。";
            return;
        }

        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "选择 PCL N 插件包 (.pnp)",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("PCL N Plugin") { Patterns = ["*.pnp"] }
                ]
            }).ConfigureAwait(true);
        if (files.Count == 0)
            return;

        string? path = files[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            _statusText.Text = "无法读取文件路径。";
            return;
        }

        try
        {
            PluginSidecarClient client = PluginSidecarSupervisor.Instance.Client
                ?? throw new InvalidOperationException("client null");
            PluginSidecarResult result = await client.InstallPnpAsync(path).ConfigureAwait(true);
            _statusText.Text = result.Message ?? (result.Ok ? "安装完成。" : "安装失败。");
        }
        catch (Exception ex)
        {
            _statusText.Text = "安装失败：" + ex.Message;
        }
        finally
        {
            RefreshPage();
        }
    }
}

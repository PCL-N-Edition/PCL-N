// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using PCL.Application.Instances;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Features.Launching;
using PCL.Desktop.Localization;

namespace PCL.Desktop.Features.Instances.Views;

public sealed partial class ServerCard : MyCard
{
    private const string DefaultServerIcon = "avares://PCL.Desktop/Assets/Legacy/Icons/DefaultServer.png";

    private readonly Image _serverIcon;
    private readonly TextBlock _serverName;
    private readonly TextBlock _serverPlayer;
    private readonly TextBlock _serverMotd;
    private readonly Border _selectionBack;
    private readonly Border _selectionIndicator;
    private readonly Grid _interactionSurface;
    private readonly StackPanel _actionPanel;
    private MinecraftServerEntry? _server;
    private string? _instanceDirectory;
    private bool _rowPressStarted;
    private bool _selected;

    internal string SelectionAnimationKey => $"ServerCard Selection {uuid}";

    public ServerCard()
    {
        AvaloniaXamlLoader.Load(this);
        _serverIcon = Required<Image>("ServerIcon");
        _serverName = Required<TextBlock>("ServerName");
        _serverPlayer = Required<TextBlock>("ServerPlayer");
        _serverMotd = Required<TextBlock>("ServerMotD");
        _selectionBack = Required<Border>("RectSelectionBack");
        _selectionIndicator = Required<Border>("RectSelectionIndicator");
        _interactionSurface = Required<Grid>("PanBack");
        _actionPanel = Required<StackPanel>("PanActions");
        _interactionSurface.PointerPressed += PanBack_PointerPressed;
        _interactionSurface.PointerReleased += PanBack_PointerReleased;
        SetDefaultIcon();
    }

    public event EventHandler<MinecraftServerEntry>? ConnectRequested;

    public event EventHandler<MinecraftServerEntry>? RefreshRequested;

    public event EventHandler<MinecraftServerEntry>? EditRequested;

    public event EventHandler<MinecraftServerEntry>? RemoveRequested;

    public event EventHandler? PinChanged;

    public event EventHandler? SelectionChanged;

    public MinecraftServerEntry? Server => _server;

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value)
                return;
            _selected = value;
            RefreshSelectionVisual();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void UpdateServerInfo(MinecraftServerEntry server, string? instanceDirectory = null)
    {
        _server = server;
        if (!string.IsNullOrWhiteSpace(instanceDirectory))
            _instanceDirectory = instanceDirectory;
        _serverName.Text = server.Name;
        ToolTip.SetTip(_serverName, server.Name);
        _serverPlayer.Text = server.Address;
        ToolTip.SetTip(_serverPlayer, server.Address);
        _serverMotd.Text = "服务器状态等待刷新";
        SetIcon(server.Icon);
        RefreshPinButton();
    }

    public void SetInstanceDirectory(string instanceDirectory)
    {
        _instanceDirectory = instanceDirectory;
        RefreshPinButton();
    }

    private void PanBack_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsEnabled ||
            !e.GetCurrentPoint(_interactionSurface).Properties.IsLeftButtonPressed ||
            IsActionSource(e.Source))
        {
            return;
        }

        _rowPressStarted = true;
        Focus();
        e.Handled = true;
    }

    private void PanBack_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        bool shouldToggle = _rowPressStarted && !IsActionSource(e.Source);
        _rowPressStarted = false;
        if (!shouldToggle)
            return;

        Selected = !Selected;
        e.Handled = true;
    }

    private bool IsActionSource(object? source) =>
        source is Control control &&
        (ReferenceEquals(control, _actionPanel) || control.GetVisualAncestors().Contains(_actionPanel));

    private void RefreshSelectionVisual()
    {
        ListSelectionMotion.AnimateRow(
            this,
            _selectionIndicator,
            Selected,
            SelectionAnimationKey,
            _selectionBack);
    }

    private void RefreshPinButton()
    {
        if (this.FindControl<MyIconButton>("BtnPin") is not { } pinButton)
            return;

        bool enabled = LaunchShortcutStore.IsFeatureEnabled() &&
                       !string.IsNullOrWhiteSpace(_instanceDirectory) &&
                       _server is not null;
        pinButton.IsVisible = enabled;
        if (!enabled || _server is null || string.IsNullOrWhiteSpace(_instanceDirectory))
            return;

        bool pinned = LaunchShortcutStore.IsPinned(
            LaunchShortcutKind.Server,
            _instanceDirectory,
            _server.Address);
        pinButton.SvgIcon = pinned ? "lucide/pin-off" : "lucide/pin";
        pinButton.ToolTip = AvaloniaLocalizationManager.GetText(
            pinned ? "Launch.Experimental.Shortcuts.Unpin" : "Launch.Experimental.Shortcuts.Pin",
            pinned ? "取消固定" : "固定到启动页快捷栏");
    }

    private void BtnPin_Click(object? sender, EventArgs e)
    {
        if (_server is null || string.IsNullOrWhiteSpace(_instanceDirectory))
            return;

        LaunchShortcutPin pin = new(
            LaunchShortcutPin.CreateId(LaunchShortcutKind.Server, _instanceDirectory, _server.Address),
            LaunchShortcutKind.Server,
            _instanceDirectory,
            string.IsNullOrWhiteSpace(_server.Name) ? _server.Address : _server.Name,
            _server.Address);
        LaunchShortcutStore.Toggle(pin);
        RefreshPinButton();
        PinChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetRefreshing()
    {
        _serverPlayer.Text = "正在连接";
        _serverMotd.Text = "正在获取服务器状态…";
    }

    public void UpdateStatus(MinecraftServerStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        _serverPlayer.Text = status.MaximumPlayers > 0
            ? $"{status.OnlinePlayers}/{status.MaximumPlayers} · {Math.Max(0d, status.Latency.TotalMilliseconds):N0} ms"
            : $"{Math.Max(0d, status.Latency.TotalMilliseconds):N0} ms";
        _serverMotd.Text = string.IsNullOrWhiteSpace(status.Description)
            ? status.VersionName
            : status.Description;
        ToolTip.SetTip(_serverMotd, _serverMotd.Text);
        if (!string.IsNullOrWhiteSpace(status.Icon))
            SetIcon(status.Icon);
    }

    public void UpdateStatusError(string message)
    {
        _serverPlayer.Text = "连接失败";
        _serverMotd.Text = string.IsNullOrWhiteSpace(message) ? "无法获取服务器状态" : message;
        ToolTip.SetTip(_serverMotd, _serverMotd.Text);
    }

    private void BtnConnect_Click(object? sender, EventArgs e)
    {
        if (_server is not null)
            ConnectRequested?.Invoke(this, _server);
    }

    private void BtnSetting_Click(object? sender, EventArgs e)
    {
        if (sender is MyIconButton { ContextMenu: { } menu } button)
            menu.Open(button);
    }

    private void BtnRefresh_Click(object? sender, RoutedEventArgs e)
    {
        if (_server is not null)
            RefreshRequested?.Invoke(this, _server);
    }

    private async void BtnCopy_Click(object? sender, RoutedEventArgs e)
    {
        if (_server is null)
            return;

        if (TopLevel.GetTopLevel(this)?.Clipboard is IClipboard clipboard)
            await clipboard.SetTextAsync(_server.Address).ConfigureAwait(true);
    }

    private void BtnEdit_Click(object? sender, RoutedEventArgs e)
    {
        if (_server is not null)
            EditRequested?.Invoke(this, _server);
    }

    private void BtnRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (_server is not null)
            RemoveRequested?.Invoke(this, _server);
    }

    private void SetIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon))
        {
            SetDefaultIcon();
            return;
        }

        try
        {
            string normalized = NormalizeIcon(icon);
            byte[] bytes = Convert.FromBase64String(normalized);
            _serverIcon.Source = new Bitmap(new MemoryStream(bytes));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException)
        {
            SetDefaultIcon();
        }
    }

    private static string NormalizeIcon(string icon)
    {
        const string prefix = "data:image/png;base64,";
        string trimmed = icon.Trim();
        return trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[prefix.Length..]
            : trimmed;
    }

    private void SetDefaultIcon()
    {
        using Stream stream = AssetLoader.Open(new Uri(DefaultServerIcon));
        _serverIcon.Source = new Bitmap(stream);
    }

    private T Required<T>(string name)
        where T : Control =>
        this.FindControl<T>(name)
        ?? throw new InvalidOperationException($"缺少服务器卡片控件：{name}");
}

// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net.Sockets;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
using PCL.Application.Instances;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Features.Launching.Views;

namespace PCL.Desktop.Features.Instances.Views;

public partial class PageInstanceServerRight : MyPageRight
{
    private readonly IMinecraftServerStatusService _statusService;
    private readonly List<ServerCard> _serverCards = [];
    private readonly HashSet<ServerCard> _selectedCards = [];
    private LaunchInstanceInfo? _instance;
    private bool _isUpdatingSelection;

    public PageInstanceServerRight()
        : this(new MinecraftServerStatusService())
    {
    }

    public PageInstanceServerRight(IMinecraftServerStatusService statusService)
    {
        _statusService = statusService ?? throw new ArgumentNullException(nameof(statusService));
        AvaloniaXamlLoader.Load(this);
        PanScroll = this.FindControl<MyScrollViewer>("PanBack");
        KeyDown += Page_KeyDown;
        if (this.FindControl<MyButton>("BtnSelectAll") is { } selectAll)
            selectAll.Click += BtnSelectAll_Click;
        if (this.FindControl<MyIconTextButton>("BtnSelectRefresh") is { } selectRefresh)
            selectRefresh.Click += BtnSelectRefresh_Click;
        if (this.FindControl<MyIconTextButton>("BtnSelectDelete") is { } selectDelete)
            selectDelete.Click += BtnSelectDelete_Click;
        if (this.FindControl<MyIconTextButton>("BtnSelectCancel") is { } selectCancel)
            selectCancel.Click += BtnSelectCancel_Click;
    }

    public event EventHandler<LaunchInstanceInfo>? RefreshRequested;

    public event EventHandler<LaunchInstanceInfo>? AddServerRequested;

    public event EventHandler<MinecraftServerEntry>? ConnectServerRequested;

    public event EventHandler<MinecraftServerEntry>? RefreshServerRequested;

    public event EventHandler<MinecraftServerEntry>? EditServerRequested;

    public event EventHandler<MinecraftServerEntry>? RemoveServerRequested;

    public event EventHandler<IReadOnlyList<MinecraftServerEntry>>? RemoveServersRequested;

    public void SetInstance(LaunchInstanceInfo instance)
    {
        _instance = instance;
        Reload(autoQueryStatus: true);
    }

    public void Reload() => Reload(autoQueryStatus: false);

    public void Reload(bool autoQueryStatus)
    {
        if (this.FindControl<StackPanel>("PanServers") is not { } panel)
            return;

        _serverCards.Clear();
        _selectedCards.Clear();
        UpdateSelectionBar();
        panel.Children.Clear();
        if (_instance is null)
        {
            SetEmptyVisible(true);
            return;
        }

        // servers.dat lives under the active game directory (isolated version folder or shared root).
        string gameDir = InstanceGameDirectory.ResolveAsync(_instance).GetAwaiter().GetResult();
        IReadOnlyList<MinecraftServerEntry> servers;
        try
        {
            servers = MinecraftServerListService.LoadAsync(gameDir).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            servers = [];
        }

        if (servers.Count == 0)
        {
            SetEmptyVisible(true);
            return;
        }

        SetEmptyVisible(false);
        MyCard card = new()
        {
            Title = "服务器列表",
            Margin = new Thickness(0d, 0d, 0d, 15d)
        };
        StackPanel stack = new()
        {
            Margin = new Thickness(20d, 40d, 18d, 15d)
        };
        List<ServerCard> createdCards = [];
        foreach (MinecraftServerEntry server in servers)
        {
            ServerCard serverCard = new();
            serverCard.UpdateServerInfo(server, _instance.InstanceDirectory);
            serverCard.RefreshRequested += (_, entry) => _ = RefreshServerCardAsync(serverCard, entry);
            serverCard.ConnectRequested += (_, entry) => ConnectServerRequested?.Invoke(this, entry);
            serverCard.EditRequested += (_, entry) => EditServerRequested?.Invoke(this, entry);
            serverCard.RemoveRequested += (_, entry) => RemoveServerRequested?.Invoke(this, entry);
            serverCard.SelectionChanged += ServerCard_SelectionChanged;
            stack.Children.Add(serverCard);
            createdCards.Add(serverCard);
            _serverCards.Add(serverCard);
        }
        card.Children.Add(stack);
        panel.Children.Add(card);

        // Auto-query status on first open / instance switch (same as the Refresh button path).
        if (autoQueryStatus)
        {
            foreach (ServerCard serverCard in createdCards)
            {
                if (serverCard.Server is { } entry)
                    _ = RefreshServerCardAsync(serverCard, entry);
            }
        }
    }

    private void BtnRefresh_Click(object? sender, EventArgs e)
    {
        if (_instance is not null)
            RefreshRequested?.Invoke(this, _instance);
        Reload(autoQueryStatus: true);
    }

    private void BtnAddServer_Click(object? sender, EventArgs e)
    {
        if (_instance is not null)
            AddServerRequested?.Invoke(this, _instance);
    }

    private void BtnSelectAll_Click(object? sender, EventArgs e) =>
        ChangeAllSelected(_selectedCards.Count < _serverCards.Count);

    private async void BtnSelectRefresh_Click(object? sender, EventArgs e)
    {
        ServerCard[] selected = _selectedCards.ToArray();
        ChangeAllSelected(false);
        await Task.WhenAll(selected.Select(card =>
                card.Server is { } server
                    ? RefreshServerCardAsync(card, server)
                    : Task.CompletedTask))
            .ConfigureAwait(true);
    }

    private void BtnSelectDelete_Click(object? sender, EventArgs e)
    {
        MinecraftServerEntry[] selected = _serverCards
            .Where(_selectedCards.Contains)
            .Select(static card => card.Server)
            .OfType<MinecraftServerEntry>()
            .ToArray();
        if (selected.Length > 0)
            RemoveServersRequested?.Invoke(this, selected);
    }

    private void BtnSelectCancel_Click(object? sender, EventArgs e) => ChangeAllSelected(false);

    private void ServerCard_SelectionChanged(object? sender, EventArgs e)
    {
        if (_isUpdatingSelection || sender is not ServerCard card)
            return;

        if (card.Selected)
            _selectedCards.Add(card);
        else
            _selectedCards.Remove(card);
        UpdateSelectionBar();
    }

    private void ChangeAllSelected(bool selected)
    {
        _isUpdatingSelection = true;
        try
        {
            _selectedCards.Clear();
            foreach (ServerCard card in _serverCards)
            {
                card.Selected = selected;
                if (selected)
                    _selectedCards.Add(card);
            }
        }
        finally
        {
            _isUpdatingSelection = false;
        }

        UpdateSelectionBar();
    }

    private void UpdateSelectionBar()
    {
        bool selected = _selectedCards.Count > 0;
        if (this.FindControl<MyCard>("CardSelect") is { } card)
            card.IsVisible = selected;
        if (this.FindControl<TextBlock>("LabSelect") is { } label && selected)
        {
            label.Text = GetText(
                "Instance.Server.SelectedCount",
                "已选择 {0} 个服务器",
                _selectedCards.Count.ToString(CultureInfo.CurrentCulture));
        }
        if (this.FindControl<StackPanel>("PanServers") is { } servers)
            servers.Margin = new Thickness(15d, 0d, 15d, selected ? 95d : 15d);
    }

    private void Page_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.A ||
            (!e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
             !e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        {
            return;
        }

        ChangeAllSelected(true);
        e.Handled = true;
    }

    private void SetEmptyVisible(bool isVisible)
    {
        if (this.FindControl<Control>("PanNoServer") is { } empty)
            empty.IsVisible = isVisible;
        if (this.FindControl<Control>("PanContent") is { } content)
            content.IsVisible = !isVisible;
        if (this.FindControl<Control>("PanServers") is { } servers)
            servers.IsVisible = !isVisible;
    }

    private async Task RefreshServerCardAsync(ServerCard card, MinecraftServerEntry server)
    {
        card.SetRefreshing();
        RefreshServerRequested?.Invoke(this, server);
        try
        {
            MinecraftServerStatus status = await _statusService.QueryAsync(server.Address).ConfigureAwait(true);
            if (card.IsAttachedToVisualTree() && Equals(card.Server, server))
                card.UpdateStatus(status);
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or
                                   OperationCanceledException or FormatException or JsonException or InvalidDataException)
        {
            if (card.IsAttachedToVisualTree() && Equals(card.Server, server))
                card.UpdateStatusError(ex is OperationCanceledException ? "连接超时" : ex.Message);
        }
    }

    private static string GetMinecraftRootFromInstance(LaunchInstanceInfo instance)
    {
        DirectoryInfo versionDirectory = new(instance.InstanceDirectory);
        DirectoryInfo? versionsDirectory = versionDirectory.Parent;
        if (versionsDirectory?.Parent is not null &&
            string.Equals(versionsDirectory.Name, "versions", StringComparison.OrdinalIgnoreCase))
        {
            return versionsDirectory.Parent.FullName;
        }

        return instance.InstanceDirectory;
    }

    private bool TryGetText(string key, out string value)
    {
        if (Avalonia.Application.Current?.TryGetResource(key, ActualThemeVariant, out object? appResource) == true &&
            appResource is string appText)
        {
            value = appText;
            return true;
        }

        if (TryGetResource(key, ActualThemeVariant, out object? localResource) && localResource is string localText)
        {
            value = localText;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private string GetText(string key, string fallback, params string[] args)
    {
        string format = TryGetText(key, out string value) ? value : fallback;
        return args.Length == 0
            ? format
            : string.Format(CultureInfo.CurrentCulture, format, args);
    }
}

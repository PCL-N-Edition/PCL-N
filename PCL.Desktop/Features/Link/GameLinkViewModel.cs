// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCL.Application.Link;
using PCL.Application.Settings;
using PCL.Core.Link.Scaffolding.Client.Models;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Hosting;
using PCL.Platform.Paths;

namespace PCL.Desktop.Features.Link;

public sealed partial class GameLinkViewModel : ObservableObject, IDisposable
{
    private const string EulaSetting = "LinkEula";
    private const string UserNameSetting = "LinkUsername";
    private readonly TerracottaLobbyService _service;
    private readonly LauncherSettingsStore _settingsStore;
    private bool _initialized;
    private bool _disposed;

    public GameLinkViewModel(TerracottaLobbyService service)
    {
        _service = service;
        DefaultPlatformPathProvider paths = new();
        _settingsStore = new LauncherSettingsStore(
            Path.Combine(paths.ApplicationDataDirectory, "PCL-N", "launcher-settings.json"));
        _service.StatusChanged += ServiceStatusChanged;
        _service.WorldsChanged += ServiceWorldsChanged;
        _service.PlayersChanged += ServicePlayersChanged;
        _service.ServerStopped += ServiceServerStopped;
    }

    public ObservableCollection<TerracottaWorld> Worlds { get; } = [];

    public ObservableCollection<PlayerProfile> Players { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAgreement))]
    [NotifyPropertyChangedFor(nameof(ShowSelection))]
    private bool _agreementAccepted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLoading))]
    [NotifyPropertyChangedFor(nameof(ShowSelection))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConnected))]
    [NotifyPropertyChangedFor(nameof(ShowSelection))]
    private bool _isConnected;

    [ObservableProperty]
    private string _statusTitle = "陶瓦联机";

    [ObservableProperty]
    private string _statusDetail = "正在读取联机设置";

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private MyHint.Themes _statusTheme = MyHint.Themes.Blue;

    [ObservableProperty]
    private string _userName = string.Empty;

    [ObservableProperty]
    private string _lobbyCode = string.Empty;

    [ObservableProperty]
    private string _manualPort = string.Empty;

    [ObservableProperty]
    private TerracottaWorld? _selectedWorld;

    [ObservableProperty]
    private string _connectionQuality = "正在检查";

    [ObservableProperty]
    private string _latencyText = "- ms";

    [ObservableProperty]
    private string _connectionType = "正在连接";

    [ObservableProperty]
    private string _roleText = "成员";

    [ObservableProperty]
    private string _localAddress = string.Empty;

    [ObservableProperty]
    private string _natSummary = "网络测试";

    public bool ShowAgreement => !AgreementAccepted && !IsBusy;

    public bool ShowLoading => IsBusy;

    public bool ShowSelection => AgreementAccepted && !IsBusy && !IsConnected;

    public bool ShowConnected => IsConnected && !IsBusy;

    public async Task InitializeAsync()
    {
        if (_initialized || _disposed)
            return;
        _initialized = true;
        LauncherSettings settings = (await _settingsStore.LoadAsync().ConfigureAwait(true)).Settings;
        AgreementAccepted = settings.BooleanOptions.TryGetValue(EulaSetting, out bool accepted) && accepted;
        UserName = settings.TextOptions.TryGetValue(UserNameSetting, out string? name) &&
                   !string.IsNullOrWhiteSpace(name)
            ? name
            : Environment.UserName;
        StatusTitle = "陶瓦联机";
        StatusDetail = AgreementAccepted ? "可以创建或加入房间" : "请先阅读并接受联机服务协议";
        if (AgreementAccepted)
            _ = RefreshWorldsAsync();
    }

    [RelayCommand]
    private async Task AcceptAgreementAsync()
    {
        AgreementAccepted = true;
        await SaveSettingsAsync().ConfigureAwait(true);
        _ = RefreshWorldsAsync();
    }

    [RelayCommand]
    private async Task DisableAgreementAsync()
    {
        if (IsConnected)
            await LeaveAsync().ConfigureAwait(true);
        AgreementAccepted = false;
        await SaveSettingsAsync().ConfigureAwait(true);
        StatusTitle = "联机功能已停用";
        StatusDetail = "重新接受协议后可以继续使用";
    }

    [RelayCommand]
    private async Task RefreshWorldsAsync()
    {
        if (IsBusy || !AgreementAccepted)
            return;
        await RunBusyAsync(async cancellationToken =>
        {
            IReadOnlyList<TerracottaWorld> worlds =
                await _service.DiscoverWorldsAsync(cancellationToken).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Replace(Worlds, worlds);
                SelectedWorld = Worlds.FirstOrDefault();
            });
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task TestNatAsync()
    {
        if (IsBusy)
            return;
        await RunBusyAsync(async cancellationToken =>
        {
            EasyTierNatStatus result = await _service.TestNatAsync(cancellationToken).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                NatSummary =
                    $"UDP {NatName(result.UdpNatType)} · TCP {NatName(result.TcpNatType)} · IPv6 {(result.SupportsIpv6 ? "可用" : "不可用")}";
            });
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CreateLobbyAsync()
    {
        if (IsBusy)
            return;
        if (string.IsNullOrWhiteSpace(UserName))
        {
            SetError("请输入联机显示名。");
            return;
        }

        int port;
        if (!string.IsNullOrWhiteSpace(ManualPort))
        {
            if (!int.TryParse(ManualPort, NumberStyles.None, CultureInfo.InvariantCulture, out port) ||
                port is <= 0 or > ushort.MaxValue)
            {
                SetError("手动端口必须是 1 到 65535 之间的整数。");
                return;
            }
        }
        else if (SelectedWorld is not null)
        {
            port = SelectedWorld.Port;
        }
        else
        {
            SetError("没有发现局域网世界，请刷新或输入手动端口。");
            return;
        }

        await SaveSettingsAsync().ConfigureAwait(true);
        await RunBusyAsync(
            cancellationToken => _service.CreateLobbyAsync(
                port,
                UserName,
                CreateInstallProgress(),
                cancellationToken),
            updateConnection: true).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task JoinLobbyAsync()
    {
        if (IsBusy)
            return;
        if (string.IsNullOrWhiteSpace(UserName))
        {
            SetError("请输入联机显示名。");
            return;
        }
        if (string.IsNullOrWhiteSpace(LobbyCode))
        {
            SetError("请输入房间码。");
            return;
        }

        await SaveSettingsAsync().ConfigureAwait(true);
        await RunBusyAsync(
            cancellationToken => _service.JoinLobbyAsync(
                LobbyCode.Trim(),
                UserName,
                CreateInstallProgress(),
                cancellationToken),
            updateConnection: true).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task PasteLobbyCodeAsync()
    {
        string? value = await DesktopHostClipboard.Instance.ReadTextAsync().ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(value))
            LobbyCode = value.Trim();
    }

    [RelayCommand]
    private void ClearLobbyCode() => LobbyCode = string.Empty;

    [RelayCommand]
    private async Task CopyLobbyCodeAsync()
    {
        if (!string.IsNullOrWhiteSpace(LobbyCode))
            await DesktopHostClipboard.Instance.WriteTextAsync(LobbyCode).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CopyLocalAddressAsync()
    {
        if (!string.IsNullOrWhiteSpace(LocalAddress))
            await DesktopHostClipboard.Instance.WriteTextAsync(LocalAddress).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task LeaveAsync()
    {
        if (IsBusy)
            return;
        await RunBusyAsync(
            cancellationToken => _service.LeaveLobbyAsync(),
            updateConnection: true).ConfigureAwait(true);
    }

    [RelayCommand]
    private static async Task OpenUrlAsync(string? address)
    {
        if (Uri.TryCreate(address, UriKind.Absolute, out Uri? uri))
            await DesktopHostUriLauncher.Instance.OpenAsync(uri).ConfigureAwait(false);
    }

    private async Task RunBusyAsync(
        Func<CancellationToken, Task> operation,
        bool updateConnection = false)
    {
        IsBusy = true;
        StatusTheme = MyHint.Themes.Blue;
        using CancellationTokenSource cancellation = new();
        try
        {
            await operation(cancellation.Token).ConfigureAwait(true);
            if (updateConnection)
                UpdateConnectionState();
        }
        catch (OperationCanceledException)
        {
            StatusTheme = MyHint.Themes.Yellow;
            StatusTitle = "操作已取消";
            StatusDetail = "联机状态未改变";
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
            if (updateConnection)
                UpdateConnectionState();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Progress<EasyTierInstallProgress> CreateInstallProgress() =>
        new Progress<EasyTierInstallProgress>(progress =>
        {
            StatusTitle = progress.Stage switch
            {
                "Download" => "正在下载 EasyTier",
                "Extract" => "正在解压 EasyTier",
                _ => "正在准备联机组件"
            };
            StatusDetail = progress.Detail;
            ProgressPercent = progress.Progress * 100;
        });

    private void ServiceStatusChanged(TerracottaStatus status) =>
        Dispatcher.UIThread.Post(() =>
        {
            StatusTitle = status.Stage;
            StatusDetail = status.Detail;
            ProgressPercent = status.Progress * 100;
            StatusTheme = status.State == TerracottaLobbyState.Error
                ? MyHint.Themes.Red
                : status.State == TerracottaLobbyState.Connected
                    ? MyHint.Themes.Blue
                    : MyHint.Themes.Yellow;
            if (status.State is TerracottaLobbyState.Connected or TerracottaLobbyState.Idle or TerracottaLobbyState.Error)
                UpdateConnectionState();
        });

    private void ServiceWorldsChanged(IReadOnlyList<TerracottaWorld> worlds) =>
        Dispatcher.UIThread.Post(() =>
        {
            Replace(Worlds, worlds);
            SelectedWorld = Worlds.FirstOrDefault();
        });

    private void ServicePlayersChanged(IReadOnlyList<PlayerProfile> players) =>
        Dispatcher.UIThread.Post(() => Replace(Players, players));

    private void ServiceServerStopped() =>
        Dispatcher.UIThread.Post(() =>
        {
            SetError("房主或联机核心已停止。");
            UpdateConnectionState();
        });

    private void UpdateConnectionState()
    {
        IsConnected = _service.State == TerracottaLobbyState.Connected;
        if (!IsConnected)
            return;
        LobbyCode = _service.LobbyCode ?? LobbyCode;
        RoleText = _service.IsHost ? "房主" : "成员";
        ConnectionType = _service.IsHost ? "本机创建的房间" : "EasyTier P2P / 中继";
        ConnectionQuality = _service.State == TerracottaLobbyState.Connected ? "连接正常" : "正在检查";
        LatencyText = _service.IsHost
            ? "本机"
            : _service.HostLatency is long latency
                ? $"{latency} ms"
                : "- ms";
        LocalAddress = _service.LocalMinecraftPort > 0
            ? $"127.0.0.1:{_service.LocalMinecraftPort}"
            : string.Empty;
        Replace(Players, _service.Players);
    }

    private async Task SaveSettingsAsync()
    {
        await _settingsStore.UpdateAsync(settings =>
        {
            Dictionary<string, bool> booleans = new(settings.BooleanOptions, StringComparer.OrdinalIgnoreCase)
            {
                [EulaSetting] = AgreementAccepted
            };
            Dictionary<string, string> texts = new(settings.TextOptions, StringComparer.OrdinalIgnoreCase)
            {
                [UserNameSetting] = UserName.Trim()
            };
            return settings with { BooleanOptions = booleans, TextOptions = texts };
        }).ConfigureAwait(true);
    }

    private void SetError(string detail)
    {
        StatusTheme = MyHint.Themes.Red;
        StatusTitle = "联机操作失败";
        StatusDetail = detail;
    }

    private static string NatName(int type) => type switch
    {
        0 or 1 => "开放",
        2 => "完全锥形",
        3 => "受限",
        4 => "端口受限",
        5 => "易打洞对称型",
        6 => "对称型",
        7 => "对称型防火墙",
        8 => "UDP 阻止",
        _ => "未知"
    };

    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> values)
    {
        collection.Clear();
        foreach (T value in values)
            collection.Add(value);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _service.StatusChanged -= ServiceStatusChanged;
        _service.WorldsChanged -= ServiceWorldsChanged;
        _service.PlayersChanged -= ServicePlayersChanged;
        _service.ServerStopped -= ServiceServerStopped;
        _settingsStore.Dispose();
        GC.SuppressFinalize(this);
    }
}

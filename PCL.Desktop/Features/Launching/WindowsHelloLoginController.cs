// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Controls;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Platform;

namespace PCL.Desktop.Features.Launching;

/// <summary>Shared Windows Hello gate for online account login surfaces.</summary>
internal sealed class WindowsHelloLoginController(
    Control owner,
    MyButton button,
    TextBlock status,
    string providerName,
    Action verified)
{
    private bool _available;
    private bool _loginBusy;
    private bool _verifying;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _available = await WindowsHelloAccountVerifier.IsAvailableAsync(cancellationToken);
        button.IsVisible = _available;
        button.IsEnabled = _available && !_loginBusy;
    }

    public void SetLoginBusy(bool busy)
    {
        _loginBusy = busy;
        button.IsEnabled = _available && !busy && !_verifying;
        if (busy)
            status.IsVisible = false;
    }

    public async Task VerifyAndContinueAsync(CancellationToken cancellationToken = default)
    {
        if (!_available || _loginBusy || _verifying)
            return;

        _verifying = true;
        button.IsEnabled = false;
        status.Text = "请在 Windows Hello 中验证身份…";
        status.IsVisible = true;
        try
        {
            WindowsHelloVerificationStatus result = await WindowsHelloAccountVerifier.VerifyAsync(
                owner,
                $"验证身份以登录 {providerName}",
                cancellationToken);
            switch (result)
            {
                case WindowsHelloVerificationStatus.Verified:
                    status.Text = "身份验证成功，正在继续登录…";
                    verified();
                    break;
                case WindowsHelloVerificationStatus.Canceled:
                    status.Text = "已取消 Windows Hello 验证。";
                    break;
                case WindowsHelloVerificationStatus.Unavailable:
                    _available = false;
                    button.IsVisible = false;
                    status.Text = "此设备未配置 Windows Hello。";
                    break;
                default:
                    status.Text = "Windows Hello 验证失败，请重试。";
                    break;
            }
        }
        finally
        {
            _verifying = false;
            button.IsEnabled = _available && !_loginBusy;
        }
    }
}

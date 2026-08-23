// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PCL.Desktop.Controls.Legacy;

namespace PCL.Desktop.Features.Launching.Views;

public partial class PageLoginMs : StackPanel, PageLaunchLeft.ILoginPage
{
    private readonly WindowsHelloLoginController _windowsHello;

    public PageLoginMs()
    {
        AvaloniaXamlLoader.Load(this);
        _windowsHello = new WindowsHelloLoginController(
            this,
            this.FindControl<MyButton>("BtnWindowsHello")!,
            this.FindControl<TextBlock>("LabWindowsHelloStatus")!,
            "Microsoft",
            RequestLogin);
        _ = _windowsHello.InitializeAsync();
    }

    public bool IsLoggingIn { get; private set; }

    public event EventHandler? BackRequested;

    public event EventHandler? LoginRequested;

    public event EventHandler? PurchaseRequested;

    public event EventHandler? WebsiteRequested;

    public void Reload()
    {
        if (!IsLoggingIn)
            ResetLoginButton();
    }

    public void StartLogin()
    {
        IsLoggingIn = true;
        _windowsHello.SetLoginBusy(true);
        if (this.FindControl<MyButton>("BtnLogin") is { } login)
        {
            login.IsEnabled = false;
            login.Text = "0 %";
        }
        if (this.FindControl<MyTextButton>("BtnBack") is { } back)
            back.IsVisible = false;
    }

    public void UpdateProgress(double progress)
    {
        if (this.FindControl<MyButton>("BtnLogin") is { } login)
            login.Text = Math.Clamp(progress, 0d, 1d).ToString("P0", System.Globalization.CultureInfo.CurrentCulture);
    }

    public void FinishLogin()
    {
        IsLoggingIn = false;
        ResetLoginButton();
    }

    public void RequestLogin()
    {
        if (IsLoggingIn)
            return;

        StartLogin();
        LoginRequested?.Invoke(this, EventArgs.Empty);
    }

    private void BtnBackClick(object? sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private void BtnLoginClick(object? sender, EventArgs e) => RequestLogin();

    private void BtnWindowsHelloClick(object? sender, EventArgs e) =>
        _ = _windowsHello.VerifyAndContinueAsync();

    private void BtnPurchaseClick(object? sender, RoutedEventArgs e) => PurchaseRequested?.Invoke(this, EventArgs.Empty);

    private void BtnWebsiteClick(object? sender, RoutedEventArgs e) => WebsiteRequested?.Invoke(this, EventArgs.Empty);

    private void ResetLoginButton()
    {
        _windowsHello.SetLoginBusy(false);
        if (this.FindControl<MyButton>("BtnLogin") is { } login)
        {
            login.IsEnabled = true;
            login.Text = "开始登录";
        }
        if (this.FindControl<MyTextButton>("BtnBack") is { } back)
            back.IsVisible = true;
    }
}

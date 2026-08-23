// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Controls;
using PCL.Core.Logging;

namespace PCL.Desktop.Platform;

internal enum WindowsHelloVerificationStatus
{
    Unavailable,
    Verified,
    Canceled,
    Failed
}

/// <summary>
/// Windows-only account consent gate. The portable build deliberately keeps
/// this API unavailable; official Windows artifacts opt into it with
/// <c>PclWindowsHello=true</c>.
/// </summary>
internal static class WindowsHelloAccountVerifier
{
#if PCL_WINDOWS_HELLO
    public const bool IsCompiled = true;
#else
    public const bool IsCompiled = false;
#endif

    public static async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (!IsCompiled || !OperatingSystem.IsWindows())
            return false;

#if PCL_WINDOWS_HELLO
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Windows.Security.Credentials.UI.UserConsentVerifierAvailability availability =
                await Windows.Security.Credentials.UI.UserConsentVerifier.CheckAvailabilityAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return availability == Windows.Security.Credentials.UI.UserConsentVerifierAvailability.Available;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            PortableLog.Debug("WindowsHello", $"无法检查 Windows Hello 可用性：{exception.Message}");
            return false;
        }
#else
        await Task.CompletedTask;
        return false;
#endif
    }

    public static async Task<WindowsHelloVerificationStatus> VerifyAsync(
        Control owner,
        string message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (!IsCompiled || !OperatingSystem.IsWindows())
            return WindowsHelloVerificationStatus.Unavailable;

#if PCL_WINDOWS_HELLO
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Window? window = TopLevel.GetTopLevel(owner) as Window;
            nint hwnd = window?.TryGetPlatformHandle()?.Handle ?? 0;
            if (hwnd == 0)
                return WindowsHelloVerificationStatus.Unavailable;

            Windows.Security.Credentials.UI.UserConsentVerificationResult result =
                await Windows.Security.Credentials.UI.UserConsentVerifierInterop
                    .RequestVerificationForWindowAsync(hwnd, message);
            cancellationToken.ThrowIfCancellationRequested();
            return result switch
            {
                Windows.Security.Credentials.UI.UserConsentVerificationResult.Verified =>
                    WindowsHelloVerificationStatus.Verified,
                Windows.Security.Credentials.UI.UserConsentVerificationResult.Canceled =>
                    WindowsHelloVerificationStatus.Canceled,
                Windows.Security.Credentials.UI.UserConsentVerificationResult.DeviceNotPresent or
                Windows.Security.Credentials.UI.UserConsentVerificationResult.NotConfiguredForUser or
                Windows.Security.Credentials.UI.UserConsentVerificationResult.DisabledByPolicy =>
                    WindowsHelloVerificationStatus.Unavailable,
                _ => WindowsHelloVerificationStatus.Failed
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            PortableLog.Warn(exception, "WindowsHello", "Windows Hello 账户验证失败。");
            return WindowsHelloVerificationStatus.Failed;
        }
#else
        await Task.CompletedTask;
        return WindowsHelloVerificationStatus.Unavailable;
#endif
    }
}

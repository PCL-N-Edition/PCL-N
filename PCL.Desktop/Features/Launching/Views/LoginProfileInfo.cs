// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Desktop.Localization;

namespace PCL.Desktop.Features.Launching.Views;

public sealed record LoginProfileInfo(
    string Username,
    string Info,
    LaunchLoginProfileKind Kind,
    string Uuid = "",
    string Logo = "",
    string SvgIcon = "lucide/user",
    string? SkinAddress = null,
    string AuthServer = "",
    string AccessToken = "",
    string RefreshToken = "",
    string ClientToken = "",
    string ProviderAccessToken = "",
    long ProviderTokenExpiresAtUnix = 0)
{
    public string DisplayInfo => FormatDisplayInfo(Info, Kind, AuthServer);

    public bool UsesYggdrasil =>
        Kind is LaunchLoginProfileKind.ThirdParty or
            LaunchLoginProfileKind.LittleSkin or
            LaunchLoginProfileKind.NCloud;

    public static string FormatDisplayInfo(
        string info,
        LaunchLoginProfileKind kind,
        string? authServer = null)
    {
        if (kind != LaunchLoginProfileKind.ThirdParty)
            return info;

        string baseName = AvaloniaLocalizationManager.GetText(
            "Launch.Account.Type.ThirdParty",
            "第三方");
        string serverLabel = GetServerLabel(authServer);
        if (string.IsNullOrWhiteSpace(serverLabel))
        {
            serverLabel = info?.Trim() ?? string.Empty;
            int separator = serverLabel.LastIndexOf('·');
            if (separator >= 0 && separator + 1 < serverLabel.Length)
                serverLabel = serverLabel[(separator + 1)..].Trim();
        }

        return string.IsNullOrWhiteSpace(serverLabel) ||
               string.Equals(serverLabel, baseName, StringComparison.OrdinalIgnoreCase)
            ? baseName
            : baseName + " · " + serverLabel;
    }

    private static string GetServerLabel(string? authServer)
    {
        if (string.IsNullOrWhiteSpace(authServer))
            return string.Empty;

        string normalized = authServer.Trim();
        if (!normalized.Contains("://", StringComparison.Ordinal))
            normalized = "https://" + normalized;
        return Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri)
            ? uri.Host
            : authServer.Trim().TrimEnd('/');
    }

    /// <summary>True when a skin texture can be shown (remote, local, or offline default).</summary>
    public bool HasSkin =>
        !string.IsNullOrWhiteSpace(SkinAddress) ||
        File.Exists(Logo ?? string.Empty) ||
        Kind == LaunchLoginProfileKind.Offline ||
        (!string.IsNullOrWhiteSpace(Uuid) &&
         (Kind == LaunchLoginProfileKind.Microsoft || !string.IsNullOrWhiteSpace(AuthServer)));

    /// <summary>Full skin PNG / session profile URL for MySkin layered head (WPF Address).</summary>
    public string DisplaySkinAddress
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SkinAddress))
                return SkinAddress.Trim();

            // Offline: WPF loads built-in Steve/Alex default by UUID parity (McSkinSex).
            if (Kind == LaunchLoginProfileKind.Offline)
                return ResolveOfflineDefaultSkinAddress(Uuid);

            // Third-party: Authlib sessionserver. Microsoft: Mojang sessionserver.
            return PCL.Desktop.Controls.Legacy.MySkin.ResolveSkinAddress(
                skinAddress: null,
                uuid: Uuid,
                authServer: UsesYggdrasil ? AuthServer : null);
        }
    }

    /// <summary>
    /// Minecraft offline default model: Steve if UUID parity bits even, Alex if odd (WPF ModSkin.McSkinSex).
    /// </summary>
    public static string ResolveOfflineDefaultSkinAddress(string? uuid)
    {
        string model = ResolveOfflineDefaultModel(uuid);
        return "avares://PCL.Desktop/Assets/Legacy/Skins/" + model + ".png";
    }

    public static string ResolveOfflineDefaultModel(string? uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid))
            return "Steve";

        string normalized = new string(uuid.Where(static ch => ch is not ('-' or ' ')).ToArray());
        if (normalized.Length != 32)
            return "Steve";

        try
        {
            int a = Convert.ToInt32(normalized[7].ToString(), 16);
            int b = Convert.ToInt32(normalized[15].ToString(), 16);
            int c = Convert.ToInt32(normalized[23].ToString(), 16);
            int d = Convert.ToInt32(normalized[31].ToString(), 16);
            return ((a ^ b ^ c ^ d) % 2) != 0 ? "Alex" : "Steve";
        }
        catch
        {
            return "Steve";
        }
    }

    /// <summary>List logo: real skin / session / offline default (async crop in MyListItem).</summary>
    public string DisplayHeadAddress
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Logo) &&
                (Logo.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                 Logo.StartsWith("avares://", StringComparison.OrdinalIgnoreCase) ||
                 File.Exists(Logo)))
            {
                return Logo;
            }

            return DisplaySkinAddress;
        }
    }

    /// <summary>Only show SVG when no skin address is available for the list thumbnail.</summary>
    public string ListSvgIcon =>
        string.IsNullOrWhiteSpace(DisplayHeadAddress) ? SvgIcon : string.Empty;
}

public enum LaunchLoginProfileKind
{
    Microsoft,
    ThirdParty,
    Offline,
    LittleSkin,
    NCloud
}

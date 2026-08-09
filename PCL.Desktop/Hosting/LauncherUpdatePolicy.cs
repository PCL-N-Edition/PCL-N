// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Settings;
using PCL.Application.Updates;
using PCL.Core.App;

namespace PCL.Desktop.Hosting;

internal readonly record struct LauncherUpdatePolicy(UpdateChannel Channel, int Mode)
{
    public const string ChannelSettingKey = "SystemUpdateChannel";
    public const string ModeSettingKey = "SystemUpdateMode";

    public static LauncherUpdatePolicy Resolve(
        LauncherSettings settings,
        string buildConfiguration,
        LauncherInstallationContext? installation = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        int defaultChannel = buildConfiguration.Trim().ToUpperInvariant() switch
        {
            "BETA" => 1,
            "CI" or "DEV" => 2,
            _ => 0
        };
        int channelIndex = settings.TryGetIntegerOption(ChannelSettingKey, out int configuredChannel)
            ? Math.Clamp(configuredChannel, 0, 2)
            : defaultChannel;
        installation ??= LauncherInstallationContext.Detect();
        if (channelIndex == 2 && !installation.SupportsCiChannel)
        {
            channelIndex = buildConfiguration.Trim().ToUpperInvariant() is "BETA" or "CI" or "DEV"
                ? 1
                : 0;
        }
        int mode = Math.Clamp(
            settings.GetIntegerOption(
                ModeSettingKey,
                LauncherSettingDefaults.GetInteger(ModeSettingKey, 1)),
            0,
            3);

        return new LauncherUpdatePolicy(
            channelIndex switch
            {
                1 => UpdateChannel.Beta,
                2 => UpdateChannel.CI,
                _ => UpdateChannel.Release
            },
            mode);
    }
}

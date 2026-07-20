// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.Desktop.Platform;

internal static class DesktopPlatformApi
{
    public static bool IsSupportedDesktopPlatform =>
        OperatingSystem.IsWindows() ||
        OperatingSystem.IsLinux() ||
        OperatingSystem.IsMacOS();

    public static bool IsMacOsDesktop => OperatingSystem.IsMacOS();

    /// <summary>True when caption controls should use left traffic lights.</summary>
    public static bool UsesMacOsTrafficLights => IsMacOsDesktop;

    public static string CreateSingleInstanceMutexName(string suffix) =>
        OperatingSystem.IsWindows()
            ? $@"Local\PCLN.Desktop.{suffix}"
            : $"PCLN.Desktop.{suffix}";
}

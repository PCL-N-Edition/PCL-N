// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.Desktop.Diagnostics;

internal readonly record struct UltraLowPowerActivity(
    bool HasActiveTask,
    bool IsLaunching,
    bool IsLoggingIn)
{
    public bool HasForegroundWork => HasActiveTask || IsLaunching || IsLoggingIn;
}

internal static class UltraLowPowerPolicy
{
    public static bool CanEnter(
        bool enabled,
        bool isWindowActive,
        UltraLowPowerActivity activity) =>
        enabled && !isWindowActive && !activity.HasForegroundWork;
}

// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Updates;

namespace PCL.Desktop.Hosting;

internal static class PclLauncherBuildIdentity
{
    public static LauncherBuildIdentity Current { get; } = Create();

    private static LauncherBuildIdentity Create()
    {
        string runtime = PclBuildInfo.RuntimeVariant.StartsWith("NoRuntime", StringComparison.OrdinalIgnoreCase)
            ? "NoRuntime"
            : "SelfContained";
        bool includesPlugin = PclBuildInfo.IncludesPlugin ||
            PclBuildInfo.RuntimeVariant.Contains("WithPlugin", StringComparison.OrdinalIgnoreCase);
        string plugin = includesPlugin ? "WithPlugin" : "NoPlugin";
        return new LauncherBuildIdentity(
            PclMetadata.Current.DisplayVersion,
            LauncherUpdateService.ResolveRuntimeId(),
            runtime + "_" + plugin,
            PclMetadata.Current.UpdateConfiguration);
    }
}

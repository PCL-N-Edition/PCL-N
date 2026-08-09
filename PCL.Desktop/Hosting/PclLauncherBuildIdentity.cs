// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Updates;

namespace PCL.Desktop.Hosting;

internal static class PclLauncherBuildIdentity
{
    public static LauncherBuildIdentity Current { get; } = Create();

    private static LauncherBuildIdentity Create()
    {
        // NativeAOT host packages differ only in whether the plugin sidecar
        // embeds CoreCLR (SelfContained) or uses an installed runtime (NoRuntime).
        string runtime = PclBuildInfo.RuntimeVariant.StartsWith("NoRuntime", StringComparison.OrdinalIgnoreCase)
            ? "NoRuntime"
            : "SelfContained";
        LauncherInstallationContext installation = LauncherInstallationContext.Detect();
        LauncherDistributionLayout layout = OperatingSystem.IsWindows() &&
                                            installation.Kind == LauncherInstallationKind.Portable
            ? LauncherDistributionLayout.SingleFile
            : LauncherDistributionLayout.Scatter;
        return new LauncherBuildIdentity(
            PclMetadata.Current.DisplayVersion,
            LauncherUpdateService.ResolveRuntimeId(),
            runtime,
            PclMetadata.Current.UpdateConfiguration)
        {
            DistributionLayout = layout
        };
    }
}

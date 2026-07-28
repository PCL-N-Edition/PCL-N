// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.Desktop.Hosting.PluginSidecar;

internal static class PluginSidecarPaths
{
    public static string ExecutableFileName =>
        OperatingSystem.IsWindows() ? "PCL.Plugin.Sidecar.exe" : "PCL.Plugin.Sidecar";

    /// <summary>
    /// Resolve sidecar executable path. Order:
    /// 1) PCL_PLUGIN_SIDECAR_PATH env
    /// 2) {appBase}/sidecar/PCL.Plugin.Sidecar(.exe)
    /// 3) {appBase}/PCL.Plugin.Sidecar(.exe)
    /// 4) dev: repo PCL.Plugin/PCL.Plugin.Sidecar/bin/...
    /// </summary>
    public static string? ResolveExecutable()
    {
        string? env = Environment.GetEnvironmentVariable("PCL_PLUGIN_SIDECAR_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return Path.GetFullPath(env);

        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, "sidecar", ExecutableFileName),
            Path.Combine(baseDir, ExecutableFileName)
        ];

        foreach (string path in candidates)
        {
            if (File.Exists(path))
                return Path.GetFullPath(path);
        }

        // Local dev layout: host repo / PCL.Plugin / PCL.Plugin.Sidecar / bin
        string? repoHint = FindAncestorWithPluginSidecarProject(baseDir);
        if (repoHint is not null)
        {
            foreach (string config in new[] { "Debug", "Release" })
            {
                string dev = Path.Combine(
                    repoHint,
                    "PCL.Plugin",
                    "PCL.Plugin.Sidecar",
                    "bin",
                    config,
                    "net10.0",
                    ExecutableFileName);
                if (File.Exists(dev))
                    return Path.GetFullPath(dev);
            }
        }

        return null;
    }

    private static string? FindAncestorWithPluginSidecarProject(string start)
    {
        DirectoryInfo? dir = new(start);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            string csproj = Path.Combine(dir.FullName, "PCL.Plugin", "PCL.Plugin.Sidecar", "PCL.Plugin.Sidecar.csproj");
            if (File.Exists(csproj))
                return dir.FullName;
        }

        return null;
    }
}

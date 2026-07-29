// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Desktop.Paths;

namespace PCL.Desktop.Hosting.PluginSidecar;

internal static class PluginSidecarPaths
{
    public static string ExecutableFileName =>
        OperatingSystem.IsWindows() ? "PCL.Plugin.Sidecar.exe" : "PCL.Plugin.Sidecar";

    /// <summary>
    /// Resolve sidecar executable path. Order:
    /// 1) PCL_PLUGIN_SIDECAR_PATH env
    /// 2) Extracted embedded payload under config/data dir
    /// 3) {hostDir}/sidecar/… and {base}/sidecar/…
    /// 4) dev: repo PCL.Plugin/PCL.Plugin.Sidecar/bin/...
    /// </summary>
    public static string? ResolveExecutable()
    {
        string? env = Environment.GetEnvironmentVariable("PCL_PLUGIN_SIDECAR_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return Path.GetFullPath(env);

        // Prefer payload already extracted into the active configuration directory.
        try
        {
            string dataRoot = LauncherPathLayout.ResolveDataDirectory();
            string runtimeRoot = Path.Combine(
                dataRoot,
                PclEmbeddedPluginSidecar.RelativeRuntimeFolder.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(runtimeRoot))
            {
                foreach (string dir in Directory.EnumerateDirectories(runtimeRoot))
                {
                    string candidate = Path.Combine(dir, ExecutableFileName);
                    if (File.Exists(candidate) && File.Exists(Path.Combine(dir, ".extracted")))
                        return Path.GetFullPath(candidate);
                }
            }
        }
        catch
        {
            // fall through
        }

        string hostDir = LauncherPathLayout.GetHostDirectory();
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(hostDir, "sidecar", ExecutableFileName),
            Path.Combine(hostDir, ExecutableFileName),
            Path.Combine(baseDir, "sidecar", ExecutableFileName),
            Path.Combine(baseDir, ExecutableFileName)
        ];

        foreach (string path in candidates)
        {
            if (File.Exists(path))
                return Path.GetFullPath(path);
        }

        // Local dev layout: host repo / PCL.Plugin / PCL.Plugin.Sidecar / bin
        string? repoHint = FindAncestorWithPluginSidecarProject(baseDir)
            ?? FindAncestorWithPluginSidecarProject(hostDir);
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

    /// <summary>
    /// Ensure embedded sidecar is extracted (if present), then resolve the executable.
    /// </summary>
    public static async Task<string?> ResolveExecutableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            string? extracted = await PclEmbeddedPluginSidecar.EnsureExtractedAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(extracted) && File.Exists(extracted))
                return extracted;
        }
        catch
        {
            // fall through to loose/dev paths
        }

        return ResolveExecutable();
    }

    private static string? FindAncestorWithPluginSidecarProject(string start)
    {
        try
        {
            DirectoryInfo? dir = new(start);
            for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            {
                string csproj = Path.Combine(
                    dir.FullName,
                    "PCL.Plugin",
                    "PCL.Plugin.Sidecar",
                    "PCL.Plugin.Sidecar.csproj");
                if (File.Exists(csproj))
                    return dir.FullName;
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }
}

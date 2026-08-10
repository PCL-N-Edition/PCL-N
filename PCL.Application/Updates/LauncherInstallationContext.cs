// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.Application.Updates;

/// <summary>How the currently running launcher was delivered to the device.</summary>
public enum LauncherInstallationKind
{
    Portable,
    WindowsInstaller,
    MacApplicationBundle,
    DebianPackage,
    RpmPackage,
    AppImage,
    LinuxPackage,
    Scatter
}

/// <summary>
/// Describes whether the running distribution can safely use the legacy raw
/// executable replacement updater. Package-managed and signed bundles must be
/// updated by installing a new package instead.
/// </summary>
public sealed record LauncherInstallationContext(
    LauncherInstallationKind Kind,
    bool SupportsInPlaceUpdate,
    string DisplayName)
{
    public const string InstallKindEnvironmentVariable = "PCL_N_INSTALL_KIND";
    public const string InstallKindMarkerFileName = "pcln-install-kind";

    /// <summary>
    /// CI is a rolling developer channel for the Windows single-file portable
    /// build only. Expanded scatter trees and package-managed installs stay on
    /// versioned Release/Beta updates.
    /// </summary>
    public bool SupportsCiChannel => Kind == LauncherInstallationKind.Portable;

    public static LauncherInstallationContext Detect(string? executablePath = null)
    {
        string path = executablePath ?? Environment.ProcessPath ?? string.Empty;
        string? launcherRoot = Environment.GetEnvironmentVariable("PCL_LAUNCHER_ROOT");
        string? marker = null;
        foreach (string directory in CandidateMarkerDirectories(path, launcherRoot))
        {
            try
            {
                string markerPath = Path.Combine(directory, InstallKindMarkerFileName);
                if (File.Exists(markerPath))
                {
                    marker = File.ReadAllText(markerPath).Trim();
                    break;
                }
            }
            catch (IOException)
            {
                // A missing/unreadable marker is equivalent to a portable build.
            }
            catch (UnauthorizedAccessException)
            {
                // Path-based detection below still protects system packages.
            }
        }

        return Detect(
            path,
            Environment.GetEnvironmentVariable(InstallKindEnvironmentVariable),
            Environment.GetEnvironmentVariable("APPIMAGE"),
            marker,
            launcherRoot);
    }

    internal static LauncherInstallationContext Detect(
        string executablePath,
        string? environmentKind,
        string? appImagePath,
        string? markerKind,
        string? launcherRoot = null)
    {
        string declaredKind = FirstNonEmpty(environmentKind, markerKind).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(appImagePath) || declaredKind == "appimage")
            return Package(LauncherInstallationKind.AppImage, "AppImage");
        if (declaredKind == "deb")
            return Package(LauncherInstallationKind.DebianPackage, "DEB");
        if (declaredKind == "rpm")
            return Package(LauncherInstallationKind.RpmPackage, "RPM");
        if (declaredKind is "windows-msi" or "windows-exe")
            return new(LauncherInstallationKind.WindowsInstaller, true, "Windows Installer");

        string normalizedPath = (executablePath ?? string.Empty).Replace('\\', '/');
        if (normalizedPath.Contains(".app/Contents/MacOS/", StringComparison.OrdinalIgnoreCase))
            return Package(LauncherInstallationKind.MacApplicationBundle, "macOS DMG");
        if (normalizedPath.StartsWith("/opt/pcl-n/", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith("/usr/lib/pcl-n/", StringComparison.OrdinalIgnoreCase))
        {
            return Package(LauncherInstallationKind.LinuxPackage, "Linux package");
        }

        if (IsScatterRoot(launcherRoot) ||
            IsScatterRoot(Path.GetDirectoryName(executablePath)) ||
            IsScatterRoot(Path.GetDirectoryName(Path.GetDirectoryName(executablePath))))
        {
            return new(LauncherInstallationKind.Scatter, true, "Scatter");
        }

        return new(LauncherInstallationKind.Portable, true, "Portable");
    }

    private static IEnumerable<string> CandidateMarkerDirectories(string executablePath, string? launcherRoot)
    {
        HashSet<string> yielded = OperatingSystem.IsWindows()
            ? new(StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.Ordinal);
        foreach (string? candidate in new[]
                 {
                     launcherRoot,
                     Path.GetDirectoryName(executablePath),
                     Path.GetDirectoryName(Path.GetDirectoryName(executablePath))
                 })
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            string fullPath;
            try { fullPath = Path.GetFullPath(candidate); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            if (yielded.Add(fullPath))
                yield return fullPath;
        }
    }

    private static bool IsScatterRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try { return File.Exists(Path.Combine(Path.GetFullPath(path), "pcln-layout")); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static LauncherInstallationContext Package(LauncherInstallationKind kind, string displayName) =>
        new(kind, false, displayName);

    private static string FirstNonEmpty(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first.Trim() : second?.Trim() ?? string.Empty;
}

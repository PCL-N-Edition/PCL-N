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
    LinuxPackage
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

    public static LauncherInstallationContext Detect(string? executablePath = null)
    {
        string path = executablePath ?? Environment.ProcessPath ?? string.Empty;
        string? marker = null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                string markerPath = Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty,
                    InstallKindMarkerFileName);
                if (File.Exists(markerPath))
                    marker = File.ReadAllText(markerPath).Trim();
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
            marker);
    }

    internal static LauncherInstallationContext Detect(
        string executablePath,
        string? environmentKind,
        string? appImagePath,
        string? markerKind)
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

        return new(LauncherInstallationKind.Portable, true, "Portable");
    }

    private static LauncherInstallationContext Package(LauncherInstallationKind kind, string displayName) =>
        new(kind, false, displayName);

    private static string FirstNonEmpty(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first.Trim() : second?.Trim() ?? string.Empty;
}

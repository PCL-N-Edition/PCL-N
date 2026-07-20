// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.Desktop.Session;

/// <summary>Shared path normalization for session stores (Avalonia-free).</summary>
public static class SessionPath
{
    public static string? NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public static string NormalizeSelectedMinecraftRoot(string selectedDirectory)
    {
        string root = NormalizeDirectory(selectedDirectory) ?? selectedDirectory;
        string nestedMinecraft = Path.Combine(root, ".minecraft");
        return Directory.Exists(Path.Combine(nestedMinecraft, "versions")) &&
               !Directory.Exists(Path.Combine(root, "versions"))
            ? nestedMinecraft
            : root;
    }

    public static string? TryGetMinecraftRootFromInstanceDirectory(string? instanceDirectory)
    {
        string? normalized = NormalizeDirectory(instanceDirectory);
        if (normalized is null)
            return null;

        DirectoryInfo? versions = Directory.GetParent(normalized);
        return versions?.Parent?.FullName is { } root ? NormalizeDirectory(root) : null;
    }
}

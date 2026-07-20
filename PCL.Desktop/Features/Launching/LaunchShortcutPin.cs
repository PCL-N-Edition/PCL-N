// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.Desktop.Features.Launching;

public enum LaunchShortcutKind
{
    World = 0,
    Server = 1
}

/// <summary>
/// A world or server pinned to the experimental launch-home shortcut dock.
/// </summary>
public sealed record LaunchShortcutPin(
    string Id,
    LaunchShortcutKind Kind,
    string InstanceDirectory,
    string Title,
    string Target,
    string? IconPath = null)
{
    public static string CreateId(LaunchShortcutKind kind, string instanceDirectory, string target) =>
        string.Concat(
            kind.ToString().ToLowerInvariant(),
            "|",
            instanceDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToLowerInvariant(),
            "|",
            target.Trim().ToLowerInvariant());
}

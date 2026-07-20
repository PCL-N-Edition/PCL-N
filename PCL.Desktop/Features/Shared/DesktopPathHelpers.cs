// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.Desktop.Features.Shared;

/// <summary>Small path/name helpers shared by desktop features.</summary>
internal static class DesktopPathHelpers
{
    public static string GetDesktopOrBaseDirectory()
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return string.IsNullOrWhiteSpace(desktop) ? AppContext.BaseDirectory : desktop;
    }

    public static string SanitizeFileName(string name)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        string sanitized = new(name.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "Minecraft" : sanitized;
    }
}

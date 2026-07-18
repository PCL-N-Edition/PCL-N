// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Hosting.RuntimeExtensions;
using PCL.Desktop.Localization;

namespace PCL.Desktop.Hosting;

internal sealed class DesktopHostLocalization : IHostLocalization
{
    public static DesktopHostLocalization Instance { get; } = new();

    public string CurrentCulture => AvaloniaLocalizationManager.CurrentLanguageCode;

    public string CurrentFormatCulture => AvaloniaLocalizationManager.CurrentFormatCulture.Name;

    public event EventHandler? LanguageChanged
    {
        add => AvaloniaLocalizationManager.LanguageChanged += value;
        remove => AvaloniaLocalizationManager.LanguageChanged -= value;
    }
}

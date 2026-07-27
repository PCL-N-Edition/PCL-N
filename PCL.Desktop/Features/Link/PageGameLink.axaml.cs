// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Markup.Xaml;
using PCL.Application.Link;
using PCL.Desktop.Controls.Legacy;
using PCL.Platform.Paths;

namespace PCL.Desktop.Features.Link;

public sealed partial class PageGameLink : MyPageRight, IDisposable
{
    private readonly TerracottaLobbyService _service;
    private readonly GameLinkViewModel _viewModel;

    public PageGameLink()
    {
        DefaultPlatformPathProvider paths = new();
        _service = new TerracottaLobbyService(paths.ApplicationDataDirectory);
        _viewModel = new GameLinkViewModel(_service);
        AvaloniaXamlLoader.Load(this);
        DataContext = _viewModel;
    }

    public new void PageOnEnter()
    {
        base.PageOnEnter();
        _ = _viewModel.InitializeAsync();
    }

    public override void Dispose()
    {
        _viewModel.Dispose();
        _service.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}

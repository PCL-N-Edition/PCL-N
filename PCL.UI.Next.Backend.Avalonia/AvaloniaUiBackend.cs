// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Threading;
using PCL.UI.Next;

namespace PCL.UI.Next.Backend.Avalonia;

/// <summary>Avalonia platform adapter for retained ECS render commits.</summary>
public sealed class AvaloniaUiBackend : IUiBackend
{
    private readonly Action _invalidate;
    private bool _initialized;

    public AvaloniaUiBackend(AvaloniaTextEngine textEngine)
    {
        Surface = new PclUiSurface(textEngine ?? throw new ArgumentNullException(nameof(textEngine)));
        _invalidate = Surface.InvalidateVisual;
    }

    public PclUiSurface Surface { get; }

    public UiBackendCapabilities Capabilities => UiBackendCapabilities.None;

    public void Initialize(in UiBackendContext context)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_initialized)
            throw new InvalidOperationException("Backend is already initialized.");
        Surface.Initialize(in context);
        _initialized = true;
    }

    public void Commit(in UiCommitBatch batch)
    {
        Dispatcher.UIThread.VerifyAccess();
        EnsureInitialized();
        Surface.Apply(in batch);
    }

    public void RequestFrame()
    {
        EnsureInitialized();
        if (Dispatcher.UIThread.CheckAccess())
            _invalidate();
        else
            Dispatcher.UIThread.Post(_invalidate, DispatcherPriority.Render);
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException("Backend must be initialized before use.");
    }
}

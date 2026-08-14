// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Unique composition owner for one window. It owns the window scope, input root,
/// interactive runtime and rendering runtime, and freezes their teardown order.
/// </summary>
public sealed class UiWindowRuntime : IDisposable
{
    private bool _disposed;

    public UiWindowRuntime(
        UiWorld world,
        ITextEngine textEngine,
        IUiBackend backend,
        UiScopeId applicationScope,
        UiSize viewport,
        float rasterScale = 1f,
        bool applyDefaults = true,
        int textCacheCapacity = 512,
        UiGestureThresholds? gestureThresholds = null,
        UiMotionRegistry? motionRegistry = null)
    {
        World = world ?? throw new ArgumentNullException(nameof(world));
        ArgumentNullException.ThrowIfNull(textEngine);
        ArgumentNullException.ThrowIfNull(backend);
        if (!world.Scopes.IsAlive(applicationScope))
            throw new InvalidOperationException("Application scope is not alive: " + applicationScope);
        UiRuntimeContract.EnsureSupported(backend.RequiredContractVersion, backend.GetType().FullName);

        ApplicationScope = applicationScope;
        WindowScope = world.CreateScope(applicationScope);
        UiInteractiveRuntime? interactive = null;
        try
        {
            interactive = new UiInteractiveRuntime(
                world,
                textEngine,
                viewport,
                applyDefaults,
                textCacheCapacity,
                gestureThresholds,
                motionRegistry);
            InputRoot = interactive.Input.InputRoots.Register(WindowScope);
            Rendering = new UiRenderingRuntime(
                world,
                backend,
                interactive.TextCache,
                WindowScope,
                viewport,
                rasterScale,
                interactive.Input);
        }
        catch
        {
            world.DisposeScope(WindowScope);
            interactive?.Dispose();
            throw;
        }

        Interactive = interactive;
    }

    public UiWorld World { get; }

    public UiScopeId ApplicationScope { get; }

    public UiScopeId WindowScope { get; }

    public UiInputRootId InputRoot { get; }

    public UiInteractiveRuntime Interactive { get; }

    public UiRenderingRuntime Rendering { get; }

    public void Dispose()
    {
        if (_disposed)
            return;

        Rendering.Dispose();
        World.DisposeScope(WindowScope);
        Interactive.Dispose();
        _disposed = true;
    }
}

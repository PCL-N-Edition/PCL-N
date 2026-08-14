// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Composition root for retained render diff and backend commit systems.</summary>
public sealed class UiRenderingRuntime : IDisposable
{
    private readonly RenderDiffSystem _diff;
    private readonly BackendCommitSystem _commit;
    private readonly UiNativeHostRuntime? _nativeHosts;
    private readonly UiAccessibilityRuntime _accessibility;
    private bool _disposed;

    public UiRenderingRuntime(
        UiWorld world,
        IUiBackend backend,
        TextLayoutCache textLayouts,
        UiScopeId rootScope,
        UiSize viewport,
        float rasterScale = 1f,
        UiInputRuntime? input = null)
    {
        World = world ?? throw new ArgumentNullException(nameof(world));
        Backend = backend ?? throw new ArgumentNullException(nameof(backend));
        ArgumentNullException.ThrowIfNull(textLayouts);
        if (!world.Scopes.IsAlive(rootScope))
            throw new InvalidOperationException("Render root scope is not alive: " + rootScope);
        RootScope = rootScope;
        Scene = new RenderScene(textLayouts);
        _diff = new RenderDiffSystem(world, Scene, rootScope);
        _commit = new BackendCommitSystem(_diff, backend);
        UiBackendContext context = new(viewport, rasterScale);
        backend.Initialize(in context);
        if (backend is INativeHostBackend nativeHostBackend)
            _nativeHosts = new UiNativeHostRuntime(world, nativeHostBackend, rootScope, input);
        _accessibility = new UiAccessibilityRuntime(world, rootScope, backend as IAccessibilityBackend, input);
        world.Systems.Register(_diff);
        world.Systems.Register(_commit);
        world.Scheduler.RequestReactiveFrame();
    }

    public UiWorld World { get; }

    public IUiBackend Backend { get; }

    public UiScopeId RootScope { get; }

    public RenderScene Scene { get; }

    public UiNativeHostRuntime? NativeHosts => _nativeHosts;

    public UiAccessibilityRuntime Accessibility => _accessibility;

    public void Dispose()
    {
        if (_disposed)
            return;
        _accessibility.Dispose();
        _nativeHosts?.Dispose();
        World.Systems.Unregister(_commit);
        World.Systems.Unregister(_diff);
        _diff.Dispose();
        Scene.Dispose();
        _disposed = true;
    }
}

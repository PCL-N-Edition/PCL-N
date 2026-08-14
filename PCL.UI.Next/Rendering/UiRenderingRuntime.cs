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
    private readonly IDisposable _textLayoutLease;
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
        UiRuntimeContract.EnsureSupported(backend.RequiredContractVersion, backend.GetType().FullName);
        if (!world.Scopes.IsAlive(rootScope))
            throw new InvalidOperationException("Render root scope is not alive: " + rootScope);
        RootScope = rootScope;
        _textLayoutLease = textLayouts.AcquireBorrowLease();
        RenderScene scene = new(textLayouts);
        RenderDiffSystem diff = new(world, scene, rootScope);
        BackendCommitSystem commit = new(diff, backend);
        UiNativeHostRuntime? nativeHosts = null;
        UiAccessibilityRuntime? accessibility = null;
        bool backendInitialized = false;
        bool diffRegistered = false;
        bool commitRegistered = false;
        try
        {
            UiBackendContext context = new(viewport, rasterScale);
            backend.Initialize(in context);
            backendInitialized = true;
            if (backend is INativeHostBackend nativeHostBackend)
                nativeHosts = new UiNativeHostRuntime(world, nativeHostBackend, rootScope, input);
            accessibility = new UiAccessibilityRuntime(world, rootScope, backend as IAccessibilityBackend, input);
            world.Systems.Register(diff);
            diffRegistered = true;
            world.Systems.Register(commit);
            commitRegistered = true;
        }
        catch
        {
            if (commitRegistered)
                world.Systems.Unregister(commit);
            if (diffRegistered)
                world.Systems.Unregister(diff);
            accessibility?.Dispose();
            nativeHosts?.Dispose();
            diff.Dispose();
            if (backendInitialized)
                backend.Shutdown();
            scene.Dispose();
            _textLayoutLease.Dispose();
            throw;
        }

        Scene = scene;
        _diff = diff;
        _commit = commit;
        _nativeHosts = nativeHosts;
        _accessibility = accessibility!;
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
        Backend.Shutdown();
        Scene.Dispose();
        _textLayoutLease.Dispose();
        _disposed = true;
    }
}

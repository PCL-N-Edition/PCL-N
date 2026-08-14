// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

[Flags]
public enum UiBackendCapabilities : ulong
{
    None = 0,
    CompositionTransform = 1ul << 0,
    CompositionOpacity = 1ul << 1,
    Clip = 1ul << 2,
    Blur = 1ul << 3,
    Shadow = 1ul << 4,
    Vector = 1ul << 5,
    Hdr = 1ul << 6,
    NativeTextInput = 1ul << 7,
    Accessibility = 1ul << 8
}

public readonly record struct UiBackendContext
{
    public UiBackendContext(
        UiSize viewport,
        float rasterScale = 1f,
        UiContractVersion? runtimeContractVersion = null)
    {
        if (viewport.Width < 0f || viewport.Height < 0f || !viewport.IsFinite)
            throw new ArgumentOutOfRangeException(nameof(viewport));
        if (rasterScale <= 0f || !float.IsFinite(rasterScale))
            throw new ArgumentOutOfRangeException(nameof(rasterScale));
        Viewport = viewport;
        RasterScale = rasterScale;
        RuntimeContractVersion = runtimeContractVersion ?? UiRuntimeContract.Current;
        if (!RuntimeContractVersion.IsValid)
            throw new ArgumentOutOfRangeException(nameof(runtimeContractVersion));
    }

    public UiSize Viewport { get; }

    public float RasterScale { get; }

    public UiContractVersion RuntimeContractVersion { get; }
}

/// <summary>Retained backend contract. Commit must not call back into the Runtime.</summary>
public interface IUiBackend
{
    UiContractVersion RequiredContractVersion { get; }

    UiBackendCapabilities Capabilities { get; }

    void Initialize(in UiBackendContext context);

    void Commit(in UiCommitBatch batch);

    void RequestFrame();

    void Shutdown();
}

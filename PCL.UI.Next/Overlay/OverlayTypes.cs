// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

public readonly record struct UiOverlayHandle(int Index, uint Generation)
{
    public static UiOverlayHandle None => default;
    public bool IsNone => Index <= 0 || Generation == 0;
    public override string ToString() => IsNone ? "Overlay(None)" : $"Overlay({Index}:{Generation})";
}

public enum UiOverlayKind : byte
{
    Tooltip = 0,
    Popup = 1,
    Modal = 2
}

public enum UiOverlayPlacement : byte
{
    Auto = 0,
    BelowStart = 1,
    BelowEnd = 2,
    AboveStart = 3,
    AboveEnd = 4,
    Pointer = 5,
    Center = 6
}

public readonly record struct UiTooltipOptions(
    double DelaySeconds,
    UiOverlayPlacement Placement,
    float Offset,
    float ViewportPadding,
    double AutoCloseSeconds)
{
    public static UiTooltipOptions Default => new(0.5d, UiOverlayPlacement.Pointer, 12f, 8f, 0d);
}

public readonly record struct UiPopupOptions(
    UiOverlayPlacement Placement,
    float Offset,
    float ViewportPadding,
    bool DismissOnOutsidePointer,
    bool DismissOnEscape,
    bool TrapFocus,
    bool RestorePreviousFocus)
{
    public static UiPopupOptions Default => new(
        UiOverlayPlacement.Auto,
        6f,
        8f,
        DismissOnOutsidePointer: true,
        DismissOnEscape: true,
        TrapFocus: false,
        RestorePreviousFocus: true);
}

public readonly record struct UiModalOptions(
    float ViewportPadding,
    bool DismissOnBarrierPointer,
    bool DismissOnEscape,
    bool RestorePreviousFocus)
{
    public static UiModalOptions Default => new(
        16f,
        DismissOnBarrierPointer: false,
        DismissOnEscape: true,
        RestorePreviousFocus: true);
}

public readonly record struct UiOverlaySnapshot(
    UiOverlayHandle Handle,
    UiOverlayKind Kind,
    UiScopeId Scope,
    UiEntity RootEntity,
    UiEntity BarrierEntity,
    UiEntity AnchorEntity,
    UiOverlayPlacement Placement);

public sealed class UiTooltipRegistration : IDisposable
{
    private UiOverlayRuntime? _runtime;
    private readonly int _id;

    internal UiTooltipRegistration(UiOverlayRuntime runtime, int id)
    {
        _runtime = runtime;
        _id = id;
    }

    public UiOverlayHandle ActiveOverlay => _runtime?.GetTooltipOverlay(_id) ?? UiOverlayHandle.None;

    public void Dispose()
    {
        UiOverlayRuntime? runtime = Interlocked.Exchange(ref _runtime, null);
        runtime?.RemoveTooltip(_id);
    }
}

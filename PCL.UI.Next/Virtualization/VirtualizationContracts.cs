// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Compiled/runtime virtualization metadata stored inline on the host entity.</summary>
public struct Virtualization
{
    public float EstimatedItemExtent { get; set; }
    public ushort OverscanBefore { get; set; }
    public ushort OverscanAfter { get; set; }

    public static Virtualization Default => new()
    {
        EstimatedItemExtent = 48f,
        OverscanBefore = 6,
        OverscanAfter = 6
    };
}

/// <summary>
/// Typed collection adapter consumed by the Runtime. Generated PXML code can implement this
/// contract without reflection or exposing business services to UI entities.
/// </summary>
public interface IUiVirtualItemSource
{
    int Count { get; }

    ulong Version { get; }

    long GetKey(int index);

    void BindItem(int index, PresentationStore presentation);

    bool TryGetIndex(long key, out int index);
}

public readonly record struct UiVirtualizationSnapshot(
    int ItemCount,
    int VisibleStart,
    int VisibleEndExclusive,
    int RealizedStart,
    int RealizedEndExclusive,
    int RealizedCount,
    int RecyclePoolCount,
    float Extent);

/// <summary>Disposable registration of one item source/template against a VirtualList host.</summary>
public sealed class UiVirtualListRegistration : IDisposable
{
    private Action? _dispose;

    internal UiVirtualListRegistration(Action dispose)
    {
        _dispose = dispose;
    }

    public void Dispose()
    {
        Action? dispose = Interlocked.Exchange(ref _dispose, null);
        dispose?.Invoke();
    }
}

/// <summary>Runtime metadata attached to recycled item roots.</summary>
public struct VirtualItemSlot
{
    public int LogicalIndex { get; set; }
    public long Key { get; set; }
    public float Offset { get; set; }
    public float Extent { get; set; }
    public bool IsRealized { get; set; }
}

// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

public enum UiRoutedEventKind : byte
{
    PointerMove = 0,
    PointerDown = 1,
    PointerUp = 2,
    PointerCancel = 3,
    PointerEnter = 4,
    PointerLeave = 5,
    KeyDown = 6,
    KeyUp = 7,
    GotFocus = 8,
    LostFocus = 9,
    Click = 10,
    DoubleClick = 11,
    LongPress = 12,
    DragStarted = 13,
    DragDelta = 14,
    DragCompleted = 15,
    PanStarted = 16,
    PanDelta = 17,
    PanCompleted = 18,
    PinchStarted = 19,
    PinchDelta = 20,
    PinchCompleted = 21
}

[Flags]
public enum UiRoutedEventPhase : byte
{
    None = 0,
    Capture = 1 << 0,
    Target = 1 << 1,
    Bubble = 1 << 2,
    All = Capture | Target | Bubble
}

public readonly record struct UiRoutedEventData(
    UiTimestamp Timestamp,
    UiPoint Position = default,
    UiPoint Delta = default,
    int PointerId = 0,
    UiPointerButton PointerButton = UiPointerButton.None,
    UiKey Key = UiKey.None,
    UiInputModifiers Modifiers = UiInputModifiers.None,
    float Scale = 1f,
    UiInputRootId InputRoot = default);

public readonly record struct UiRoutedEventRecord(
    UiRoutedEventKind Kind,
    UiRoutedEventPhase Phase,
    UiEntity Target,
    UiEntity CurrentTarget,
    UiRoutedEventData Data,
    bool Handled);

public sealed class UiRoutedEventContext
{
    internal UiRoutedEventContext(
        UiRoutedEventKind kind,
        UiEntity target,
        in UiRoutedEventData data)
    {
        Reset(kind, target, in data);
    }

    public UiRoutedEventKind Kind { get; private set; }
    public UiRoutedEventPhase Phase { get; internal set; }
    public UiEntity Target { get; private set; }
    public UiEntity CurrentTarget { get; internal set; }
    public UiRoutedEventData Data { get; private set; }
    public bool Handled { get; set; }
    public bool PropagationStopped { get; private set; }

    public void StopPropagation() => PropagationStopped = true;

    internal void Reset(
        UiRoutedEventKind kind,
        UiEntity target,
        in UiRoutedEventData data)
    {
        Kind = kind;
        Phase = UiRoutedEventPhase.None;
        Target = target;
        CurrentTarget = UiEntity.None;
        Data = data;
        Handled = false;
        PropagationStopped = false;
    }
}

public delegate void UiRoutedEventHandler(UiRoutedEventContext context);

/// <summary>
/// Central routed-event table. Handlers are stored by entity in the Runtime, not as
/// per-control CLR events, and are removed automatically with entity generation death.
/// </summary>
public sealed class UiRoutedEventRouter : IDisposable
{
    private readonly UiWorld _world;
    private readonly Dictionary<UiEntity, List<HandlerEntry>> _handlers = [];
    private readonly HashSet<UiEntity> _handlerCompaction = [];
    private readonly List<UiRoutedEventRecord> _records = [];
    private readonly Stack<List<UiEntity>> _routePool = [];
    private readonly Stack<UiRoutedEventContext> _contextPool = [];
    private int _dispatchDepth;
    private bool _disposed;

    public UiRoutedEventRouter(UiWorld world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _world.EntityDestroying += OnEntityDestroying;
    }

    public IReadOnlyList<UiRoutedEventRecord> FrameRecords => _records;

    public IDisposable Register(
        UiEntity entity,
        UiRoutedEventKind kind,
        UiRoutedEventHandler handler,
        UiRoutedEventPhase phases = UiRoutedEventPhase.All)
    {
        ThrowIfDisposed();
        _world.Entities.EnsureAlive(entity);
        ArgumentNullException.ThrowIfNull(handler);
        if (phases == UiRoutedEventPhase.None)
            throw new ArgumentOutOfRangeException(nameof(phases));
        if (!_handlers.TryGetValue(entity, out List<HandlerEntry>? entries))
        {
            entries = [];
            _handlers[entity] = entries;
        }

        HandlerEntry entry = new(kind, phases, handler);
        entries.Add(entry);
        return new HandlerRegistration(this, entity, entry);
    }

    public bool Dispatch(UiRoutedEventKind kind, UiEntity target, in UiRoutedEventData data)
    {
        ThrowIfDisposed();
        if (!_world.Entities.IsAlive(target))
            return false;

        List<UiEntity> route = RentRoute(target);
        UiRoutedEventContext context = RentContext(kind, target, in data);
        _dispatchDepth++;
        try
        {
            for (int i = route.Count - 1; i >= 1; i--)
            {
                Invoke(route[i], UiRoutedEventPhase.Capture, context);
                if (context.PropagationStopped)
                    return context.Handled;
            }

            Invoke(target, UiRoutedEventPhase.Target, context);
            if (context.PropagationStopped)
                return context.Handled;

            for (int i = 1; i < route.Count; i++)
            {
                Invoke(route[i], UiRoutedEventPhase.Bubble, context);
                if (context.PropagationStopped)
                    break;
            }

            return context.Handled;
        }
        finally
        {
            ReturnContext(context);
            ReturnRoute(route);
            _dispatchDepth--;
            if (_dispatchDepth == 0)
                CompactHandlers();
        }
    }

    internal void BeginFrame() => _records.Clear();

    public void Dispose()
    {
        if (_disposed)
            return;
        _world.EntityDestroying -= OnEntityDestroying;
        _handlers.Clear();
        _handlerCompaction.Clear();
        _records.Clear();
        _routePool.Clear();
        _contextPool.Clear();
        _disposed = true;
    }

    private void Invoke(UiEntity current, UiRoutedEventPhase phase, UiRoutedEventContext context)
    {
        context.CurrentTarget = current;
        context.Phase = phase;
        if (_handlers.TryGetValue(current, out List<HandlerEntry>? entries))
        {
            int initialCount = entries.Count;
            for (int i = 0; i < initialCount; i++)
            {
                HandlerEntry entry = entries[i];
                if (!entry.IsActive)
                    continue;
                if (entry.Kind == context.Kind && (entry.Phases & phase) != 0)
                    entry.Handler(context);
                if (context.PropagationStopped)
                    break;
            }
        }

        _records.Add(new UiRoutedEventRecord(
            context.Kind,
            phase,
            context.Target,
            current,
            context.Data,
            context.Handled));
    }

    private List<UiEntity> RentRoute(UiEntity target)
    {
        List<UiEntity> route = _routePool.TryPop(out List<UiEntity>? pooled) ? pooled : [];
        UiEntity current = target;
        int guard = 0;
        while (_world.Entities.IsAlive(current) && guard++ < 1_000_000)
        {
            route.Add(current);
            if (!_world.Hierarchy.TryGetNode(current, out HierarchyNode node) || node.Parent == UiEntity.None)
                break;
            current = node.Parent;
        }

        return route;
    }

    private void ReturnRoute(List<UiEntity> route)
    {
        route.Clear();
        _routePool.Push(route);
    }

    private UiRoutedEventContext RentContext(
        UiRoutedEventKind kind,
        UiEntity target,
        in UiRoutedEventData data)
    {
        if (_contextPool.TryPop(out UiRoutedEventContext? context))
        {
            context.Reset(kind, target, in data);
            return context;
        }

        return new UiRoutedEventContext(kind, target, in data);
    }

    private void ReturnContext(UiRoutedEventContext context) => _contextPool.Push(context);

    private void OnEntityDestroying(UiEntity entity)
    {
        if (!_handlers.Remove(entity, out List<HandlerEntry>? entries))
            return;

        for (int i = 0; i < entries.Count; i++)
            entries[i].IsActive = false;
        _handlerCompaction.Remove(entity);
    }

    private void Unregister(UiEntity entity, HandlerEntry entry)
    {
        if (!entry.IsActive || !_handlers.TryGetValue(entity, out List<HandlerEntry>? entries))
            return;

        entry.IsActive = false;
        if (_dispatchDepth != 0)
        {
            _handlerCompaction.Add(entity);
            return;
        }

        entries.Remove(entry);
        if (entries.Count == 0)
            _handlers.Remove(entity);
    }

    private void CompactHandlers()
    {
        if (_handlerCompaction.Count == 0)
            return;

        foreach (UiEntity entity in _handlerCompaction)
        {
            if (!_handlers.TryGetValue(entity, out List<HandlerEntry>? entries))
                continue;

            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (!entries[i].IsActive)
                    entries.RemoveAt(i);
            }

            if (entries.Count == 0)
                _handlers.Remove(entity);
        }

        _handlerCompaction.Clear();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class HandlerEntry(
        UiRoutedEventKind kind,
        UiRoutedEventPhase phases,
        UiRoutedEventHandler handler)
    {
        public UiRoutedEventKind Kind { get; } = kind;

        public UiRoutedEventPhase Phases { get; } = phases;

        public UiRoutedEventHandler Handler { get; } = handler;

        public bool IsActive { get; set; } = true;
    }

    private sealed class HandlerRegistration : IDisposable
    {
        private UiRoutedEventRouter? _owner;
        private readonly UiEntity _entity;
        private readonly HandlerEntry _entry;

        public HandlerRegistration(UiRoutedEventRouter owner, UiEntity entity, HandlerEntry entry)
        {
            _owner = owner;
            _entity = entity;
            _entry = entry;
        }

        public void Dispose()
        {
            UiRoutedEventRouter? owner = _owner;
            if (owner is null)
                return;
            _owner = null;
            owner.Unregister(_entity, _entry);
        }
    }
}

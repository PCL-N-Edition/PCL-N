// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Owns window overlays, child scopes, placement, timers and input/focus barriers.</summary>
public sealed class UiOverlayRuntime : IUiSystem, IDisposable
{
    private const int FirstOverlayZ = 100_000;
    private readonly UiWorld _world;
    private readonly UiInteractiveRuntime _runtime;
    private readonly BlueprintInstantiator _instantiator;
    private readonly UiScopeId _windowScope;
    private readonly List<Entry?> _entries = [null];
    private readonly List<uint> _generations = [0];
    private readonly Stack<int> _free = [];
    private readonly Dictionary<int, TooltipEntry> _tooltips = [];
    private readonly List<UiOverlayHandle> _closeScratch = [];
    private readonly IDisposable _windowScopeRegistration;
    private IDisposable? _timerLease;
    private int _nextTooltipId = 1;
    private int _nextZ = FirstOverlayZ;
    private bool _disposed;

    public UiOverlayRuntime(
        UiWorld world,
        UiInteractiveRuntime runtime,
        BlueprintInstantiator instantiator,
        UiScopeId windowScope)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _instantiator = instantiator ?? throw new ArgumentNullException(nameof(instantiator));
        if (!ReferenceEquals(world, runtime.World))
            throw new InvalidOperationException("Overlay runtime and interactive runtime must use the same world.");
        if (!world.Scopes.IsAlive(windowScope))
            throw new InvalidOperationException("Overlay window scope is not alive: " + windowScope);
        _windowScope = windowScope;
        OverlayRoot = CreateOverlayRoot();
        _windowScopeRegistration = world.Scopes.RegisterDisposeHandler(windowScope, _ => Dispose());
        _world.EntityDestroying += OnEntityDestroying;
        _world.Systems.Register(this);
    }

    public UiSystemPhase Phase => UiSystemPhase.AnimationTick;
    public string Name => "overlay.update";
    public UiEntity OverlayRoot { get; }
    public int OverlayCount => _entries.Count(static entry => entry is not null);
    public int TooltipRegistrationCount => _tooltips.Count;

    public UiOverlayHandle OpenPopup(
        UiBlueprint content,
        UiEntity anchor,
        UiPopupOptions? options = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(content);
        ValidateAnchor(anchor);
        UiPopupOptions settings = options ?? UiPopupOptions.Default;
        ValidatePlacement(settings.Placement, allowCenter: true, allowPointer: false);
        ValidateGeometry(settings.Offset, settings.ViewportPadding);
        UiScopeId parentScope = _world.Entities.GetScope(anchor);
        return Open(
            UiOverlayKind.Popup,
            content,
            parentScope,
            anchor,
            default,
            settings.Placement,
            settings.Offset,
            settings.ViewportPadding,
            createBarrier: settings.DismissOnOutsidePointer,
            dismissOnBarrier: settings.DismissOnOutsidePointer,
            dismissOnEscape: settings.DismissOnEscape,
            focusScope: true,
            trapFocus: settings.TrapFocus,
            restorePreviousFocus: settings.RestorePreviousFocus,
            passThrough: false,
            autoCloseSeconds: 0d);
    }

    public UiOverlayHandle OpenPopupAt(
        UiBlueprint content,
        UiEntity owner,
        UiPoint pointerPosition,
        UiPopupOptions? options = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(content);
        ValidateAnchor(owner);
        UiPopupOptions settings = options ?? UiPopupOptions.Default;
        ValidateGeometry(settings.Offset, settings.ViewportPadding);
        return Open(
            UiOverlayKind.Popup,
            content,
            _world.Entities.GetScope(owner),
            owner,
            pointerPosition,
            UiOverlayPlacement.Pointer,
            settings.Offset,
            settings.ViewportPadding,
            createBarrier: settings.DismissOnOutsidePointer,
            dismissOnBarrier: settings.DismissOnOutsidePointer,
            dismissOnEscape: settings.DismissOnEscape,
            focusScope: true,
            trapFocus: settings.TrapFocus,
            restorePreviousFocus: settings.RestorePreviousFocus,
            passThrough: false,
            autoCloseSeconds: 0d);
    }

    public UiOverlayHandle ShowModal(UiBlueprint content, UiModalOptions? options = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(content);
        UiModalOptions settings = options ?? UiModalOptions.Default;
        ValidateGeometry(0f, settings.ViewportPadding);
        return Open(
            UiOverlayKind.Modal,
            content,
            _windowScope,
            UiEntity.None,
            default,
            UiOverlayPlacement.Center,
            0f,
            settings.ViewportPadding,
            createBarrier: true,
            dismissOnBarrier: settings.DismissOnBarrierPointer,
            dismissOnEscape: settings.DismissOnEscape,
            focusScope: true,
            trapFocus: true,
            restorePreviousFocus: settings.RestorePreviousFocus,
            passThrough: false,
            autoCloseSeconds: 0d);
    }

    public UiTooltipRegistration AttachTooltip(
        UiEntity owner,
        UiBlueprint content,
        UiTooltipOptions? options = null)
    {
        ThrowIfDisposed();
        ValidateAnchor(owner);
        ArgumentNullException.ThrowIfNull(content);
        UiTooltipOptions settings = options ?? UiTooltipOptions.Default;
        if (!double.IsFinite(settings.DelaySeconds) || settings.DelaySeconds < 0d)
            throw new ArgumentOutOfRangeException(nameof(options), "Tooltip delay must be finite and non-negative.");
        if (!double.IsFinite(settings.AutoCloseSeconds) || settings.AutoCloseSeconds < 0d)
            throw new ArgumentOutOfRangeException(nameof(options), "Tooltip auto-close must be finite and non-negative.");
        ValidatePlacement(settings.Placement, allowCenter: false, allowPointer: true);
        ValidateGeometry(settings.Offset, settings.ViewportPadding);

        int id = _nextTooltipId++;
        TooltipEntry entry = new(id, owner, content, settings);
        entry.Registrations.Add(_runtime.Input.RoutedEvents.Register(
            owner,
            UiRoutedEventKind.PointerEnter,
            context => BeginTooltip(entry, context.Data)));
        entry.Registrations.Add(_runtime.Input.RoutedEvents.Register(
            owner,
            UiRoutedEventKind.PointerMove,
            context => UpdateTooltipPointer(entry, context.Data)));
        entry.Registrations.Add(_runtime.Input.RoutedEvents.Register(
            owner,
            UiRoutedEventKind.PointerLeave,
            _ => EndTooltip(entry)));
        entry.Registrations.Add(_runtime.Input.RoutedEvents.Register(
            owner,
            UiRoutedEventKind.PointerDown,
            _ => EndTooltip(entry)));
        _tooltips.Add(id, entry);
        return new UiTooltipRegistration(this, id);
    }

    public bool Close(UiOverlayHandle handle)
    {
        if (!TryGet(handle, out Entry entry) || entry.Closing)
            return false;
        entry.Closing = true;
        DeactivateBarrier(entry.BarrierEntity);
        if (entry.FocusScope && _world.Entities.IsAlive(entry.RootEntity))
            _runtime.Input.Focus.DeactivateScope(entry.RootEntity, _world.Clock.Now);
        if (_world.Scopes.IsAlive(entry.Scope))
            _world.DisposeScope(entry.Scope);
        else
            ReleaseEntry(entry);
        UpdateTimerLease();
        return true;
    }

    public bool TryGetOverlay(UiOverlayHandle handle, out UiOverlaySnapshot snapshot)
    {
        if (!TryGet(handle, out Entry entry))
        {
            snapshot = default;
            return false;
        }
        snapshot = new UiOverlaySnapshot(
            entry.Handle,
            entry.Kind,
            entry.Scope,
            entry.RootEntity,
            entry.BarrierEntity,
            entry.AnchorEntity,
            entry.Placement);
        return true;
    }

    public void Update(UiWorld world, in UiFrameContext frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool placementChanged = false;
        _closeScratch.Clear();
        foreach (TooltipEntry tooltip in _tooltips.Values)
        {
            if (tooltip.Pending && frame.Now.CompareTo(tooltip.ShowAt) >= 0)
                OpenTooltip(tooltip, frame.Now);
        }
        for (int i = 1; i < _entries.Count; i++)
        {
            Entry? entry = _entries[i];
            if (entry is null)
                continue;
            if (!entry.AnchorEntity.IsNone && !_world.Entities.IsAlive(entry.AnchorEntity))
            {
                _closeScratch.Add(entry.Handle);
                continue;
            }
            if (entry.AutoCloseAt != UiTimestamp.Zero && frame.Now.CompareTo(entry.AutoCloseAt) >= 0)
            {
                _closeScratch.Add(entry.Handle);
                continue;
            }
            placementChanged |= UpdatePlacement(entry);
        }
        for (int i = 0; i < _closeScratch.Count; i++)
            Close(_closeScratch[i]);
        if (placementChanged)
            _runtime.Layout.Arrange();
        UpdateTimerLease();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _world.Systems.Unregister(this);
        _world.EntityDestroying -= OnEntityDestroying;
        foreach (TooltipEntry tooltip in _tooltips.Values.ToArray())
            DisposeTooltip(tooltip);
        _tooltips.Clear();
        for (int i = _entries.Count - 1; i >= 1; i--)
        {
            Entry? entry = _entries[i];
            if (entry is null)
                continue;
            entry.Closing = true;
            DeactivateBarrier(entry.BarrierEntity);
            if (entry.FocusScope && _world.Entities.IsAlive(entry.RootEntity))
                _runtime.Input.Focus.DeactivateScope(entry.RootEntity, _world.Clock.Now);
            if (_world.Scopes.IsAlive(entry.Scope))
                _world.DisposeScope(entry.Scope);
            else
                ReleaseEntry(entry);
        }
        _timerLease?.Dispose();
        _timerLease = null;
        if (_world.Entities.IsAlive(OverlayRoot))
            _world.DestroyEntity(OverlayRoot);
        _windowScopeRegistration.Dispose();
    }

    internal UiOverlayHandle GetTooltipOverlay(int id) =>
        _tooltips.TryGetValue(id, out TooltipEntry? entry) ? entry.ActiveOverlay : UiOverlayHandle.None;

    internal void RemoveTooltip(int id)
    {
        if (!_tooltips.Remove(id, out TooltipEntry? entry))
            return;
        DisposeTooltip(entry);
        UpdateTimerLease();
    }

    private UiEntity CreateOverlayRoot()
    {
        UiEntity entity = _world.CreateEntity(_windowScope);
        _world.Set(entity, new NodeKindComponent { Kind = UiNodeKind.Overlay });
        _world.Set(entity, new LayoutStyle
        {
            Width = UiLength.Percent(1f),
            Height = UiLength.Percent(1f),
            MinSize = UiSize.Zero,
            MaxSize = UiSize.Infinite,
            HorizontalAlignment = UiHorizontalAlignment.Stretch,
            VerticalAlignment = UiVerticalAlignment.Stretch,
            IsMeasureBoundary = true
        });
        _world.Set(entity, new AbsoluteLayout());
        _world.Dirty.Mark(entity, UiDirtyFlags.StructuralCascade | UiDirtyFlags.Style);
        return entity;
    }

    private UiOverlayHandle Open(
        UiOverlayKind kind,
        UiBlueprint content,
        UiScopeId parentScope,
        UiEntity anchor,
        UiPoint pointerAnchor,
        UiOverlayPlacement placement,
        float offset,
        float viewportPadding,
        bool createBarrier,
        bool dismissOnBarrier,
        bool dismissOnEscape,
        bool focusScope,
        bool trapFocus,
        bool restorePreviousFocus,
        bool passThrough,
        double autoCloseSeconds)
    {
        UiOverlayHandle handle = AllocateHandle();
        UiScopeId scope = _world.CreateScope(parentScope);
        try
        {
            UiEntity previousFocus = UiEntity.None;
            if (focusScope &&
                _runtime.Input.InputRoots.TryResolve(parentScope, out UiInputRootId inputRoot))
            {
                previousFocus = _runtime.Input.Focus.GetFocused(inputRoot);
            }
            int z = _nextZ;
            _nextZ = checked(_nextZ + 100);
            UiEntity barrier = UiEntity.None;
            if (createBarrier)
                barrier = CreateBarrier(scope, z, kind == UiOverlayKind.Modal);

            BlueprintInstance instance = _instantiator.Instantiate(content, scope);
            UiEntity root = instance.RootEntity;
            _world.AttachChild(OverlayRoot, root);
            _world.Set(root, new AbsolutePlacement());
            if (focusScope)
            {
                _world.Set(root, new FocusScopeComponent
                {
                    IsTrap = trapFocus,
                    RestorePreviousFocus = restorePreviousFocus
                });
            }
            if (kind == UiOverlayKind.Tooltip)
                _world.Set(root, new SemanticRole { Value = UiSemanticRole.Tooltip });
            ConfigureInputSubtree(root, z + 1, passThrough);
            _world.Set(root, new UiNativeHostOcclusion
            {
                RootScope = _windowScope,
                AllowedScope = scope,
                ZIndex = z + 1
            });

            Entry entry = new(
                handle,
                kind,
                scope,
                instance,
                root,
                barrier,
                anchor,
                pointerAnchor,
                placement,
                offset,
                viewportPadding,
                focusScope,
                autoCloseSeconds > 0d ? _world.Clock.Now.AddSeconds(autoCloseSeconds) : UiTimestamp.Zero);
            _entries[handle.Index] = entry;
            entry.ScopeRegistration = _world.Scopes.RegisterDisposeHandler(scope, _ => ReleaseEntry(entry));

            if (_world.Entities.IsAlive(barrier))
            {
                entry.Registrations.Add(_runtime.Input.RoutedEvents.Register(
                    barrier,
                    UiRoutedEventKind.PointerDown,
                    context =>
                    {
                        context.Handled = true;
                        context.StopPropagation();
                        if (dismissOnBarrier)
                            Close(handle);
                    }));
            }
            if (dismissOnEscape)
            {
                entry.Registrations.Add(_runtime.Input.RoutedEvents.Register(
                    root,
                    UiRoutedEventKind.KeyDown,
                    context =>
                    {
                        if (context.Data.Key != UiKey.Escape)
                            return;
                        context.Handled = true;
                        context.StopPropagation();
                        Close(handle);
                    },
                    UiRoutedEventPhase.Bubble | UiRoutedEventPhase.Target));
            }

            if (focusScope)
                _runtime.Input.Focus.ActivateScope(root, previousFocus, _world.Clock.Now);
            UpdatePlacement(entry);
            UpdateTimerLease();
            _world.Scheduler.RequestReactiveFrame();
            return handle;
        }
        catch
        {
            if (_world.Scopes.IsAlive(scope))
                _world.DisposeScope(scope);
            if (_entries[handle.Index] is null && _generations[handle.Index] == handle.Generation)
                RecycleUnusedHandle(handle);
            throw;
        }
    }

    private UiEntity CreateBarrier(UiScopeId scope, int z, bool dimmed)
    {
        UiEntity barrier = _world.CreateEntity(scope);
        _world.Set(barrier, new NodeKindComponent { Kind = UiNodeKind.Container });
        _world.Set(barrier, new LayoutStyle
        {
            Width = UiLength.Percent(1f),
            Height = UiLength.Percent(1f),
            MinSize = UiSize.Zero,
            MaxSize = UiSize.Infinite,
            HorizontalAlignment = UiHorizontalAlignment.Stretch,
            VerticalAlignment = UiVerticalAlignment.Stretch
        });
        _world.Set(barrier, new OverlayLayout());
        _world.Set(barrier, new AbsolutePlacement());
        _world.Set(barrier, new HitTestableComponent { IsVisible = true, IsEnabled = true, ZIndex = z });
        _world.Set(barrier, new InteractionStateComponent());
        _world.Set(barrier, new UiInteractionBarrier
        {
            RootScope = _windowScope,
            AllowedScope = scope,
            BlockedCapabilities = dimmed
                ? UiInteractionCapability.All
                : UiInteractionCapability.Pointer | UiInteractionCapability.NativeHost,
            ZIndex = z
        });
        if (dimmed)
        {
            _world.Set(barrier, StyleClassSet.From([UiClass.ModalBarrier.Id]));
            _world.Set(barrier, new UiNativeHostOcclusion
            {
                RootScope = _windowScope,
                AllowedScope = scope,
                ZIndex = z
            });
        }
        _world.AttachChild(OverlayRoot, barrier);
        _world.Dirty.Mark(barrier, UiDirtyFlags.StructuralCascade | UiDirtyFlags.Style);
        return barrier;
    }

    private void DeactivateBarrier(UiEntity barrier)
    {
        if (!_world.Entities.IsAlive(barrier))
            return;
        _world.Remove<UiInteractionBarrier>(barrier);
        if (_world.Components.TryGet(barrier, out HitTestableComponent hit))
        {
            hit.IsVisible = false;
            hit.IsEnabled = false;
            _world.Set(barrier, hit);
        }
    }

    private void ConfigureInputSubtree(UiEntity entity, int z, bool passThrough)
    {
        if (passThrough)
        {
            if (_world.Components.Has<HitTestableComponent>(entity))
                _world.Remove<HitTestableComponent>(entity);
        }
        else if (_world.Components.TryGet(entity, out HitTestableComponent hit))
        {
            hit.ZIndex = z;
            _world.Set(entity, hit);
        }
        else
        {
            _world.Set(entity, new HitTestableComponent { IsVisible = true, IsEnabled = true, ZIndex = z });
        }

        if (!_world.Hierarchy.TryGetNode(entity, out HierarchyNode node))
            return;
        UiEntity child = node.FirstChild;
        while (child != UiEntity.None)
        {
            UiEntity next = _world.Hierarchy.TryGetNode(child, out HierarchyNode childNode)
                ? childNode.NextSibling
                : UiEntity.None;
            ConfigureInputSubtree(child, z, passThrough);
            child = next;
        }
    }

    private bool UpdatePlacement(Entry entry)
    {
        if (!_world.Entities.IsAlive(entry.RootEntity) ||
            !_world.Components.TryGet(entry.RootEntity, out DesiredSize desired))
        {
            return false;
        }

        UiSize viewport = _runtime.Layout.Viewport;
        float width = Math.Min(desired.Value.Width, Math.Max(0f, viewport.Width - entry.ViewportPadding * 2f));
        float height = Math.Min(desired.Value.Height, Math.Max(0f, viewport.Height - entry.ViewportPadding * 2f));
        UiRect anchor = !entry.AnchorEntity.IsNone
            ? UiVisualGeometry.ResolveBounds(_world, entry.AnchorEntity)
            : new UiRect(entry.PointerAnchor.X, entry.PointerAnchor.Y, 0f, 0f);
        (float x, float y) = ResolvePlacement(entry, anchor, width, height, viewport);
        x = Math.Clamp(x, entry.ViewportPadding, Math.Max(entry.ViewportPadding, viewport.Width - entry.ViewportPadding - width));
        y = Math.Clamp(y, entry.ViewportPadding, Math.Max(entry.ViewportPadding, viewport.Height - entry.ViewportPadding - height));
        AbsolutePlacement next = new() { Left = x, Top = y };
        if (_world.Components.TryGet(entry.RootEntity, out AbsolutePlacement current) &&
            current.Left.Equals(next.Left) && current.Top.Equals(next.Top))
        {
            return false;
        }
        _world.Set(entry.RootEntity, next);
        LayoutInvalidation.MarkArrange(_world, OverlayRoot);
        return true;
    }

    private static (float X, float Y) ResolvePlacement(
        Entry entry,
        UiRect anchor,
        float width,
        float height,
        UiSize viewport)
    {
        UiOverlayPlacement placement = entry.Placement;
        if (placement == UiOverlayPlacement.Auto)
        {
            placement = anchor.Bottom + entry.Offset + height <= viewport.Height - entry.ViewportPadding
                ? UiOverlayPlacement.BelowStart
                : UiOverlayPlacement.AboveStart;
        }
        return placement switch
        {
            UiOverlayPlacement.BelowStart => (anchor.X, anchor.Bottom + entry.Offset),
            UiOverlayPlacement.BelowEnd => (anchor.Right - width, anchor.Bottom + entry.Offset),
            UiOverlayPlacement.AboveStart => (anchor.X, anchor.Y - entry.Offset - height),
            UiOverlayPlacement.AboveEnd => (anchor.Right - width, anchor.Y - entry.Offset - height),
            UiOverlayPlacement.Pointer => (entry.PointerAnchor.X + entry.Offset, entry.PointerAnchor.Y + entry.Offset),
            UiOverlayPlacement.Center => ((viewport.Width - width) * 0.5f, (viewport.Height - height) * 0.5f),
            _ => (anchor.X, anchor.Bottom + entry.Offset)
        };
    }

    private void BeginTooltip(TooltipEntry tooltip, in UiRoutedEventData data)
    {
        tooltip.Pointer = data.Position;
        tooltip.Pending = true;
        tooltip.ShowAt = data.Timestamp.AddSeconds(tooltip.Options.DelaySeconds);
        if (tooltip.Options.DelaySeconds == 0d)
            OpenTooltip(tooltip, data.Timestamp);
        UpdateTimerLease();
    }

    private static void UpdateTooltipPointer(TooltipEntry tooltip, in UiRoutedEventData data) =>
        tooltip.Pointer = data.Position;

    private void EndTooltip(TooltipEntry tooltip)
    {
        tooltip.Pending = false;
        if (!tooltip.ActiveOverlay.IsNone)
        {
            UiOverlayHandle handle = tooltip.ActiveOverlay;
            tooltip.ActiveOverlay = UiOverlayHandle.None;
            Close(handle);
        }
        UpdateTimerLease();
    }

    private void OpenTooltip(TooltipEntry tooltip, UiTimestamp now)
    {
        if (!tooltip.Pending || !tooltip.ActiveOverlay.IsNone || !_world.Entities.IsAlive(tooltip.Owner))
            return;
        tooltip.Pending = false;
        tooltip.ActiveOverlay = Open(
            UiOverlayKind.Tooltip,
            tooltip.Content,
            _world.Entities.GetScope(tooltip.Owner),
            tooltip.Owner,
            tooltip.Pointer,
            tooltip.Options.Placement,
            tooltip.Options.Offset,
            tooltip.Options.ViewportPadding,
            createBarrier: false,
            dismissOnBarrier: false,
            dismissOnEscape: false,
            focusScope: false,
            trapFocus: false,
            restorePreviousFocus: false,
            passThrough: true,
            autoCloseSeconds: tooltip.Options.AutoCloseSeconds);
        _ = now;
    }

    private void DisposeTooltip(TooltipEntry tooltip)
    {
        tooltip.Pending = false;
        if (!tooltip.ActiveOverlay.IsNone)
        {
            UiOverlayHandle handle = tooltip.ActiveOverlay;
            tooltip.ActiveOverlay = UiOverlayHandle.None;
            Close(handle);
        }
        for (int i = 0; i < tooltip.Registrations.Count; i++)
            tooltip.Registrations[i].Dispose();
        tooltip.Registrations.Clear();
    }

    private void OnEntityDestroying(UiEntity entity)
    {
        foreach (TooltipEntry tooltip in _tooltips.Values.Where(item => item.Owner == entity).ToArray())
        {
            _tooltips.Remove(tooltip.Id);
            DisposeTooltip(tooltip);
        }
    }

    private void ReleaseEntry(Entry entry)
    {
        if (!TryGet(entry.Handle, out Entry live) || !ReferenceEquals(live, entry))
            return;
        for (int i = 0; i < entry.Registrations.Count; i++)
            entry.Registrations[i].Dispose();
        entry.Registrations.Clear();
        entry.ScopeRegistration?.Dispose();
        entry.ScopeRegistration = null;
        foreach (TooltipEntry tooltip in _tooltips.Values)
        {
            if (tooltip.ActiveOverlay == entry.Handle)
                tooltip.ActiveOverlay = UiOverlayHandle.None;
        }
        _entries[entry.Handle.Index] = null;
        uint next = unchecked(_generations[entry.Handle.Index] + 1);
        _generations[entry.Handle.Index] = next == 0 ? 1 : next;
        _free.Push(entry.Handle.Index);
    }

    private UiOverlayHandle AllocateHandle()
    {
        int index;
        if (_free.TryPop(out int recycled))
        {
            index = recycled;
        }
        else
        {
            index = _entries.Count;
            _entries.Add(null);
            _generations.Add(1);
        }
        return new UiOverlayHandle(index, _generations[index]);
    }

    private void RecycleUnusedHandle(UiOverlayHandle handle)
    {
        uint next = unchecked(_generations[handle.Index] + 1);
        _generations[handle.Index] = next == 0 ? 1 : next;
        _free.Push(handle.Index);
    }

    private bool TryGet(UiOverlayHandle handle, out Entry entry)
    {
        if (handle.IsNone || handle.Index >= _entries.Count || _generations[handle.Index] != handle.Generation)
        {
            entry = null!;
            return false;
        }
        Entry? candidate = _entries[handle.Index];
        if (candidate is null)
        {
            entry = null!;
            return false;
        }
        entry = candidate;
        return true;
    }

    private void UpdateTimerLease()
    {
        bool needsTimer = _tooltips.Values.Any(static tooltip => tooltip.Pending) ||
                          _entries.Any(static entry => entry is not null && entry.AutoCloseAt != UiTimestamp.Zero);
        if (needsTimer && _timerLease is null)
            _timerLease = _world.Scheduler.AcquireContinuousFrame(UiContinuousReason.OverlayTimer);
        else if (!needsTimer && _timerLease is not null)
        {
            _timerLease.Dispose();
            _timerLease = null;
        }
    }

    private void ValidateAnchor(UiEntity anchor)
    {
        _world.Entities.EnsureAlive(anchor);
        if (!_world.Entities.TryGetScope(anchor, out UiScopeId scope) || !IsScopeInWindow(scope))
            throw new InvalidOperationException("Overlay anchor is outside the configured window scope: " + anchor);
    }

    private bool IsScopeInWindow(UiScopeId scope)
    {
        int guard = 0;
        while (_world.Scopes.IsAlive(scope) && guard++ < 1_000_000)
        {
            if (scope == _windowScope)
                return true;
            if (!_world.Scopes.TryGetParent(scope, out scope) || scope.IsNone)
                break;
        }
        return false;
    }

    private static void ValidatePlacement(UiOverlayPlacement placement, bool allowCenter, bool allowPointer)
    {
        if (!Enum.IsDefined(placement) ||
            (!allowCenter && placement == UiOverlayPlacement.Center) ||
            (!allowPointer && placement == UiOverlayPlacement.Pointer))
            throw new ArgumentOutOfRangeException(nameof(placement));
    }

    private static void ValidateGeometry(float offset, float viewportPadding)
    {
        if (!float.IsFinite(offset) || offset < 0f)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (!float.IsFinite(viewportPadding) || viewportPadding < 0f)
            throw new ArgumentOutOfRangeException(nameof(viewportPadding));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class TooltipEntry(
        int id,
        UiEntity owner,
        UiBlueprint content,
        UiTooltipOptions options)
    {
        public int Id { get; } = id;
        public UiEntity Owner { get; } = owner;
        public UiBlueprint Content { get; } = content;
        public UiTooltipOptions Options { get; } = options;
        public List<IDisposable> Registrations { get; } = [];
        public bool Pending { get; set; }
        public UiTimestamp ShowAt { get; set; }
        public UiPoint Pointer { get; set; }
        public UiOverlayHandle ActiveOverlay { get; set; }
    }

    private sealed class Entry(
        UiOverlayHandle handle,
        UiOverlayKind kind,
        UiScopeId scope,
        BlueprintInstance instance,
        UiEntity rootEntity,
        UiEntity barrierEntity,
        UiEntity anchorEntity,
        UiPoint pointerAnchor,
        UiOverlayPlacement placement,
        float offset,
        float viewportPadding,
        bool focusScope,
        UiTimestamp autoCloseAt)
    {
        public UiOverlayHandle Handle { get; } = handle;
        public UiOverlayKind Kind { get; } = kind;
        public UiScopeId Scope { get; } = scope;
        public BlueprintInstance Instance { get; } = instance;
        public UiEntity RootEntity { get; } = rootEntity;
        public UiEntity BarrierEntity { get; } = barrierEntity;
        public UiEntity AnchorEntity { get; } = anchorEntity;
        public UiPoint PointerAnchor { get; } = pointerAnchor;
        public UiOverlayPlacement Placement { get; } = placement;
        public float Offset { get; } = offset;
        public float ViewportPadding { get; } = viewportPadding;
        public bool FocusScope { get; } = focusScope;
        public UiTimestamp AutoCloseAt { get; } = autoCloseAt;
        public List<IDisposable> Registrations { get; } = [];
        public IDisposable? ScopeRegistration { get; set; }
        public bool Closing { get; set; }
    }
}

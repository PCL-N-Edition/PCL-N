// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Generation-safe page scope, transition and cache state machine.</summary>
public sealed class UiNavigationRuntime : IUiSystem, IDisposable
{
    private readonly UiWorld _world;
    private readonly UiInteractiveRuntime _runtime;
    private readonly BlueprintInstantiator _instantiator;
    private readonly UiScopeId _hostScope;
    private readonly UiNavigationOptions _options;
    private readonly Dictionary<UiPageKey, UiPageDefinition> _definitions = [];
    private readonly Dictionary<UiPageKey, PageEntry> _pages = [];
    private readonly UiAnimationEventReader _animationEvents;
    private readonly IDisposable _hostScopeRegistration;
    private UiNavigationRequest? _requested;
    private PreparedNavigation? _prepared;
    private ActiveTransition? _transition;
    private UiPageKey _currentPage;
    private uint _navigationGeneration;
    private long _useSequence;
    private bool _disposed;

    public UiNavigationRuntime(
        UiWorld world,
        UiInteractiveRuntime runtime,
        BlueprintInstantiator instantiator,
        UiScopeId hostScope,
        UiEntity parent = default,
        UiNavigationOptions? options = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _instantiator = instantiator ?? throw new ArgumentNullException(nameof(instantiator));
        if (!ReferenceEquals(world, runtime.World))
            throw new InvalidOperationException("Navigation and interactive runtimes must use the same world.");
        if (!world.Scopes.IsAlive(hostScope))
            throw new InvalidOperationException("Navigation host scope is not alive: " + hostScope);
        _hostScope = hostScope;
        _options = options ?? UiNavigationOptions.Default;
        ValidateOptions(_options);
        NavigationRoot = CreateNavigationRoot(parent);
        Events = new UiNavigationEventJournal();
        _animationEvents = runtime.Animation.Events.CreateReader(UiAnimationEventReaderStart.NextPublished);
        _hostScopeRegistration = world.Scopes.RegisterDisposeHandler(hostScope, _ => Dispose());
        _world.Systems.Register(this);
    }

    public UiSystemPhase Phase => UiSystemPhase.TransitionPlanning;
    public string Name => "navigation.update";
    public UiEntity NavigationRoot { get; }
    public UiNavigationEventJournal Events { get; }
    public UiPageKey CurrentPage => _currentPage;
    public uint NavigationGeneration => _navigationGeneration;
    public int LivePageCount => _pages.Count;

    public void Register(UiPageDefinition definition)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(definition);
        if (_definitions.ContainsKey(definition.Key))
            throw new InvalidOperationException("Page is already registered: " + definition.Key);
        _definitions.Add(definition.Key, definition);
    }

    public bool Unregister(UiPageKey page)
    {
        ThrowIfDisposed();
        if (_pages.ContainsKey(page))
            throw new InvalidOperationException("Cannot unregister a page while its scope is alive: " + page);
        return _definitions.Remove(page);
    }

    public UiNavigationRequest Navigate(UiPageKey page)
    {
        ThrowIfDisposed();
        if (!_definitions.ContainsKey(page))
            throw new KeyNotFoundException("Page is not registered: " + page);
        uint generation = unchecked(_navigationGeneration + 1);
        if (generation == 0)
            generation = 1;
        _navigationGeneration = generation;
        UiNavigationRequest request = new(page, generation);
        _requested = request;
        Events.Publish(
            _world.FrameIndex,
            UiNavigationEventKind.Requested,
            generation,
            page,
            UiNavigationPageState.Created);
        _world.Scheduler.RequestReactiveFrame();
        return request;
    }

    public bool TryGetPage(UiPageKey page, out UiNavigationPageSnapshot snapshot)
    {
        if (!_pages.TryGetValue(page, out PageEntry? entry))
        {
            snapshot = default;
            return false;
        }
        snapshot = ToSnapshot(entry);
        return true;
    }

    public void CopyPagesTo(List<UiNavigationPageSnapshot> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        foreach (PageEntry entry in _pages.Values)
            destination.Add(ToSnapshot(entry));
    }

    public void Update(UiWorld world, in UiFrameContext frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DrainCompletedGroups(frame.FrameIndex);

        if (_requested is { } request)
        {
            _requested = null;
            if (_prepared is { } obsolete && obsolete.Generation != request.Generation)
                CancelPrepared(obsolete, frame.FrameIndex);
            Prepare(request, frame.FrameIndex);
        }

        if (_prepared is { } prepared &&
            prepared.Generation == _navigationGeneration &&
            frame.FrameIndex > prepared.PreparedFrame)
        {
            _prepared = null;
            StartTransition(prepared, frame.FrameIndex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _world.Systems.Unregister(this);
        _requested = null;
        _prepared = null;
        _transition = null;
        foreach (PageEntry page in _pages.Values.ToArray())
            DestroyPage(page, UiNavigationEventKind.Canceled, _world.FrameIndex);
        _pages.Clear();
        if (_world.Entities.IsAlive(NavigationRoot))
            _world.DestroyEntity(NavigationRoot);
        _hostScopeRegistration.Dispose();
        Events.Clear();
    }

    private UiEntity CreateNavigationRoot(UiEntity parent)
    {
        if (!parent.IsNone)
        {
            _world.Entities.EnsureAlive(parent);
            if (!_world.Entities.TryGetScope(parent, out UiScopeId parentScope) || !IsScopeInHost(parentScope))
                throw new InvalidOperationException("Navigation parent is outside the host scope: " + parent);
        }
        UiEntity root = _world.CreateEntity(_hostScope, asHierarchyRoot: parent.IsNone);
        if (!parent.IsNone)
            _world.AttachChild(parent, root);
        _world.Set(root, new NodeKindComponent { Kind = UiNodeKind.Overlay });
        _world.Set(root, new LayoutStyle
        {
            Width = UiLength.Percent(1f),
            Height = UiLength.Percent(1f),
            MinSize = UiSize.Zero,
            MaxSize = UiSize.Infinite,
            HorizontalAlignment = UiHorizontalAlignment.Stretch,
            VerticalAlignment = UiVerticalAlignment.Stretch,
            IsMeasureBoundary = true
        });
        _world.Set(root, new OverlayLayout());
        _world.Dirty.Mark(root, UiDirtyFlags.StructuralCascade | UiDirtyFlags.Style);
        return root;
    }

    private void Prepare(UiNavigationRequest request, long frameIndex)
    {
        if (!_currentPage.IsNone && _currentPage == request.Page &&
            _pages.TryGetValue(request.Page, out PageEntry? current) &&
            current.State == UiNavigationPageState.Active && _transition is null)
        {
            SetState(current, UiNavigationPageState.Active, request.Generation, frameIndex);
            current.LastUsedSequence = ++_useSequence;
            Events.Publish(
                frameIndex,
                UiNavigationEventKind.Completed,
                request.Generation,
                request.Page,
                UiNavigationPageState.Active);
            return;
        }

        PageEntry target = GetOrCreatePage(request.Page, request.Generation, frameIndex);
        SetState(target, UiNavigationPageState.Preparing, request.Generation, frameIndex);
        SetPageInteraction(target, visible: false, enabled: false);
        target.LastUsedSequence = ++_useSequence;
        _prepared = new PreparedNavigation(request.Page, request.Generation, frameIndex);
        _world.Scheduler.RequestReactiveFrame();
    }

    private void StartTransition(PreparedNavigation prepared, long frameIndex)
    {
        if (!_pages.TryGetValue(prepared.Page, out PageEntry? target) ||
            !_world.Entities.IsAlive(target.RootEntity))
        {
            Events.Publish(
                frameIndex,
                UiNavigationEventKind.Canceled,
                prepared.Generation,
                prepared.Page,
                UiNavigationPageState.Destroyed);
            return;
        }

        List<PageEntry> outgoing = [];
        List<UiAnimationHandle> channels = [];
        foreach (PageEntry page in _pages.Values)
        {
            if (ReferenceEquals(page, target) ||
                page.State == UiNavigationPageState.Dormant ||
                page.State == UiNavigationPageState.Destroyed)
            {
                continue;
            }
            SetState(page, UiNavigationPageState.Leaving, prepared.Generation, frameIndex);
            SetPageInteraction(page, visible: true, enabled: false);
            channels.Add(_runtime.Animation.Retarget(
                page.RootEntity,
                UiAnimationProperty.Opacity,
                0f,
                NavigationSpec()));
            channels.Add(_runtime.Animation.Retarget(
                page.RootEntity,
                UiAnimationProperty.TranslateX,
                _options.ExitOffset,
                NavigationSpec()));
            outgoing.Add(page);
        }

        _runtime.Animation.SetDirect(
            target.RootEntity,
            UiAnimationProperty.Opacity,
            0f,
            owner: UiAnimationOwnerReason.Navigation);
        _runtime.Animation.SetDirect(
            target.RootEntity,
            UiAnimationProperty.TranslateX,
            _options.EnterOffset,
            owner: UiAnimationOwnerReason.Navigation);
        SetState(target, UiNavigationPageState.Entering, prepared.Generation, frameIndex);
        SetPageInteraction(target, visible: true, enabled: true);
        channels.Add(_runtime.Animation.Retarget(
            target.RootEntity,
            UiAnimationProperty.Opacity,
            1f,
            NavigationSpec()));
        channels.Add(_runtime.Animation.Retarget(
            target.RootEntity,
            UiAnimationProperty.TranslateX,
            0f,
            NavigationSpec()));

        UiTransitionGroupId group = _runtime.Animation.CreateTransitionGroup(_hostScope, channels.ToArray());
        _transition = new ActiveTransition(group, prepared.Page, prepared.Generation, outgoing);
        _world.Scheduler.RequestReactiveFrame();
    }

    private UiAnimationSpec NavigationSpec() =>
        new(_options.Motion, owner: UiAnimationOwnerReason.Navigation);

    private void DrainCompletedGroups(long frameIndex)
    {
        while (_animationEvents.TryRead(out UiAnimationEvent animationEvent))
        {
            if (animationEvent.Kind != UiAnimationEventKind.TransitionGroupCompleted)
                continue;
            UiTransitionGroupCompleted completed = animationEvent.TransitionGroup;
            if (_transition is not { } transition ||
                transition.Group != completed.Group ||
                transition.Generation != _navigationGeneration)
            {
                continue;
            }
            _transition = null;
            CompleteTransition(transition, frameIndex);
        }
    }

    private void CompleteTransition(ActiveTransition transition, long frameIndex)
    {
        if (_pages.TryGetValue(transition.Target, out PageEntry? target) &&
            target.NavigationGeneration == transition.Generation)
        {
            SetState(target, UiNavigationPageState.Active, transition.Generation, frameIndex);
            SetPageInteraction(target, visible: true, enabled: true);
            target.LastUsedSequence = ++_useSequence;
            _currentPage = target.Definition.Key;
        }
        else
        {
            _currentPage = default;
        }

        for (int i = 0; i < transition.Outgoing.Count; i++)
        {
            PageEntry outgoing = transition.Outgoing[i];
            if (!_pages.TryGetValue(outgoing.Definition.Key, out PageEntry? live) ||
                !ReferenceEquals(live, outgoing) ||
                outgoing.NavigationGeneration != transition.Generation)
            {
                continue;
            }
            FinalizeOutgoing(outgoing, frameIndex);
        }
        EnforceLru(frameIndex);
        Events.Publish(
            frameIndex,
            UiNavigationEventKind.Completed,
            transition.Generation,
            transition.Target,
            UiNavigationPageState.Active);
    }

    private void FinalizeOutgoing(PageEntry page, long frameIndex)
    {
        switch (page.Definition.CachePolicy)
        {
            case UiPageCachePolicy.KeepEntities:
            case UiPageCachePolicy.Lru:
            case UiPageCachePolicy.Pinned:
                SetState(page, UiNavigationPageState.Dormant, page.NavigationGeneration, frameIndex);
                SetPageInteraction(page, visible: false, enabled: false);
                break;
            case UiPageCachePolicy.None:
            case UiPageCachePolicy.KeepPresentationState:
                DestroyPage(page, UiNavigationEventKind.StateChanged, frameIndex);
                break;
            default:
                throw new InvalidOperationException("Unsupported page cache policy: " + page.Definition.CachePolicy);
        }
    }

    private void EnforceLru(long frameIndex)
    {
        PageEntry[] dormant = _pages.Values
            .Where(static page => page.Definition.CachePolicy == UiPageCachePolicy.Lru &&
                                  page.State == UiNavigationPageState.Dormant)
            .OrderBy(static page => page.LastUsedSequence)
            .ToArray();
        int excess = dormant.Length - _options.LruCapacity;
        for (int i = 0; i < excess; i++)
            DestroyPage(dormant[i], UiNavigationEventKind.CacheEvicted, frameIndex);
    }

    private PageEntry GetOrCreatePage(UiPageKey key, uint generation, long frameIndex)
    {
        if (_pages.TryGetValue(key, out PageEntry? cached) &&
            _world.Entities.IsAlive(cached.RootEntity) &&
            _world.Scopes.IsAlive(cached.Scope))
        {
            return cached;
        }

        UiPageDefinition definition = _definitions[key];
        UiScopeId scope = _world.CreateScope(_hostScope);
        try
        {
            BlueprintInstance instance = _instantiator.Instantiate(definition.Blueprint, scope);
            UiEntity root = instance.RootEntity;
            _world.AttachChild(NavigationRoot, root);
            if (_world.Components.TryGet(root, out HitTestableComponent hit))
            {
                hit.IsVisible = true;
                hit.IsEnabled = false;
                _world.Set(root, hit);
            }
            else
            {
                _world.Set(root, new HitTestableComponent { IsVisible = true, IsEnabled = false });
            }
            PageEntry page = new(definition, scope, instance, root, generation);
            _pages[key] = page;
            page.ScopeRegistration = _world.Scopes.RegisterDisposeHandler(scope, _ => OnPageScopeDisposed(page));
            SetState(page, UiNavigationPageState.Created, generation, frameIndex);
            return page;
        }
        catch
        {
            if (_world.Scopes.IsAlive(scope))
                _world.DisposeScope(scope);
            throw;
        }
    }

    private void CancelPrepared(PreparedNavigation prepared, long frameIndex)
    {
        if (!_pages.TryGetValue(prepared.Page, out PageEntry? page) || page.State != UiNavigationPageState.Preparing)
            return;
        if (page.Definition.CachePolicy is UiPageCachePolicy.KeepEntities or UiPageCachePolicy.Lru or UiPageCachePolicy.Pinned)
        {
            SetState(page, UiNavigationPageState.Dormant, prepared.Generation, frameIndex);
            SetPageInteraction(page, visible: false, enabled: false);
        }
        else
        {
            DestroyPage(page, UiNavigationEventKind.StateChanged, frameIndex);
        }
        Events.Publish(
            frameIndex,
            UiNavigationEventKind.Canceled,
            prepared.Generation,
            prepared.Page,
            page.State);
    }

    private void DestroyPage(PageEntry page, UiNavigationEventKind reason, long frameIndex)
    {
        if (page.Destroying || page.State == UiNavigationPageState.Destroyed)
            return;
        page.Destroying = true;
        SetState(page, UiNavigationPageState.Destroyed, page.NavigationGeneration, frameIndex);
        if (reason != UiNavigationEventKind.StateChanged)
        {
            Events.Publish(
                frameIndex,
                reason,
                page.NavigationGeneration,
                page.Definition.Key,
                UiNavigationPageState.Destroyed);
        }
        if (_world.Scopes.IsAlive(page.Scope))
            _world.DisposeScope(page.Scope);
        else
            OnPageScopeDisposed(page);
    }

    private void OnPageScopeDisposed(PageEntry page)
    {
        page.ScopeRegistration?.Dispose();
        page.ScopeRegistration = null;
        if (_pages.TryGetValue(page.Definition.Key, out PageEntry? current) && ReferenceEquals(current, page))
            _pages.Remove(page.Definition.Key);
        if (_currentPage == page.Definition.Key)
            _currentPage = default;
        if (_prepared is { } prepared && prepared.Page == page.Definition.Key)
            _prepared = null;
        if (_transition is { } transition &&
            (transition.Target == page.Definition.Key || transition.Outgoing.Contains(page)))
        {
            _transition = null;
            Events.Publish(
                _world.FrameIndex,
                UiNavigationEventKind.Canceled,
                transition.Generation,
                transition.Target,
                UiNavigationPageState.Destroyed);
        }
    }

    private void SetState(PageEntry page, UiNavigationPageState state, uint generation, long frameIndex)
    {
        page.State = state;
        page.NavigationGeneration = generation;
        if (_world.Entities.IsAlive(page.RootEntity))
        {
            _world.Set(page.RootEntity, new NavigationPageComponent
            {
                Page = page.Definition.Key,
                State = state,
                NavigationGeneration = generation
            });
        }
        Events.Publish(
            frameIndex,
            UiNavigationEventKind.StateChanged,
            generation,
            page.Definition.Key,
            state);
    }

    private void SetPageInteraction(PageEntry page, bool visible, bool enabled)
    {
        if (!_world.Entities.IsAlive(page.RootEntity))
            return;
        HitTestableComponent hit = _world.Components.TryGet(page.RootEntity, out HitTestableComponent current)
            ? current
            : HitTestableComponent.Default;
        hit.IsVisible = visible;
        hit.IsEnabled = enabled;
        _world.Set(page.RootEntity, hit);
        _world.Dirty.Mark(
            page.RootEntity,
            UiDirtyFlags.HitTest | UiDirtyFlags.Render | UiDirtyFlags.Accessibility);
        _world.Scheduler.RequestReactiveFrame();
    }

    private bool IsScopeInHost(UiScopeId scope)
    {
        int guard = 0;
        while (_world.Scopes.IsAlive(scope) && guard++ < 1_000_000)
        {
            if (scope == _hostScope)
                return true;
            if (!_world.Scopes.TryGetParent(scope, out scope) || scope.IsNone)
                break;
        }
        return false;
    }

    private static UiNavigationPageSnapshot ToSnapshot(PageEntry page) => new(
        page.Definition.Key,
        page.State,
        page.Definition.CachePolicy,
        page.Scope,
        page.RootEntity,
        page.NavigationGeneration,
        page.LastUsedSequence);

    private static void ValidateOptions(UiNavigationOptions options)
    {
        if (options.LruCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "LRU capacity cannot be negative.");
        if (!float.IsFinite(options.EnterOffset) || !float.IsFinite(options.ExitOffset))
            throw new ArgumentOutOfRangeException(nameof(options), "Navigation offsets must be finite.");
        if (options.Motion.IsNone)
            throw new ArgumentOutOfRangeException(nameof(options), "Navigation motion cannot be None.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private readonly record struct PreparedNavigation(UiPageKey Page, uint Generation, long PreparedFrame);

    private sealed record ActiveTransition(
        UiTransitionGroupId Group,
        UiPageKey Target,
        uint Generation,
        List<PageEntry> Outgoing);

    private sealed class PageEntry(
        UiPageDefinition definition,
        UiScopeId scope,
        BlueprintInstance instance,
        UiEntity rootEntity,
        uint generation)
    {
        public UiPageDefinition Definition { get; } = definition;
        public UiScopeId Scope { get; } = scope;
        public BlueprintInstance Instance { get; } = instance;
        public UiEntity RootEntity { get; } = rootEntity;
        public UiNavigationPageState State { get; set; } = UiNavigationPageState.Created;
        public uint NavigationGeneration { get; set; } = generation;
        public long LastUsedSequence { get; set; }
        public IDisposable? ScopeRegistration { get; set; }
        public bool Destroying { get; set; }
    }
}

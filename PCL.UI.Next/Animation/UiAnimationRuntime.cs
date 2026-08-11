// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Numerics;

namespace PCL.UI.Next;

/// <summary>Runtime-authoritative target-driven animation composition root.</summary>
public sealed class UiAnimationRuntime : IDisposable
{
    private readonly FloatAnimationStore _store;
    private readonly TransitionGroupStore _groups;
    private readonly StyleTransitionPlanningSystem _stylePlanning;
    private readonly LayoutTransitionPlanningSystem _layoutPlanning;
    private readonly AnimationTickSystem _tickSystem;
    private readonly TransformCompositionSystem _transformSystem;
    private bool _motionPolicyDirty;
    private bool _disposed;

    public UiAnimationRuntime(UiWorld world, UiMotionRegistry? motions = null)
    {
        World = world ?? throw new ArgumentNullException(nameof(world));
        Motions = motions ?? new UiMotionRegistry();
        _store = new FloatAnimationStore(world, Motions);
        _groups = new TransitionGroupStore(world, _store);
        _stylePlanning = new StyleTransitionPlanningSystem(this);
        _layoutPlanning = new LayoutTransitionPlanningSystem(this);
        _tickSystem = new AnimationTickSystem(this);
        _transformSystem = new TransformCompositionSystem();
        World.EntityDestroying += OnEntityDestroying;
        World.Systems.Register(_stylePlanning);
        World.Systems.Register(_layoutPlanning);
        World.Systems.Register(_tickSystem);
        World.Systems.Register(_transformSystem);
    }

    public UiWorld World { get; }

    public UiMotionRegistry Motions { get; }

    public int ChannelCount => _store.ChannelCount;

    public int ActiveChannelCount => _store.ActiveCount;

    public IReadOnlyList<UiAnimationSettled> FrameSettlements => _store.FrameSettlements;

    public int ActiveTransitionGroupCount => _groups.ActiveCount;

    public IReadOnlyList<UiTransitionGroupCompleted> FrameTransitionGroupCompletions =>
        _groups.FrameCompletions;

    public bool AnimationsEnabled { get; private set; } = true;

    public bool ReducedMotion { get; private set; }

    public UiAnimationHandle Retarget(
        UiEntity entity,
        UiAnimationProperty property,
        float target,
        in UiAnimationSpec spec)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UiAnimationHandle handle = _store.Retarget(
            entity,
            property,
            target,
            in spec,
            AnimationsEnabled,
            ReducedMotion);
        InvalidateRetargetedGroups(handle);
        World.Scheduler.RequestReactiveFrame();
        RefreshScheduling();
        return handle;
    }

    public UiAnimationHandle SetDirect(
        UiEntity entity,
        UiAnimationProperty property,
        float current,
        float velocity = 0f,
        UiAnimationOwnerReason owner = UiAnimationOwnerReason.Gesture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UiAnimationHandle handle = _store.SetDirect(entity, property, current, velocity, owner);
        InvalidateRetargetedGroups(handle);
        World.Scheduler.RequestReactiveFrame();
        RefreshScheduling();
        return handle;
    }

    public UiAnimationHandle StartDecay(
        UiEntity entity,
        UiAnimationProperty property,
        float velocity,
        in UiAnimationSpec spec)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UiAnimationHandle handle = _store.StartDecay(
            entity,
            property,
            velocity,
            in spec,
            AnimationsEnabled,
            ReducedMotion);
        InvalidateRetargetedGroups(handle);
        RefreshScheduling();
        return handle;
    }

    public bool Cancel(
        UiEntity entity,
        UiAnimationProperty property,
        UiAnimationCancelMode mode = UiAnimationCancelMode.SnapToCurrent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool hadChannel = _store.TryGetSnapshot(entity, property, out UiAnimationSnapshot snapshot);
        bool canceled = _store.Cancel(entity, property, mode);
        if (canceled)
        {
            if (hadChannel)
                _groups.InvalidateChannel(snapshot.Channel);
            World.Scheduler.RequestReactiveFrame();
        }
        RefreshScheduling();
        return canceled;
    }

    public bool TryGetSnapshot(
        UiEntity entity,
        UiAnimationProperty property,
        out UiAnimationSnapshot snapshot) =>
        _store.TryGetSnapshot(entity, property, out snapshot);

    public bool TryGetSnapshot(UiAnimationHandle handle, out UiAnimationSnapshot snapshot) =>
        _store.TryGetSnapshot(handle, out snapshot);

    public bool IsCurrent(in UiAnimationSettled settled) => _store.IsCurrent(in settled);

    public UiTransitionGroupId CreateTransitionGroup(
        UiScopeId scope,
        ReadOnlySpan<UiAnimationHandle> channels)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _groups.Create(scope, channels);
    }

    public void SetAnimationsEnabled(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (AnimationsEnabled == enabled)
            return;
        AnimationsEnabled = enabled;
        _motionPolicyDirty = true;
        World.Scheduler.RequestReactiveFrame();
    }

    public void SetReducedMotion(bool reducedMotion)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ReducedMotion == reducedMotion)
            return;
        ReducedMotion = reducedMotion;
        _motionPolicyDirty = true;
        World.Scheduler.RequestReactiveFrame();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        World.Systems.Unregister(_transformSystem);
        World.Systems.Unregister(_tickSystem);
        World.Systems.Unregister(_layoutPlanning);
        World.Systems.Unregister(_stylePlanning);
        World.EntityDestroying -= OnEntityDestroying;
        _layoutPlanning.Dispose();
        _groups.Clear();
        _store.Clear();
        World.Scheduler.ReleaseContinuousFrame(UiContinuousReason.Animation);
        _disposed = true;
    }

    internal void BeginPlanningFrame()
    {
        _store.BeginFrame();
        _groups.BeginFrame();
        if (!_motionPolicyDirty)
            return;
        _store.ApplyMotionPolicy(AnimationsEnabled, ReducedMotion);
        _motionPolicyDirty = false;
        RefreshScheduling();
    }

    internal void Tick(in UiFrameContext frame)
    {
        _store.Tick(frame.FrameIndex, frame.DeltaSeconds);
        _groups.ProcessSettlements();
        RefreshScheduling();
    }

    internal void Snap(
        UiEntity entity,
        UiAnimationProperty property,
        float target,
        UiAnimationOwnerReason owner)
    {
        UiAnimationHandle handle = _store.SetDirect(entity, property, target, velocity: 0f, owner);
        InvalidateRetargetedGroups(handle);
    }

    private void RefreshScheduling()
    {
        if (_store.ActiveCount > 0)
            World.Scheduler.RequestContinuousFrame(UiContinuousReason.Animation);
        else
            World.Scheduler.ReleaseContinuousFrame(UiContinuousReason.Animation);
    }

    private void OnEntityDestroying(UiEntity entity)
    {
        _groups.RemoveEntity(entity);
        _store.RemoveEntity(entity);
        RefreshScheduling();
    }

    private void InvalidateRetargetedGroups(UiAnimationHandle handle)
    {
        if (_store.TryGetSnapshot(handle, out UiAnimationSnapshot snapshot))
            _groups.InvalidateRetarget(handle, snapshot.TargetGeneration);
    }
}

internal sealed class StyleTransitionPlanningSystem(UiAnimationRuntime animations) : IUiSystem
{
    private static readonly UiAnimationProperty[] TargetProperties =
    [
        UiAnimationProperty.Opacity,
        UiAnimationProperty.CornerRadius,
        UiAnimationProperty.TranslateX,
        UiAnimationProperty.TranslateY,
        UiAnimationProperty.ScaleX,
        UiAnimationProperty.ScaleY,
        UiAnimationProperty.Rotation
    ];

    private readonly List<UiEntity> _dirty = [];

    public UiSystemPhase Phase => UiSystemPhase.TransitionPlanning;

    public string Name => "animation.style-transitions";

    public void Update(UiWorld world, in UiFrameContext frame)
    {
        _ = frame;
        animations.BeginPlanningFrame();
        _dirty.Clear();
        world.Dirty.Collect(UiDirtyFlags.Animation, _dirty);
        for (int i = 0; i < _dirty.Count; i++)
        {
            UiEntity entity = _dirty[i];
            if (!world.Entities.IsAlive(entity) || !world.Components.TryGet(entity, out ResolvedStyle target))
                continue;

            bool hasCurrent = world.Components.TryGet(entity, out ComputedVisual visual);
            if (!hasCurrent)
            {
                visual = ComputedVisual.FromResolved(in target);
                world.Set(entity, visual);
                world.Dirty.Mark(entity, UiDirtyFlags.Transform | UiDirtyFlags.Render);
                world.Dirty.Clear(entity, UiDirtyFlags.Animation);
                continue;
            }

            visual.Background = target.Background;
            visual.Foreground = target.Foreground;
            world.Set(entity, visual);
            UiTransitionSet transitions = world.Components.TryGet(entity, out TransitionSetComponent component)
                ? component.Value
                : default;
            for (int propertyIndex = 0; propertyIndex < TargetProperties.Length; propertyIndex++)
            {
                UiAnimationProperty property = TargetProperties[propertyIndex];
                float propertyTarget = AnimationPropertyRegistry.ReadTarget(world, entity, property);
                if (transitions.TryGet(property, out UiTransitionDefinition transition))
                {
                    UiAnimationSpec spec = transition.ToSpec(UiAnimationOwnerReason.StyleTransition);
                    animations.Retarget(entity, property, propertyTarget, in spec);
                }
                else if (animations.TryGetSnapshot(entity, property, out UiAnimationSnapshot snapshot))
                {
                    if (snapshot.IsActive ||
                        !ApproximatelyEqual(snapshot.Current, propertyTarget) ||
                        !ApproximatelyEqual(snapshot.Target, propertyTarget))
                    {
                        animations.Snap(
                            entity,
                            property,
                            propertyTarget,
                            UiAnimationOwnerReason.StyleTransition);
                    }
                }
                else if (!ApproximatelyEqual(
                             AnimationPropertyRegistry.ReadCurrent(world, entity, property),
                             propertyTarget))
                {
                    AnimationPropertyRegistry.WriteCurrent(world, entity, property, propertyTarget);
                }
            }

            world.Dirty.Clear(entity, UiDirtyFlags.Animation);
        }
    }

    private static bool ApproximatelyEqual(float left, float right) =>
        MathF.Abs(left - right) <= 0.00001f;
}

internal sealed class LayoutTransitionPlanningSystem : IUiSystem, IDisposable
{
    private readonly UiAnimationRuntime _animations;
    private readonly Dictionary<UiEntity, UiRect> _previous = [];
    private readonly List<UiEntity> _dirty = [];

    public LayoutTransitionPlanningSystem(UiAnimationRuntime animations)
    {
        _animations = animations;
        _animations.World.EntityDestroying += OnEntityDestroying;
    }

    public UiSystemPhase Phase => UiSystemPhase.TransitionPlanning;

    public string Name => "animation.layout-transitions";

    public void Update(UiWorld world, in UiFrameContext frame)
    {
        _ = frame;
        _dirty.Clear();
        world.Dirty.Collect(UiDirtyFlags.Transform, _dirty);
        for (int i = 0; i < _dirty.Count; i++)
        {
            UiEntity entity = _dirty[i];
            if (!world.Entities.IsAlive(entity) ||
                !world.Components.TryGet(entity, out LayoutTransitionComponent transition) ||
                !world.Components.TryGet(entity, out LayoutRect layout))
            {
                continue;
            }

            UiRect next = layout.Value;
            if (!_previous.TryGetValue(entity, out UiRect previous))
            {
                _previous[entity] = next;
                AnimationPropertyRegistry.EnsureVisual(world, entity);
                continue;
            }
            if (previous == next)
                continue;

            float currentTranslateX = ReadCurrent(entity, UiAnimationProperty.LayoutTranslateX, 0f);
            float currentTranslateY = ReadCurrent(entity, UiAnimationProperty.LayoutTranslateY, 0f);
            float currentScaleX = ReadCurrent(entity, UiAnimationProperty.LayoutScaleX, 1f);
            float currentScaleY = ReadCurrent(entity, UiAnimationProperty.LayoutScaleY, 1f);
            float velocityTranslateX = ReadVelocity(entity, UiAnimationProperty.LayoutTranslateX);
            float velocityTranslateY = ReadVelocity(entity, UiAnimationProperty.LayoutTranslateY);
            float velocityScaleX = ReadVelocity(entity, UiAnimationProperty.LayoutScaleX);
            float velocityScaleY = ReadVelocity(entity, UiAnimationProperty.LayoutScaleY);

            float visualX = previous.X + currentTranslateX;
            float visualY = previous.Y + currentTranslateY;
            float visualWidth = previous.Width * currentScaleX;
            float visualHeight = previous.Height * currentScaleY;
            float rebasedScaleX = next.Width > 0.0001f ? visualWidth / next.Width : 1f;
            float rebasedScaleY = next.Height > 0.0001f ? visualHeight / next.Height : 1f;
            float scaleVelocityX = next.Width > 0.0001f
                ? velocityScaleX * previous.Width / next.Width
                : 0f;
            float scaleVelocityY = next.Height > 0.0001f
                ? velocityScaleY * previous.Height / next.Height
                : 0f;

            _animations.SetDirect(
                entity,
                UiAnimationProperty.LayoutTranslateX,
                visualX - next.X,
                velocityTranslateX,
                UiAnimationOwnerReason.LayoutTransition);
            _animations.SetDirect(
                entity,
                UiAnimationProperty.LayoutTranslateY,
                visualY - next.Y,
                velocityTranslateY,
                UiAnimationOwnerReason.LayoutTransition);
            _animations.SetDirect(
                entity,
                UiAnimationProperty.LayoutScaleX,
                rebasedScaleX,
                scaleVelocityX,
                UiAnimationOwnerReason.LayoutTransition);
            _animations.SetDirect(
                entity,
                UiAnimationProperty.LayoutScaleY,
                rebasedScaleY,
                scaleVelocityY,
                UiAnimationOwnerReason.LayoutTransition);

            UiAnimationSpec spec = new(
                transition.Motion,
                UiAnimationContinuity.PreserveVelocity,
                UiAnimationFlags.AllowRebase,
                UiAnimationOwnerReason.LayoutTransition);
            _animations.Retarget(entity, UiAnimationProperty.LayoutTranslateX, 0f, in spec);
            _animations.Retarget(entity, UiAnimationProperty.LayoutTranslateY, 0f, in spec);
            _animations.Retarget(entity, UiAnimationProperty.LayoutScaleX, 1f, in spec);
            _animations.Retarget(entity, UiAnimationProperty.LayoutScaleY, 1f, in spec);
            _previous[entity] = next;
        }
    }

    public void Dispose()
    {
        _animations.World.EntityDestroying -= OnEntityDestroying;
        _previous.Clear();
        _dirty.Clear();
    }

    private float ReadCurrent(UiEntity entity, UiAnimationProperty property, float fallback) =>
        _animations.TryGetSnapshot(entity, property, out UiAnimationSnapshot snapshot)
            ? snapshot.Current
            : fallback;

    private float ReadVelocity(UiEntity entity, UiAnimationProperty property) =>
        _animations.TryGetSnapshot(entity, property, out UiAnimationSnapshot snapshot)
            ? snapshot.Velocity
            : 0f;

    private void OnEntityDestroying(UiEntity entity) => _previous.Remove(entity);
}

internal sealed class AnimationTickSystem(UiAnimationRuntime animations) : IUiSystem
{
    public UiSystemPhase Phase => UiSystemPhase.AnimationTick;

    public string Name => "animation.tick";

    public void Update(UiWorld world, in UiFrameContext frame)
    {
        _ = world;
        animations.Tick(in frame);
    }
}

internal sealed class TransformCompositionSystem : IUiSystem
{
    private readonly List<UiEntity> _dirty = [];

    public UiSystemPhase Phase => UiSystemPhase.Transform;

    public string Name => "visual.transform";

    public void Update(UiWorld world, in UiFrameContext frame)
    {
        _ = frame;
        _dirty.Clear();
        world.Dirty.Collect(UiDirtyFlags.Transform, _dirty);
        for (int i = 0; i < _dirty.Count; i++)
        {
            UiEntity entity = _dirty[i];
            if (!world.Entities.IsAlive(entity) || HasDirtyTransformAncestor(world, entity))
                continue;
            Matrix3x2 parent = ParentTransform(world, entity);
            ResolveSubtree(world, entity, parent);
        }
    }

    private static void ResolveSubtree(UiWorld world, UiEntity entity, Matrix3x2 parent)
    {
        Matrix3x2 local = CreateLocalTransform(world, entity);
        Matrix3x2 computed = local * parent;
        bool changed = !world.Components.TryGet(entity, out ComputedTransform previous) ||
                       previous.Value != computed;
        world.Set(entity, new ComputedTransform { Value = computed });
        world.Dirty.Clear(entity, UiDirtyFlags.Transform);
        if (changed)
            world.Dirty.Mark(entity, UiDirtyFlags.HitTest | UiDirtyFlags.Render);

        if (!world.Hierarchy.TryGetNode(entity, out HierarchyNode node))
            return;
        UiEntity child = node.FirstChild;
        while (child != UiEntity.None)
        {
            UiEntity next = world.Hierarchy.TryGetNode(child, out HierarchyNode childNode)
                ? childNode.NextSibling
                : UiEntity.None;
            if (world.Entities.IsAlive(child))
                ResolveSubtree(world, child, computed);
            child = next;
        }
    }

    private static Matrix3x2 CreateLocalTransform(UiWorld world, UiEntity entity)
    {
        if (!world.Components.TryGet(entity, out LayoutRect layout))
            return Matrix3x2.Identity;
        UiVisualTransform style;
        UiVisualTransform flip;
        if (world.Components.TryGet(entity, out ComputedVisual visual))
        {
            style = visual.Transform;
            flip = visual.LayoutTransform;
        }
        else
        {
            ResolvedStyle resolved = world.Components.TryGet(entity, out ResolvedStyle target)
                ? target
                : ResolvedStyle.Default;
            style = new UiVisualTransform(
                resolved.TranslateX,
                resolved.TranslateY,
                resolved.ScaleX,
                resolved.ScaleY,
                resolved.Rotation);
            flip = UiVisualTransform.Identity;
        }

        UiPoint origin = new(layout.Value.X, layout.Value.Y);
        return Matrix3x2.CreateTranslation(-origin.X, -origin.Y) *
               Matrix3x2.CreateScale(flip.ScaleX, flip.ScaleY) *
               Matrix3x2.CreateTranslation(flip.TranslateX, flip.TranslateY) *
               Matrix3x2.CreateScale(style.ScaleX, style.ScaleY) *
               Matrix3x2.CreateRotation(style.Rotation * (MathF.PI / 180f)) *
               Matrix3x2.CreateTranslation(style.TranslateX, style.TranslateY) *
               Matrix3x2.CreateTranslation(origin.X, origin.Y);
    }

    private static Matrix3x2 ParentTransform(UiWorld world, UiEntity entity)
    {
        if (!world.Hierarchy.TryGetNode(entity, out HierarchyNode node) ||
            node.Parent == UiEntity.None ||
            !world.Components.TryGet(node.Parent, out ComputedTransform parent))
        {
            return Matrix3x2.Identity;
        }
        return parent.Value;
    }

    private static bool HasDirtyTransformAncestor(UiWorld world, UiEntity entity)
    {
        UiEntity current = entity;
        int guard = 0;
        while (world.Hierarchy.TryGetNode(current, out HierarchyNode node) &&
               node.Parent != UiEntity.None &&
               guard++ < 1_000_000)
        {
            current = node.Parent;
            if ((world.Dirty.GetFlags(current) & UiDirtyFlags.Transform) != 0)
                return true;
        }
        return false;
    }
}

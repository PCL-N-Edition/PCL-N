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
        Events = new UiAnimationEventJournal();
        _store = new FloatAnimationStore(world, Motions);
        _groups = new TransitionGroupStore(world, _store);
        _store.Settled += OnSettled;
        _groups.Completed += OnTransitionGroupCompleted;
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

    public UiAnimationEventJournal Events { get; }

    /// <summary>Lossless in-process lifecycle path; observers use <see cref="Events"/> instead.</summary>
    internal event Action<UiTransitionGroupCompleted>? TransitionGroupCompleted;

    public int ChannelCount => _store.ChannelCount;

    public int ActiveChannelCount => _store.ActiveCount;

    public int ActiveTransitionGroupCount => _groups.ActiveCount;

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
        UiTransitionGroupId group = _groups.Create(scope, channels);
        World.Scheduler.RequestReactiveFrame();
        return group;
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
        _groups.Completed -= OnTransitionGroupCompleted;
        _store.Settled -= OnSettled;
        _layoutPlanning.Dispose();
        _groups.Clear();
        _store.Clear();
        Events.Clear();
        World.Scheduler.ReleaseContinuousFrame(UiContinuousReason.Animation);
        _disposed = true;
    }

    internal void BeginPlanningFrame()
    {
        if (!_motionPolicyDirty)
            return;
        _store.ApplyMotionPolicy(AnimationsEnabled, ReducedMotion);
        _motionPolicyDirty = false;
        RefreshScheduling();
    }

    internal void Tick(in UiFrameContext frame)
    {
        _store.Tick(frame.Now);
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

    private void OnSettled(UiAnimationSettled settled)
    {
        Events.Publish(World.FrameIndex, in settled);
        _groups.ProcessSettlement(in settled);
    }

    private void OnTransitionGroupCompleted(UiTransitionGroupCompleted completed)
    {
        Events.Publish(World.FrameIndex, in completed);
        TransitionGroupCompleted?.Invoke(completed);
        World.Scheduler.RequestReactiveFrame();
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
    private static readonly UiAnimationProperty[] MatrixProperties =
    [
        UiAnimationProperty.LayoutM11,
        UiAnimationProperty.LayoutM12,
        UiAnimationProperty.LayoutM21,
        UiAnimationProperty.LayoutM22,
        UiAnimationProperty.LayoutM31,
        UiAnimationProperty.LayoutM32
    ];

    private static readonly float[] IdentityValues = [1f, 0f, 0f, 1f, 0f, 0f];

    private readonly UiAnimationRuntime _animations;
    private readonly Dictionary<UiEntity, UiRect> _previous = [];
    private readonly List<UiEntity> _dirty = [];
    private readonly List<UiEntity> _changed = [];
    private readonly HashSet<UiEntity> _changedSet = [];

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
        _changed.Clear();
        _changedSet.Clear();
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
                if (!world.Components.Has<ComputedLayoutTransform>(entity))
                    world.Set(entity, ComputedLayoutTransform.Identity);
                continue;
            }
            if (previous != next && _changedSet.Add(entity))
                _changed.Add(entity);
        }

        int changedRootCount = _changed.Count;
        for (int i = 0; i < changedRootCount; i++)
            AppendAnimatedDescendants(world, _changed[i]);
        SortByHierarchyDepth(world);
        for (int i = 0; i < _changed.Count; i++)
        {
            UiEntity entity = _changed[i];
            LayoutTransitionComponent transition = world.Components.Get<LayoutTransitionComponent>(entity);
            UiRect next = world.Components.Get<LayoutRect>(entity).Value;
            UiRect previous = _previous[entity];

            Matrix3x2 previousWorld = world.Components.TryGet(entity, out ComputedTransform computed)
                ? computed.Value
                : Matrix3x2.Identity;
            Matrix3x2 desiredWorld = UiTransformMath.MapRect(next, previous) * previousWorld;
            Matrix3x2 parentWorld = UiTransformMath.ComputeParentWorld(world, entity);
            Matrix3x2 style = UiTransformMath.CreateStyleTransform(world, entity);
            if (!Matrix3x2.Invert(parentWorld, out Matrix3x2 inverseParent) ||
                !Matrix3x2.Invert(style, out Matrix3x2 inverseStyle))
            {
                throw new InvalidOperationException(
                    "Cannot rebase a layout transition through a non-invertible transform.");
            }
            Matrix3x2 localFlip = desiredWorld * inverseParent * inverseStyle;

            UiAnimationSpec spec = new(
                transition.Motion,
                UiAnimationContinuity.PreserveVelocity,
                UiAnimationFlags.AllowRebase,
                UiAnimationOwnerReason.LayoutTransition);
            for (int propertyIndex = 0; propertyIndex < MatrixProperties.Length; propertyIndex++)
            {
                UiAnimationProperty property = MatrixProperties[propertyIndex];
                float velocity = ReadVelocity(entity, property);
                _animations.SetDirect(
                    entity,
                    property,
                    ReadMatrixValue(localFlip, property),
                    velocity,
                    UiAnimationOwnerReason.LayoutTransition);
                _animations.Retarget(entity, property, IdentityValues[propertyIndex], in spec);
            }
            _previous[entity] = next;
        }
    }

    public void Dispose()
    {
        _animations.World.EntityDestroying -= OnEntityDestroying;
        _previous.Clear();
        _dirty.Clear();
        _changed.Clear();
        _changedSet.Clear();
    }

    private float ReadVelocity(UiEntity entity, UiAnimationProperty property) =>
        _animations.TryGetSnapshot(entity, property, out UiAnimationSnapshot snapshot)
            ? snapshot.Velocity
            : 0f;

    private void SortByHierarchyDepth(UiWorld world)
    {
        for (int i = 1; i < _changed.Count; i++)
        {
            UiEntity entity = _changed[i];
            int depth = HierarchyDepth(world, entity);
            int position = i - 1;
            while (position >= 0 && HierarchyDepth(world, _changed[position]) > depth)
            {
                _changed[position + 1] = _changed[position];
                position--;
            }
            _changed[position + 1] = entity;
        }
    }

    private void AppendAnimatedDescendants(UiWorld world, UiEntity entity)
    {
        if (!world.Hierarchy.TryGetNode(entity, out HierarchyNode node))
            return;
        UiEntity child = node.FirstChild;
        while (child != UiEntity.None)
        {
            UiEntity next = world.Hierarchy.TryGetNode(child, out HierarchyNode childNode)
                ? childNode.NextSibling
                : UiEntity.None;
            if (world.Entities.IsAlive(child))
            {
                if (world.Components.Has<LayoutTransitionComponent>(child) &&
                    world.Components.TryGet(child, out LayoutRect layout))
                {
                    if (_previous.ContainsKey(child))
                    {
                        if (_changedSet.Add(child))
                            _changed.Add(child);
                    }
                    else
                    {
                        _previous[child] = layout.Value;
                        AnimationPropertyRegistry.EnsureVisual(world, child);
                        if (!world.Components.Has<ComputedLayoutTransform>(child))
                            world.Set(child, ComputedLayoutTransform.Identity);
                    }
                }
                AppendAnimatedDescendants(world, child);
            }
            child = next;
        }
    }

    private static int HierarchyDepth(UiWorld world, UiEntity entity)
    {
        int depth = 0;
        while (world.Hierarchy.TryGetNode(entity, out HierarchyNode node) &&
               node.Parent != UiEntity.None &&
               depth++ < 1_000_000)
        {
            entity = node.Parent;
        }
        return depth;
    }

    private static float ReadMatrixValue(Matrix3x2 matrix, UiAnimationProperty property) =>
        property switch
        {
            UiAnimationProperty.LayoutM11 => matrix.M11,
            UiAnimationProperty.LayoutM12 => matrix.M12,
            UiAnimationProperty.LayoutM21 => matrix.M21,
            UiAnimationProperty.LayoutM22 => matrix.M22,
            UiAnimationProperty.LayoutM31 => matrix.M31,
            UiAnimationProperty.LayoutM32 => matrix.M32,
            _ => throw new ArgumentOutOfRangeException(nameof(property))
        };

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
        Matrix3x2 local = UiTransformMath.CreateLocalTransform(world, entity);
        Matrix3x2 computed = local * parent;
        bool changed = !world.Components.TryGet(entity, out ComputedTransform previous) ||
                       previous.Value != computed;
        world.Set(entity, new ComputedTransform { Value = computed });
        world.Dirty.Clear(entity, UiDirtyFlags.Transform);
        if (changed)
            world.Dirty.Mark(entity, UiDirtyFlags.HitTest | UiDirtyFlags.Render | UiDirtyFlags.Accessibility);

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

internal static class UiTransformMath
{
    public static Matrix3x2 CreateLocalTransform(UiWorld world, UiEntity entity)
    {
        Matrix3x2 layout = world.Components.TryGet(entity, out ComputedLayoutTransform flip)
            ? flip.Value
            : Matrix3x2.Identity;
        Matrix3x2 scroll = world.Components.TryGet(entity, out ScrollContentTransform offset)
            ? Matrix3x2.CreateTranslation(-offset.X, -offset.Y)
            : Matrix3x2.Identity;
        return layout * CreateStyleTransform(world, entity) * scroll;
    }

    public static Matrix3x2 CreateStyleTransform(UiWorld world, UiEntity entity)
    {
        if (!world.Components.TryGet(entity, out LayoutRect layout))
            return Matrix3x2.Identity;
        UiVisualTransform style;
        if (world.Components.TryGet(entity, out ComputedVisual visual))
        {
            style = visual.Transform;
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
        }

        UiPoint origin = new(layout.Value.X, layout.Value.Y);
        return Matrix3x2.CreateTranslation(-origin.X, -origin.Y) *
               Matrix3x2.CreateScale(style.ScaleX, style.ScaleY) *
               Matrix3x2.CreateRotation(style.Rotation * (MathF.PI / 180f)) *
               Matrix3x2.CreateTranslation(style.TranslateX, style.TranslateY) *
               Matrix3x2.CreateTranslation(origin.X, origin.Y);
    }

    public static Matrix3x2 ComputeParentWorld(UiWorld world, UiEntity entity)
    {
        if (!world.Hierarchy.TryGetNode(entity, out HierarchyNode node) ||
            node.Parent == UiEntity.None)
        {
            return Matrix3x2.Identity;
        }
        return ComputeWorld(world, node.Parent);
    }

    public static Matrix3x2 MapRect(UiRect from, UiRect to)
    {
        float scaleX = from.Width > 0.0001f ? to.Width / from.Width : 1f;
        float scaleY = from.Height > 0.0001f ? to.Height / from.Height : 1f;
        return Matrix3x2.CreateTranslation(-from.X, -from.Y) *
               Matrix3x2.CreateScale(scaleX, scaleY) *
               Matrix3x2.CreateTranslation(to.X, to.Y);
    }

    public static Matrix3x2 ComputeWorld(UiWorld world, UiEntity entity)
    {
        Matrix3x2 parent = ComputeParentWorld(world, entity);
        return CreateLocalTransform(world, entity) * parent;
    }
}

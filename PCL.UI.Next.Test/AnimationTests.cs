// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.UI.Next.Test;

[TestClass]
public sealed class AnimationTests
{
    [TestMethod]
    public void Retarget_DoesNotChangeCurrent_AndSameTargetKeepsGeneration()
    {
        using TestContext context = Create(new UiSize(200, 100));
        UiEntity entity = context.Instantiate(
            Ui.Container().Width(UiLength.Pixels(50)).Height(UiLength.Pixels(40))).RootEntity;
        Drain(context);
        UiAnimationSpec spec = new(UiMotion.Hover);

        UiAnimationHandle handle = context.Runtime.Animation.Retarget(
            entity,
            UiAnimationProperty.TranslateX,
            100f,
            in spec);
        Assert.IsTrue(context.Runtime.Animation.TryGetSnapshot(handle, out UiAnimationSnapshot first));
        Assert.AreEqual(0f, first.Current, 0.0001f);

        context.Runtime.Animation.Retarget(entity, UiAnimationProperty.TranslateX, 100f, in spec);
        Assert.IsTrue(context.Runtime.Animation.TryGetSnapshot(handle, out UiAnimationSnapshot sameTarget));
        Assert.AreEqual(first.TargetGeneration, sameTarget.TargetGeneration);
        Assert.AreEqual(first.Current, sameTarget.Current, 0.0001f);
    }

    [TestMethod]
    public void SpringRetarget_PreservesCurrentAndVelocity()
    {
        using TestContext context = Create(new UiSize(200, 100));
        UiEntity entity = context.Instantiate(Ui.Container()).RootEntity;
        Drain(context);
        UiAnimationSpec spec = new(UiMotion.Hover);
        context.Runtime.Animation.Retarget(entity, UiAnimationProperty.TranslateX, 100f, in spec);
        AdvanceFrame(context, 0.05);
        Assert.IsTrue(context.Runtime.Animation.TryGetSnapshot(
            entity,
            UiAnimationProperty.TranslateX,
            out UiAnimationSnapshot before));
        Assert.IsTrue(before.Current > 0f);
        Assert.IsTrue(before.Velocity > 0f);

        context.Runtime.Animation.Retarget(entity, UiAnimationProperty.TranslateX, 0f, in spec);
        Assert.IsTrue(context.Runtime.Animation.TryGetSnapshot(
            entity,
            UiAnimationProperty.TranslateX,
            out UiAnimationSnapshot after));

        Assert.AreEqual(before.Current, after.Current, 0.0001f);
        Assert.AreEqual(before.Velocity, after.Velocity, 0.0001f);
        Assert.IsTrue(after.TargetGeneration > before.TargetGeneration);
    }

    [TestMethod]
    public void TweenRetarget_ContinuesFromCurrentWithoutJump()
    {
        using TestContext context = Create(new UiSize(200, 100));
        UiEntity entity = context.Instantiate(Ui.Container()).RootEntity;
        Drain(context);
        UiAnimationSpec spec = new(UiMotion.Standard);
        context.Runtime.Animation.Retarget(entity, UiAnimationProperty.Opacity, 0f, in spec);
        AdvanceFrame(context, 0.08);
        Assert.IsTrue(context.Runtime.Animation.TryGetSnapshot(
            entity,
            UiAnimationProperty.Opacity,
            out UiAnimationSnapshot before));

        context.Runtime.Animation.Retarget(entity, UiAnimationProperty.Opacity, 1f, in spec);
        Assert.IsTrue(context.Runtime.Animation.TryGetSnapshot(
            entity,
            UiAnimationProperty.Opacity,
            out UiAnimationSnapshot after));

        Assert.AreEqual(before.Current, after.Current, 0.0001f);
        Assert.AreEqual(1f, after.Target, 0.0001f);
    }

    [TestMethod]
    public void Retarget_AfterSnapToCurrent_RestartsSameTarget()
    {
        using TestContext context = Create(new UiSize(200, 100));
        UiEntity entity = context.Instantiate(Ui.Container()).RootEntity;
        Drain(context);
        UiAnimationSpec spec = new(UiMotion.Standard);
        context.Runtime.Animation.Retarget(entity, UiAnimationProperty.Opacity, 0f, in spec);
        AdvanceFrame(context, 0.08);
        Assert.IsTrue(context.Runtime.Animation.Cancel(
            entity,
            UiAnimationProperty.Opacity,
            UiAnimationCancelMode.SnapToCurrent));
        Assert.IsTrue(context.Runtime.Animation.TryGetSnapshot(
            entity,
            UiAnimationProperty.Opacity,
            out UiAnimationSnapshot canceled));
        Assert.IsFalse(canceled.IsActive);
        Assert.IsTrue(canceled.Current > 0f);

        context.Runtime.Animation.Retarget(entity, UiAnimationProperty.Opacity, 0f, in spec);

        Assert.IsTrue(context.Runtime.Animation.TryGetSnapshot(
            entity,
            UiAnimationProperty.Opacity,
            out UiAnimationSnapshot restarted));
        Assert.IsTrue(restarted.IsActive);
        Assert.IsTrue(restarted.TargetGeneration > canceled.TargetGeneration);
    }

    [TestMethod]
    public void Spring_SettlesExactlyAndReleasesContinuousFrame()
    {
        using TestContext context = Create(new UiSize(200, 100));
        UiEntity entity = context.Instantiate(Ui.Container()).RootEntity;
        Drain(context);
        UiAnimationSpec spec = new(UiMotion.Hover);
        context.Runtime.Animation.Retarget(entity, UiAnimationProperty.ScaleX, 1.2f, in spec);

        Settle(context);

        Assert.IsTrue(context.Runtime.Animation.TryGetSnapshot(
            entity,
            UiAnimationProperty.ScaleX,
            out UiAnimationSnapshot snapshot));
        Assert.AreEqual(1.2f, snapshot.Current, 0.000001f);
        Assert.AreEqual(0f, snapshot.Velocity, 0.000001f);
        Assert.IsFalse(snapshot.IsActive);
        Assert.AreEqual(0, context.Runtime.Animation.ActiveChannelCount);
        Assert.IsFalse((context.World.Scheduler.ContinuousReasons & UiContinuousReason.Animation) != 0);
    }

    [TestMethod]
    public void CompletedGeneration_BecomesStaleAfterRetarget()
    {
        using TestContext context = Create(new UiSize(200, 100));
        UiEntity entity = context.Instantiate(Ui.Container()).RootEntity;
        Drain(context);
        UiAnimationSpec spec = new(UiMotion.FastFade);
        context.Runtime.Animation.Retarget(entity, UiAnimationProperty.Opacity, 0.25f, in spec);
        Settle(context);
        UiAnimationSettled settled = DrainAnimationEvents(context)
            .Single(record => record.Kind == UiAnimationEventKind.Settled &&
                              record.Settlement.Entity == entity &&
                              record.Settlement.Property == UiAnimationProperty.Opacity)
            .Settlement;
        Assert.IsTrue(context.Runtime.Animation.IsCurrent(in settled));

        context.Runtime.Animation.Retarget(entity, UiAnimationProperty.Opacity, 1f, in spec);

        Assert.IsFalse(context.Runtime.Animation.IsCurrent(in settled));
    }

    [TestMethod]
    public void TransitionGroup_CompletesAfterAllRequiredChannelsSettle()
    {
        using TestContext context = Create(new UiSize(200, 100));
        UiEntity entity = context.Instantiate(Ui.Container()).RootEntity;
        Drain(context);
        UiAnimationSpec spec = new(UiMotion.FastFade);
        UiAnimationHandle opacity = context.Runtime.Animation.Retarget(
            entity,
            UiAnimationProperty.Opacity,
            0.4f,
            in spec);
        UiAnimationHandle translation = context.Runtime.Animation.Retarget(
            entity,
            UiAnimationProperty.TranslateX,
            20f,
            in spec);
        UiAnimationHandle[] channels = [opacity, translation];
        UiTransitionGroupId group = context.Runtime.Animation.CreateTransitionGroup(
            context.WindowScope,
            channels);

        Settle(context);

        Assert.AreEqual(0, context.Runtime.Animation.ActiveTransitionGroupCount);
        Assert.IsTrue(DrainAnimationEvents(context).Any(record =>
            record.Kind == UiAnimationEventKind.TransitionGroupCompleted &&
            record.TransitionGroup.Group == group &&
            record.TransitionGroup.Scope == context.WindowScope));
    }

    [TestMethod]
    public void TransitionGroup_OldGenerationCannotCompleteAfterRetarget()
    {
        using TestContext context = Create(new UiSize(200, 100));
        UiEntity entity = context.Instantiate(Ui.Container()).RootEntity;
        Drain(context);
        UiAnimationSpec spec = new(UiMotion.Standard);
        UiAnimationHandle channel = context.Runtime.Animation.Retarget(
            entity,
            UiAnimationProperty.Opacity,
            0f,
            in spec);
        UiAnimationHandle[] channels = [channel];
        UiTransitionGroupId oldGroup = context.Runtime.Animation.CreateTransitionGroup(
            context.WindowScope,
            channels);

        context.Runtime.Animation.Retarget(entity, UiAnimationProperty.Opacity, 0.5f, in spec);
        Settle(context);

        Assert.AreEqual(0, context.Runtime.Animation.ActiveTransitionGroupCount);
        Assert.IsFalse(DrainAnimationEvents(context).Any(record =>
            record.Kind == UiAnimationEventKind.TransitionGroupCompleted &&
            record.TransitionGroup.Group == oldGroup));
    }

    [TestMethod]
    public void TransitionGroup_CancelInvalidatesCompletionRequirement()
    {
        using TestContext context = Create(new UiSize(200, 100));
        UiEntity entity = context.Instantiate(Ui.Container()).RootEntity;
        Drain(context);
        UiAnimationSpec spec = new(UiMotion.Standard);
        UiAnimationHandle channel = context.Runtime.Animation.Retarget(
            entity,
            UiAnimationProperty.Opacity,
            0f,
            in spec);
        UiAnimationHandle[] channels = [channel];
        UiTransitionGroupId group = context.Runtime.Animation.CreateTransitionGroup(
            context.WindowScope,
            channels);

        Assert.IsTrue(context.Runtime.Animation.Cancel(
            entity,
            UiAnimationProperty.Opacity,
            UiAnimationCancelMode.SnapToCurrent));
        Drain(context);

        Assert.AreEqual(0, context.Runtime.Animation.ActiveTransitionGroupCount);
        Assert.IsFalse(DrainAnimationEvents(context).Any(record =>
            record.Kind == UiAnimationEventKind.TransitionGroupCompleted &&
            record.TransitionGroup.Group == group));
    }

    [TestMethod]
    public void TransitionGroup_RejectsDuplicateChannel()
    {
        using TestContext context = Create(new UiSize(200, 100));
        UiEntity entity = context.Instantiate(Ui.Container()).RootEntity;
        Drain(context);
        UiAnimationSpec spec = new(UiMotion.Standard);
        UiAnimationHandle channel = context.Runtime.Animation.Retarget(
            entity,
            UiAnimationProperty.Opacity,
            0f,
            in spec);
        UiAnimationHandle[] channels = [channel, channel];

        Assert.ThrowsExactly<ArgumentException>(() =>
            context.Runtime.Animation.CreateTransitionGroup(context.WindowScope, channels));
    }

    [TestMethod]
    public void ImmediateRetarget_BeforeTransitionPlanning_PreservesSettlement()
    {
        using TestContext context = Create(new UiSize(200, 100));
        UiEntity entity = context.Instantiate(Ui.Container()).RootEntity;
        Drain(context);
        context.World.Systems.Register(new OneShotSystem(
            UiSystemPhase.Interaction,
            _ =>
            {
                UiAnimationSpec spec = new(UiMotion.Instant);
                context.Runtime.Animation.Retarget(
                    entity,
                    UiAnimationProperty.Opacity,
                    0f,
                    in spec);
            }));
        context.World.Scheduler.RequestReactiveFrame();

        Assert.IsTrue(context.World.Update());

        List<UiAnimationEvent> events = DrainAnimationEvents(context);
        Assert.IsTrue(events.Any(record =>
            record.Kind == UiAnimationEventKind.Settled &&
            record.Settlement.Entity == entity &&
            record.Settlement.Property == UiAnimationProperty.Opacity));
    }

    [TestMethod]
    public void ReducedMotion_TransitionGroupCompletion_IsNotLost()
    {
        using TestContext context = Create(new UiSize(200, 100));
        UiEntity entity = context.Instantiate(Ui.Container()).RootEntity;
        Drain(context);
        context.Runtime.Animation.SetReducedMotion(true);
        Assert.IsTrue(context.World.Update());
        UiTransitionGroupId group = default;
        context.World.Systems.Register(new OneShotSystem(
            UiSystemPhase.Interaction,
            _ =>
            {
                UiAnimationSpec spec = new(UiMotion.Navigation);
                UiAnimationHandle channel = context.Runtime.Animation.Retarget(
                    entity,
                    UiAnimationProperty.TranslateX,
                    50f,
                    in spec);
                UiAnimationHandle[] channels = [channel];
                group = context.Runtime.Animation.CreateTransitionGroup(
                    context.WindowScope,
                    channels);
            }));
        context.World.Scheduler.RequestReactiveFrame();

        Assert.IsTrue(context.World.Update());

        List<UiAnimationEvent> events = DrainAnimationEvents(context);
        Assert.IsTrue(events.Any(record =>
            record.Kind == UiAnimationEventKind.TransitionGroupCompleted &&
            record.TransitionGroup.Group == group));
        for (int i = 1; i < events.Count; i++)
            Assert.IsTrue(events[i].Sequence > events[i - 1].Sequence);
    }

    [TestMethod]
    public void AlreadySettledGroup_CompletesReliablyOnNextConsumerPass()
    {
        using TestContext context = Create(new UiSize(200, 100));
        UiEntity entity = context.Instantiate(Ui.Container()).RootEntity;
        Drain(context);
        UiAnimationHandle channel = context.Runtime.Animation.SetDirect(
            entity,
            UiAnimationProperty.Opacity,
            0.5f);
        UiAnimationHandle[] channels = [channel];
        UiTransitionGroupId group = context.Runtime.Animation.CreateTransitionGroup(
            context.WindowScope,
            channels);

        Drain(context);

        Assert.IsTrue(DrainAnimationEvents(context).Any(record =>
            record.Kind == UiAnimationEventKind.TransitionGroupCompleted &&
            record.TransitionGroup.Group == group));
    }

    [TestMethod]
    public void UnsupportedContinuityPolicies_ThrowInsteadOfFallingBack()
    {
        Assert.ThrowsExactly<NotSupportedException>(() =>
            new UiAnimationSpec(UiMotion.Standard, UiAnimationContinuity.Restart));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            new UiTransitionDefinition(
                UiAnimationProperty.Opacity,
                UiMotion.Standard,
                UiAnimationContinuity.PreserveRemainingRatio));
        UiMotionRegistry motions = new();
        UiMotionDefinition unsupported = new(
            UiAnimationSolverKind.Tween,
            UiAnimationContinuity.Restart,
            0.2f,
            UiEasing.Linear,
            0f,
            1f,
            0f);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            motions.Set(new UiMotionToken(5000), in unsupported));
    }

    [TestMethod]
    public void ScopeDispose_RemovesOwnedChannelsAndContinuousFrame()
    {
        using TestContext context = Create(new UiSize(200, 100));
        UiEntity entity = context.Instantiate(Ui.Container()).RootEntity;
        Drain(context);
        UiAnimationSpec spec = new(UiMotion.Navigation);
        context.Runtime.Animation.Retarget(entity, UiAnimationProperty.TranslateX, 100f, in spec);
        Assert.AreEqual(1, context.Runtime.Animation.ActiveChannelCount);

        Assert.IsTrue(context.World.DisposeScope(context.WindowScope));

        Assert.AreEqual(0, context.Runtime.Animation.ChannelCount);
        Assert.AreEqual(0, context.Runtime.Animation.ActiveChannelCount);
        Assert.IsFalse((context.World.Scheduler.ContinuousReasons & UiContinuousReason.Animation) != 0);
    }

    [TestMethod]
    public void ReducedMotion_SnapsNonEssentialAnimation()
    {
        using TestContext context = Create(new UiSize(200, 100));
        UiEntity entity = context.Instantiate(Ui.Container()).RootEntity;
        Drain(context);
        context.Runtime.Animation.SetReducedMotion(true);
        Assert.IsTrue(context.World.Update());
        UiAnimationSpec spec = new(UiMotion.Navigation);

        context.Runtime.Animation.Retarget(entity, UiAnimationProperty.TranslateX, 80f, in spec);

        Assert.IsTrue(context.Runtime.Animation.TryGetSnapshot(
            entity,
            UiAnimationProperty.TranslateX,
            out UiAnimationSnapshot snapshot));
        Assert.AreEqual(80f, snapshot.Current, 0.0001f);
        Assert.IsFalse(snapshot.IsActive);
        Assert.AreEqual(0, context.Runtime.Animation.ActiveChannelCount);
    }

    [TestMethod]
    public void RetargetAfterLongIdle_DoesNotFastForwardTween()
    {
        using TestContext context = Create(new UiSize(200, 100));
        UiEntity entity = context.Instantiate(Ui.Container()).RootEntity;
        Drain(context);
        context.Clock.Advance(30d);
        UiAnimationSpec spec = new(UiMotion.Standard);

        context.Runtime.Animation.Retarget(entity, UiAnimationProperty.Opacity, 0f, in spec);
        context.Clock.Advance(0.001d);
        Assert.IsTrue(context.World.Update());

        Assert.IsTrue(context.Runtime.Animation.TryGetSnapshot(
            entity,
            UiAnimationProperty.Opacity,
            out UiAnimationSnapshot snapshot));
        Assert.IsTrue(snapshot.IsActive);
        Assert.IsTrue(snapshot.Current > 0.9f);
    }

    [TestMethod]
    public void SpringStartedAfterLongIdle_DoesNotConsumeIdleTime()
    {
        using TestContext context = Create(new UiSize(200, 100));
        UiEntity entity = context.Instantiate(Ui.Container()).RootEntity;
        Drain(context);
        context.Clock.Advance(30d);
        UiAnimationSpec spec = new(UiMotion.Hover);

        context.Runtime.Animation.Retarget(entity, UiAnimationProperty.TranslateX, 100f, in spec);
        context.Clock.Advance(0.001d);
        Assert.IsTrue(context.World.Update());

        Assert.IsTrue(context.Runtime.Animation.TryGetSnapshot(
            entity,
            UiAnimationProperty.TranslateX,
            out UiAnimationSnapshot snapshot));
        Assert.IsTrue(snapshot.IsActive);
        Assert.IsTrue(snapshot.Current is > 0f and < 1f);
    }

    [TestMethod]
    public void DirectToSpring_PreservesReleaseVelocity()
    {
        using TestContext context = Create(new UiSize(200, 100));
        UiEntity entity = context.Instantiate(Ui.Container()).RootEntity;
        Drain(context);
        context.Runtime.Animation.SetDirect(
            entity,
            UiAnimationProperty.TranslateX,
            current: 40f,
            velocity: 320f);
        UiAnimationSpec spec = new(UiMotion.Navigation);

        context.Runtime.Animation.Retarget(entity, UiAnimationProperty.TranslateX, 100f, in spec);

        Assert.IsTrue(context.Runtime.Animation.TryGetSnapshot(
            entity,
            UiAnimationProperty.TranslateX,
            out UiAnimationSnapshot snapshot));
        Assert.AreEqual(40f, snapshot.Current, 0.0001f);
        Assert.AreEqual(320f, snapshot.Velocity, 0.0001f);
        Assert.AreEqual(UiAnimationSolverKind.Spring, snapshot.Solver);
    }

    [TestMethod]
    public void Decay_MergesVelocityImpulse()
    {
        using TestContext context = Create(new UiSize(200, 100));
        UiEntity entity = context.Instantiate(Ui.Container()).RootEntity;
        Drain(context);
        context.Runtime.Animation.SetDirect(entity, UiAnimationProperty.TranslateX, 0f);
        UiAnimationSpec spec = new(UiMotion.Scroll, owner: UiAnimationOwnerReason.Scroll);
        context.Runtime.Animation.StartDecay(entity, UiAnimationProperty.TranslateX, 600f, in spec);
        AdvanceFrame(context, 0.1);
        Assert.IsTrue(context.Runtime.Animation.TryGetSnapshot(
            entity,
            UiAnimationProperty.TranslateX,
            out UiAnimationSnapshot before));
        Assert.IsTrue(before.Current > 0f);
        Assert.IsTrue(before.Velocity is > 0f and < 600f);

        context.Runtime.Animation.StartDecay(entity, UiAnimationProperty.TranslateX, 200f, in spec);
        Assert.IsTrue(context.Runtime.Animation.TryGetSnapshot(
            entity,
            UiAnimationProperty.TranslateX,
            out UiAnimationSnapshot after));
        Assert.AreEqual(before.Velocity + 200f, after.Velocity, 0.001f);
    }

    [TestMethod]
    public void Retarget_RejectsDecayMotionWithoutCreatingChannel()
    {
        using TestContext context = Create(new UiSize(200, 100));
        UiEntity entity = context.Instantiate(Ui.Container()).RootEntity;
        Drain(context);
        UiAnimationSpec spec = new(UiMotion.Scroll);

        Assert.ThrowsExactly<ArgumentException>(() =>
            context.Runtime.Animation.Retarget(
                entity,
                UiAnimationProperty.TranslateX,
                100f,
                in spec));
        context.Runtime.Animation.SetAnimationsEnabled(false);
        Assert.IsTrue(context.World.Update());
        Assert.ThrowsExactly<ArgumentException>(() =>
            context.Runtime.Animation.Retarget(
                entity,
                UiAnimationProperty.TranslateX,
                100f,
                in spec));

        Assert.AreEqual(0, context.Runtime.Animation.ChannelCount);
        Assert.IsFalse(context.World.Components.Has<ComputedVisual>(entity));
    }

    [TestMethod]
    public void StartDecay_RejectsTargetMotionWithoutCreatingChannel()
    {
        using TestContext context = Create(new UiSize(200, 100));
        UiEntity entity = context.Instantiate(Ui.Container()).RootEntity;
        Drain(context);
        UiAnimationSpec spec = new(UiMotion.Standard);

        Assert.ThrowsExactly<ArgumentException>(() =>
            context.Runtime.Animation.StartDecay(
                entity,
                UiAnimationProperty.TranslateX,
                600f,
                in spec));
        context.Runtime.Animation.SetReducedMotion(true);
        Assert.IsTrue(context.World.Update());
        Assert.ThrowsExactly<ArgumentException>(() =>
            context.Runtime.Animation.StartDecay(
                entity,
                UiAnimationProperty.TranslateX,
                600f,
                in spec));

        Assert.AreEqual(0, context.Runtime.Animation.ChannelCount);
        Assert.IsFalse(context.World.Components.Has<ComputedVisual>(entity));
    }

    [TestMethod]
    public void MotionRegistry_RejectsDirectMotionToken()
    {
        UiMotionRegistry motions = new();
        UiMotionDefinition direct = new(
            UiAnimationSolverKind.Direct,
            UiAnimationContinuity.ContinueFromCurrent,
            0f,
            UiEasing.Linear,
            0f,
            1f,
            0f);

        Assert.ThrowsExactly<ArgumentException>(() =>
            motions.Set(new UiMotionToken(5001), in direct));
    }

    [TestMethod]
    public void MotionRegistry_RejectsTweenPreserveVelocity()
    {
        UiMotionRegistry motions = new();
        UiMotionDefinition definition = new(
            UiAnimationSolverKind.Tween,
            UiAnimationContinuity.PreserveVelocity,
            0.2f,
            UiEasing.Linear,
            0f,
            1f,
            0f);

        Assert.ThrowsExactly<ArgumentException>(() =>
            motions.Set(new UiMotionToken(5002), in definition));
    }

    [TestMethod]
    public void MotionRegistry_RejectsSpringPreserveSpeed()
    {
        UiMotionRegistry motions = new();
        UiMotionDefinition definition = new(
            UiAnimationSolverKind.Spring,
            UiAnimationContinuity.PreserveSpeed,
            0f,
            UiEasing.Linear,
            0.3f,
            1f,
            0f);

        Assert.ThrowsExactly<ArgumentException>(() =>
            motions.Set(new UiMotionToken(5003), in definition));
    }

    [TestMethod]
    public void MotionRegistry_RejectsDecayPreserveVelocity()
    {
        UiMotionRegistry motions = new();
        UiMotionDefinition definition = new(
            UiAnimationSolverKind.Decay,
            UiAnimationContinuity.PreserveVelocity,
            0f,
            UiEasing.Linear,
            0f,
            1f,
            8f);

        Assert.ThrowsExactly<ArgumentException>(() =>
            motions.Set(new UiMotionToken(5004), in definition));
    }

    [TestMethod]
    public void Retarget_RejectsIncompatibleContinuityOverride()
    {
        using TestContext context = Create(new UiSize(200, 100));
        UiEntity entity = context.Instantiate(Ui.Container()).RootEntity;
        Drain(context);
        UiAnimationSpec spec = new(
            UiMotion.Standard,
            UiAnimationContinuity.PreserveVelocity);

        Assert.ThrowsExactly<ArgumentException>(() =>
            context.Runtime.Animation.Retarget(
                entity,
                UiAnimationProperty.Opacity,
                0f,
                in spec));

        Assert.AreEqual(0, context.Runtime.Animation.ChannelCount);
    }

    [TestMethod]
    public void StartDecay_RejectsIncompatibleContinuityOverride()
    {
        using TestContext context = Create(new UiSize(200, 100));
        UiEntity entity = context.Instantiate(Ui.Container()).RootEntity;
        Drain(context);
        UiAnimationSpec spec = new(
            UiMotion.Scroll,
            UiAnimationContinuity.PreserveVelocity);

        Assert.ThrowsExactly<ArgumentException>(() =>
            context.Runtime.Animation.StartDecay(
                entity,
                UiAnimationProperty.TranslateX,
                600f,
                in spec));

        Assert.AreEqual(0, context.Runtime.Animation.ChannelCount);
    }

    [TestMethod]
    public void AnimationTick_AllocatesZeroAfterWarmup()
    {
        using TestContext context = Create(new UiSize(200, 100));
        UiEntity entity = context.Instantiate(Ui.Container()).RootEntity;
        Drain(context);
        UiMotionToken longTween = new(990);
        UiMotionDefinition definition = new(
            UiAnimationSolverKind.Tween,
            UiAnimationContinuity.ContinueFromCurrent,
            DurationSeconds: 10f,
            UiEasing.Linear,
            SpringResponse: 0f,
            SpringDampingRatio: 1f,
            DecayFriction: 0f);
        context.Runtime.Animation.Motions.Set(longTween, in definition);
        UiAnimationSpec spec = new(longTween);
        context.Runtime.Animation.Retarget(entity, UiAnimationProperty.Opacity, 0.2f, in spec);
        for (int i = 0; i < 20; i++)
        {
            context.Clock.Advance(1d / 120d);
            context.World.Update();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++)
        {
            context.Clock.Advance(1d / 120d);
            context.World.Update();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.AreEqual(0L, allocated, "Steady-state animation frames must not allocate.");
    }

    [TestMethod]
    public void InitialMount_WithTransition_SnapsCurrentToTarget()
    {
        using TestContext context = Create(new UiSize(200, 100));
        UiClass animated = new(900, "Animated");
        context.Runtime.Styles.Add(new UiStyleRule(
            animated,
            default(UiStyleValues).WithOpacity(0.35f)));

        UiEntity entity = context.Instantiate(
            Ui.Container()
                .Class(animated)
                .Transition(UiAnimationProperty.Opacity, UiMotion.FastFade)).RootEntity;
        Drain(context);

        Assert.AreEqual(0, context.Runtime.Animation.ActiveChannelCount);
        Assert.AreEqual(0.35f, context.World.Components.Get<ComputedVisual>(entity).Opacity, 0.0001f);
    }

    [TestMethod]
    public void StyleTransition_RetargetsFromCurrentVisualValue()
    {
        using TestContext context = Create(new UiSize(200, 100));
        UiClass animated = new(901, "InteractiveAnimated");
        context.Runtime.Styles.Add(new UiStyleRule(
            animated,
            default(UiStyleValues).WithOpacity(1f)));
        context.Runtime.Styles.Add(new UiStyleRule(
            animated,
            default(UiStyleValues).WithOpacity(0.2f),
            requiredState: InteractionState.Hovered,
            priority: 10));
        UiEntity entity = context.Instantiate(
            Ui.Button("Animated")
                .Class(animated)
                .Transition(UiAnimationProperty.Opacity, UiMotion.Standard)).RootEntity;
        Drain(context);

        SetInteractionState(context, entity, InteractionState.Hovered);
        Assert.IsTrue(context.World.Update());
        Assert.AreEqual(1f, context.World.Components.Get<ComputedVisual>(entity).Opacity, 0.0001f);
        AdvanceFrame(context, 0.08);
        float current = context.World.Components.Get<ComputedVisual>(entity).Opacity;
        Assert.IsTrue(current is > 0.2f and < 1f);

        SetInteractionState(context, entity, InteractionState.None);
        Assert.IsTrue(context.World.Update());
        Assert.AreEqual(current, context.World.Components.Get<ComputedVisual>(entity).Opacity, 0.0001f);
        Assert.IsTrue(context.Runtime.Animation.TryGetSnapshot(
            entity,
            UiAnimationProperty.Opacity,
            out UiAnimationSnapshot snapshot));
        Assert.AreEqual(1f, snapshot.Target, 0.0001f);
    }

    [TestMethod]
    public void StyleTransition_CreatesChannelsOnlyForDeclaredOrChangingProperties()
    {
        using TestContext context = Create(new UiSize(200, 100));
        UiClass animated = new(902, "OpacityOnly");
        context.Runtime.Styles.Add(new UiStyleRule(
            animated,
            default(UiStyleValues).WithOpacity(1f)));
        context.Runtime.Styles.Add(new UiStyleRule(
            animated,
            default(UiStyleValues).WithOpacity(0.4f),
            requiredState: InteractionState.Hovered,
            priority: 10));
        UiEntity entity = context.Instantiate(
            Ui.Button("Animated")
                .Class(animated)
                .Transition(UiAnimationProperty.Opacity, UiMotion.Standard)).RootEntity;
        Drain(context);

        SetInteractionState(context, entity, InteractionState.Hovered);
        Assert.IsTrue(context.World.Update());

        Assert.AreEqual(1, context.Runtime.Animation.ChannelCount);
        Assert.IsTrue(context.Runtime.Animation.TryGetSnapshot(
            entity,
            UiAnimationProperty.Opacity,
            out _));
        Assert.IsFalse(context.Runtime.Animation.TryGetSnapshot(
            entity,
            UiAnimationProperty.CornerRadius,
            out _));
    }

    [TestMethod]
    public void ThemeChange_RetargetsFromCurrentVisualValue()
    {
        using TestContext context = Create(new UiSize(200, 100));
        ThemeToken<float> opacity = new(2001, "Opacity.Animated");
        UiClass animated = new(903, "ThemeAnimated");
        context.Runtime.Theme.Set(opacity, 0.2f);
        context.Runtime.Styles.Add(new UiStyleRule(
            animated,
            default(UiStyleValues).WithOpacity(opacity)));
        UiEntity entity = context.Instantiate(
            Ui.Container()
                .Class(animated)
                .Transition(UiAnimationProperty.Opacity, UiMotion.Standard)).RootEntity;
        Drain(context);

        context.Runtime.Theme.Set(opacity, 0.8f);
        Assert.IsTrue(context.World.Update());
        AdvanceFrame(context, 0.08);
        float current = context.World.Components.Get<ComputedVisual>(entity).Opacity;
        Assert.IsTrue(current is > 0.2f and < 0.8f);

        context.Runtime.Theme.Set(opacity, 0.4f);
        Assert.IsTrue(context.World.Update());

        Assert.AreEqual(current, context.World.Components.Get<ComputedVisual>(entity).Opacity, 0.0001f);
        Assert.IsTrue(context.Runtime.Animation.TryGetSnapshot(
            entity,
            UiAnimationProperty.Opacity,
            out UiAnimationSnapshot snapshot));
        Assert.AreEqual(0.4f, snapshot.Target, 0.0001f);
    }

    [TestMethod]
    public void LayoutTransition_UsesFlipAndKeepsLayoutStaticDuringAnimation()
    {
        using TestContext context = Create(new UiSize(200, 100));
        UiEntity entity = context.Instantiate(
            Ui.Container()
                .Width(UiLength.Percent(0.5f))
                .Height(UiLength.Pixels(40))
                .AnimateLayout(UiMotion.Layout)).RootEntity;
        Drain(context);
        Assert.AreEqual(100f, context.World.Components.Get<LayoutRect>(entity).Value.Width, 0.001f);

        context.Runtime.SetViewport(new UiSize(400, 100));
        Assert.IsTrue(context.World.Update());

        UiRect targetLayout = context.World.Components.Get<LayoutRect>(entity).Value;
        Assert.AreEqual(200f, targetLayout.Width, 0.001f);
        Assert.IsTrue(context.Runtime.Animation.TryGetSnapshot(
            entity,
            UiAnimationProperty.LayoutM11,
            out UiAnimationSnapshot flip));
        Assert.AreEqual(0.5f, flip.Current, 0.001f);
        Assert.AreEqual(1f, flip.Target, 0.001f);

        AdvanceFrame(context, 0.05);
        Assert.AreEqual(targetLayout, context.World.Components.Get<LayoutRect>(entity).Value);
        Assert.IsTrue(context.World.Components.Get<ComputedLayoutTransform>(entity).Value.M11 > 0.5f);
    }

    [TestMethod]
    public void LayoutTransition_RebasesWithoutVisualJump()
    {
        using TestContext context = Create(new UiSize(200, 100));
        UiEntity entity = context.Instantiate(
            Ui.Container()
                .Width(UiLength.Percent(0.5f))
                .Height(UiLength.Pixels(40))
                .AnimateLayout(UiMotion.Layout)).RootEntity;
        Drain(context);
        context.Runtime.SetViewport(new UiSize(400, 100));
        Assert.IsTrue(context.World.Update());
        AdvanceFrame(context, 0.06);
        UiRect beforeLayout = context.World.Components.Get<LayoutRect>(entity).Value;
        float beforeScale = context.World.Components.Get<ComputedLayoutTransform>(entity).Value.M11;
        float visualWidth = beforeLayout.Width * beforeScale;

        context.Runtime.SetViewport(new UiSize(300, 100));
        Assert.IsTrue(context.World.Update());

        UiRect afterLayout = context.World.Components.Get<LayoutRect>(entity).Value;
        float afterScale = context.World.Components.Get<ComputedLayoutTransform>(entity).Value.M11;
        Assert.AreEqual(visualWidth, afterLayout.Width * afterScale, 0.01f);
    }

    [TestMethod]
    public void NestedLayoutTransition_DoesNotDoubleApplyAncestorFlip()
    {
        using TestContext context = Create(new UiSize(200, 100));
        BlueprintInstance live = context.Instantiate(
            Ui.Column(
                    Ui.Container()
                        .HitTestVisible()
                        .Width(UiLength.Percent(1f))
                        .Height(UiLength.Pixels(40))
                        .AnimateLayout(UiMotion.Layout))
                .Width(UiLength.Percent(0.5f))
                .Height(UiLength.Pixels(40))
                .AnimateLayout(UiMotion.Layout));
        UiEntity child = live.EntityAt(1);
        Drain(context);
        Assert.AreEqual(100f, context.World.Components.Get<LayoutRect>(child).Value.Width, 0.001f);

        context.Runtime.SetViewport(new UiSize(400, 100));
        Assert.IsTrue(context.World.Update());

        UiRect bounds = VisualBounds(context.World, child);
        Assert.AreEqual(100f, bounds.Width, 0.01f);
        Assert.AreEqual(
            child,
            context.Runtime.Input.HitTest.HitTest(new UiPoint(75f, 20f), context.InputRoot));
        Assert.AreEqual(1f, context.World.Components.Get<ComputedLayoutTransform>(child).Value.M11, 0.001f);
    }

    [TestMethod]
    public void NestedLayoutTransition_PreservesChildLocalChangeAlongsideAncestorFlip()
    {
        using TestContext context = Create(new UiSize(200, 100));
        BlueprintInstance live = context.Instantiate(
            Ui.Column(
                    Ui.Container()
                        .Width(UiLength.Pixels(50))
                        .Height(UiLength.Pixels(40))
                        .AnimateLayout(UiMotion.Layout))
                .Width(UiLength.Percent(0.5f))
                .Height(UiLength.Pixels(40))
                .AnimateLayout(UiMotion.Layout));
        UiEntity child = live.EntityAt(1);
        Drain(context);

        context.Runtime.SetViewport(new UiSize(400, 100));
        Assert.IsTrue(context.World.Update());

        UiRect bounds = VisualBounds(context.World, child);
        Assert.AreEqual(50f, bounds.Width, 0.01f);
        Assert.AreEqual(2f, context.World.Components.Get<ComputedLayoutTransform>(child).Value.M11, 0.001f);
    }

    [TestMethod]
    public void HitTest_UsesCurrentAnimatedTransform()
    {
        using TestContext context = Create(new UiSize(240, 100));
        UiEntity entity = context.Instantiate(
            Ui.Container()
                .HitTestVisible()
                .Width(UiLength.Pixels(50))
                .Height(UiLength.Pixels(40))).RootEntity;
        Drain(context);

        context.Runtime.Animation.SetDirect(entity, UiAnimationProperty.TranslateX, 100f);
        Assert.IsTrue(context.World.Update());

        Assert.AreEqual(
            entity,
            context.Runtime.Input.HitTest.HitTest(new UiPoint(125, 20), context.InputRoot));
        Assert.AreEqual(
            UiEntity.None,
            context.Runtime.Input.HitTest.HitTest(new UiPoint(25, 20), context.InputRoot));
    }

    private static TestContext Create(UiSize viewport)
    {
        DeterministicUiClock clock = new();
        UiWorld world = new(clock);
        UiInteractiveRuntime runtime = new(world, new DeterministicTextEngine(), viewport);
        UiScopeId applicationScope = world.CreateRootScope();
        UiScopeId windowScope = world.CreateScope(applicationScope);
        UiInputRootId inputRoot = runtime.Input.InputRoots.Register(windowScope);
        BlueprintInstantiator instantiator = new(world, new PresentationStore());
        return new TestContext(clock, world, runtime, windowScope, inputRoot, instantiator);
    }

    private static List<UiAnimationEvent> DrainAnimationEvents(TestContext context)
    {
        List<UiAnimationEvent> events = [];
        context.Runtime.Animation.Events.Drain(events);
        return events;
    }

    private static UiRect VisualBounds(UiWorld world, UiEntity entity)
    {
        UiRect layout = world.Components.Get<LayoutRect>(entity).Value;
        Matrix3x2 transform = world.Components.Get<ComputedTransform>(entity).Value;
        Vector2 topLeft = Vector2.Transform(new Vector2(layout.X, layout.Y), transform);
        Vector2 topRight = Vector2.Transform(new Vector2(layout.Right, layout.Y), transform);
        Vector2 bottomLeft = Vector2.Transform(new Vector2(layout.X, layout.Bottom), transform);
        Vector2 bottomRight = Vector2.Transform(new Vector2(layout.Right, layout.Bottom), transform);
        float left = MathF.Min(MathF.Min(topLeft.X, topRight.X), MathF.Min(bottomLeft.X, bottomRight.X));
        float top = MathF.Min(MathF.Min(topLeft.Y, topRight.Y), MathF.Min(bottomLeft.Y, bottomRight.Y));
        float right = MathF.Max(MathF.Max(topLeft.X, topRight.X), MathF.Max(bottomLeft.X, bottomRight.X));
        float bottom = MathF.Max(MathF.Max(topLeft.Y, topRight.Y), MathF.Max(bottomLeft.Y, bottomRight.Y));
        return new UiRect(left, top, right - left, bottom - top);
    }

    private static void SetInteractionState(
        TestContext context,
        UiEntity entity,
        InteractionState state)
    {
        context.World.Set(entity, new InteractionStateComponent { Value = state });
        context.World.Dirty.Mark(entity, UiDirtyFlags.Style);
        context.World.Scheduler.RequestReactiveFrame();
    }

    private static void AdvanceFrame(TestContext context, double seconds)
    {
        context.Clock.Advance(seconds);
        Assert.IsTrue(context.World.Update());
    }

    private static void Settle(TestContext context)
    {
        int guard = 0;
        while (context.Runtime.Animation.ActiveChannelCount > 0 && guard++ < 300)
            AdvanceFrame(context, 1d / 60d);
        Assert.AreEqual(0, context.Runtime.Animation.ActiveChannelCount, "Animation did not settle.");
    }

    private static void Drain(TestContext context)
    {
        int guard = 0;
        while (context.World.Scheduler.NeedsFrame && guard++ < 16)
            Assert.IsTrue(context.World.Update());
        Assert.IsFalse(context.World.Scheduler.NeedsFrame, "Runtime did not settle to idle.");
    }

    private sealed class TestContext : IDisposable
    {
        public TestContext(
            DeterministicUiClock clock,
            UiWorld world,
            UiInteractiveRuntime runtime,
            UiScopeId windowScope,
            UiInputRootId inputRoot,
            BlueprintInstantiator instantiator)
        {
            Clock = clock;
            World = world;
            Runtime = runtime;
            WindowScope = windowScope;
            InputRoot = inputRoot;
            Instantiator = instantiator;
        }

        public DeterministicUiClock Clock { get; }
        public UiWorld World { get; }
        public UiInteractiveRuntime Runtime { get; }
        public UiScopeId WindowScope { get; }
        public UiInputRootId InputRoot { get; }
        public BlueprintInstantiator Instantiator { get; }

        public BlueprintInstance Instantiate(UiNode root) =>
            Instantiator.Instantiate(Ui.Compile(root), WindowScope);

        public void Dispose() => Runtime.Dispose();
    }

    private sealed class OneShotSystem(UiSystemPhase phase, Action<UiWorld> action) : IUiSystem
    {
        private bool _ran;

        public UiSystemPhase Phase { get; } = phase;

        public string Name => "test.animation-one-shot";

        public void Update(UiWorld world, in UiFrameContext frame)
        {
            _ = frame;
            if (_ran)
                return;
            _ran = true;
            action(world);
        }
    }
}

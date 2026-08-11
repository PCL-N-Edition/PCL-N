// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

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
        UiAnimationSettled settled = context.Runtime.Animation.FrameSettlements.Single(record =>
            record.Entity == entity && record.Property == UiAnimationProperty.Opacity);
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
        Assert.IsTrue(context.Runtime.Animation.FrameTransitionGroupCompletions.Any(completed =>
            completed.Group == group && completed.Scope == context.WindowScope));
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
        Assert.IsFalse(context.Runtime.Animation.FrameTransitionGroupCompletions.Any(completed =>
            completed.Group == oldGroup));
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
        Assert.IsFalse(context.Runtime.Animation.FrameTransitionGroupCompletions.Any(completed =>
            completed.Group == group));
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
            UiAnimationProperty.LayoutScaleX,
            out UiAnimationSnapshot flip));
        Assert.AreEqual(0.5f, flip.Current, 0.001f);
        Assert.AreEqual(1f, flip.Target, 0.001f);

        AdvanceFrame(context, 0.05);
        Assert.AreEqual(targetLayout, context.World.Components.Get<LayoutRect>(entity).Value);
        Assert.IsTrue(context.World.Components.Get<ComputedVisual>(entity).LayoutTransform.ScaleX > 0.5f);
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
        float beforeScale = context.World.Components.Get<ComputedVisual>(entity).LayoutTransform.ScaleX;
        float visualWidth = beforeLayout.Width * beforeScale;

        context.Runtime.SetViewport(new UiSize(300, 100));
        Assert.IsTrue(context.World.Update());

        UiRect afterLayout = context.World.Components.Get<LayoutRect>(entity).Value;
        float afterScale = context.World.Components.Get<ComputedVisual>(entity).LayoutTransform.ScaleX;
        Assert.AreEqual(visualWidth, afterLayout.Width * afterScale, 0.01f);
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
}

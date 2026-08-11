// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.UI.Next.Test;

[TestClass]
public sealed class RenderingTests
{
    [TestMethod]
    public void InitialRender_BuildsRetainedTree_AndNoOpFrameDoesNotCommit()
    {
        using TestContext context = Create(new UiSize(320, 180));
        BlueprintInstance live = context.Instantiate(Ui.Container(Ui.Text("Hello")));
        Drain(context);

        Assert.AreEqual(2, context.Rendering.Scene.NodeCount);
        Assert.AreEqual(2, context.Backend.NodeCount);
        Assert.IsTrue(context.Rendering.Scene.TryGetNode(live.RootEntity, out RenderNodeId parent));
        Assert.IsTrue(context.Rendering.Scene.TryGetNode(live.EntityAt(1), out RenderNodeId child));
        Assert.IsTrue(context.Backend.TryGetNode(child, out UiRenderNodeSnapshot childNode));
        Assert.AreEqual(parent, childNode.Parent);
        Assert.AreEqual(UiRenderNodeKind.Text, childNode.Kind);

        int commits = context.Backend.CommitCount;
        Assert.IsTrue(context.World.Update(force: true));
        Assert.AreEqual(commits, context.Backend.CommitCount);
    }

    [TestMethod]
    public void VisualChange_EmitsOnlyChangedField()
    {
        using TestContext context = Create(new UiSize(200, 100));
        UiEntity entity = context.Instantiate(Ui.Container()).RootEntity;
        Drain(context);
        ResolvedStyle style = context.World.Components.Get<ResolvedStyle>(entity);
        ComputedVisual visual = ComputedVisual.FromResolved(in style);
        visual.Background = UiColor.FromRgb(12, 34, 56);
        context.World.Set(entity, visual);
        context.World.Dirty.Mark(entity, UiDirtyFlags.Render);
        context.World.Scheduler.RequestReactiveFrame();

        Assert.IsTrue(context.World.Update());

        ReadOnlySpan<RenderMutation> mutations = context.Backend.LastBatch!.Value.Mutations.Span;
        Assert.AreEqual(1, mutations.Length);
        Assert.AreEqual(RenderMutationKind.SetBrush, mutations[0].Kind);
        Assert.AreEqual(visual.Background, mutations[0].Color);
    }

    [TestMethod]
    public void LayoutChange_EmitsBoundsWithoutRecreatingNode()
    {
        using TestContext context = Create(new UiSize(200, 100));
        UiEntity entity = context.Instantiate(
            Ui.Container().Width(UiLength.Pixels(40)).Height(UiLength.Pixels(20))).RootEntity;
        Drain(context);
        Assert.IsTrue(context.Rendering.Scene.TryGetNode(entity, out RenderNodeId before));
        context.World.Set(entity, new LayoutRect { Value = new UiRect(10, 5, 80, 30) });
        context.World.Dirty.Mark(entity, UiDirtyFlags.Render);
        context.World.Scheduler.RequestReactiveFrame();

        Assert.IsTrue(context.World.Update());

        Assert.IsTrue(context.Rendering.Scene.TryGetNode(entity, out RenderNodeId after));
        Assert.AreEqual(before, after);
        ReadOnlySpan<RenderMutation> mutations = context.Backend.LastBatch!.Value.Mutations.Span;
        Assert.AreEqual(1, mutations.Length);
        Assert.AreEqual(RenderMutationKind.SetBounds, mutations[0].Kind);
        Assert.AreEqual(new UiRect(10, 5, 80, 30), mutations[0].Bounds);
    }

    [TestMethod]
    public void ParentTransform_UsesRetainedCompositionWithoutTouchingChild()
    {
        using TestContext context = Create(new UiSize(240, 100));
        BlueprintInstance live = context.Instantiate(Ui.Container(Ui.Container()));
        UiEntity parent = live.RootEntity;
        UiEntity child = live.EntityAt(1);
        Drain(context);
        Assert.IsTrue(context.Rendering.Scene.TryGetNode(parent, out RenderNodeId parentNode));
        Assert.IsTrue(context.Rendering.Scene.TryGetNode(child, out RenderNodeId childNode));

        context.Runtime.Animation.SetDirect(parent, UiAnimationProperty.TranslateX, 75f);
        Assert.IsTrue(context.World.Update());

        ReadOnlySpan<RenderMutation> mutations = context.Backend.LastBatch!.Value.Mutations.Span;
        Assert.AreEqual(1, mutations.Length);
        Assert.AreEqual(RenderMutationKind.SetTransform, mutations[0].Kind);
        Assert.AreEqual(parentNode, mutations[0].Node);
        Assert.IsTrue(context.Backend.TryGetNode(childNode, out UiRenderNodeSnapshot retainedChild));
        Assert.AreEqual(System.Numerics.Matrix3x2.Identity, retainedChild.Transform);
    }

    [TestMethod]
    public void TextRemeasure_UpdatesTextLayoutHandle()
    {
        using TestContext context = Create(new UiSize(300, 100));
        UiEntity entity = context.Instantiate(Ui.Text("first")).RootEntity;
        Drain(context);
        Assert.IsTrue(context.Rendering.Scene.TryGetNode(entity, out RenderNodeId node));
        Assert.IsTrue(context.Backend.TryGetNode(node, out UiRenderNodeSnapshot before));

        context.World.Set(entity, new TextContent { Value = "other" });
        context.World.Dirty.Mark(entity, UiDirtyFlags.TextMeasure | UiDirtyFlags.Render);
        context.World.Scheduler.RequestReactiveFrame();
        Assert.IsTrue(context.World.Update());

        Assert.IsTrue(context.Backend.TryGetNode(node, out UiRenderNodeSnapshot after));
        Assert.AreNotEqual(before.TextLayout, after.TextLayout);
        Assert.IsTrue(context.Backend.LastBatch!.Value.Mutations.Span.ContainsKind(
            RenderMutationKind.SetTextLayout));
    }

    [TestMethod]
    public void StructuralMove_UpdatesRetainedParent()
    {
        using TestContext context = Create(new UiSize(300, 120));
        BlueprintInstance live = context.Instantiate(
            Ui.Row(Ui.Container(Ui.Text("child")), Ui.Container()));
        UiEntity firstParent = live.EntityAt(1);
        UiEntity child = live.EntityAt(2);
        UiEntity secondParent = live.EntityAt(3);
        Drain(context);
        Assert.IsTrue(context.Rendering.Scene.TryGetNode(secondParent, out RenderNodeId secondParentNode));
        Assert.IsTrue(context.Rendering.Scene.TryGetNode(child, out RenderNodeId childNode));

        context.World.AttachChild(secondParent, child);
        Assert.IsTrue(context.World.Update());

        Assert.IsTrue(context.Backend.TryGetNode(childNode, out UiRenderNodeSnapshot moved));
        Assert.AreEqual(secondParentNode, moved.Parent);
        Assert.IsTrue(context.Backend.LastBatch!.Value.Mutations.Span.ContainsKind(
            RenderMutationKind.SetParent));
        Assert.IsTrue(context.World.Entities.IsAlive(firstParent));
    }

    [TestMethod]
    public void DestroyAndEntitySlotReuse_UsesFreshRenderGeneration()
    {
        using TestContext context = Create(new UiSize(200, 100));
        BlueprintInstance first = context.Instantiate(Ui.Container());
        UiEntity firstEntity = first.RootEntity;
        Drain(context);
        Assert.IsTrue(context.Rendering.Scene.TryGetNode(firstEntity, out RenderNodeId firstNode));

        context.Instantiator.Destroy(first);
        Assert.IsTrue(context.World.Update());
        Assert.IsFalse(context.Backend.TryGetNode(firstNode, out _));

        BlueprintInstance second = context.Instantiate(Ui.Container());
        Drain(context);
        Assert.IsTrue(context.Rendering.Scene.TryGetNode(second.RootEntity, out RenderNodeId secondNode));
        Assert.AreEqual(firstNode.Index, secondNode.Index);
        Assert.AreNotEqual(firstNode.Generation, secondNode.Generation);
        Assert.AreEqual(firstEntity.Index, second.RootEntity.Index);
        Assert.AreNotEqual(firstEntity.Generation, second.RootEntity.Generation);
    }

    [TestMethod]
    public void HeadlessBackend_RejectsStaleMutation()
    {
        HeadlessUiBackend backend = new();
        UiBackendContext backendContext = new(new UiSize(100, 100));
        backend.Initialize(in backendContext);
        RenderNodeId stale = new(10, 1);
        RenderMutation[] mutations = [RenderMutation.SetOpacity(stale, 0.5f)];
        UiCommitBatch batch = new(1, mutations);

        Assert.ThrowsExactly<InvalidOperationException>(() => backend.Commit(in batch));
    }

    [TestMethod]
    public void CommitBatch_DefensivelyCopiesPublicInput()
    {
        RenderNodeId node = new(1, 1);
        RenderMutation[] source = [RenderMutation.SetOpacity(node, 0.25f)];
        UiCommitBatch batch = new(1, source);

        source[0] = RenderMutation.Destroy(node);

        Assert.AreEqual(RenderMutationKind.SetOpacity, batch.Mutations.Span[0].Kind);
        Assert.AreEqual(0.25f, batch.Mutations.Span[0].Scalar);
    }

    [TestMethod]
    public void HeadlessBackend_RejectsTwoLiveGenerationsForSameSlot()
    {
        HeadlessUiBackend backend = new();
        UiBackendContext backendContext = new(new UiSize(100, 100));
        backend.Initialize(in backendContext);
        RenderNodeId first = new(1, 1);
        RenderMutation[] initialMutations =
        [
            RenderMutation.Create(first, new UiEntity(1, 1), UiRenderNodeKind.Layer)
        ];
        UiCommitBatch initial = new(1, initialMutations);
        backend.Commit(in initial);
        RenderNodeId conflicting = new(1, 2);
        RenderMutation[] conflictingMutations =
        [
            RenderMutation.Create(conflicting, new UiEntity(2, 1), UiRenderNodeKind.Layer)
        ];
        UiCommitBatch conflictingBatch = new(2, conflictingMutations);

        Assert.ThrowsExactly<InvalidOperationException>(() => backend.Commit(in conflictingBatch));
    }

    [TestMethod]
    public void RemovingRenderComponents_DestroysChildrenBeforeParent()
    {
        using TestContext context = Create(new UiSize(200, 100));
        BlueprintInstance live = context.Instantiate(Ui.Container(Ui.Text("child")));
        Drain(context);
        UiEntity parent = live.RootEntity;
        UiEntity child = live.EntityAt(1);

        context.World.Remove<NodeKindComponent>(parent);
        context.World.Remove<NodeKindComponent>(child);
        context.World.Dirty.Mark(parent, UiDirtyFlags.Render);
        context.World.Dirty.Mark(child, UiDirtyFlags.Render);
        context.World.AttachChild(parent, child);
        Assert.IsTrue(context.World.Update());

        Assert.AreEqual(0, context.Rendering.Scene.NodeCount);
        Assert.AreEqual(0, context.Backend.NodeCount);
        ReadOnlySpan<RenderMutation> mutations = context.Backend.LastBatch!.Value.Mutations.Span;
        Assert.AreEqual(RenderMutationKind.DestroyNode, mutations[0].Kind);
        Assert.AreEqual(RenderMutationKind.DestroyNode, mutations[1].Kind);
    }

    [TestMethod]
    public void NodeKindChange_PreservesNodeAndLiveChildren()
    {
        using TestContext context = Create(new UiSize(200, 100));
        BlueprintInstance live = context.Instantiate(Ui.Container(Ui.Container()));
        UiEntity parent = live.RootEntity;
        UiEntity child = live.EntityAt(1);
        Drain(context);
        Assert.IsTrue(context.Rendering.Scene.TryGetNode(parent, out RenderNodeId parentNode));
        Assert.IsTrue(context.Rendering.Scene.TryGetNode(child, out RenderNodeId childNode));

        context.World.Set(parent, new NodeKindComponent { Kind = UiNodeKind.Text });
        context.World.Set(parent, new TextContent { Value = "changed" });
        context.World.Dirty.Mark(parent, UiDirtyFlags.TextMeasure | UiDirtyFlags.Render);
        context.World.Scheduler.RequestReactiveFrame();
        Assert.IsTrue(context.World.Update());

        Assert.IsTrue(context.Backend.TryGetNode(parentNode, out UiRenderNodeSnapshot changed));
        Assert.AreEqual(UiRenderNodeKind.Text, changed.Kind);
        Assert.IsTrue(context.Backend.TryGetNode(childNode, out UiRenderNodeSnapshot retainedChild));
        Assert.AreEqual(parentNode, retainedChild.Parent);
    }

    [TestMethod]
    public void ZIndexChange_UpdatesRetainedOrder()
    {
        using TestContext context = Create(new UiSize(200, 100));
        UiEntity entity = context.Instantiate(Ui.Container().HitTestVisible()).RootEntity;
        Drain(context);
        Assert.IsTrue(context.Rendering.Scene.TryGetNode(entity, out RenderNodeId node));
        HitTestableComponent hitTestable = context.World.Components.Get<HitTestableComponent>(entity);
        hitTestable.ZIndex = 7;
        context.World.Set(entity, hitTestable);
        context.World.Dirty.Mark(entity, UiDirtyFlags.Render);
        context.World.Scheduler.RequestReactiveFrame();

        Assert.IsTrue(context.World.Update());

        Assert.IsTrue(context.Backend.TryGetNode(node, out UiRenderNodeSnapshot updated));
        Assert.AreEqual(7L << 32, updated.ZOrder);
        Assert.AreEqual(RenderMutationKind.SetZOrder, context.Backend.LastBatch!.Value.Mutations.Span[0].Kind);
    }

    [TestMethod]
    public void HeadlessBackend_RejectsParentCycle()
    {
        HeadlessUiBackend backend = new();
        UiBackendContext backendContext = new(new UiSize(100, 100));
        backend.Initialize(in backendContext);
        RenderNodeId first = new(1, 1);
        RenderNodeId second = new(2, 1);
        RenderMutation[] create =
        [
            RenderMutation.Create(first, new UiEntity(1, 1), UiRenderNodeKind.Layer),
            RenderMutation.Create(second, new UiEntity(2, 1), UiRenderNodeKind.Layer),
            RenderMutation.SetParent(second, first)
        ];
        UiCommitBatch initial = new(1, create);
        backend.Commit(in initial);
        RenderMutation[] cycle = [RenderMutation.SetParent(first, second)];
        UiCommitBatch invalid = new(2, cycle);

        Assert.ThrowsExactly<InvalidOperationException>(() => backend.Commit(in invalid));
    }

    [TestMethod]
    public void TwoRenderRoots_HaveIndependentScenesAndCommits()
    {
        UiSize viewport = new(240, 120);
        UiWorld world = new(new DeterministicUiClock());
        using UiInteractiveRuntime interactive = new(world, new DeterministicTextEngine(), viewport);
        UiScopeId application = world.CreateRootScope();
        UiScopeId windowA = world.CreateScope(application);
        UiScopeId windowB = world.CreateScope(application);
        HeadlessUiBackend backendA = new();
        HeadlessUiBackend backendB = new();
        using UiRenderingRuntime renderingA = new(world, backendA, windowA, viewport);
        using UiRenderingRuntime renderingB = new(world, backendB, windowB, viewport);
        BlueprintInstantiator blueprints = new(world, new PresentationStore());
        UiEntity entityA = blueprints.Instantiate(Ui.Compile(Ui.Container()), windowA).RootEntity;
        UiEntity entityB = blueprints.Instantiate(Ui.Compile(Ui.Container()), windowB).RootEntity;
        int guard = 0;
        while (world.Scheduler.NeedsFrame && guard++ < 16)
            Assert.IsTrue(world.Update());

        Assert.AreEqual(1, renderingA.Scene.NodeCount);
        Assert.AreEqual(1, renderingB.Scene.NodeCount);
        Assert.IsTrue(renderingA.Scene.TryGetNode(entityA, out _));
        Assert.IsFalse(renderingA.Scene.TryGetNode(entityB, out _));
        Assert.IsTrue(renderingB.Scene.TryGetNode(entityB, out _));
        Assert.IsFalse(renderingB.Scene.TryGetNode(entityA, out _));

        int commitsA = backendA.CommitCount;
        int commitsB = backendB.CommitCount;
        ResolvedStyle style = world.Components.Get<ResolvedStyle>(entityA);
        ComputedVisual visual = ComputedVisual.FromResolved(in style);
        visual.Background = UiColor.FromRgb(1, 2, 3);
        world.Set(entityA, visual);
        world.Dirty.Mark(entityA, UiDirtyFlags.Render);
        world.Scheduler.RequestReactiveFrame();
        Assert.IsTrue(world.Update());

        Assert.AreEqual(commitsA + 1, backendA.CommitCount);
        Assert.AreEqual(commitsB, backendB.CommitCount);
    }

    private static TestContext Create(UiSize viewport)
    {
        DeterministicUiClock clock = new();
        UiWorld world = new(clock);
        UiInteractiveRuntime runtime = new(world, new DeterministicTextEngine(), viewport);
        HeadlessUiBackend backend = new();
        UiScopeId scope = world.CreateRootScope();
        UiRenderingRuntime rendering = new(world, backend, scope, viewport);
        BlueprintInstantiator instantiator = new(world, new PresentationStore());
        return new TestContext(world, runtime, rendering, backend, scope, instantiator);
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
            UiWorld world,
            UiInteractiveRuntime runtime,
            UiRenderingRuntime rendering,
            HeadlessUiBackend backend,
            UiScopeId scope,
            BlueprintInstantiator instantiator)
        {
            World = world;
            Runtime = runtime;
            Rendering = rendering;
            Backend = backend;
            Scope = scope;
            Instantiator = instantiator;
        }

        public UiWorld World { get; }
        public UiInteractiveRuntime Runtime { get; }
        public UiRenderingRuntime Rendering { get; }
        public HeadlessUiBackend Backend { get; }
        public UiScopeId Scope { get; }
        public BlueprintInstantiator Instantiator { get; }

        public BlueprintInstance Instantiate(UiNode node) =>
            Instantiator.Instantiate(Ui.Compile(node), Scope);

        public void Dispose()
        {
            Rendering.Dispose();
            Runtime.Dispose();
        }
    }
}

internal static class RenderMutationTestExtensions
{
    public static bool ContainsKind(this ReadOnlySpan<RenderMutation> mutations, RenderMutationKind kind)
    {
        for (int i = 0; i < mutations.Length; i++)
        {
            if (mutations[i].Kind == kind)
                return true;
        }
        return false;
    }
}

// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.UI.Next.Test;

[TestClass]
public sealed class AccessibilityTests
{
    [TestMethod]
    public void SemanticTree_UsesNearestSemanticAncestorAcrossLogicalContainers()
    {
        using TestContext context = Create();
        BlueprintInstance live = context.Instantiator.Instantiate(
            Ui.Compile(
                Ui.Column(
                        Ui.Container(
                            Ui.Button("Install").Accessible(UiSemanticRole.Button, "Install")))
                    .Accessible(UiSemanticRole.Group, "Actions")),
            context.Scope);
        Drain(context.World);

        UiSemanticTreeSnapshot tree = context.Rendering.Accessibility.Tree;
        Assert.AreEqual(3, tree.NodeCount);
        ReadOnlySpan<UiSemanticNode> nodes = tree.Nodes.Span;
        UiSemanticNode group = Find(nodes, UiSemanticRole.Group);
        UiSemanticNode button = Find(nodes, UiSemanticRole.Button);
        UiSemanticNode text = Find(nodes, UiSemanticRole.StaticText);
        Assert.AreEqual(UiSemanticNodeId.None, group.Parent);
        Assert.AreEqual(group.Id, button.Parent);
        Assert.AreEqual(button.Id, text.Parent);
        Assert.AreEqual("Install", button.Name);
        Assert.AreNotEqual(live.RootEntity, button.Owner);
    }

    [TestMethod]
    public void SemanticState_TracksFocusDisabledAndBounds()
    {
        using TestContext context = Create();
        BlueprintInstance live = context.Instantiator.Instantiate(
            Ui.Compile(Ui.Button("Continue").Width(UiLength.Pixels(120)).Height(UiLength.Pixels(40))),
            context.Scope);
        Drain(context.World);

        Assert.IsTrue(context.Runtime.Input.Focus.Focus(live.RootEntity, context.Clock.Now));
        Drain(context.World);
        UiSemanticNode focused = Find(context.Rendering.Accessibility.Tree.Nodes.Span, UiSemanticRole.Button);
        Assert.IsTrue((focused.State & UiAccessibleState.Focused) != 0);

        InteractionStateComponent interaction = context.World.Components.Get<InteractionStateComponent>(live.RootEntity);
        interaction.Value |= InteractionState.Disabled;
        context.World.Set(live.RootEntity, interaction);
        Drain(context.World);

        UiSemanticNode button = Find(context.Rendering.Accessibility.Tree.Nodes.Span, UiSemanticRole.Button);
        Assert.IsTrue((button.State & UiAccessibleState.Disabled) != 0);
        Assert.IsFalse((button.State & UiAccessibleState.Focused) != 0);
        Assert.AreEqual(120f, button.Bounds.Width, 0.01f);
        Assert.AreEqual(40f, button.Bounds.Height, 0.01f);
    }

    [TestMethod]
    public void RemovingSemanticRole_ReparentsSurvivingSemanticChild()
    {
        using TestContext context = Create();
        BlueprintInstance live = context.Instantiator.Instantiate(
            Ui.Compile(
                Ui.Container(Ui.Button("Child"))
                    .Accessible(UiSemanticRole.Group, "Parent")),
            context.Scope);
        Drain(context.World);
        UiEntity buttonEntity = context.World.Hierarchy.GetNode(live.RootEntity).FirstChild;
        UiSemanticNode before = Find(context.Rendering.Accessibility.Tree.Nodes.Span, UiSemanticRole.Button);
        Assert.IsFalse(before.Parent.IsNone);

        Assert.IsTrue(context.World.Remove<SemanticRole>(live.RootEntity));
        Drain(context.World);

        UiSemanticNode after = Find(context.Rendering.Accessibility.Tree.Nodes.Span, UiSemanticRole.Button);
        Assert.AreEqual(buttonEntity, after.Owner);
        Assert.AreEqual(UiSemanticNodeId.None, after.Parent);
        Assert.AreEqual(2, context.Rendering.Accessibility.Tree.NodeCount);
    }

    [TestMethod]
    public void AccessibilityBackend_ReceivesImmutableUpdatesIncludingEmptyTree()
    {
        using TestContext context = Create();
        BlueprintInstance live = context.Instantiator.Instantiate(Ui.Compile(Ui.Text("Status")), context.Scope);
        Drain(context.World);
        UiSemanticTreeSnapshot populated = context.Backend.LastTree!;
        Assert.AreEqual(1, populated.NodeCount);
        Assert.AreEqual(UiBackendCapabilities.Accessibility, context.Backend.Capabilities);

        context.Instantiator.Destroy(live);
        Drain(context.World);

        Assert.AreEqual(0, context.Backend.LastTree!.NodeCount);
        Assert.IsGreaterThan(0u, populated.Version);
        Assert.IsGreaterThan(populated.Version, context.Backend.LastTree.Version);
    }

    [TestMethod]
    public void AccessibilityInvoke_IsValidatedAndQueuedAsCommand()
    {
        using TestContext context = Create();
        UiCommand command = new(42);
        BlueprintInstance live = context.Instantiator.Instantiate(
            Ui.Compile(Ui.Button("Run").Command(command)),
            context.Scope);
        Drain(context.World);

        context.Backend.Emit(new UiAccessibilityActionRequest(
            live.RootEntity,
            UiAccessibleAction.Invoke,
            context.Clock.Now));
        Assert.IsTrue(context.World.Update());

        Assert.AreEqual(1, context.Rendering.Accessibility.FrameActions.Count);
        Assert.IsTrue(context.Runtime.Input.Commands.TryDequeue(out UiCommandInvocation invocation));
        Assert.AreEqual(command, invocation.Command);
        Assert.AreEqual(UiCommandTrigger.Accessibility, invocation.Trigger);
    }

    [TestMethod]
    public void Modal_RemovesBackgroundNodesFromSemanticTree()
    {
        using TestContext context = Create();
        UiEntity background = context.Instantiator.Instantiate(
            Ui.Compile(Ui.Button("Background")),
            context.Scope).RootEntity;
        Drain(context.World);
        Assert.IsTrue(ContainsOwner(context.Rendering.Accessibility.Tree, background));
        using UiOverlayRuntime overlays = new(
            context.World,
            context.Runtime,
            context.Instantiator,
            context.Scope);

        overlays.ShowModal(Ui.Compile(Ui.Button("Modal")));
        Drain(context.World);

        Assert.IsFalse(ContainsOwner(context.Rendering.Accessibility.Tree, background));
    }

    [TestMethod]
    public void Modal_BackgroundAccessibilityInvoke_IsRejected()
    {
        using TestContext context = Create();
        UiCommand command = new(73);
        UiEntity background = context.Instantiator.Instantiate(
            Ui.Compile(Ui.Button("Background").Command(command)),
            context.Scope).RootEntity;
        Drain(context.World);
        using UiOverlayRuntime overlays = new(
            context.World,
            context.Runtime,
            context.Instantiator,
            context.Scope);
        overlays.ShowModal(Ui.Compile(Ui.Button("Modal")));
        Drain(context.World);

        context.Backend.Emit(new UiAccessibilityActionRequest(
            background,
            UiAccessibleAction.Invoke,
            context.Clock.Now));
        Assert.IsTrue(context.World.Update());

        Assert.AreEqual(0, context.Rendering.Accessibility.FrameActions.Count);
        Assert.IsFalse(context.Runtime.Input.Commands.TryDequeue(out _));
    }

    [TestMethod]
    public void Modal_BackgroundAccessibilityFocus_IsRejected()
    {
        using TestContext context = Create();
        UiEntity background = context.Instantiator.Instantiate(
            Ui.Compile(Ui.Button("Background")),
            context.Scope).RootEntity;
        Drain(context.World);
        Assert.IsTrue(context.Runtime.Input.Focus.Focus(background, context.Clock.Now));
        using UiOverlayRuntime overlays = new(
            context.World,
            context.Runtime,
            context.Instantiator,
            context.Scope);
        overlays.ShowModal(Ui.Compile(Ui.Button("Modal")));
        Drain(context.World);

        context.Backend.Emit(new UiAccessibilityActionRequest(
            background,
            UiAccessibleAction.Focus,
            context.Clock.Now));
        Assert.IsTrue(context.World.Update());

        Assert.AreEqual(0, context.Rendering.Accessibility.FrameActions.Count);
        Assert.IsTrue(context.Runtime.Input.InputRoots.TryResolve(background, out UiInputRootId inputRoot));
        Assert.AreNotEqual(background, context.Runtime.Input.Focus.GetFocused(inputRoot));
    }

    [TestMethod]
    public void Modal_AccessibilityTreeContainsOnlyModalScope()
    {
        using TestContext context = Create();
        context.Instantiator.Instantiate(
            Ui.Compile(Ui.Button("Background")),
            context.Scope);
        Drain(context.World);
        using UiOverlayRuntime overlays = new(
            context.World,
            context.Runtime,
            context.Instantiator,
            context.Scope);
        UiOverlayHandle modal = overlays.ShowModal(
            Ui.Compile(
                Ui.Container(Ui.Button("Modal action"))
                    .Accessible(UiSemanticRole.Dialog, "Modal")));
        Drain(context.World);
        Assert.IsTrue(overlays.TryGetOverlay(modal, out UiOverlaySnapshot snapshot));

        ReadOnlySpan<UiSemanticNode> nodes = context.Rendering.Accessibility.Tree.Nodes.Span;
        Assert.IsGreaterThan(0, nodes.Length);
        for (int i = 0; i < nodes.Length; i++)
            Assert.IsTrue(IsWithinScope(context.World, nodes[i].Owner, snapshot.Scope));
    }

    [TestMethod]
    public void WindowScopes_HaveIndependentSemanticTreesAndDirtyConsumption()
    {
        UiWorld world = new(new DeterministicUiClock());
        UiSize viewport = new(240, 120);
        using UiInteractiveRuntime runtime = new(world, new DeterministicTextEngine(), viewport);
        UiScopeId application = world.CreateRootScope();
        UiScopeId windowA = world.CreateScope(application);
        UiScopeId windowB = world.CreateScope(application);
        AccessibilityBackend backendA = new();
        AccessibilityBackend backendB = new();
        using UiRenderingRuntime renderingA = new(world, backendA, runtime.TextCache, windowA, viewport);
        using UiRenderingRuntime renderingB = new(world, backendB, runtime.TextCache, windowB, viewport);
        BlueprintInstantiator instantiator = new(world, new PresentationStore());
        UiEntity applicationRoot = world.CreateEntity(application);
        BlueprintInstance liveA = instantiator.Instantiate(Ui.Compile(Ui.Text("Window A")), windowA);
        BlueprintInstance liveB = instantiator.Instantiate(Ui.Compile(Ui.Text("Window B")), windowB);
        world.AttachChild(applicationRoot, liveA.RootEntity);
        world.AttachChild(applicationRoot, liveB.RootEntity);
        Drain(world);

        Assert.AreEqual(1, renderingA.Accessibility.Tree.NodeCount);
        Assert.AreEqual(1, renderingB.Accessibility.Tree.NodeCount);
        Assert.AreEqual("Window A", renderingA.Accessibility.Tree.Nodes.Span[0].Name);
        Assert.AreEqual("Window B", renderingB.Accessibility.Tree.Nodes.Span[0].Name);
        uint versionB = renderingB.Accessibility.Tree.Version;

        world.Add(liveA.RootEntity, new AccessibleName { Value = "Updated A" });
        Drain(world);

        Assert.AreEqual("Updated A", renderingA.Accessibility.Tree.Nodes.Span[0].Name);
        Assert.AreEqual(versionB, renderingB.Accessibility.Tree.Version);
    }

    private static UiSemanticNode Find(ReadOnlySpan<UiSemanticNode> nodes, UiSemanticRole role)
    {
        for (int i = 0; i < nodes.Length; i++)
        {
            if (nodes[i].Role == role)
                return nodes[i];
        }
        Assert.Fail("Semantic role was not found: " + role);
        return default;
    }

    private static bool ContainsOwner(UiSemanticTreeSnapshot tree, UiEntity owner)
    {
        ReadOnlySpan<UiSemanticNode> nodes = tree.Nodes.Span;
        for (int i = 0; i < nodes.Length; i++)
        {
            if (nodes[i].Owner == owner)
                return true;
        }
        return false;
    }

    private static bool IsWithinScope(UiWorld world, UiEntity entity, UiScopeId ancestor)
    {
        if (!world.Entities.TryGetScope(entity, out UiScopeId scope))
            return false;
        int guard = 0;
        while (world.Scopes.IsAlive(scope) && guard++ < 1_000_000)
        {
            if (scope == ancestor)
                return true;
            if (!world.Scopes.TryGetParent(scope, out scope) || scope.IsNone)
                break;
        }
        return false;
    }

    private static TestContext Create()
    {
        DeterministicUiClock clock = new();
        UiWorld world = new(clock);
        UiSize viewport = new(320, 180);
        UiInteractiveRuntime runtime = new(world, new DeterministicTextEngine(), viewport);
        UiScopeId scope = world.CreateRootScope();
        runtime.Input.InputRoots.Register(scope);
        AccessibilityBackend backend = new();
        UiRenderingRuntime rendering = new(world, backend, runtime.TextCache, scope, viewport, input: runtime.Input);
        BlueprintInstantiator instantiator = new(world, new PresentationStore());
        return new TestContext(clock, world, runtime, rendering, backend, scope, instantiator);
    }

    private static void Drain(UiWorld world)
    {
        int guard = 0;
        while (world.Scheduler.NeedsFrame && guard++ < 16)
            Assert.IsTrue(world.Update());
        Assert.IsFalse(world.Scheduler.NeedsFrame, "Runtime did not settle to idle.");
    }

    private sealed record TestContext(
        DeterministicUiClock Clock,
        UiWorld World,
        UiInteractiveRuntime Runtime,
        UiRenderingRuntime Rendering,
        AccessibilityBackend Backend,
        UiScopeId Scope,
        BlueprintInstantiator Instantiator) : IDisposable
    {
        public void Dispose()
        {
            Rendering.Dispose();
            Runtime.Dispose();
        }
    }

    private sealed class AccessibilityBackend : IUiBackend, IAccessibilityBackend
    {
        public UiBackendCapabilities Capabilities => UiBackendCapabilities.Accessibility;
        public UiSemanticTreeSnapshot? LastTree { get; private set; }
        public event Action<UiAccessibilityActionRequest>? AccessibilityActionRaised;
        public void Initialize(in UiBackendContext context) => _ = context;
        public void Commit(in UiCommitBatch batch) => _ = batch;
        public void RequestFrame() { }
        public void CommitAccessibility(UiSemanticTreeSnapshot tree) => LastTree = tree;
        public void Emit(UiAccessibilityActionRequest request) => AccessibilityActionRaised?.Invoke(request);
    }
}

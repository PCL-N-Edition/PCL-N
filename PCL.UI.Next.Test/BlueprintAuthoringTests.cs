// Copyright (c) 2026 PCL N contributors.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.UI.Next.Test;

[TestClass]
public sealed class BlueprintAuthoringTests
{
    private const int TitleSlice = 1;
    private const int LoggedInSlice = 2;
    private const int UserSlice = 3;
    private const int NestedSlice = 4;
    private const int TitleSelectorId = 10;
    private const int LoggedInSelectorId = 11;
    private const int UserSelectorId = 12;
    private const int NestedSelectorId = 13;

    [TestMethod]
    public void Compile_BuildsNodeGraph_WithChildren()
    {
        UiNode tree = Ui.Column(
            Ui.Text("Hello").Class(UiClass.PageTitle),
            Ui.Button("Go").Command(new UiCommand(100, "Launch")));

        UiBlueprint bp = Ui.Compile(tree, "Home");
        Assert.AreEqual("Home", bp.Name);
        Assert.IsTrue(bp.NodeCount >= 3);
        Assert.AreEqual(UiNodeKind.Column, bp.GetNode(bp.RootIndex).Kind);
    }

    [TestMethod]
    public void Instantiate_CreatesEntities_AndStaticText()
    {
        UiWorld world = new(new DeterministicUiClock());
        UiScopeId scope = world.CreateRootScope();
        var store = new PresentationStore();
        var inst = new BlueprintInstantiator(world, store);

        UiBlueprint bp = Ui.Compile(Ui.Column(Ui.Text("Static")));
        BlueprintInstance live = inst.Instantiate(bp, scope);

        Assert.IsTrue(world.Entities.IsAlive(live.RootEntity));
        UiEntity textEntity = FindFirstKind(world, live, UiNodeKind.Text);
        Assert.IsTrue(world.Entities.IsAlive(textEntity));
        Assert.AreEqual("Static", world.Components.Pool<TextContent>().Get(textEntity).Value);
    }

    [TestMethod]
    public void Binding_UpdatesText_WhenSliceChanges()
    {
        UiWorld world = new(new DeterministicUiClock());
        UiScopeId scope = world.CreateRootScope();
        var store = new PresentationStore();
        store.Set(TitleSlice, "v1");
        var inst = new BlueprintInstantiator(world, store);

        UiSelector<string> title = UiSelectors.String(
            TitleSelectorId,
            TitleSlice,
            s => s.Get<string>(TitleSlice));

        UiBlueprint bp = Ui.Compile(Ui.Text().BindText(title));
        BlueprintInstance live = inst.Instantiate(bp, scope);
        UiEntity textEntity = live.RootEntity;
        Assert.AreEqual("v1", world.Components.Pool<TextContent>().Get(textEntity).Value);

        store.Set(TitleSlice, "v2");
        inst.Update(live);
        Assert.AreEqual("v2", world.Components.Pool<TextContent>().Get(textEntity).Value);

        inst.Update(live);
        Assert.AreEqual("v2", world.Components.Pool<TextContent>().Get(textEntity).Value);
    }

    [TestMethod]
    public void MultiDependencySelector_InvalidatesWhenEitherSliceChanges()
    {
        UiWorld world = new(new DeterministicUiClock());
        UiScopeId scope = world.CreateRootScope();
        var store = new PresentationStore();
        store.Set(TitleSlice, "A");
        store.Set(UserSlice, "u1");
        var inst = new BlueprintInstantiator(world, store);

        UiSelector<string> combined = UiSelectors.String(
            20,
            [TitleSlice, UserSlice],
            s => s.Get<string>(TitleSlice) + ":" + s.Get<string>(UserSlice));

        BlueprintInstance live = inst.Instantiate(Ui.Compile(Ui.Text().BindText(combined)), scope);
        Assert.AreEqual("A:u1", world.Components.Get<TextContent>(live.RootEntity).Value);

        store.Set(UserSlice, "u2");
        inst.Update(live);
        Assert.AreEqual("A:u2", world.Components.Get<TextContent>(live.RootEntity).Value);
    }

    [TestMethod]
    public void StructuralIf_SwapsBranches_OnConditionChange()
    {
        UiWorld world = new(new DeterministicUiClock());
        UiScopeId scope = world.CreateRootScope();
        var store = new PresentationStore();
        store.Set(LoggedInSlice, false);
        var inst = new BlueprintInstantiator(world, store);

        UiSelector<bool> loggedIn = UiSelectors.Bool(
            LoggedInSelectorId,
            LoggedInSlice,
            s => s.Get<bool>(LoggedInSlice));

        UiBlueprint bp = Ui.Compile(
            Ui.If(
                loggedIn,
                whenTrue: Ui.Text("Welcome"),
                whenFalse: Ui.Text("Login")));

        BlueprintInstance live = inst.Instantiate(bp, scope);
        Assert.AreEqual("Login", GetMountedText(world, live));

        store.Set(LoggedInSlice, true);
        inst.Update(live);
        Assert.AreEqual("Welcome", GetMountedText(world, live));

        store.Set(LoggedInSlice, false);
        inst.Update(live);
        Assert.AreEqual("Login", GetMountedText(world, live));
    }

    [TestMethod]
    public void StructuralIf_NewBranchAppliesBindingImmediately()
    {
        UiWorld world = new(new DeterministicUiClock());
        UiScopeId scope = world.CreateRootScope();
        var store = new PresentationStore();
        store.Set(LoggedInSlice, false);
        store.Set(UserSlice, "alice");
        var inst = new BlueprintInstantiator(world, store);

        UiSelector<bool> loggedIn = UiSelectors.Bool(LoggedInSelectorId, LoggedInSlice, s => s.Get<bool>(LoggedInSlice));
        UiSelector<string> user = UiSelectors.String(UserSelectorId, UserSlice, s => s.Get<string>(UserSlice));

        BlueprintInstance live = inst.Instantiate(
            Ui.Compile(Ui.If(loggedIn, Ui.Text().BindText(user), Ui.Text("Login"))),
            scope);
        Assert.AreEqual("Login", GetMountedText(world, live));

        // username version already "current" before true branch existed — must still apply on mount.
        store.Set(LoggedInSlice, true);
        inst.Update(live);
        Assert.AreEqual("alice", GetMountedText(world, live));
    }

    [TestMethod]
    public void StructuralIf_RetoggleAppliesCurrentBinding()
    {
        UiWorld world = new(new DeterministicUiClock());
        UiScopeId scope = world.CreateRootScope();
        var store = new PresentationStore();
        store.Set(LoggedInSlice, true);
        store.Set(UserSlice, "v1");
        var inst = new BlueprintInstantiator(world, store);

        UiSelector<bool> loggedIn = UiSelectors.Bool(LoggedInSelectorId, LoggedInSlice, s => s.Get<bool>(LoggedInSlice));
        UiSelector<string> user = UiSelectors.String(UserSelectorId, UserSlice, s => s.Get<string>(UserSlice));

        BlueprintInstance live = inst.Instantiate(
            Ui.Compile(Ui.If(loggedIn, Ui.Text().BindText(user), Ui.Text("Login"))),
            scope);
        Assert.AreEqual("v1", GetMountedText(world, live));

        store.Set(LoggedInSlice, false);
        inst.Update(live);
        Assert.AreEqual("Login", GetMountedText(world, live));

        // State changes while branch is unmounted.
        store.Set(UserSlice, "v2");
        store.Set(LoggedInSlice, true);
        inst.Update(live);
        Assert.AreEqual("v2", GetMountedText(world, live));
    }

    [TestMethod]
    public void NestedIf_NewBranchAppliesBinding()
    {
        UiWorld world = new(new DeterministicUiClock());
        UiScopeId scope = world.CreateRootScope();
        var store = new PresentationStore();
        store.Set(LoggedInSlice, true);
        store.Set(NestedSlice, false);
        store.Set(UserSlice, "nested-user");
        var inst = new BlueprintInstantiator(world, store);

        UiSelector<bool> outer = UiSelectors.Bool(LoggedInSelectorId, LoggedInSlice, s => s.Get<bool>(LoggedInSlice));
        UiSelector<bool> inner = UiSelectors.Bool(NestedSelectorId, NestedSlice, s => s.Get<bool>(NestedSlice));
        UiSelector<string> user = UiSelectors.String(UserSelectorId, UserSlice, s => s.Get<string>(UserSlice));

        UiBlueprint bp = Ui.Compile(
            Ui.If(
                outer,
                whenTrue: Ui.If(inner, Ui.Text().BindText(user), Ui.Text("inner-off")),
                whenFalse: Ui.Text("outer-off")));

        BlueprintInstance live = inst.Instantiate(bp, scope);
        Assert.AreEqual("inner-off", GetMountedText(world, live));

        store.Set(NestedSlice, true);
        inst.Update(live);
        Assert.AreEqual("nested-user", GetMountedText(world, live));
    }

    [TestMethod]
    public void NestedIf_OuterRemountReconcilesInnerImmediately()
    {
        // Outer starts false so Inner is never force-seeded on instantiate.
        // Only Outer slice changes → Inner host must still evaluate in the same Update.
        UiWorld world = new(new DeterministicUiClock());
        UiScopeId scope = world.CreateRootScope();
        var store = new PresentationStore();
        store.Set(LoggedInSlice, false);
        store.Set(NestedSlice, true);
        store.Set(UserSlice, "Alice");
        var inst = new BlueprintInstantiator(world, store, registerPipelineSystem: false);

        UiSelector<bool> outer = UiSelectors.Bool(LoggedInSelectorId, LoggedInSlice, s => s.Get<bool>(LoggedInSlice));
        UiSelector<bool> inner = UiSelectors.Bool(NestedSelectorId, NestedSlice, s => s.Get<bool>(NestedSlice));
        UiSelector<string> user = UiSelectors.String(UserSelectorId, UserSlice, s => s.Get<string>(UserSlice));

        UiBlueprint bp = Ui.Compile(
            Ui.If(
                outer,
                whenTrue: Ui.If(inner, Ui.Text().BindText(user), Ui.Text("inner-off")),
                whenFalse: Ui.Text("outer-off")));

        BlueprintInstance live = inst.Instantiate(bp, scope);
        Assert.AreEqual("outer-off", GetMountedText(world, live));

        store.Set(LoggedInSlice, true);
        // NestedSlice did not change — Inner must still reconcile via structural work queue.
        inst.Update(live);
        Assert.AreEqual("Alice", GetMountedText(world, live));
    }

    [TestMethod]
    public void Button_HasBehaviorsAndCommand_WithoutTextContent()
    {
        UiWorld world = new(new DeterministicUiClock());
        UiScopeId scope = world.CreateRootScope();
        var store = new PresentationStore();
        var inst = new BlueprintInstantiator(world, store);

        UiCommand launch = new(42, "Launch");
        UiBlueprint bp = Ui.Compile(Ui.Button("Start").Command(launch));
        BlueprintInstance live = inst.Instantiate(bp, scope);

        UiEntity button = FindFirstKind(world, live, UiNodeKind.Button);
        Assert.IsTrue(world.Components.Pool<BehaviorComponent>().Get(button).Flags.HasFlag(UiBehavior.Clickable));
        Assert.AreEqual(42, world.Components.Pool<CommandBindingComponent>().Get(button).CommandId);
        Assert.IsTrue(world.Components.Pool<StyleClassSet>().Get(button).Contains(UiClass.Button.Id));
        Assert.IsFalse(world.Components.Has<TextContent>(button), "Button shell must not carry TextContent");

        UiEntity label = FindFirstKind(world, live, UiNodeKind.Text);
        Assert.AreEqual("Start", world.Components.Get<TextContent>(label).Value);
    }

    [TestMethod]
    public void StyleClass_OverflowThrows()
    {
        UiNode node = Ui.Text("x")
            .Class(new UiClass(10, "a"))
            .Class(new UiClass(11, "b"))
            .Class(new UiClass(12, "c"))
            .Class(new UiClass(13, "d"));

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            node.Class(new UiClass(14, "e")));
    }

    [TestMethod]
    public void Blueprint_StyleClassIds_AreImmutable()
    {
        UiBlueprint bp = Ui.Compile(Ui.Text("x").Class(UiClass.Body));
        ReadOnlySpan<int> ids = bp.GetNode(bp.RootIndex).StyleClassIds;
        Assert.AreEqual(1, ids.Length);
        Assert.AreEqual(UiClass.Body.Id, ids[0]);
    }

    [TestMethod]
    public void ScopeDispose_UnregistersBlueprintInstanceImmediately()
    {
        UiWorld world = new(new DeterministicUiClock());
        UiScopeId root = world.CreateRootScope();
        UiScopeId page = world.CreateScope(root);
        var store = new PresentationStore();
        store.Set(TitleSlice, "t");
        var inst = new BlueprintInstantiator(world, store);

        UiSelector<string> title = UiSelectors.String(TitleSelectorId, TitleSlice, s => s.Get<string>(TitleSlice));
        BlueprintInstance live = inst.Instantiate(Ui.Compile(Ui.Text().BindText(title)), page);
        Assert.AreEqual(1, inst.Instances.Count);

        // True scope ownership: no UpdateAll required.
        world.DisposeScope(page);
        Assert.AreEqual(0, inst.Instances.Count);
        Assert.IsFalse(live.IsAlive);
    }

    [TestMethod]
    public void Pipeline_BindingUpdateRunsWithoutManualUpdate()
    {
        UiWorld world = new(new DeterministicUiClock());
        UiScopeId scope = world.CreateRootScope();
        var store = new PresentationStore();
        store.Set(TitleSlice, "before");
        var inst = new BlueprintInstantiator(world, store); // registers BlueprintRuntimeSystem

        UiSelector<string> title = UiSelectors.String(TitleSelectorId, TitleSlice, s => s.Get<string>(TitleSlice));
        BlueprintInstance live = inst.Instantiate(Ui.Compile(Ui.Text().BindText(title)), scope);
        Assert.AreEqual("before", world.Components.Get<TextContent>(live.RootEntity).Value);

        store.Set(TitleSlice, "after"); // schedules reactive frame
        Assert.IsTrue(world.Scheduler.NeedsFrame);
        Assert.IsTrue(world.Update());
        Assert.AreEqual("after", world.Components.Get<TextContent>(live.RootEntity).Value);
    }

    [TestMethod]
    public void DependencyIndex_OnlyTouchesAffectedBindings()
    {
        UiWorld world = new(new DeterministicUiClock());
        UiScopeId scope = world.CreateRootScope();
        var store = new PresentationStore();
        store.Set(TitleSlice, "t");
        store.Set(UserSlice, "u");
        var inst = new BlueprintInstantiator(world, store, registerPipelineSystem: false);

        UiSelector<string> t = UiSelectors.String(TitleSelectorId, TitleSlice, s => s.Get<string>(TitleSlice));
        UiSelector<string> u = UiSelectors.String(UserSelectorId, UserSlice, s => s.Get<string>(UserSlice));
        UiBlueprint bp = Ui.Compile(Ui.Column(Ui.Text().BindText(t), Ui.Text().BindText(u)));
        Assert.IsTrue(bp.DependencyIndex.TryGetPropertyBindings(TitleSlice, out ReadOnlySpan<int> titleBindings));
        Assert.AreEqual(1, titleBindings.Length);
        Assert.IsTrue(bp.DependencyIndex.TryGetPropertyBindings(UserSlice, out ReadOnlySpan<int> userBindings));
        Assert.AreEqual(1, userBindings.Length);

        BlueprintInstance live = inst.Instantiate(bp, scope);
        store.Set(UserSlice, "u2");
        inst.Update(live);
        // Both texts still correct; dispatch only needed UserSlice binding.
        Assert.AreEqual("t", GetMountedTexts(world, live)[0]);
        Assert.AreEqual("u2", GetMountedTexts(world, live)[1]);
    }

    [TestMethod]
    public void Destroy_RemovesInstanceEntities()
    {
        UiWorld world = new(new DeterministicUiClock());
        UiScopeId scope = world.CreateRootScope();
        var store = new PresentationStore();
        var inst = new BlueprintInstantiator(world, store);
        BlueprintInstance live = inst.Instantiate(Ui.Compile(Ui.Column(Ui.Text("x"))), scope);
        UiEntity root = live.RootEntity;
        inst.Destroy(live);
        Assert.IsFalse(world.Entities.IsAlive(root));
        Assert.IsFalse(live.IsAlive);
    }

    private static UiEntity FindFirstKind(UiWorld world, BlueprintInstance live, UiNodeKind kind)
    {
        for (int i = 0; i < live.Blueprint.NodeCount; i++)
        {
            UiEntity e = live.EntityAt(i);
            if (!world.Entities.IsAlive(e))
                continue;
            if (world.Components.Pool<NodeKindComponent>().TryGet(e, out NodeKindComponent k) && k.Kind == kind)
                return e;
        }

        return UiEntity.None;
    }

    private static string? GetMountedText(UiWorld world, BlueprintInstance live)
    {
        UiEntity text = FindFirstKind(world, live, UiNodeKind.Text);
        if (!world.Entities.IsAlive(text))
            return null;
        return world.Components.Pool<TextContent>().Get(text).Value;
    }

    private static List<string?> GetMountedTexts(UiWorld world, BlueprintInstance live)
    {
        List<string?> result = [];
        for (int i = 0; i < live.Blueprint.NodeCount; i++)
        {
            UiEntity e = live.EntityAt(i);
            if (!world.Entities.IsAlive(e))
                continue;
            if (world.Components.TryGet(e, out NodeKindComponent k) && k.Kind == UiNodeKind.Text &&
                world.Components.TryGet(e, out TextContent text))
            {
                result.Add(text.Value);
            }
        }

        return result;
    }
}

// Copyright (c) 2026 PCL N contributors.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.UI.Next.Test;

[TestClass]
public sealed class BlueprintAuthoringTests
{
    private const int TitleSlice = 1;
    private const int LoggedInSlice = 2;
    private const int TitleSelectorId = 10;
    private const int LoggedInSelectorId = 11;

    [TestMethod]
    public void Compile_BuildsNodeGraph_WithChildren()
    {
        UiNode tree = Ui.Column(
            Ui.Text("Hello").Class(UiClass.PageTitle),
            Ui.Button("Go").Command(new UiCommand(100, "Launch")));

        UiBlueprint bp = Ui.Compile(tree, "Home");
        Assert.AreEqual("Home", bp.Name);
        Assert.IsTrue(bp.NodeCount >= 3);
        Assert.AreEqual(UiNodeKind.Column, bp.Nodes[bp.RootIndex].Kind);
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

        // Same version → no churn required; value stays
        inst.Update(live);
        Assert.AreEqual("v2", world.Components.Pool<TextContent>().Get(textEntity).Value);
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
    public void Button_HasBehaviorsAndCommand()
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
}

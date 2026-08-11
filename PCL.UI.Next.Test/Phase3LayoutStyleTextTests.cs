// Copyright (c) 2026 PCL N contributors.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.UI.Next.Test;

[TestClass]
public sealed class Phase3LayoutStyleTextTests
{
    [TestMethod]
    public void StackLayout_MeasuresTextAndArrangesGap()
    {
        TestContext context = Create(new UiSize(240f, 120f));
        BlueprintInstance live = context.Instantiator.Instantiate(
            Ui.Compile(Ui.Column(
                Ui.Text("A").Class(UiClass.Body),
                Ui.Text("BB").Class(UiClass.Body)).Gap(10f)),
            context.Scope);

        Drain(context.World);

        UiRect root = context.World.Components.Get<LayoutRect>(live.RootEntity).Value;
        Assert.AreEqual(new UiRect(0f, 0f, 240f, 120f), root);

        UiEntity first = live.EntityAt(1);
        UiEntity second = live.EntityAt(2);
        UiRect firstRect = context.World.Components.Get<LayoutRect>(first).Value;
        UiRect secondRect = context.World.Components.Get<LayoutRect>(second).Value;
        Assert.AreEqual(firstRect.Bottom + 10f, secondRect.Y, 0.001f);
        Assert.IsTrue(firstRect.Height > 0f);
    }

    [TestMethod]
    public void GridLayout_ResolvesFixedAndStarTracks()
    {
        TestContext context = Create(new UiSize(300f, 100f));
        UiGridDefinition definition = new(
            [UiGridTrack.Fixed(100f), UiGridTrack.Star()],
            [UiGridTrack.Star()]);
        BlueprintInstance live = context.Instantiator.Instantiate(
            Ui.Compile(Ui.Grid(
                definition,
                Ui.Container().GridCell(0, 0),
                Ui.Container().GridCell(0, 1))),
            context.Scope);

        Drain(context.World);

        UiRect first = context.World.Components.Get<LayoutRect>(live.EntityAt(1)).Value;
        UiRect second = context.World.Components.Get<LayoutRect>(live.EntityAt(2)).Value;
        Assert.AreEqual(100f, first.Width, 0.001f);
        Assert.AreEqual(100f, second.X, 0.001f);
        Assert.AreEqual(200f, second.Width, 0.001f);
    }

    [TestMethod]
    public void ViewportResize_RecomputesStarTrackWithoutRebuildingBlueprint()
    {
        TestContext context = Create(new UiSize(300f, 100f));
        UiGridDefinition definition = new(
            [UiGridTrack.Fixed(100f), UiGridTrack.Star()],
            [UiGridTrack.Star()]);
        BlueprintInstance live = context.Instantiator.Instantiate(
            Ui.Compile(Ui.Grid(
                definition,
                Ui.Container().GridCell(0, 0),
                Ui.Container().GridCell(0, 1))),
            context.Scope);
        Drain(context.World);
        Assert.AreEqual(200f, context.World.Components.Get<LayoutRect>(live.EntityAt(2)).Value.Width, 0.001f);

        context.Runtime.SetViewport(new UiSize(400f, 100f));
        Drain(context.World);

        Assert.AreEqual(300f, context.World.Components.Get<LayoutRect>(live.EntityAt(2)).Value.Width, 0.001f);
    }

    [TestMethod]
    public void AbsoluteLayout_UsesExplicitPlacementWithoutLayoutAnimationState()
    {
        TestContext context = Create(new UiSize(200f, 100f));
        BlueprintInstance live = context.Instantiator.Instantiate(
            Ui.Compile(Ui.Absolute(
                Ui.Container()
                    .Width(UiLength.Pixels(50f))
                    .Height(UiLength.Pixels(20f))
                    .At(25f, 30f))),
            context.Scope);

        Drain(context.World);

        UiRect child = context.World.Components.Get<LayoutRect>(live.EntityAt(1)).Value;
        Assert.AreEqual(new UiRect(25f, 30f, 50f, 20f), child);
    }

    [TestMethod]
    public void TextCache_SharesIdenticalLayouts()
    {
        TestContext context = Create(new UiSize(300f, 80f));
        BlueprintInstance live = context.Instantiator.Instantiate(
            Ui.Compile(Ui.Row(
                Ui.Text("same").Class(UiClass.Body),
                Ui.Text("same").Class(UiClass.Body))),
            context.Scope);

        Drain(context.World);

        TextLayout first = context.World.Components.Get<TextLayout>(live.EntityAt(1));
        TextLayout second = context.World.Components.Get<TextLayout>(live.EntityAt(2));
        Assert.AreEqual(first.Handle, second.Handle);
        Assert.AreEqual(1, context.Runtime.TextCache.Count);
        Assert.AreEqual(1, context.TextEngine.LayoutCount);
    }

    [TestMethod]
    public void TextWrap_UsesWidthConstraintInCacheKeyAndMetrics()
    {
        TestContext context = Create(new UiSize(200f, 100f));
        BlueprintInstance live = context.Instantiator.Instantiate(
            Ui.Compile(Ui.Text("abcdefghij").Class(UiClass.Body).WrapText(25f)),
            context.Scope);

        Drain(context.World);

        TextLayout layout = context.World.Components.Get<TextLayout>(live.RootEntity);
        Assert.IsTrue(layout.Size.Width <= 25f);
        Assert.IsTrue(layout.Size.Height > 14f * 1.2f);
    }

    [TestMethod]
    public void ThemeTokenChange_InvalidatesOnlyDependentTextStyle()
    {
        TestContext context = Create(new UiSize(300f, 100f), applyDefaults: false);
        ThemeToken<float> fontA = new(1001, "Font.A");
        ThemeToken<float> fontB = new(1002, "Font.B");
        UiClass classA = new(101, "A");
        UiClass classB = new(102, "B");
        context.Runtime.Theme.Set(fontA, 10f);
        context.Runtime.Theme.Set(fontB, 12f);
        context.Runtime.Styles.Add(new UiStyleRule(classA, default(UiStyleValues).WithFontSize(fontA)));
        context.Runtime.Styles.Add(new UiStyleRule(classB, default(UiStyleValues).WithFontSize(fontB)));

        BlueprintInstance live = context.Instantiator.Instantiate(
            Ui.Compile(Ui.Row(
                Ui.Text("token").Class(classA),
                Ui.Text("token").Class(classB))),
            context.Scope);
        Drain(context.World);

        TextLayoutHandle firstBefore = context.World.Components.Get<TextLayout>(live.EntityAt(1)).Handle;
        TextLayoutHandle secondBefore = context.World.Components.Get<TextLayout>(live.EntityAt(2)).Handle;

        context.Runtime.Theme.Set(fontA, 20f);
        Drain(context.World);

        TextLayoutHandle firstAfter = context.World.Components.Get<TextLayout>(live.EntityAt(1)).Handle;
        TextLayoutHandle secondAfter = context.World.Components.Get<TextLayout>(live.EntityAt(2)).Handle;
        Assert.AreNotEqual(firstBefore, firstAfter);
        Assert.AreEqual(secondBefore, secondAfter);
    }

    [TestMethod]
    public void InteractionState_ResolvesDynamicStyleRule()
    {
        TestContext context = Create(new UiSize(160f, 60f));
        BlueprintInstance live = context.Instantiator.Instantiate(Ui.Compile(Ui.Button("Go")), context.Scope);
        Drain(context.World);

        UiEntity button = live.RootEntity;
        UiColor normal = context.World.Components.Get<ResolvedStyle>(button).Background;
        context.World.Set(button, new InteractionStateComponent { Value = InteractionState.Hovered });
        context.World.Dirty.Mark(button, UiDirtyFlags.Style);
        context.World.Scheduler.RequestReactiveFrame();
        Drain(context.World);

        UiColor hovered = context.World.Components.Get<ResolvedStyle>(button).Background;
        Assert.AreNotEqual(normal, hovered);
        Assert.AreEqual(context.Runtime.Theme.Get(UiThemeTokens.SurfaceHover), hovered);
    }

    [TestMethod]
    public void TextStyle_InheritsTypographyFromParent()
    {
        TestContext context = Create(new UiSize(200f, 80f), applyDefaults: false);
        UiClass typography = new(333, "TypographyParent");
        context.Runtime.Styles.Add(new UiStyleRule(
            typography,
            default(UiStyleValues).WithFontSize(24f).WithFontWeight(700)));
        BlueprintInstance live = context.Instantiator.Instantiate(
            Ui.Compile(Ui.Container(Ui.Text("Inherited")).Class(typography)),
            context.Scope);

        Drain(context.World);

        ResolvedStyle child = context.World.Components.Get<ResolvedStyle>(live.EntityAt(1));
        Assert.AreEqual(24f, child.FontSize);
        Assert.AreEqual(700, child.FontWeight);
    }

    [TestMethod]
    public void BoundTextChange_RemeasuresAncestorInSameFrame()
    {
        const int slice = 81;
        TestContext context = Create(new UiSize(300f, 100f));
        context.Store.Set(slice, "A");
        UiSelector<string> selector = UiSelectors.String(901, slice, s => s.Get<string>(slice));
        BlueprintInstance live = context.Instantiator.Instantiate(
            Ui.Compile(Ui.Column(Ui.Text().BindText(selector).Class(UiClass.Body))),
            context.Scope);
        Drain(context.World);
        float before = context.World.Components.Get<DesiredSize>(live.RootEntity).Value.Width;

        context.Store.Set(slice, "A much longer title");
        Assert.IsTrue(context.World.Update());
        float after = context.World.Components.Get<DesiredSize>(live.RootEntity).Value.Width;

        Assert.IsTrue(after > before);
    }

    [TestMethod]
    public void StructuralBranchChange_RemeasuresAncestorsInSameFrame()
    {
        const int slice = 82;
        TestContext context = Create(new UiSize(400f, 100f));
        context.Store.Set(slice, false);
        UiSelector<bool> condition = UiSelectors.Bool(902, slice, s => s.Get<bool>(slice));
        BlueprintInstance live = context.Instantiator.Instantiate(
            Ui.Compile(Ui.Column(Ui.If(
                condition,
                Ui.Text("a substantially wider branch").Class(UiClass.Body),
                Ui.Text("x").Class(UiClass.Body)))),
            context.Scope);
        Drain(context.World);
        float before = context.World.Components.Get<DesiredSize>(live.RootEntity).Value.Width;

        context.Store.Set(slice, true);
        Assert.IsTrue(context.World.Update());
        float after = context.World.Components.Get<DesiredSize>(live.RootEntity).Value.Width;

        Assert.IsTrue(after > before);
    }

    private static TestContext Create(UiSize viewport, bool applyDefaults = true)
    {
        UiWorld world = new(new DeterministicUiClock());
        DeterministicTextEngine text = new();
        UiPhase3Runtime runtime = new(world, text, viewport, applyDefaults);
        UiScopeId scope = world.CreateRootScope();
        PresentationStore store = new();
        BlueprintInstantiator instantiator = new(world, store);
        return new TestContext(world, runtime, text, scope, store, instantiator);
    }

    private static void Drain(UiWorld world)
    {
        int guard = 0;
        while (world.Scheduler.NeedsFrame && guard++ < 8)
            Assert.IsTrue(world.Update());
        Assert.IsFalse(world.Scheduler.NeedsFrame, "Phase 3 runtime did not settle to idle.");
    }

    private sealed record TestContext(
        UiWorld World,
        UiPhase3Runtime Runtime,
        DeterministicTextEngine TextEngine,
        UiScopeId Scope,
        PresentationStore Store,
        BlueprintInstantiator Instantiator);
}

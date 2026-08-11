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
    public void WrappedText_UsesParentAvailableWidth()
    {
        TestContext context = Create(new UiSize(200f, 200f));
        BlueprintInstance live = context.Instantiator.Instantiate(
            Ui.Compile(Ui.Column(
                Ui.Text("很长很长很长很长很长很长很长很长很长很长")
                    .Class(UiClass.Body)
                    .WrapText(500f))),
            context.Scope);

        Drain(context.World);

        TextLayout layout = context.World.Components.Get<TextLayout>(live.EntityAt(1));
        Assert.IsTrue(layout.Size.Width <= 200f);
        Assert.IsTrue(layout.Size.Height > 14f * 1.2f);
    }

    [TestMethod]
    public void WrappedText_RemeasuresOnViewportShrink()
    {
        TestContext context = Create(new UiSize(600f, 200f));
        BlueprintInstance live = context.Instantiator.Instantiate(
            Ui.Compile(Ui.Text("很长很长很长很长很长很长很长很长很长很长")
                .Class(UiClass.Body)
                .WrapText(500f)),
            context.Scope);
        Drain(context.World);
        TextLayout before = context.World.Components.Get<TextLayout>(live.RootEntity);

        context.Runtime.SetViewport(new UiSize(200f, 200f));
        Drain(context.World);
        TextLayout after = context.World.Components.Get<TextLayout>(live.RootEntity);

        Assert.AreNotEqual(before.Handle, after.Handle);
        Assert.IsTrue(after.Size.Height > before.Size.Height);
        Assert.IsTrue(after.Size.Width <= 200f);
    }

    [TestMethod]
    public void WrappedText_InFixedGridTrackUsesTrackWidth()
    {
        TestContext context = Create(new UiSize(300f, 200f));
        UiGridDefinition grid = new(
            [UiGridTrack.Fixed(100f), UiGridTrack.Star()],
            [UiGridTrack.Auto()]);
        BlueprintInstance live = context.Instantiator.Instantiate(
            Ui.Compile(Ui.Grid(
                grid,
                Ui.Text("很长很长很长很长很长很长很长很长")
                    .Class(UiClass.Body)
                    .WrapText(500f)
                    .GridCell(0, 0))),
            context.Scope);

        Drain(context.World);

        TextLayout layout = context.World.Components.Get<TextLayout>(live.EntityAt(1));
        Assert.IsTrue(layout.Size.Width <= 100f);
        Assert.IsTrue(layout.Size.Height > 14f * 1.2f);
    }

    [TestMethod]
    public void WrappedText_InPercentWidthUsesResolvedConstraint()
    {
        TestContext context = Create(new UiSize(200f, 200f));
        BlueprintInstance live = context.Instantiator.Instantiate(
            Ui.Compile(Ui.Text("很长很长很长很长很长很长很长很长")
                .Class(UiClass.Body)
                .Width(UiLength.Percent(0.5f))
                .WrapText(500f)),
            context.Scope);

        Drain(context.World);

        TextLayout layout = context.World.Components.Get<TextLayout>(live.RootEntity);
        Assert.IsTrue(layout.Size.Width <= 100f);
        Assert.AreEqual(100f, context.World.Components.Get<LayoutRect>(live.RootEntity).Value.Width, 0.001f);
    }

    [TestMethod]
    public void StarMax_RedistributesRemainingSpace()
    {
        (float first, float second) = MeasureTwoStarColumns(
            300f,
            UiGridTrack.Star(1f, min: 100f, max: 100f),
            UiGridTrack.Star());
        Assert.AreEqual(100f, first, 0.001f);
        Assert.AreEqual(200f, second, 0.001f);
    }

    [TestMethod]
    public void StarMin_RedistributesDeficit()
    {
        (float first, float second) = MeasureTwoStarColumns(
            300f,
            UiGridTrack.Star(1f, min: 200f),
            UiGridTrack.Star());
        Assert.AreEqual(200f, first, 0.001f);
        Assert.AreEqual(100f, second, 0.001f);
    }

    [TestMethod]
    public void MultipleStarMinMax_Converges()
    {
        TestContext context = Create(new UiSize(600f, 100f));
        UiGridDefinition definition = new(
            [
                UiGridTrack.Star(1f, max: 100f),
                UiGridTrack.Star(1f, min: 200f, max: 250f),
                UiGridTrack.Star(2f)
            ],
            [UiGridTrack.Star()]);
        BlueprintInstance live = context.Instantiator.Instantiate(
            Ui.Compile(Ui.Grid(
                definition,
                Ui.Container().GridCell(0, 0),
                Ui.Container().GridCell(0, 1),
                Ui.Container().GridCell(0, 2))),
            context.Scope);

        Drain(context.World);

        Assert.AreEqual(100f, context.World.Components.Get<LayoutRect>(live.EntityAt(1)).Value.Width, 0.001f);
        Assert.AreEqual(200f, context.World.Components.Get<LayoutRect>(live.EntityAt(2)).Value.Width, 0.001f);
        Assert.AreEqual(300f, context.World.Components.Get<LayoutRect>(live.EntityAt(3)).Value.Width, 0.001f);
    }

    [TestMethod]
    public void StarWeights_WithClampedTrack()
    {
        (float first, float second) = MeasureTwoStarColumns(
            500f,
            UiGridTrack.Star(1f, max: 100f),
            UiGridTrack.Star(3f));
        Assert.AreEqual(100f, first, 0.001f);
        Assert.AreEqual(400f, second, 0.001f);
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

    [TestMethod]
    public void AutoSizedBoundary_PropagatesWhenDesiredSizeChanges()
    {
        const int slice = 83;
        TestContext context = Create(new UiSize(400f, 100f));
        context.Store.Set(slice, "A");
        UiSelector<string> text = UiSelectors.String(903, slice, s => s.Get<string>(slice));
        BlueprintInstance live = context.Instantiator.Instantiate(
            Ui.Compile(Ui.Column(
                Ui.Container(Ui.Text().BindText(text).Class(UiClass.Body))
                    .LayoutBoundary())),
            context.Scope);
        Drain(context.World);
        float before = context.World.Components.Get<DesiredSize>(live.RootEntity).Value.Width;

        context.Store.Set(slice, "A much much longer boundary value");
        Assert.IsTrue(context.World.Update());
        float after = context.World.Components.Get<DesiredSize>(live.RootEntity).Value.Width;

        Assert.IsTrue(after > before);
        Assert.IsTrue(context.Runtime.Layout.LastMeasureCount > 2);
    }

    [TestMethod]
    public void FixedBoundary_StopsPropagationWhenDesiredSizeStable()
    {
        const int slice = 84;
        TestContext context = Create(new UiSize(400f, 100f));
        context.Store.Set(slice, "A");
        UiSelector<string> text = UiSelectors.String(904, slice, s => s.Get<string>(slice));
        BlueprintInstance live = context.Instantiator.Instantiate(
            Ui.Compile(Ui.Column(
                Ui.Container(Ui.Text().BindText(text).Class(UiClass.Body))
                    .Width(UiLength.Pixels(100f))
                    .Height(UiLength.Pixels(30f))
                    .LayoutBoundary())),
            context.Scope);
        Drain(context.World);
        UiSize before = context.World.Components.Get<DesiredSize>(live.RootEntity).Value;

        context.Store.Set(slice, "A much much longer fixed boundary value");
        Assert.IsTrue(context.World.Update());
        UiSize after = context.World.Components.Get<DesiredSize>(live.RootEntity).Value;

        Assert.AreEqual(before, after);
        Assert.AreEqual(2, context.Runtime.Layout.LastMeasureCount);
    }

    [TestMethod]
    public void TextCache_EvictsLeastRecentlyUsedUnusedEntries()
    {
        TestContext context = Create(new UiSize(200f, 100f), textCacheCapacity: 2);
        foreach (string value in new[] { "one", "two", "three" })
        {
            BlueprintInstance live = context.Instantiator.Instantiate(Ui.Compile(Ui.Text(value)), context.Scope);
            Drain(context.World);
            context.Instantiator.Destroy(live);
        }

        Assert.AreEqual(2, context.Runtime.TextCache.Count);
        Assert.AreEqual(2, context.TextEngine.LayoutCount);
        Assert.IsTrue(context.TextEngine.ReleaseCount >= 1);
    }

    [TestMethod]
    public void Phase3RuntimeDispose_ReleasesActiveTextHandles()
    {
        TestContext context = Create(new UiSize(200f, 100f));
        context.Instantiator.Instantiate(Ui.Compile(Ui.Text("active")), context.Scope);
        Drain(context.World);
        Assert.AreEqual(1, context.TextEngine.LayoutCount);

        context.Runtime.Dispose();

        Assert.AreEqual(0, context.TextEngine.LayoutCount);
        Assert.AreEqual(1, context.TextEngine.ReleaseCount);
    }

    [TestMethod]
    public void MinContent_IsExplicitlyUnsupportedUntilSolverExists()
    {
        TestContext context = Create(new UiSize(200f, 100f));
        context.Instantiator.Instantiate(
            Ui.Compile(Ui.Container().Width(new UiLength(UiLengthKind.MinContent, 0f))),
            context.Scope);

        Assert.ThrowsExactly<NotSupportedException>(() => context.World.Update());
    }

    private static (float First, float Second) MeasureTwoStarColumns(
        float width,
        UiGridTrack firstTrack,
        UiGridTrack secondTrack)
    {
        TestContext context = Create(new UiSize(width, 100f));
        UiGridDefinition definition = new([firstTrack, secondTrack], [UiGridTrack.Star()]);
        BlueprintInstance live = context.Instantiator.Instantiate(
            Ui.Compile(Ui.Grid(
                definition,
                Ui.Container().GridCell(0, 0),
                Ui.Container().GridCell(0, 1))),
            context.Scope);
        Drain(context.World);
        return (
            context.World.Components.Get<LayoutRect>(live.EntityAt(1)).Value.Width,
            context.World.Components.Get<LayoutRect>(live.EntityAt(2)).Value.Width);
    }

    private static TestContext Create(
        UiSize viewport,
        bool applyDefaults = true,
        int textCacheCapacity = 512)
    {
        UiWorld world = new(new DeterministicUiClock());
        DeterministicTextEngine text = new();
        UiPhase3Runtime runtime = new(world, text, viewport, applyDefaults, textCacheCapacity);
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

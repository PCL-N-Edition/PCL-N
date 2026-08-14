// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.UI.Next.Test;

[TestClass]
public sealed class NavigationTests
{
    private static readonly UiPageKey PageA = new("A");
    private static readonly UiPageKey PageB = new("B");
    private static readonly UiPageKey PageC = new("C");

    [TestMethod]
    public void Navigation_FollowsPreparingEnteringActiveStateFlow()
    {
        using TestContext context = Create(reducedMotion: false);
        context.Navigation.Register(Page(PageA, UiPageCachePolicy.None));
        UiNavigationRequest request = context.Navigation.Navigate(PageA);

        Assert.IsTrue(context.World.Update());
        Assert.IsTrue(context.Navigation.TryGetPage(PageA, out UiNavigationPageSnapshot preparing));
        Assert.AreEqual(UiNavigationPageState.Preparing, preparing.State);
        Assert.AreEqual(request.Generation, preparing.NavigationGeneration);
        Assert.AreEqual(0, context.Rendering.Accessibility.Tree.NodeCount);

        context.Clock.Advance(0.016d);
        Assert.IsTrue(context.World.Update());
        Assert.IsTrue(context.Navigation.TryGetPage(PageA, out UiNavigationPageSnapshot entering));
        Assert.AreEqual(UiNavigationPageState.Entering, entering.State);

        AdvanceUntilSettled(context);
        Assert.AreEqual(PageA, context.Navigation.CurrentPage);
        Assert.IsTrue(context.Navigation.TryGetPage(PageA, out UiNavigationPageSnapshot active));
        Assert.AreEqual(UiNavigationPageState.Active, active.State);
        Assert.AreEqual(2, context.Rendering.Accessibility.Tree.NodeCount);

        List<UiNavigationEvent> events = [];
        context.Navigation.Events.Drain(events);
        Assert.IsTrue(events.Any(item => item.Kind == UiNavigationEventKind.StateChanged &&
                                         item.State == UiNavigationPageState.Preparing));
        Assert.IsTrue(events.Any(item => item.Kind == UiNavigationEventKind.StateChanged &&
                                         item.State == UiNavigationPageState.Entering));
        Assert.IsTrue(events.Any(item => item.Kind == UiNavigationEventKind.Completed &&
                                         item.NavigationGeneration == request.Generation));
    }

    [TestMethod]
    public void KeepEntities_ReusesScopeWhileNoneDestroysOutgoingPage()
    {
        using TestContext context = Create(reducedMotion: true);
        context.Navigation.Register(Page(PageA, UiPageCachePolicy.None));
        context.Navigation.Register(Page(PageB, UiPageCachePolicy.KeepEntities));

        NavigateAndSettle(context, PageB);
        Assert.IsTrue(context.Navigation.TryGetPage(PageB, out UiNavigationPageSnapshot firstB));
        NavigateAndSettle(context, PageA);
        Assert.IsTrue(context.Navigation.TryGetPage(PageB, out UiNavigationPageSnapshot dormantB));
        Assert.AreEqual(UiNavigationPageState.Dormant, dormantB.State);
        Assert.AreEqual(firstB.Scope, dormantB.Scope);
        Assert.AreEqual(firstB.RootEntity, dormantB.RootEntity);
        Assert.AreEqual(2, context.Rendering.Accessibility.Tree.NodeCount);

        NavigateAndSettle(context, PageB);
        Assert.IsFalse(context.Navigation.TryGetPage(PageA, out _));
        Assert.IsTrue(context.Navigation.TryGetPage(PageB, out UiNavigationPageSnapshot secondB));
        Assert.AreEqual(firstB.Scope, secondB.Scope);
        Assert.AreEqual(firstB.RootEntity, secondB.RootEntity);
        Assert.AreEqual(UiNavigationPageState.Active, secondB.State);
    }

    [TestMethod]
    public void Lru_EvictsOldestDormantEntityPageWithinCapacity()
    {
        using TestContext context = Create(
            reducedMotion: true,
            options: UiNavigationOptions.Default with { LruCapacity = 1 });
        context.Navigation.Register(Page(PageA, UiPageCachePolicy.Lru));
        context.Navigation.Register(Page(PageB, UiPageCachePolicy.Lru));
        context.Navigation.Register(Page(PageC, UiPageCachePolicy.Lru));

        NavigateAndSettle(context, PageA);
        NavigateAndSettle(context, PageB);
        Assert.IsTrue(context.Navigation.TryGetPage(PageA, out UiNavigationPageSnapshot cachedA));
        Assert.AreEqual(UiNavigationPageState.Dormant, cachedA.State);

        NavigateAndSettle(context, PageC);

        Assert.IsFalse(context.Navigation.TryGetPage(PageA, out _));
        Assert.IsTrue(context.Navigation.TryGetPage(PageB, out UiNavigationPageSnapshot cachedB));
        Assert.AreEqual(UiNavigationPageState.Dormant, cachedB.State);
        Assert.AreEqual(PageC, context.Navigation.CurrentPage);
        Assert.AreEqual(2, context.Navigation.LivePageCount);
        List<UiNavigationEvent> events = [];
        context.Navigation.Events.Drain(events);
        Assert.IsTrue(events.Any(item => item.Kind == UiNavigationEventKind.CacheEvicted && item.Page == PageA));
    }

    [TestMethod]
    public void NavigateDuringTransition_IgnoresOldGenerationCompletion()
    {
        using TestContext context = Create(reducedMotion: false);
        context.Navigation.Register(Page(PageA, UiPageCachePolicy.None));
        context.Navigation.Register(Page(PageB, UiPageCachePolicy.None));
        context.Navigation.Register(Page(PageC, UiPageCachePolicy.None));
        NavigateAndSettle(context, PageA);
        context.Navigation.Events.Drain([]);

        UiNavigationRequest toB = context.Navigation.Navigate(PageB);
        Assert.IsTrue(context.World.Update());
        context.Clock.Advance(0.016d);
        Assert.IsTrue(context.World.Update());
        Assert.IsTrue(context.Navigation.TryGetPage(PageB, out UiNavigationPageSnapshot enteringB));
        Assert.AreEqual(UiNavigationPageState.Entering, enteringB.State);

        context.Clock.Advance(0.05d);
        Assert.IsTrue(context.World.Update());
        UiNavigationRequest toC = context.Navigation.Navigate(PageC);
        Assert.IsGreaterThan(toB.Generation, toC.Generation);
        Assert.IsTrue(context.World.Update());
        context.Clock.Advance(0.016d);
        Assert.IsTrue(context.World.Update());
        AdvanceUntilSettled(context);

        Assert.AreEqual(PageC, context.Navigation.CurrentPage);
        Assert.IsFalse(context.Navigation.TryGetPage(PageA, out _));
        Assert.IsFalse(context.Navigation.TryGetPage(PageB, out _));
        Assert.IsTrue(context.Navigation.TryGetPage(PageC, out UiNavigationPageSnapshot activeC));
        Assert.AreEqual(UiNavigationPageState.Active, activeC.State);
        List<UiNavigationEvent> events = [];
        context.Navigation.Events.Drain(events);
        Assert.IsFalse(events.Any(item => item.Kind == UiNavigationEventKind.Completed &&
                                          item.NavigationGeneration == toB.Generation));
        Assert.IsTrue(events.Any(item => item.Kind == UiNavigationEventKind.Completed &&
                                         item.NavigationGeneration == toC.Generation));
    }

    [TestMethod]
    public void HostScopeDisposal_DestroysAllPageScopesAndRuntimeRoot()
    {
        using TestContext context = Create(reducedMotion: true);
        context.Navigation.Register(Page(PageA, UiPageCachePolicy.Pinned));
        NavigateAndSettle(context, PageA);
        UiEntity navigationRoot = context.Navigation.NavigationRoot;
        Assert.IsTrue(context.Navigation.TryGetPage(PageA, out UiNavigationPageSnapshot page));

        Assert.IsTrue(context.World.DisposeScope(context.WindowScope));

        Assert.IsFalse(context.World.Entities.IsAlive(navigationRoot));
        Assert.IsFalse(context.World.Scopes.IsAlive(page.Scope));
        Assert.AreEqual(0, context.Navigation.LivePageCount);
    }

    [TestMethod]
    public void RepeatedNavigation_DoesNotGrowAnimationJournalUnbounded()
    {
        using TestContext context = Create(reducedMotion: true);
        context.Navigation.Register(Page(PageA, UiPageCachePolicy.KeepEntities));
        context.Navigation.Register(Page(PageB, UiPageCachePolicy.KeepEntities));
        UiAnimationEventReader slowReader = context.Runtime.Animation.Events.CreateReader(
            UiAnimationEventReaderStart.NextPublished);

        for (int i = 0; i < 240; i++)
            NavigateAndSettle(context, (i & 1) == 0 ? PageA : PageB);

        Assert.AreEqual(
            context.Runtime.Animation.Events.Capacity,
            context.Runtime.Animation.Events.RetainedCount);
        Assert.IsLessThanOrEqualTo(
            context.Runtime.Animation.Events.Capacity,
            context.Runtime.Animation.Events.Count);
        Assert.IsTrue(slowReader.TryRead(out _));
        Assert.IsGreaterThan(0L, slowReader.DroppedCount);
    }

    [TestMethod]
    public void NavigationEventJournal_IsBoundedAndReadersReportDrops()
    {
        using TestContext context = Create(reducedMotion: true);
        context.Navigation.Register(Page(PageA, UiPageCachePolicy.KeepEntities));
        UiNavigationEventReader reader = context.Navigation.Events.CreateReader(
            UiNavigationEventReaderStart.NextPublished);
        const int overflow = 64;

        for (int i = 0; i < UiNavigationEventJournal.DefaultCapacity + overflow; i++)
            context.Navigation.Navigate(PageA);

        List<UiNavigationEvent> events = [];
        reader.Drain(events);
        Assert.AreEqual(UiNavigationEventJournal.DefaultCapacity, events.Count);
        Assert.AreEqual(overflow, reader.DroppedCount);
        Assert.AreEqual(context.Navigation.Events.Capacity, context.Navigation.Events.RetainedCount);
    }

    [TestMethod]
    public void InternalTransitionConsumer_DoesNotStealPublicAnimationEvents()
    {
        using TestContext context = Create(reducedMotion: true);
        context.Navigation.Register(Page(PageA, UiPageCachePolicy.None));
        UiAnimationEventReader publicReader = context.Runtime.Animation.Events.CreateReader(
            UiAnimationEventReaderStart.NextPublished);

        NavigateAndSettle(context, PageA);

        List<UiAnimationEvent> publicEvents = [];
        publicReader.Drain(publicEvents);
        List<UiAnimationEvent> compatibilityEvents = [];
        context.Runtime.Animation.Events.Drain(compatibilityEvents);
        Assert.AreEqual(UiNavigationPageState.Active, GetPage(context, PageA).State);
        Assert.IsTrue(publicEvents.Any(static item =>
            item.Kind == UiAnimationEventKind.TransitionGroupCompleted));
        Assert.IsTrue(compatibilityEvents.Any(static item =>
            item.Kind == UiAnimationEventKind.TransitionGroupCompleted));
        Assert.IsTrue(publicEvents.Select(static item => item.Sequence)
            .Intersect(compatibilityEvents.Select(static item => item.Sequence))
            .Any());
    }

    [TestMethod]
    public void NavigationCompletion_SurvivesJournalOverflowSameFrame()
    {
        using TestContext context = Create(reducedMotion: true);
        context.Navigation.Register(Page(PageA, UiPageCachePolicy.None));
        UiEntity noise = context.World.CreateEntity(context.WindowScope);
        UiAnimationEventReader publicReader = context.Runtime.Animation.Events.CreateReader(
            UiAnimationEventReaderStart.NextPublished);
        context.Navigation.Navigate(PageA);
        Assert.IsTrue(context.World.Update());
        context.World.Systems.Register(new OneShotSystem(
            UiSystemPhase.TransitionPlanning,
            _ =>
            {
                UiAnimationSpec spec = new(UiMotion.FastFade);
                for (int i = 0; i < 1_600; i++)
                {
                    context.Runtime.Animation.Retarget(
                        noise,
                        UiAnimationProperty.CornerRadius,
                        (i & 1) == 0 ? 1f : 0f,
                        in spec);
                }
            }));

        context.Clock.Advance(0.016d);
        Assert.IsTrue(context.World.Update());
        List<UiAnimationEvent> retained = [];
        publicReader.Drain(retained);
        Assert.IsGreaterThan(0L, publicReader.DroppedCount);
        Assert.IsFalse(retained.Any(static item =>
            item.Kind == UiAnimationEventKind.TransitionGroupCompleted));

        Assert.IsTrue(context.World.Update());
        Assert.AreEqual(UiNavigationPageState.Active, GetPage(context, PageA).State);
        Assert.AreEqual(PageA, context.Navigation.CurrentPage);
    }

    [TestMethod]
    public void DormantPage_FocusedDescendantLosesFocus()
    {
        using TestContext context = Create(reducedMotion: true);
        context.Navigation.Register(new UiPageDefinition(
            PageA,
            Ui.Compile(Ui.Column(Ui.Button("Focused descendant"))),
            UiPageCachePolicy.KeepEntities));
        context.Navigation.Register(Page(PageB, UiPageCachePolicy.KeepEntities));
        NavigateAndSettle(context, PageA);
        UiNavigationPageSnapshot pageA = GetPage(context, PageA);
        Assert.IsTrue(context.World.Hierarchy.TryGetNode(pageA.RootEntity, out HierarchyNode root));
        UiEntity descendant = root.FirstChild;
        Assert.IsTrue(context.Runtime.Input.Focus.Focus(descendant, context.Clock.Now));
        Assert.AreEqual(descendant, context.Runtime.Input.Focus.GetFocused(context.InputRoot));

        NavigateAndSettle(context, PageB);

        Assert.AreEqual(UiNavigationPageState.Dormant, GetPage(context, PageA).State);
        Assert.AreNotEqual(descendant, context.Runtime.Input.Focus.GetFocused(context.InputRoot));
        Assert.IsFalse(UiEffectiveState.IsVisible(context.World, descendant));
        Assert.IsFalse(UiEffectiveState.IsEnabled(context.World, descendant));
    }

    private static UiPageDefinition Page(UiPageKey key, UiPageCachePolicy policy) =>
        new(
            key,
            Ui.Compile(
                Ui.Button(key.Value!)
                    .Width(UiLength.Pixels(160))
                    .Height(UiLength.Pixels(60))),
            policy);

    private static UiNavigationPageSnapshot GetPage(TestContext context, UiPageKey page)
    {
        Assert.IsTrue(context.Navigation.TryGetPage(page, out UiNavigationPageSnapshot snapshot));
        return snapshot;
    }

    private static void NavigateAndSettle(TestContext context, UiPageKey page)
    {
        context.Navigation.Navigate(page);
        AdvanceUntilSettled(context);
        Assert.AreEqual(page, context.Navigation.CurrentPage);
    }

    private static void AdvanceUntilSettled(TestContext context)
    {
        int guard = 0;
        while (context.World.Scheduler.NeedsFrame && guard++ < 240)
        {
            context.Clock.Advance(0.025d);
            Assert.IsTrue(context.World.Update());
        }
        Assert.IsFalse(context.World.Scheduler.NeedsFrame, "Navigation did not settle to idle.");
    }

    private static TestContext Create(
        bool reducedMotion,
        UiNavigationOptions? options = null)
    {
        DeterministicUiClock clock = new();
        UiWorld world = new(clock);
        UiSize viewport = new(320, 180);
        UiInteractiveRuntime runtime = new(world, new DeterministicTextEngine(), viewport);
        if (reducedMotion)
            runtime.Animation.SetReducedMotion(true);
        UiScopeId applicationScope = world.CreateRootScope();
        UiScopeId windowScope = world.CreateScope(applicationScope);
        UiInputRootId inputRoot = runtime.Input.InputRoots.Register(windowScope);
        BlueprintInstantiator instantiator = new(world, new PresentationStore());
        HeadlessUiBackend backend = new();
        UiRenderingRuntime rendering = new(
            world,
            backend,
            runtime.TextCache,
            windowScope,
            viewport,
            input: runtime.Input);
        UiNavigationRuntime navigation = new(
            world,
            runtime,
            instantiator,
            windowScope,
            options: options);
        return new TestContext(
            clock,
            world,
            runtime,
            rendering,
            navigation,
            windowScope,
            inputRoot);
    }

    private sealed record TestContext(
        DeterministicUiClock Clock,
        UiWorld World,
        UiInteractiveRuntime Runtime,
        UiRenderingRuntime Rendering,
        UiNavigationRuntime Navigation,
        UiScopeId WindowScope,
        UiInputRootId InputRoot) : IDisposable
    {
        public void Dispose()
        {
            Navigation.Dispose();
            Rendering.Dispose();
            Runtime.Dispose();
        }
    }

    private sealed class OneShotSystem(UiSystemPhase phase, Action<UiWorld> action) : IUiSystem
    {
        private bool _ran;

        public UiSystemPhase Phase { get; } = phase;
        public string Name => "test.navigation-one-shot";

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

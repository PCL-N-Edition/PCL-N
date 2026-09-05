using PCL.Xsr;

namespace PCL.UI.Next.Tests;

internal static partial class Program
{
    private static readonly (string Name, Func<ValueTask> Body)[] TestCases =
    [
        // XSR-201: ECS kernel.
        ("entities create and destroy with recycled handles", Sync(EntityCreateDestroyRecyclesHandles)),
        ("attach preserves deterministic child order", Sync(AttachPreservesDeterministicChildOrder)),
        ("attach rejects cycles and self attachment", Sync(AttachRejectsCycles)),
        ("destroy releases the whole subtree", Sync(DestroyReleasesWholeSubtree)),
        ("components set get and remove", Sync(ComponentsSetGetAndRemove)),
        ("component changes mark structure dirty", Sync(ComponentChangesMarkStructureDirty)),
        ("dirty flags bubble to ancestors and clear precisely", Sync(DirtyFlagsBubbleAndClear)),
        ("dirty enumeration is ascending and deterministic", Sync(DirtyEnumerationIsDeterministic)),
        ("state bridge marks only bound entities dirty", Sync(StateBridgeMarksOnlyBoundEntities)),
        ("destroyed entities drop state dependencies", Sync(DestroyedEntitiesDropStateDependencies)),
        ("entities carry multiple state bindings", Sync(EntitiesCarryMultipleStateBindings)),
        ("walk visits depth first in order", Sync(WalkVisitsDepthFirstInOrder)),
        // XSR-202: layout and scene production.
        ("fixed leaf produces exact rect", Sync(FixedLeafProducesExactRect)),
        ("vertical stack flows top down", Sync(VerticalStackFlowsTopDown)),
        ("horizontal stack flows left right", Sync(HorizontalStackFlowsLeftRight)),
        ("padding insets and margin offsets", Sync(PaddingInsetsAndMarginOffsets)),
        ("cross alignment positions children", Sync(CrossAxisAlignmentPositionsChildren)),
        ("weighted stack distributes star slots and honors limits", Sync(WeightedStackDistributesStarSlotsAndHonorsLimits)),
        ("weighted stack fills remaining vertical space", Sync(WeightedStackFillsRemainingVerticalSpace)),
        ("maximum size and end alignment constrain paint rect", Sync(MaximumSizeAndEndAlignmentConstrainPaintRect)),
        ("stack measurement includes child margins", Sync(StackMeasurementIncludesChildMargins)),
        ("explicit width constrains wrapped intrinsic height", Sync(ExplicitWidthConstrainsWrappedIntrinsicHeight)),
        ("invisible entities leave scene and layout", Sync(InvisibleEntitiesLeaveSceneAndLayout)),
        ("hidden list mutations invalidate cached layout before reopening", Sync(HiddenListMutationsInvalidateLayout)),
        ("clean tree returns same scene", Sync(CleanTreeReturnsSameScene)),
        ("dirty leaf relayouts only its subtree", Sync(DirtyLeafRelayoutsOnlyItsSubtree)),
        ("sibling rects follow slot changes", Sync(SiblingRectsFollowSlotChanges)),
        ("shrink keeps siblings correct too", Sync(ShrinkKeepsSiblingsCorrectToo)),
        ("state bound text renders applied value", Sync(StateBoundTextRendersAppliedValue)),
        ("state bound visibility updates scene and layout", Sync(StateBoundVisibilityUpdatesSceneAndLayout)),
        ("scene order is depth first pre order", Sync(SceneOrderIsDepthFirstPreOrder)),
        ("render without root throws", Sync(RenderWithoutRootThrows)),
        ("viewport change relayouts", Sync(ViewportChangeRelayouts)),
        // XSR-203: input, focus, navigation, overlay, accessibility.
        ("hit test returns top most entity", Sync(HitTestReturnsTopMostEntity)),
        ("pointer activation emits command intent", Sync(PointerActivationEmitsCommandIntent)),
        ("pointer press on non clickable is not handled", Sync(PointerPressOnNonClickableIsNotHandled)),
        ("pointer release outside does not activate", Sync(PointerReleaseOutsideDoesNotActivate)),
        ("pointer move tracks hover", Sync(PointerMoveTracksHover)),
        ("pointer leaving input requests repaint", Sync(PointerLeavingInputRequestsRepaint)),
        ("pointer exited clears hover immediately", Sync(PointerExitedClearsHoverImmediately)),
        ("pointer cursor follows enabled interactive hit targets", Sync(PointerCursorFollowsInteractiveTargets)),
        ("focus cycles through focusable entities", Sync(FocusCyclesThroughFocusableEntities)),
        ("keyboard activation emits intent", Sync(KeyboardActivationEmitsIntent)),
        ("focused entity is visible in the scene", Sync(FocusedEntityIsVisibleInTheScene)),
        ("navigator push pop and replace swap pages", Sync(NavigatorPushPopAndReplaceSwapPages)),
        ("navigator rejects unknown pages", Sync(NavigatorRejectsUnknownPages)),
        ("stage overlays draw above page", Sync(StageOverlaysDrawAbovePage)),
        ("stage overlays stay out of stack flow and carry motion anchors", Sync(StageOverlaysStayOutOfStackFlow)),
        ("non-modal overlay whitespace passes pointer input to the page", Sync(NonModalOverlayWhitespacePassesPointerInput)),
        ("closing notifications leave stack flow immediately", Sync(ClosingNotificationLeavesStackFlowImmediately)),
        ("modal overlays isolate page input and escape emits their dismiss route", Sync(ModalOverlaysIsolatePageAndRouteEscape)),
        ("stage dismiss removes top overlay", Sync(StageDismissRemovesTopOverlay)),
        ("stage navigation swaps page content", Sync(StageNavigationSwapsPageContent)),
        ("reduced motion is a presentation contract flag", Sync(ReducedMotionIsAPresentationContractFlag)),
        ("derived state drives bound text", Sync(DerivedStateDrivesBoundText)),
        ("coalesced state becomes visible without manual flush", Sync(CoalescedStateBecomesVisibleWithoutManualFlush)),
        ("animator advances and completes", Sync(AnimatorAdvancesAndCompletes)),
        ("reduced motion completes animations immediately", Sync(ReducedMotionCompletesAnimationsImmediately)),
        // XSR-206: renderer kernel completion.
        ("easing curves are deterministic", Sync(EasingCurvesAreDeterministic)),
        ("animator applies easing and keyframes", Sync(AnimatorAppliesEasingAndKeyframes)),
        ("keyframes hold boundary values", Sync(KeyframesHoldBoundaryValues)),
        ("scroll offsets children and clamps", Sync(ScrollOffsetsChildrenAndClamps)),
        ("scroll offsets remain continuous and expose indicator facts", Sync(ContinuousScrollExposesIndicatorFacts)),
        ("scroll hit test follows offset", Sync(ScrollHitTestFollowsOffset)),
        ("image source carries to the scene", Sync(ImageSourceCarriesToTheScene)),
        // XSR-701: product shell foundation.
        ("experimental shell preserves semantic chrome", Sync(ExperimentalShellPreservesSemanticChrome)),
        ("shell navigation selection updates scene and intent", Sync(ShellNavigationSelectionUpdatesSceneAndIntent)),
        ("shell rejects unknown navigation selection", Sync(ShellRejectsUnknownNavigationSelection)),
        ("shell runtime context drains host publications", Sync(ShellRuntimeContextDrainsHostPublications)),
        ("removed shell style cannot change presentation", Sync(RemovedShellStyleCannotChangePresentation)),
        // XSR-703: product base plate.
        ("shell rail toggle expands and collapses", Sync(ShellRailToggleExpandsAndCollapses)),
        ("shell rail toggle intent expands through the renderer", Sync(ShellRailToggleIntentExpandsThroughRenderer)),
        ("rail presentation matches hit test during motion", Sync(RailPresentationMatchesHitTestDuringMotion)),
        ("reduced motion skips rail presentation motion", Sync(ReducedMotionSkipsRailPresentationMotion)),
        ("pager clips pages and excludes inactive input and accessibility", Sync(PagerClipsPagesAndExcludesInactiveInput)),
        ("pager supports wheel keyboard drag cancellation and reduced motion", Sync(PagerSupportsAllInputPaths)),
    ];

    private static async Task<int> Main()
    {
        foreach ((string name, Func<ValueTask> body) in TestCases)
        {
            await body().ConfigureAwait(false);
            Console.WriteLine($"PASS: {name}");
        }

        Console.WriteLine($"UI.Next renderer tests passed: {TestCases.Length}.");
        return 0;
    }

    private static Func<ValueTask> Sync(Action action) => () =>
    {
        action();
        return ValueTask.CompletedTask;
    };

    private static void AssertTrue(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException("Expected true but received false.");
        }
    }

    private static void AssertFalse(bool value) => AssertTrue(!value);

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}' but received '{actual}'.");
        }
    }

    private static void AssertClose(double expected, double actual)
    {
        if (Math.Abs(expected - actual) > 1e-9)
        {
            throw new InvalidOperationException(
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"Expected {expected} but received {actual}."));
        }
    }

    private static void AssertSequence<T>(T[] expected, T[] actual)
        where T : IEquatable<T>
    {
        if (expected.Length != actual.Length
            || !expected.Zip(actual, (left, right) => left.Equals(right)).All(equal => equal))
        {
            throw new InvalidOperationException(
                $"Expected sequence [{string.Join(", ", expected)}] but received [{string.Join(", ", actual)}].");
        }
    }

    private static void AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected exception {typeof(TException).Name}.");
    }

    private static XsrSemanticId AsXsrId(this string value) => XsrSemanticId.Parse(value);
}

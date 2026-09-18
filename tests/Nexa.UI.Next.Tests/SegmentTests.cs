using Nexa.Xsr;
using Nexa.Xsr.State;
namespace Nexa.UI.Next.Tests;

internal static partial class Program
{
    private sealed class SegmentSink(XsrUiSegmentedTrack track) : IXsrUiIntentSink
    {
        public int Count { get; private set; }
        public void Emit(XsrSemanticId command, XsrUiEntityId source, XsrCorrelationId correlationId) { track.Selected = source; Count++; }
    }
    private static void SegmentedTrackSupportsDragSnapKeyboardAndCancellation()
    {
        XsrUiTree tree = new(); XsrUiEntityId root = tree.Create("track"), thumb = tree.Create("thumb");
        tree.SetComponent(root, new XsrUiElement { Width = 300, Height = 40 });
        tree.SetComponent(root, new XsrUiStackPanel(XsrUiOrientation.Horizontal));
        tree.SetComponent(thumb, new XsrUiElement { IsVisible = false });
        tree.SetComponent(thumb, new XsrUiTransition()); tree.Attach(thumb, root);
        XsrUiSegmentedTrack track = new(thumb); tree.SetComponent(root, track);
        List<XsrUiEntityId> segments = [];
        for (int i = 0; i < 3; i++)
        {
            XsrUiEntityId segment = tree.Create($"segment-{i}"); tree.Attach(segment, root);
            tree.SetComponent(segment, new XsrUiElement { Width = 100, Height = 40 });
            tree.SetComponent(segment, new XsrUiInput { Clickable = true, Focusable = true });
            tree.SetComponent(segment, new XsrUiCommandBinding(XsrSemanticId.Parse("test.segment")));
            tree.SetComponent(segment, new XsrUiSegmentReveal(100)); segments.Add(segment);
        }
        track.Selected = segments[0]; SegmentSink sink = new(track);
        XsrUiRenderer renderer = new(tree, new XsrStateStoreBuilder().Build(), sink); renderer.SetRoot(root);
        renderer.Render();
        AssertTrue(renderer.PointerPressed(new(50, 20)));
        AssertTrue(renderer.PointerMoved(new(180, 20)));
        XsrUiScene scene = renderer.Render();
        AssertEqual(segments[1], track.Selected);
        AssertClose(130, scene.Nodes.Single(n => n.Entity == thumb).Rect.X);
        AssertFalse(scene.Nodes.Single(n => n.Entity == thumb).IsAccessible);
        AssertTrue(renderer.PointerReleased(new(180, 20)));
        renderer.Render();
        AssertClose(30, renderer.GetTransitionOffset(thumb));
        renderer.SetTransitionOffset(thumb, 0);
        scene = renderer.Render(); AssertClose(100, scene.Nodes.Single(n => n.Entity == thumb).Rect.X);
        AssertTrue(renderer.PointerPressed(new(140, 20)));
        AssertTrue(renderer.PointerMoved(new(270, 20))); renderer.Render();
        AssertTrue(renderer.CancelPointerGesture());
        AssertFalse(renderer.PointerReleased(new(270, 20)));
        renderer.ReducedMotion = true; renderer.Render();
        AssertClose(0, renderer.GetTransitionOffset(thumb));
        AssertTrue(renderer.Focus(segments[2])); AssertTrue(renderer.HandleKey(XsrUiKey.Left));
        renderer.Render(); AssertEqual(segments[1], track.Selected);
        renderer.SetSegmentExpanded(segments[2], false); renderer.Render();
        AssertFalse(renderer.Activate(segments[2]));
        AssertTrue(renderer.HandleKey(XsrUiKey.Right)); AssertEqual(segments[1], track.Selected);
        renderer.ReducedMotion = false;
        renderer.SetSegmentExpanded(segments[2], true); renderer.SetSegmentRevealProgress(segments[2], .4);
        scene = renderer.Render(); AssertClose(40, scene.Nodes.Single(n => n.Entity == segments[2]).Rect.Width);
        renderer.SetSegmentExpanded(segments[2], false);
        AssertClose(.4, renderer.GetSegmentRevealProgress(segments[2]));
        AssertFalse(renderer.Activate(segments[2]));
        renderer.SetSegmentRevealProgress(segments[2], 0); renderer.Render();
        AssertFalse(tree.GetComponent<XsrUiElement>(segments[2])!.IsVisible);
    }
    private sealed class GestureClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _ticks;
        public void Advance(int milliseconds) => _ticks += milliseconds;
    }
    private static void ListDragKeepsClicksSeparateAndPublishesInertia()
    {
        XsrUiTree tree = new(); XsrUiEntityId root = tree.Create("list");
        tree.SetComponent(root, new XsrUiElement { Width = 200, Height = 100 });
        tree.SetComponent(root, new XsrUiStackPanel(XsrUiOrientation.Vertical));
        tree.SetComponent(root, new XsrUiScroll { ShowsVerticalIndicator = true });
        tree.SetComponent(root, new XsrUiScrollGesture());
        tree.SetComponent(root, new XsrUiStableContent());
        for (int i = 0; i < 20; i++)
        {
            XsrUiEntityId row = tree.Create($"row-{i}"); tree.Attach(row, root);
            tree.SetComponent(row, new XsrUiElement { Height = 50 });
            tree.SetComponent(row, new XsrUiInput { Clickable = true, Focusable = true });
            tree.SetComponent(row, new XsrUiCommandBinding(XsrSemanticId.Parse("test.row")));
        }
        XsrUiIntentBuffer sink = new(); GestureClock clock = new();
        XsrUiRenderer renderer = new(tree, new XsrStateStoreBuilder().Build(), sink, timeProvider: clock);
        renderer.SetRoot(root); renderer.Render();
        AssertTrue(renderer.PointerPressed(new(50, 25))); clock.Advance(16);
        renderer.PointerMoved(new(50, 23)); AssertTrue(renderer.PointerReleased(new(50, 23)));
        AssertEqual(1, sink.Count);
        AssertTrue(renderer.PointerPressed(new(50, 80))); clock.Advance(40);
        AssertTrue(renderer.PointerMoved(new(50, 40))); renderer.Render();
        clock.Advance(40); AssertTrue(renderer.PointerMoved(new(50, 0))); renderer.Render();
        AssertTrue(renderer.PointerReleased(new(50, 0)));
        XsrUiScene scene = renderer.Render();
        AssertEqual(1, sink.Count); AssertClose(80, renderer.GetScrollPresentationOffset(root));
        AssertClose(1000, scene.Nodes.Single(n => n.Entity == root).ScrollMotion!.Value.Velocity);
        renderer.SetScrollPresentationOffset(root, 150); renderer.Render(); AssertClose(150, renderer.GetScrollPresentationOffset(root));
        renderer.PointerPressed(new(50, 50)); renderer.SetScrollPresentationOffset(root, 250);
        AssertClose(150, renderer.GetScrollPresentationOffset(root)); renderer.CancelPointerGesture();
        renderer.SetScrollPresentationOffset(root, 300); AssertClose(150, renderer.GetScrollPresentationOffset(root));
        renderer.ReducedMotion = true; renderer.PointerPressed(new(50, 80)); clock.Advance(40);
        renderer.PointerMoved(new(50, 20)); renderer.PointerReleased(new(50, 20)); scene = renderer.Render();
        AssertClose(0, scene.Nodes.Single(n => n.Entity == root).ScrollMotion!.Value.Velocity);
        AssertTrue(scene.Nodes.All(n => n.SuppressEntryAnimation));
        renderer.ReducedMotion = false; renderer.PointerPressed(new(50, 80)); clock.Advance(40);
        renderer.PointerMoved(new(50, 20)); renderer.PointerReleased(new(50, 20)); renderer.Render();
        renderer.SetScrollPresentationOffset(root, 10000); scene = renderer.Render();
        AssertClose(900, renderer.GetScrollPresentationOffset(root));
        renderer.FinishScrollInertia(root); scene = renderer.Render();
        AssertClose(0, scene.Nodes.Single(n => n.Entity == root).ScrollMotion!.Value.Velocity);
    }
}

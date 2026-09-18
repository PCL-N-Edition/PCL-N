namespace Nexa.UI.Next;

public sealed partial class XsrUiRenderer
{
    private readonly TimeProvider _gestureTime;
    private XsrUiEntityId _gestureScroll;
    private XsrUiPoint _scrollGrab;
    private double _scrollGrabOffset, _scrollLastOffset;
    private long _scrollLastTime;
    private bool _scrollCommitted;
    private readonly Queue<(long Time, double Offset)> _scrollSamples = new();

    public double GetScrollPresentationOffset(XsrUiEntityId entity) => _tree.GetComponent<XsrUiScroll>(entity)?.OffsetY ?? 0;
    public void SetScrollPresentationOffset(XsrUiEntityId entity, double offset)
    {
        if (!double.IsFinite(offset) || !_tree.IsAlive(entity) || !IsInVisibleTree(entity)
            || _tree.GetComponent<XsrUiScrollGesture>(entity) is not { Dragging: false, Velocity: not 0 }
            || _tree.GetComponent<XsrUiScroll>(entity) is not { } scroll || ReducedMotion) return;
        scroll.OffsetY = Math.Clamp(offset, 0, scroll.MaximumOffsetY);
        _tree.MarkDirty(entity, XsrUiDirtyKinds.Layout);
    }
    /// <summary>Ends released motion when its clock finishes or the viewport leaves the scene.</summary>
    public void FinishScrollInertia(XsrUiEntityId entity)
    {
        if (_tree.IsAlive(entity) && _tree.GetComponent<XsrUiScrollGesture>(entity) is { Dragging: false, Velocity: not 0 })
            StopScrollMotion(entity);
    }
    private void StopScrollMotion(XsrUiEntityId entity)
    {
        if (_tree.GetComponent<XsrUiScrollGesture>(entity) is not { } motion) return;
        motion.Velocity = 0; motion.Revision++;
        _tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
    }
    private bool BeginScrollGesture(XsrUiPoint point)
    {
        XsrUiEntityId entity = HitTest(point);
        while (entity.IsAssigned && _tree.IsAlive(entity))
        {
            if (_tree.GetComponent<XsrUiScrollGesture>(entity) is { } motion
                && _tree.GetComponent<XsrUiScroll>(entity) is { MaximumOffsetY: > 0 } scroll)
            {
                StopScrollMotion(entity);
                _gestureScroll = entity; _scrollGrab = point; _scrollGrabOffset = _scrollLastOffset = scroll.OffsetY;
                _scrollLastTime = _gestureTime.GetTimestamp(); _scrollCommitted = false;
                _scrollSamples.Clear(); _scrollSamples.Enqueue((_scrollLastTime, scroll.OffsetY));
                motion.Dragging = true;
                return true;
            }
            entity = _tree.Parent(entity);
        }
        return false;
    }
    private bool MoveScrollGesture(XsrUiPoint point)
    {
        if (!_gestureScroll.IsAssigned) return false;
        if (!_tree.IsAlive(_gestureScroll) || !IsInVisibleTree(_gestureScroll)) { AbandonScrollGesture(); return false; }
        double delta = _scrollGrab.Y - point.Y;
        if (!_scrollCommitted)
        {
            if (Math.Abs(delta) < 8 || Math.Abs(delta) <= Math.Abs(point.X - _scrollGrab.X)) return false;
            _scrollCommitted = true;
            ClearPointerPress();
            // A vertical list gesture wins over its horizontal pager ancestor.
            if (_gesturePager.IsAssigned && _tree.GetComponent<XsrUiPager>(_gesturePager) is { } pager)
            {
                pager.IsDragging = false; pager.ReleaseVelocity = 0;
                SetPagerTarget(_gesturePager, pager, pager.PageIndex);
            }
            _gesturePager = default; _pagerDragCommitted = false;
        }
        XsrUiScroll scroll = _tree.GetComponent<XsrUiScroll>(_gestureScroll)!;
        XsrUiScrollGesture motion = _tree.GetComponent<XsrUiScrollGesture>(_gestureScroll)!;
        scroll.OffsetY = Math.Clamp(_scrollGrabOffset + delta, 0, scroll.MaximumOffsetY);
        long now = _gestureTime.GetTimestamp();
        if ((scroll.OffsetY - _scrollLastOffset) * motion.Velocity < 0)
        {
            _scrollSamples.Clear(); _scrollSamples.Enqueue((_scrollLastTime, _scrollLastOffset));
        }
        while (_scrollSamples.Count > 1 && _gestureTime.GetElapsedTime(_scrollSamples.Peek().Time, now).TotalMilliseconds > 80) _scrollSamples.Dequeue();
        var first = _scrollSamples.Peek();
        double seconds = _gestureTime.GetElapsedTime(first.Time, now).TotalSeconds;
        if (seconds > .001) motion.Velocity = Math.Clamp((scroll.OffsetY - first.Offset) / seconds, -5000, 5000);
        _scrollSamples.Enqueue((now, scroll.OffsetY));
        _scrollLastTime = now; _scrollLastOffset = scroll.OffsetY;
        _tree.MarkDirty(_gestureScroll, XsrUiDirtyKinds.Layout);
        return true;
    }
    private bool EndScrollGesture(bool cancelled)
    {
        XsrUiEntityId entity = _gestureScroll; _gestureScroll = default;
        bool committed = _scrollCommitted; _scrollCommitted = false;
        if (!entity.IsAssigned || !_tree.IsAlive(entity)) return false;
        XsrUiScrollGesture motion = _tree.GetComponent<XsrUiScrollGesture>(entity)!;
        motion.Dragging = false;
        if (cancelled || !committed || ReducedMotion || _gestureTime.GetElapsedTime(_scrollLastTime).TotalMilliseconds > 120)
            motion.Velocity = 0;
        motion.Revision++;
        if (committed) ClearPointerPress();
        _tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
        return committed;
    }
    private void AbandonScrollGesture() => EndScrollGesture(cancelled: true);
}

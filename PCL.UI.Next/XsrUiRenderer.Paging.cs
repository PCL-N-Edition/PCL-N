using System.Diagnostics;

namespace PCL.UI.Next;

public sealed partial class XsrUiRenderer
{
    private XsrUiEntityId _gesturePager;
    private XsrUiPoint _pagerGrab;
    private double _pagerGrabPosition;
    private double _pagerLastPosition;
    private long _pagerLastTimestamp;
    private bool _pagerDragCommitted;
    private readonly Queue<(long Time, double Position)> _pagerSamples = new();

    /// <summary>Moves by one page in the supplied direction; endpoints do not wrap.</summary>
    public bool MovePager(XsrUiEntityId entity, int direction)
    {
        if (!entity.IsAssigned || !_tree.IsAlive(entity)
            || _tree.GetComponent<XsrUiPager>(entity) is not { } pager) return false;
        int index = Math.Clamp(pager.PageIndex + Math.Sign(direction), 0, Math.Max(0, pager.PageCount - 1));
        if (index == pager.PageIndex) return false;
        if (_gesturePager == entity)
        {
            _gesturePager = default;
            _pagerDragCommitted = false;
            ClearPointerPress();
        }
        pager.IsDragging = false;
        pager.ReleaseVelocity = 0;
        SetPagerTarget(entity, pager, index);
        return true;
    }

    /// <summary>Advances presentation from a backend clock; a live drag always owns its position.</summary>
    public void SetPagerPresentationPosition(XsrUiEntityId entity, double position)
    {
        if (!double.IsFinite(position) || !_tree.IsAlive(entity)
            || _tree.GetComponent<XsrUiPager>(entity) is not { IsDragging: false } pager) return;
        if (ReducedMotion) position = pager.PageIndex;
        if (pager.Position == position) return;
        pager.Position = position;
        _tree.MarkDirty(entity, XsrUiDirtyKinds.Layout);
    }

    /// <summary>Releases captured gesture state when the platform cancels pointer capture.</summary>
    public bool CancelPointerGesture()
    {
        bool handled = _gesturePager.IsAssigned || _pressed.IsAssigned;
        if (_gesturePager.IsAssigned && _tree.IsAlive(_gesturePager)
            && _tree.GetComponent<XsrUiPager>(_gesturePager) is { } pager)
        {
            pager.ReleaseVelocity = 0;
            pager.IsDragging = false;
            SetPagerTarget(_gesturePager, pager, pager.PageIndex);
        }
        _gesturePager = default;
        _pagerDragCommitted = false;
        ClearPointerPress();
        return handled;
    }

    private void SetPagerTarget(XsrUiEntityId entity, XsrUiPager pager, int index)
    {
        pager.PageIndex = index;
        pager.Revision++;
        if (ReducedMotion) pager.Position = index;
        if (_focused != entity && FindPager(_focused) == entity)
            _ = Focus(entity, showIndicator: false);
        _tree.MarkDirty(entity, XsrUiDirtyKinds.Layout);
    }

    private XsrUiEntityId FindPager(XsrUiEntityId entity)
    {
        while (entity.IsAssigned && _tree.IsAlive(entity))
        {
            if (_tree.GetComponent<XsrUiPager>(entity) is not null) return entity;
            entity = _tree.Parent(entity);
        }
        return default;
    }

    private bool BeginPagerGesture(XsrUiPoint point)
    {
        _gesturePager = FindPager(HitTest(point));
        if (!_gesturePager.IsAssigned) return false;
        XsrUiPager pager = _tree.GetComponent<XsrUiPager>(_gesturePager)!;
        _pagerGrab = point;
        _pagerGrabPosition = _pagerLastPosition = pager.Position;
        _pagerLastTimestamp = Stopwatch.GetTimestamp();
        _pagerSamples.Clear();
        _pagerSamples.Enqueue((_pagerLastTimestamp, pager.Position));
        _pagerDragCommitted = false;
        pager.IsDragging = true;
        pager.ReleaseVelocity = 0;
        pager.Revision++;
        _tree.MarkDirty(_gesturePager, XsrUiDirtyKinds.Paint);
        _ = Focus(_gesturePager, showIndicator: false);
        return true;
    }

    private bool MovePagerGesture(XsrUiPoint point)
    {
        if (!_gesturePager.IsAssigned || !_tree.IsAlive(_gesturePager)
            || _tree.GetComponent<XsrUiPager>(_gesturePager) is not { } pager
            || !_paintRects.TryGetValue(_gesturePager.Index, out XsrUiRect rect) || rect.Height <= 0)
            return false;
        double delta = _pagerGrab.Y - point.Y;
        if (!_pagerDragCommitted)
        {
            if (Math.Abs(delta) < 8 || Math.Abs(delta) < Math.Abs(point.X - _pagerGrab.X)) return false;
            _pagerDragCommitted = true;
            ClearPointerPress();
        }
        double position = _pagerGrabPosition + delta / rect.Height;
        double bound = Math.Clamp(position, 0, Math.Max(0, pager.PageCount - 1));
        double overflow = position - bound;
        pager.Position = bound + overflow * .55 / (1 + Math.Abs(overflow) * .55);
        long now = Stopwatch.GetTimestamp();
        // Use recent movement, not a single event interval. On a reversal, discard the old
        // direction so a quick change of mind does not fling the card the other way.
        double movement = pager.Position - _pagerLastPosition;
        if (movement * pager.ReleaseVelocity < 0)
        {
            _pagerSamples.Clear();
            _pagerSamples.Enqueue((_pagerLastTimestamp, _pagerLastPosition));
        }
        while (_pagerSamples.Count > 1 && Stopwatch.GetElapsedTime(_pagerSamples.Peek().Time, now).TotalMilliseconds > 80)
            _pagerSamples.Dequeue();
        (long startTime, double startPosition) = _pagerSamples.Peek();
        double seconds = Stopwatch.GetElapsedTime(startTime, now).TotalSeconds;
        if (seconds > .001)
            pager.ReleaseVelocity = Math.Clamp((pager.Position - startPosition) / seconds, -6, 6);
        _pagerSamples.Enqueue((now, pager.Position));
        _pagerLastPosition = pager.Position;
        _pagerLastTimestamp = now;
        _tree.MarkDirty(_gesturePager, XsrUiDirtyKinds.Layout);
        return true;
    }

    private bool EndPagerGesture()
    {
        XsrUiEntityId entity = _gesturePager;
        _gesturePager = default;
        if (!entity.IsAssigned || !_tree.IsAlive(entity)
            || _tree.GetComponent<XsrUiPager>(entity) is not { } pager) return false;
        bool dragged = _pagerDragCommitted;
        _pagerDragCommitted = false;
        pager.IsDragging = false;
        if (!dragged || Stopwatch.GetElapsedTime(_pagerLastTimestamp).TotalMilliseconds > 120)
            pager.ReleaseVelocity = 0;
        int target = pager.PageIndex;
        if (dragged)
        {
            // Exponential-decay projection (Apple's fluid-interface model). Page snapping is
            // bounded to a neighbouring card; a flick never skips several cards at once.
            const double decelerationRate = .998;
            double projected = pager.Position + pager.ReleaseVelocity / 1000 * decelerationRate / (1 - decelerationRate);
            target = Math.Clamp((int)Math.Round(projected, MidpointRounding.AwayFromZero),
                Math.Max(0, pager.PageIndex - 1), Math.Max(0, Math.Min(pager.PageCount - 1, pager.PageIndex + 1)));
            ClearPointerPress();
        }
        SetPagerTarget(entity, pager, target);
        return dragged;
    }

    private void ClearPointerPress()
    {
        if (_pressed.IsAssigned && _tree.IsAlive(_pressed)
            && _tree.GetComponent<XsrUiInput>(_pressed) is { } input)
        {
            input.IsPressed = false;
            _tree.MarkDirty(_pressed, XsrUiDirtyKinds.Paint);
        }
        _pressed = default;
    }
}

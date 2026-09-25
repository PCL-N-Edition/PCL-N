using Avalonia;

namespace Nexa.UI.Next.Backend.Avalonia;

// Window-local continuation of a click across navigation. No product or service knowledge.
internal sealed class PostNavigationDoubleClick
{
    private Point _position;
    private long _pressedAt;
    private bool _primary;
    private Action? _action;

    public void Arm(Action action) { if (_primary) _action = action; }
    public void Cancel() => _action = null;

    public Action? Press(Point position, long now, bool primary, TimeSpan interval, double width, double height)
    {
        Action? result = primary && _primary && now >= _pressedAt && now - _pressedAt <= interval.TotalMilliseconds
            && Math.Abs(position.X - _position.X) <= width && Math.Abs(position.Y - _position.Y) <= height ? _action : null;
        _action = null;
        _primary = primary && result is null;
        _position = position;
        _pressedAt = now;
        return result;
    }
}

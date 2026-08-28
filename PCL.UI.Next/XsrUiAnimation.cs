namespace PCL.UI.Next;

/// <summary>
/// One linear animation over an entity's paint state. The progress value is renderer-local
/// ephemeral state written by the animator; the scene node carries it so the backend can present
/// it. Easing curves, keyframes, and transitions are later kernel units.
/// </summary>
public sealed class XsrUiAnimation(TimeSpan duration)
{
    public TimeSpan Duration { get; set; } = duration;

    /// <summary>
    /// Gets the animation progress between 0 and 1. Written by the animator on the render thread.
    /// </summary>
    public double Progress { get; internal set; }

    public bool IsComplete => Progress >= 1;
}

/// <summary>
/// Drives animation progress on the render thread. Composition ticks once per frame; the
/// animator advances every active animation, writes the progress into the entity's component,
/// and marks the entity paint-dirty so the next scene carries the new value. Reduced motion
/// completes animations immediately instead of advancing them.
/// </summary>
public sealed class XsrUiAnimator
{
    private readonly XsrUiTree _tree;
    private readonly List<(XsrUiEntityId Entity, XsrUiAnimation Animation)> _active = [];

    public XsrUiAnimator(XsrUiTree tree)
    {
        _tree = tree ?? throw new ArgumentNullException(nameof(tree));
    }

    public int ActiveCount => _active.Count;

    /// <summary>
    /// Starts one animation on an entity carrying an animation component. Restarts reset progress.
    /// </summary>
    public void Start(XsrUiEntityId entity)
    {
        XsrUiAnimation? animation = _tree.GetComponent<XsrUiAnimation>(entity)
            ?? throw new InvalidOperationException($"The entity '{entity}' carries no animation component.");

        animation.Progress = 0;
        if (!_active.Any(entry => entry.Entity.Equals(entity)))
        {
            _active.Add((entity, animation));
        }

        _tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
    }

    /// <summary>
    /// Advances every active animation. Reduced motion completes them immediately.
    /// </summary>
    public void Tick(TimeSpan delta, bool reducedMotion)
    {
        for (int index = _active.Count - 1; index >= 0; index--)
        {
            (XsrUiEntityId entity, XsrUiAnimation animation) = _active[index];
            if (!_tree.IsAlive(entity))
            {
                _active.RemoveAt(index);
                continue;
            }

            if (reducedMotion || animation.Duration <= TimeSpan.Zero)
            {
                animation.Progress = 1;
            }
            else
            {
                animation.Progress = Math.Min(1, animation.Progress + delta.TotalSeconds / animation.Duration.TotalSeconds);
            }

            _tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
            if (animation.IsComplete)
            {
                _active.RemoveAt(index);
            }
        }
    }
}

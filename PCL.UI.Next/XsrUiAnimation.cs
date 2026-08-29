namespace PCL.UI.Next;

/// <summary>
/// Maps raw animation progress (0..1) to the presented fraction. Easing functions are pure and
/// deterministic; the animator applies them on the render thread.
/// </summary>
public delegate double XsrUiEasing(double progress);

/// <summary>
/// The built-in easing curves.
/// </summary>
public static class XsrUiEasings
{
    public static readonly XsrUiEasing Linear = static progress => progress;

    public static readonly XsrUiEasing EaseInQuad = static progress => progress * progress;

    public static readonly XsrUiEasing EaseOutQuad = static progress => 1 - ((1 - progress) * (1 - progress));

    public static readonly XsrUiEasing EaseInOutQuad = static progress => progress < 0.5
        ? 2 * progress * progress
        : 1 - ((2 - (2 * progress)) * (2 - (2 * progress)) / 2);
}

/// <summary>
/// One keyframe on an animation value track: at raw progress <see cref="Progress"/>, the value is
/// <see cref="Value"/>. Between keyframes the animator interpolates linearly over the eased
/// progress; outside the track the boundary values hold.
/// </summary>
public readonly record struct XsrUiKeyframe(double Progress, double Value);

/// <summary>
/// One animation over an entity's paint state. The progress value is renderer-local ephemeral
/// state written by the animator; the scene node carries it so the backend can present it.
/// </summary>
public sealed class XsrUiAnimation(TimeSpan duration)
{
    public TimeSpan Duration { get; set; } = duration;

    /// <summary>
    /// Gets or sets the easing applied to the raw progress before keyframe evaluation.
    /// </summary>
    public XsrUiEasing Easing { get; set; } = XsrUiEasings.Linear;

    /// <summary>
    /// Gets or sets the value track. Null means the animation has no value track and only the
    /// raw progress is reported.
    /// </summary>
    public IReadOnlyList<XsrUiKeyframe>? Keyframes { get; set; }

    /// <summary>
    /// Gets the animation progress between 0 and 1. Written by the animator on the render thread.
    /// </summary>
    public double Progress { get; internal set; }

    /// <summary>
    /// Gets the eased, keyframe-interpolated value. Written by the animator; without keyframes
    /// it equals the eased progress.
    /// </summary>
    public double Value { get; internal set; }

    public bool IsComplete => Progress >= 1;

    internal void Apply(double rawProgress)
    {
        Progress = rawProgress;
        double eased = Math.Clamp(Easing(rawProgress), 0, 1);
        Value = Keyframes is { Count: > 0 } track ? EvaluateTrack(track, eased) : eased;
    }

    private static double EvaluateTrack(IReadOnlyList<XsrUiKeyframe> track, double progress)
    {
        if (progress <= track[0].Progress)
        {
            return track[0].Value;
        }

        if (progress >= track[^1].Progress)
        {
            return track[^1].Value;
        }

        for (int index = 1; index < track.Count; index++)
        {
            XsrUiKeyframe end = track[index];
            if (progress <= end.Progress)
            {
                XsrUiKeyframe start = track[index - 1];
                double span = end.Progress - start.Progress;
                double fraction = span <= 0 ? 1 : (progress - start.Progress) / span;
                return start.Value + ((end.Value - start.Value) * fraction);
            }
        }

        return track[^1].Value;
    }
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

        animation.Apply(0);
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
                animation.Apply(1);
            }
            else
            {
                double raw = Math.Min(1, animation.Progress + (delta.TotalSeconds / animation.Duration.TotalSeconds));
                animation.Apply(raw);
            }

            _tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
            if (animation.IsComplete)
            {
                _active.RemoveAt(index);
            }
        }
    }
}

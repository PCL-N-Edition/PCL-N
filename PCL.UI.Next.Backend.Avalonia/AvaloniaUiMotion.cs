using System.Diagnostics;
using Avalonia.Threading;

namespace PCL.UI.Next.Backend.Avalonia;

/// <summary>
/// The shared frame clock behind backend presentation motion. Every animated value starts from
/// its currently presented value, so a new animation on the same value replaces a running one
/// continuously and a cancellation freezes the presented value — the fluid-interface
/// interruption contract. Values settle critically damped (cubic ease-out) unless a caller
/// asks for the mirrored ease-in collapse. The renderer-side animator cannot be used here
/// because native window affordances and transform properties are outside the UI.Next tree.
/// </summary>
internal static class AvaloniaUiMotion
{
    public static readonly Func<double, double> EaseOut = progress => 1 - Math.Pow(1 - progress, 3);

    public static readonly Func<double, double> EaseIn = progress => progress * progress * progress;

    private static readonly object Gate = new();
    private static readonly Dictionary<(object Owner, object Value), Track> Active = [];
    private static readonly Stopwatch Clock = new();
    private static DispatcherTimer? _timer;

    /// <summary>
    /// Animates one double value from its current presented value to the target. A repeat call
    /// for the same owner/value pair replaces the running animation; zero duration jumps
    /// straight to the target. The optional <paramref name="reducedMotion"/> policy is checked
    /// on every frame: the moment it turns true, the track writes its target and completes, so
    /// a live policy change settles running motion instead of letting it keep writing stale
    /// values over facts that were applied immediately.
    /// </summary>
    public static void Animate(
        object owner,
        object value,
        Func<double> read,
        Action<double> write,
        double target,
        double durationMilliseconds,
        Func<double, double>? easing = null,
        double delayMilliseconds = 0,
        Action? completed = null,
        Func<bool>? reducedMotion = null)
    {
        lock (Gate)
        {
            Active.Remove((owner, value));
            if (durationMilliseconds <= 0)
            {
                write(target);
                completed?.Invoke();
                return;
            }

            Active[(owner, value)] = new Track
            {
                Read = read,
                Write = write,
                Target = target,
                Easing = easing ?? EaseOut,
                StartMilliseconds = ElapsedMilliseconds() + delayMilliseconds,
                From = read(),
                DurationMilliseconds = durationMilliseconds,
                Completed = completed,
                ReducedMotion = reducedMotion,
            };
            EnsureClock();
        }
    }

    /// <summary>Stops one animated value, freezing it at the currently presented value.</summary>
    public static void Cancel(object owner, object value)
    {
        lock (Gate)
        {
            Active.Remove((owner, value));
        }
    }

    /// <summary>Stops every animated value owned by the given object.</summary>
    public static void CancelAll(object owner)
    {
        lock (Gate)
        {
            foreach ((object _, object value) in Active.Keys.Where(key => key.Owner == owner).ToArray())
            {
                Active.Remove((owner, value));
            }
        }
    }

    private sealed class Track
    {
        public required Func<double> Read { get; init; }

        public required Action<double> Write { get; init; }

        public required double Target { get; init; }

        public required Func<double, double> Easing { get; init; }

        public required double StartMilliseconds { get; init; }

        public required double From { get; init; }

        public required double DurationMilliseconds { get; init; }

        public Action? Completed { get; init; }

        public Func<bool>? ReducedMotion { get; init; }
    }

    private static double ElapsedMilliseconds()
    {
        if (!Clock.IsRunning)
        {
            Clock.Start();
        }

        return Clock.Elapsed.TotalMilliseconds;
    }

    private static void EnsureClock()
    {
        if (_timer is null)
        {
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(AvaloniaMotionTokens.FrameMilliseconds),
            };
            _timer.Tick += OnTick;
        }

        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    private static void OnTick(object? sender, EventArgs e)
    {
        List<(object Owner, object Value)> finished = [];
        lock (Gate)
        {
            double now = ElapsedMilliseconds();
            foreach (((object owner, object value), Track track) in Active)
            {
                // A live reduced-motion policy settles the track on the frame after the flag
                // flips: the final value is written once and the track completes, so no stale
                // animation can keep writing over facts that were applied immediately.
                if (track.ReducedMotion?.Invoke() == true)
                {
                    track.Write(track.Target);
                    finished.Add((owner, value));
                    continue;
                }

                double progress = Math.Clamp(
                    (now - track.StartMilliseconds) / track.DurationMilliseconds,
                    0,
                    1);
                track.Write(track.From + ((track.Target - track.From) * track.Easing(progress)));
                if (progress >= 1)
                {
                    finished.Add((owner, value));
                }
            }

            foreach ((object owner, object value) in finished)
            {
                Track track = Active[(owner, value)];
                Active.Remove((owner, value));
                track.Completed?.Invoke();
            }

            if (Active.Count == 0 && _timer is { } timer)
            {
                timer.Stop();
            }
        }
    }
}

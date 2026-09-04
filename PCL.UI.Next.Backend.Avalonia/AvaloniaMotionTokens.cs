namespace PCL.UI.Next.Backend.Avalonia;

/// <summary>
/// The single source of truth for product motion timing, classified per the oil-motion runtime
/// contract. Every animation is one of three controllers and must not grow page-local constants:
/// - <c>segment-play</c>: input selects a state, the motion then completes on its own and a
///   reverse input cancels the running play before retracing from the currently presented
///   value (hover, press, selection, rail expansion, launch card swaps);
/// - <c>scrub</c>: an input value maps continuously onto the presented state (the rail
///   presentation progress pumped by the shell);
/// - <c>autonomous</c>: the motion advances by time alone (startup reveal, close collapse,
///   page enter, icon bounce), with the contract's static state as the reduced-motion
///   fallback.
/// Fast repeated inputs keep only the newest target: every track is replaceable and
/// cancellable, and each track carries the live reduced-motion policy.
/// </summary>
internal static class AvaloniaMotionTokens
{
    /// <summary>
    /// The startup reveal: a smooth mask expands from the small circle behind the inherited
    /// splash icon out to the full window (legacy circular-reveal timing).
    /// </summary>
    public const int StartupRevealMilliseconds = 340;

    /// <summary>Close reverses the reveal: the window content collapses back to radius zero.</summary>
    public const int CloseCollapseMilliseconds = 280;

    /// <summary>The inherited icon bounces slightly upward before it folds away.</summary>
    public const int IconBounceMilliseconds = 110;

    /// <summary>
    /// Page enter (segment-play, autonomous completion): the entering page's children fade
    /// and rise into place with a per-child stagger, mirroring the legacy page enter.
    /// </summary>
    public const int PageEnterMilliseconds = 240;

    public const int PageEnterStaggerMilliseconds = 14;

    public const int PageEnterMaxChildren = 36;

    public const double PageEnterOffsetYPixels = 10;

    /// <summary>The final stage where the icon shrinks into (or out of) the content.</summary>
    public const int IconCollapseMilliseconds = 190;

    /// <summary>Press responds on pointer-down; the release settles without a bounce.</summary>
    public const double PressScale = 0.97;

    public const int PressMilliseconds = 120;

    /// <summary>Hover feedback is a fast symmetric mirrored fade.</summary>
    public const int HoverMilliseconds = 120;

    public const int HoverOutMilliseconds = 180;

    public const int SelectionInMilliseconds = 300;

    public const int SelectionOutMilliseconds = 120;

    public const int RailExpandMilliseconds = 200;

    /// <summary>The splash and hover fade clocks tick at display cadence.</summary>
    public const int FrameMilliseconds = 16;
}

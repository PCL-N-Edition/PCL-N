namespace PCL.UI.Next.Backend.Avalonia;

/// <summary>
/// The motion vocabulary for the product shell. Timings mirror the legacy experimental base
/// plate; the easing character follows fluid-interface guidance: click-driven changes settle
/// critically damped without overshoot, pointer feedback starts on pointer-down, and every
/// animated value starts from the current presented value so an interruption stays continuous.
/// Reduced motion replaces all of it with immediate value changes (the callers already render
/// the end state).
/// </summary>
internal static class AvaloniaMotionTokens
{
    /// <summary>
    /// The startup reveal: a smooth mask expands from the small circle behind the inherited
    /// splash icon out to the full window (legacy circular-reveal timing).
    /// </summary>
    public const int StartupRevealMilliseconds = 340;

    /// <summary>Close reverses the reveal: the window collapses back into the icon circle.</summary>
    public const int CloseCollapseMilliseconds = 280;

    /// <summary>Radius of the circle the reveal starts from, just behind the 112 px icon.</summary>
    public const double StartupRevealStartRadius = 68;

    /// <summary>The inherited icon bounces slightly upward before it folds away.</summary>
    public const int IconBounceMilliseconds = 110;

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

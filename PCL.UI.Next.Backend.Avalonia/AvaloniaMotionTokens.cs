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
    /// <summary>Linear splash fade-out after the shell window reports opened.</summary>
    public const int SplashFadeMilliseconds = 280;

    public const int WindowFadeMilliseconds = 250;

    /// <summary>The window rises into place; critically damped, no overshoot bounce.</summary>
    public const int WindowRiseMilliseconds = 600;

    public const int WindowRotateMilliseconds = 500;

    public const int WindowEntranceDelayMilliseconds = 100;

    public const double WindowEntranceRisePixels = 60;

    public const double WindowEntranceAngleDegrees = -4;

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

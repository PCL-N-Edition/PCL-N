namespace PCL.UI.Next;

/// <summary>Shared corner hierarchy for product surfaces.</summary>
public static class XsrUiCornerRadii
{
    public const double Surface = 16;
    public const double Inset = 12;
    public const double Compact = 8;

    public static double Pill(double height) => Math.Max(0, height) / 2;
}

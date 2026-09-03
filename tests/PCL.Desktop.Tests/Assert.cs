using PCL.UI.Next;
using PCL.Xsr.State;

namespace PCL.Desktop.Tests;

internal static partial class Program
{
    private static void AssertTrue(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException("Expected true but received false.");
        }
    }

    private static void AssertFalse(bool value) => AssertTrue(!value);

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}' but received '{actual}'.");
        }
    }

    private static void AssertRectClose(XsrUiRect expected, XsrUiRect actual)
    {
        AssertClose(expected.X, actual.X);
        AssertClose(expected.Y, actual.Y);
        AssertClose(expected.Width, actual.Width);
        AssertClose(expected.Height, actual.Height);
    }

    private static void AssertClose(double expected, double actual)
    {
        if (Math.Abs(expected - actual) > 1e-6)
        {
            throw new InvalidOperationException($"Expected '{expected}' but received '{actual}'.");
        }
    }
}

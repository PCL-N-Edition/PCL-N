using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.UI.Next;

/// <summary>Host facts only: no native object enters the sealed state store.</summary>
public static class XsrUiShellWindowState
{
    public static readonly XsrSemanticId TrafficLightPadding = XsrSemanticId.Parse("shell.window.traffic_light_padding");
    public static readonly XsrSemanticId Fullscreen = XsrSemanticId.Parse("shell.window.is_fullscreen");
    public static readonly XsrSemanticId ContentInset = XsrSemanticId.Parse("shell.window.content_inset");
    public static void Declare(XsrStateStoreBuilder builder)
    {
        builder.Cell<double>(TrafficLightPadding, "Nexa.UI.Next.Window");
        builder.Cell<double>(ContentInset, "Nexa.UI.Next.Window");
        builder.Cell<bool>(Fullscreen, "Nexa.UI.Next.Window");
    }
}

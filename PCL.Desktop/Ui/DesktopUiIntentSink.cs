using PCL.UI.Next;
using PCL.Xsr;

namespace PCL.Desktop.Ui;

/// <summary>
/// The Desktop composition-edge dispatcher for semantic UI intent. The shell itself owns local
/// selection; product vertical slices subscribe explicit typed bindings here without letting a
/// backend call services directly.
/// </summary>
internal sealed class DesktopUiIntentSink : IXsrUiIntentSink
{
    public event EventHandler<DesktopUiIntentEventArgs>? IntentEmitted;

    public void Emit(XsrSemanticId command, XsrUiEntityId source, XsrCorrelationId correlationId)
    {
        IntentEmitted?.Invoke(
            this,
            new DesktopUiIntentEventArgs(new DesktopUiIntent(command, source, correlationId)));
    }
}

/// <summary>One semantic intent that crossed the UI.Next-to-Desktop composition boundary.</summary>
internal readonly record struct DesktopUiIntent(
    XsrSemanticId Command,
    XsrUiEntityId Source,
    XsrCorrelationId CorrelationId);

/// <summary>Event data for one semantic UI intent at the Desktop composition edge.</summary>
internal sealed class DesktopUiIntentEventArgs(DesktopUiIntent intent) : EventArgs
{
    public DesktopUiIntent Intent { get; } = intent;
}

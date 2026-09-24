using Nexa.UI.Next;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Ui;

/// <summary>One compact bottom-up dock for shell bubbles. Exiting items release their slot immediately.</summary>
internal static class DesktopBubbleLayout
{
    private sealed class Slot(int order)
    {
        public int Order { get; } = order;
        public DateTimeOffset? ExitAt { get; set; }
        public bool DestroyOnExit { get; set; }
        public ITimer? Timer { get; set; }
    }

    public static XsrUiVisualStyle Style() => new()
    {
        Surface = XsrUiSurfaceKind.Solid,
        Background = DesktopUiPalette.CapsuleBackground,
        Foreground = DesktopUiPalette.CapsuleForeground,
        Hover = DesktopUiPalette.CapsuleHover,
        CornerRadius = 24,
        HoverExpand = true,
        FontSize = 13,
    };

    public static void Register(XsrUiShell shell, XsrUiEntityId entity, int order)
    {
        shell.Tree.SetComponent(entity, new Slot(order));
        shell.Tree.SetComponent(entity, new XsrUiOverlayMotion(XsrUiOverlayMotionKind.Notification));
    }

    public static void SetVisible(XsrUiShell shell, XsrStateStore store, XsrUiEntityId entity, bool visible, bool destroy = false)
    {
        var element = shell.Tree.GetComponent<XsrUiElement>(entity)!;
        var slot = shell.Tree.GetComponent<Slot>(entity)!;
        var motion = shell.Tree.GetComponent<XsrUiOverlayMotion>(entity)!;
        if (visible == element.IsVisible && !motion.IsClosing) return;
        if (!visible && motion.IsClosing) return;
        if (visible)
        {
            slot.Timer?.Dispose(); slot.Timer = null; slot.ExitAt = null;
            motion.IsClosing = false;
            element.IsVisible = true;
            shell.Tree.GetComponent<XsrUiInput>(entity)!.Enabled = true;
        }
        else if (element.IsVisible && !motion.IsClosing)
        {
            motion.IsClosing = true;
            shell.Tree.GetComponent<XsrUiInput>(entity)!.Enabled = false;
            slot.DestroyOnExit = destroy;
            slot.ExitAt = DateTimeOffset.UtcNow.AddMilliseconds(shell.Renderer.ReducedMotion ? 0 : 360);
            if (!shell.Renderer.ReducedMotion)
                slot.Timer = TimeProvider.System.CreateTimer(_ =>
                {
                    try { store.Publish(store.Resolve(TaskBubbleState.Revision), DateTime.UtcNow.Ticks); }
                    catch (ObjectDisposedException) { }
                }, null, TimeSpan.FromMilliseconds(370), Timeout.InfiniteTimeSpan);
        }
        shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }

    public static void Arrange(XsrUiShell shell)
    {
        double bottom = 18;
        foreach (var entity in shell.Tree.Children(shell.Stage.Root)
            .Where(entity => shell.Tree.GetComponent<Slot>(entity) is not null)
            .OrderBy(entity => shell.Tree.GetComponent<Slot>(entity)!.Order).ToArray())
        {
            var slot = shell.Tree.GetComponent<Slot>(entity)!;
            var element = shell.Tree.GetComponent<XsrUiElement>(entity)!;
            if (slot.ExitAt is { } exit && DateTimeOffset.UtcNow >= exit)
            {
                slot.Timer?.Dispose(); slot.Timer = null; slot.ExitAt = null;
                if (slot.DestroyOnExit) { shell.Tree.Destroy(entity); continue; }
                element.IsVisible = false;
                shell.Tree.GetComponent<XsrUiOverlayMotion>(entity)!.IsClosing = false;
                shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
            }
            if (!element.IsVisible || shell.Tree.GetComponent<XsrUiOverlayMotion>(entity)!.IsClosing) continue;
            var margin = new XsrUiThickness(0, 0, 18, bottom);
            if (element.Margin != margin)
            {
                element.Margin = margin;
                shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Layout);
            }
            bottom += (element.Height ?? 48) + 10;
        }
    }
}


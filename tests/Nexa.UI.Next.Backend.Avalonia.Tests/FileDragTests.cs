using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Nexa.UI.Next;
using Nexa.UI.Next.Backend.Avalonia;
using Nexa.Xsr;

namespace Nexa.UI.Next.Backend.Avalonia.Tests;

internal static partial class Program
{
    private static async Task VerifyNativeFileDragThreshold(AvaloniaUiShellWindow window, XsrUiShell shell, AvaloniaUiSceneSurface surface)
    {
        var root = shell.Tree.Create("file-drag-page");
        var row = shell.Tree.Create("file-drag-row");
        shell.Tree.SetComponent(row, new XsrUiElement { Width = 300, Height = 100 });
        shell.Tree.SetComponent(row, new XsrUiInput { Clickable = true, Focusable = true });
        shell.Tree.SetComponent(row, new XsrUiSemantic(XsrUiSemanticRole.Button, "Version directory"));
        shell.Tree.SetComponent(row, new XsrUiFileDrag([Path.GetTempPath()],
            XsrUiFileDragEffects.Copy | XsrUiFileDragEffects.Move | XsrUiFileDragEffects.Link, XsrSemanticId.Parse("test.refresh")));
        shell.Tree.Attach(row, root);
        shell.Stage.Navigation.Replace(root);
        surface.CommitScene();
        window.UpdateLayout();
        var rect = Node(surface.Scene!, row).Rect;
        Point point = surface.TranslatePoint(new Point(rect.X + 50, rect.Y + 50), window)!.Value;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<DragDropEffects>(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = surface.FileDragRunner;
        surface.FileDragRunner = (_, data, effects) =>
        {
            AssertTrue(data.Contains(DataFormat.File));
            AssertEqual(DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link, effects);
            entered.SetResult();
            return completed.Task;
        };
        try
        {
            window.MouseDown(point, MouseButton.Left);
            window.MouseMove(point.WithX(point.X + 2), RawInputModifiers.LeftMouseButton);
            AssertFalse(surface.IsFileDragActive);
            window.MouseMove(point.WithX(point.X + 12), RawInputModifiers.LeftMouseButton);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            AssertTrue(surface.IsFileDragActive);
            window.MouseUp(point, MouseButton.Left);
            AssertFalse(shell.Tree.GetComponent<XsrUiInput>(row)!.IsPressed);
            completed.SetResult(DragDropEffects.None); // Escape/cancel, not a click.
            await Task.Yield();
        }
        finally { completed.TrySetResult(DragDropEffects.None); surface.FileDragRunner = original; }
    }
}

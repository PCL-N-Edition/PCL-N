using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.UI.Next.Tests;

internal static partial class Program
{
    private static void FileDragCancelsClickAndRejectsRetiredSources()
    {
        var tree = new XsrUiTree();
        var store = new XsrStateStoreBuilder().Build();
        var intents = new XsrUiIntentBuffer();
        var root = tree.Create("root");
        var row = tree.Create("row");
        tree.SetComponent(root, new XsrUiElement { Width = 300, Height = 200 });
        tree.SetComponent(row, new XsrUiElement { Width = 300, Height = 80 });
        tree.SetComponent(row, new XsrUiInput { Clickable = true, Focusable = true });
        tree.SetComponent(row, new XsrUiCommandBinding(XsrSemanticId.Parse("test.select")));
        tree.SetComponent(row, new XsrUiSemantic(XsrUiSemanticRole.Button, "Version"));
        var transfer = new XsrUiFileDrag([Path.GetTempPath()], XsrUiFileDragEffects.Copy | XsrUiFileDragEffects.Move,
            XsrSemanticId.Parse("test.refresh"));
        tree.SetComponent(row, transfer);
        tree.Attach(row, root);
        var renderer = new XsrUiRenderer(tree, store, intents);
        renderer.SetRoot(root);
        renderer.Render();
        AssertEqual(row, renderer.FileDragTarget(new(20, 20)));
        renderer.PointerPressed(new(20, 20));
        AssertTrue(ReferenceEquals(transfer, renderer.BeginFileDrag(row)));
        renderer.PointerReleased(new(20, 20));
        AssertEqual(0, intents.Count); // Neither successful drag nor Escape becomes a click.
        renderer.CompleteFileDrag(row, transfer);
        AssertEqual(1, intents.Count);
        tree.GetComponent<XsrUiInput>(row)!.Enabled = false;
        AssertTrue(renderer.BeginFileDrag(row) is null);
        tree.Destroy(row);
        renderer.CompleteFileDrag(row, transfer);
        AssertEqual(1, intents.Count);
        AssertTrue(renderer.BeginFileDrag(row) is null);
    }
}

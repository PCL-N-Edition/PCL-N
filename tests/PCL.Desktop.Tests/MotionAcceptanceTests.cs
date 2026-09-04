using PCL.UI.Next;

namespace PCL.Desktop.Tests;

internal static partial class Program
{
    private static void CapsulesIgnoreGeometryOnlyHoverMoves()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]), addProfile: true);
        XsrUiShell shell = fixture.Shell;
        shell.Renderer.ReducedMotion = false;
        XsrUiScene scene = shell.Render(AccountTestSize);
        XsrUiSceneNode capsule = FindByKey(shell, scene, "AccountSwitch");
        XsrUiPoint pointer = new(capsule.Rect.X + 1, capsule.Rect.Y + 18);
        shell.Renderer.PointerMoved(pointer);
        // A neighbour's geometry or a native arrange can move the same capsule under a stationary cursor.
        XsrUiElement actions = shell.Tree.GetComponent<XsrUiElement>(FindByKey(shell, scene, "AccountActions").Entity)!;
        actions.Margin = new XsrUiThickness(0, 20, 48, 0);
        shell.Tree.MarkDirty(FindByKey(shell, scene, "AccountActions").Entity, XsrUiDirtyKinds.Layout);
        for (int frame = 0; frame <= 30; frame++)
        {
            shell.Renderer.SetCapsulePresentationProgress(capsule.Entity, frame / 30d);
            scene = shell.Render(AccountTestSize);
            shell.Renderer.PointerMoved(pointer);
            AssertTrue(shell.Tree.GetComponent<XsrUiInput>(capsule.Entity)!.IsHovered);
        }
        // Real pointer departure is still immediate. Hysteresis never changes click hit rectangles.
        shell.Renderer.PointerMoved(new XsrUiPoint(800, 460));
        AssertFalse(shell.Tree.GetComponent<XsrUiInput>(capsule.Entity)!.IsHovered);
        shell.Renderer.PointerMoved(new XsrUiPoint(-1, -1));
    }

    private static void NavigationMotionHasOutgoingLayersAndLiveHitGeometry()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]), addProfile: true);
        XsrUiShell shell = fixture.Shell;
        shell.Renderer.ReducedMotion = true;
        XsrUiScene scene = shell.Render(AccountTestSize);
        shell.Renderer.ReducedMotion = false;
        shell.Renderer.Activate(FindByKey(shell, scene, "AccountWardrobe").Entity);
        scene = shell.Render(AccountTestSize);
        XsrUiEntityId title = FindByKey(shell, scene, "TitleSubpage").Entity;
        XsrUiEntityId backEntity = FindByKey(shell, scene, "TitleBack").Entity;
        AssertTrue(shell.Tree.GetComponent<XsrUiTransition>(FindByKey(shell, scene, "TitleNavigation").Entity) is null);
        AssertClose(128, shell.Renderer.GetTransitionOffset(title));
        AssertClose(-8, shell.Renderer.GetTransitionOffset(backEntity));
        AssertClose(0, shell.Renderer.GetTransitionOffsetY(shell.Content));
        AssertTrue(scene.Nodes.Where(node => node.TransitionOffsetX != 0 || node.TransitionOffsetY != 0)
            .All(node => shell.Tree.GetComponent<XsrUiTransition>(node.Entity)!.MovesSelf));
        AssertTrue(scene.Outgoing.Any(layer => layer.Group == title && layer.Nodes.Any(node => node.Text == "Nexa Launcher")));
        foreach (double offset in new[] { 96d, 64, 32, 0 })
        {
            shell.Renderer.SetTransitionOffset(title, offset);
            scene = shell.Render(AccountTestSize);
            XsrUiSceneNode back = FindByKey(shell, scene, "TitleBack");
            // Text movement cannot drag its sibling return control.
            AssertClose(4, back.Rect.X);
            AssertEqual(back.Entity, shell.Renderer.HitTest(new XsrUiPoint(back.Rect.X + 15, back.Rect.Y + 15)));
            AssertEqual(1d, back.PresentationOpacity);
            AssertTrue(scene.Outgoing.SelectMany(layer => layer.Nodes).All(node => !node.IsAccessible && !node.IsClickable && !node.IsFocusable));
        }
        shell.Renderer.SetTransitionOffset(backEntity, 0);
        shell.Renderer.Activate(FindByKey(shell, scene, "TitleBack").Entity);
        scene = shell.Render(AccountTestSize);
        XsrUiEntityId main = FindByKey(shell, scene, "TitleMain").Entity;
        AssertClose(-128, shell.Renderer.GetTransitionOffset(main));
        AssertTrue(shell.Tree.GetComponent<XsrUiTransition>(FindByKey(shell, scene, "AccountBody").Entity) is null);
        AssertTrue(shell.Tree.GetComponent<XsrUiTransition>(FindByKey(shell, scene, "CardAccount").Entity) is null);
        AssertClose(-6, shell.Renderer.GetTransitionOffsetY(FindByKey(shell, scene, "VersionName").Entity));
        XsrUiSceneNode versionName = FindByKey(shell, scene, "VersionName");
        AssertEqual(0d, versionName.PresentationOpacity);
        AssertTrue(versionName.TransitionEntryOrder > 0);
        foreach (var sequence in scene.Nodes.Where(node => node.TransitionEntryOrder >= 0).GroupBy(node => node.TransitionKey))
            AssertTrue(sequence.Select(node => node.TransitionEntryOrder).SequenceEqual(Enumerable.Range(0, sequence.Count())));
        shell.Renderer.SetTransitionOffsetY(versionName.Entity, -3);
        scene = shell.Render(AccountTestSize);
        AssertEqual(.5d, FindByKey(shell, scene, "VersionName").PresentationOpacity);
        // Repeated retargeting retains at most three frozen presentations per individual control.
        for (int iteration = 0; iteration < 20; iteration++)
        {
            shell.Renderer.SetTransitionOffset(main, -64);
            shell.Renderer.Activate(FindByKey(shell, scene, "AccountWardrobe").Entity);
            scene = shell.Render(AccountTestSize);
            AssertTrue(scene.Outgoing.All(layer => layer.Nodes.Count <= 3 && layer.BehindSelf));
            shell.Renderer.SetTransitionOffset(title, 0);
            scene = shell.Render(AccountTestSize);
            shell.Renderer.Activate(FindByKey(shell, scene, "TitleBack").Entity);
            scene = shell.Render(AccountTestSize);
        }
        shell.Renderer.ReducedMotion = true;
        scene = shell.Render(AccountTestSize);
        AssertEqual(0, scene.Outgoing.Count);
        AssertClose(0, shell.Renderer.GetTransitionOffset(main));
        AssertClose(0, shell.Renderer.GetTransitionOffsetY(shell.Content));
    }
}

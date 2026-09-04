using Avalonia.VisualTree;
using PCL.Core.Media;
using PCL.UI.Next;
using PCL.UI.Next.Backend.Avalonia;

namespace PCL.UI.Next.Backend.Avalonia.Tests;

internal static partial class Program
{
    private static async Task VerifyTransitionGroupsAndMedia(XsrUiShell shell, AvaloniaUiSceneSurface surface)
    {
        // Native arrange must preserve the scene's fractional trailing edge throughout expansion.
        AvaloniaUiSceneNodeControl capsule = new(_ => { }, _ => { }, () => true);
        for (int step = 0; step <= 100; step++)
        {
            double width = 36 + 74 * step / 100d;
            capsule.Measure(new global::Avalonia.Size(200, 36));
            capsule.Arrange(new global::Avalonia.Rect(200 - width, 0, width, 36));
            AssertTrue(Math.Abs(capsule.Bounds.Right - 200) < .000001);
            AssertTrue(Math.Abs(capsule.Bounds.Width - width) < .000001);
        }
        capsule.ReleasePresentation();
        XsrUiEntityId previous = shell.Stage.Navigation.Current;
        bool wasReduced = shell.Renderer.ReducedMotion;
        var previousNavigation = shell.SelectedNavigationId;
        XsrUiTransition? previousTitle = shell.Tree.GetComponent<XsrUiTransition>(shell.TitleBar);
        XsrUiEntityId page = shell.Tree.Create("transition-test"), group = shell.Tree.Create("content-group"), text = shell.Tree.Create("group-heading");
        shell.Tree.SetComponent(page, new XsrUiStackPanel(XsrUiOrientation.Vertical));
        shell.Tree.SetComponent(group, new XsrUiStackPanel(XsrUiOrientation.Vertical));
        XsrUiTransition bodyTransition = new() { Key = "identity" }, titleTransition = new() { Key = "main", OffsetX = 32 };
        shell.Tree.SetComponent(group, bodyTransition);
        shell.Tree.SetComponent(shell.TitleBar, titleTransition);
        shell.Tree.SetComponent(text, new XsrUiText("更衣橱 Agjp"));
        shell.Tree.SetComponent(text, new XsrUiVisualStyle { FontSize = 22 });
        shell.Tree.Attach(text, group); shell.Tree.Attach(group, page);
        shell.Renderer.ReducedMotion = true;
        shell.Stage.Navigation.Replace(page); surface.CommitScene();
        AssertTrue(surface.Scene!.Nodes.Single(node => node.Entity == text).Rect.Height >= 30);
        XsrUiEntityId title = shell.Tree.Children(shell.TitleBar).First(entity => shell.Tree.GetComponent<XsrUiText>(entity) is not null);
        double titleX = surface.Scene.Nodes.Single(node => node.Entity == title).Rect.X;
        shell.Renderer.ReducedMotion = false;
        bodyTransition.Key = "picker"; titleTransition.Key = "wardrobe";
        shell.Tree.MarkDirty(group, XsrUiDirtyKinds.Paint); shell.Tree.MarkDirty(shell.TitleBar, XsrUiDirtyKinds.Paint);
        surface.CommitScene();
        AssertTrue(surface.TryGetPresentedEnterProgress(text, out double bodyStart) && bodyStart < 1);
        AssertTrue(surface.TryGetPresentedEnterProgress(title, out double titleStart) && titleStart == 1);
        AssertEqual(titleX + 32, surface.Scene!.Nodes.Single(node => node.Entity == title).Rect.X);
        await Task.Delay(40);
        AssertTrue(surface.TryGetPresentedEnterProgress(text, out double middle) && middle > bodyStart && middle < 1);
        AssertTrue(shell.Renderer.GetTransitionOffset(shell.TitleBar) is > 0 and < 32);
        bodyTransition.Key = "identity";
        shell.Tree.MarkDirty(group, XsrUiDirtyKinds.Paint); surface.CommitScene();
        AssertTrue(surface.TryGetPresentedEnterProgress(text, out double retargeted) && retargeted >= middle);
        shell.Renderer.ReducedMotion = true;
        bodyTransition.Key = "picker"; titleTransition.Key = "main";
        shell.Tree.MarkDirty(group, XsrUiDirtyKinds.Paint); shell.Tree.MarkDirty(shell.TitleBar, XsrUiDirtyKinds.Paint); surface.CommitScene();
        AssertTrue(surface.TryGetPresentedEnterProgress(text, out double settled) && settled == 1);
        AssertTrue(surface.TryGetPresentedEnterProgress(title, out double titleSettled) && titleSettled == 1);
        AssertEqual(0d, shell.Renderer.GetTransitionOffset(shell.TitleBar));
        shell.Renderer.ReducedMotion = false;
        shell.Select(shell.NavigationItems.First(item => item.Id != shell.SelectedNavigationId).Id);
        surface.CommitScene();
        AssertTrue(surface.TryGetPresentedEnterProgress(text, out double reused) && reused < 1);

        using Stream resource = typeof(AvaloniaUiShellHost).Assembly.GetManifestResourceStream("PCL.UI.Next.Backend.Avalonia.Assets.Avatars.Steve.png")!;
        using MemoryStream bytes = new(); resource.CopyTo(bytes);
        PngImage png = PngImage.TryCreate(bytes.ToArray())!;
        XsrUiEntityId image = shell.Tree.Create("decoded-avatar");
        XsrUiImage media = new("pcl/avatar/steve") { Raster = new(png, [new(new(8, 8, 8, 8), new(0, 0, 1, 1))]) };
        shell.Tree.SetComponent(image, new XsrUiElement { Width = 72, Height = 72 });
        shell.Tree.SetComponent(image, media); shell.Tree.Attach(image, page); surface.CommitScene();
        AvaloniaUiSceneNodeControl control = surface.GetVisualDescendants().OfType<AvaloniaUiSceneNodeControl>().Single(item => item.Node.Entity == image);
        AssertTrue(control.HasDecodedRaster);
        media.Raster = null; shell.Tree.MarkDirty(image, XsrUiDirtyKinds.Paint); surface.CommitScene();
        AssertFalse(control.HasDecodedRaster);
        AssertEqual("pcl/avatar/steve", control.Node.ImageSource);
        // A detached page loses its native tracks, but its renderer presentation survives.
        bodyTransition.OffsetX = 24; bodyTransition.Key = "resume-slide";
        shell.Tree.MarkDirty(group, XsrUiDirtyKinds.Paint); surface.CommitScene();
        AssertEqual(24d, shell.Renderer.GetTransitionOffset(group));
        XsrUiEntityId temporary = shell.Tree.Create("temporary-page");
        shell.Stage.Navigation.Replace(temporary); surface.CommitScene();
        shell.Stage.Navigation.Replace(page); surface.CommitScene();
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (shell.Renderer.GetTransitionOffset(group) != 0 && DateTime.UtcNow < deadline) await Task.Delay(16);
        AssertEqual(0d, shell.Renderer.GetTransitionOffset(group));
        shell.Tree.Destroy(temporary);

        // Independent content motion must not move its parent, sibling image, or hit geometry separately.
        shell.Renderer.ReducedMotion = true;
        XsrUiTransition labelTransition = new() { Key = "before", MovesSelf = true, OffsetY = 6 };
        shell.Tree.SetComponent(text, labelTransition); surface.CommitScene();
        double siblingY = surface.Scene!.Nodes.Single(node => node.Entity == image).Rect.Y;
        double labelY = surface.Scene.Nodes.Single(node => node.Entity == text).Rect.Y;
        shell.Renderer.ReducedMotion = false;
        labelTransition.Key = "after";
        shell.Tree.MarkDirty(text, XsrUiDirtyKinds.Paint); surface.CommitScene();
        AssertEqual(labelY + 6, surface.Scene!.Nodes.Single(node => node.Entity == text).Rect.Y);
        AssertEqual(siblingY, surface.Scene.Nodes.Single(node => node.Entity == image).Rect.Y);
        AssertTrue(surface.Scene.Outgoing.Any(layer => layer.Group == text && layer.BehindSelf));
        await Task.Delay(40);
        double live = shell.Renderer.GetTransitionOffsetY(text);
        AssertTrue(live is > 0 and < 6);
        labelTransition.Key = "reverse";
        shell.Tree.MarkDirty(text, XsrUiDirtyKinds.Paint); surface.CommitScene();
        AssertEqual(live, shell.Renderer.GetTransitionOffsetY(text));
        shell.Renderer.ReducedMotion = true; surface.CommitScene();
        AssertEqual(0d, shell.Renderer.GetTransitionOffsetY(text));
        AssertEqual(0, surface.Scene!.Outgoing.Count);

        // The reading-order ordinal drives a real delayed spring, not a simultaneous fade.
        XsrUiTransition imageTransition = new() { Key = "sequence", MovesSelf = true, StaggerEntry = true, OffsetY = 6 };
        labelTransition.StaggerEntry = true; labelTransition.Key = "sequence";
        shell.Tree.SetComponent(image, imageTransition);
        shell.Tree.MarkDirty(text, XsrUiDirtyKinds.Paint); surface.CommitScene();
        AssertEqual(0, surface.Scene!.Nodes.Single(node => node.Entity == text).TransitionEntryOrder);
        AssertEqual(1, surface.Scene.Nodes.Single(node => node.Entity == image).TransitionEntryOrder);
        shell.Renderer.ReducedMotion = false;
        labelTransition.Key = imageTransition.Key = "sequence-next";
        shell.Tree.MarkDirty(text, XsrUiDirtyKinds.Paint); shell.Tree.MarkDirty(image, XsrUiDirtyKinds.Paint);
        surface.CommitScene();
        AssertEqual(0d, surface.Scene!.Nodes.Single(node => node.Entity == image).PresentationOpacity);
        await Task.Delay(40);
        AssertTrue(shell.Renderer.GetTransitionOffsetY(text) < shell.Renderer.GetTransitionOffsetY(image));
        shell.Renderer.ReducedMotion = true; surface.CommitScene();
        AssertEqual(1d, surface.Scene!.Nodes.Single(node => node.Entity == image).PresentationOpacity);
        shell.Renderer.ReducedMotion = wasReduced;
        shell.Select(previousNavigation);
        shell.Tree.SetComponent(shell.TitleBar, previousTitle ?? new XsrUiTransition());
        if (previous.IsAssigned) { shell.Stage.Navigation.Replace(previous); surface.CommitScene(); shell.Tree.Destroy(page); }
        Console.WriteLine("PASS: independent motion, live retargeting, legacy transitions, reused pages, reduced motion and raster lifetime");
    }
}

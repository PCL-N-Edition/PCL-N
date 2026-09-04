using Avalonia.VisualTree;
using PCL.Core.Media;
using PCL.UI.Next;
using PCL.UI.Next.Backend.Avalonia;

namespace PCL.UI.Next.Backend.Avalonia.Tests;

internal static partial class Program
{
    private static async Task VerifyTransitionGroupsAndMedia(XsrUiShell shell, AvaloniaUiSceneSurface surface)
    {
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
        shell.Renderer.ReducedMotion = wasReduced;
        shell.Select(previousNavigation);
        shell.Tree.SetComponent(shell.TitleBar, previousTitle ?? new XsrUiTransition());
        if (previous.IsAssigned) { shell.Stage.Navigation.Replace(previous); surface.CommitScene(); shell.Tree.Destroy(page); }
        Console.WriteLine("PASS: grouped content/title transitions, retargeting, reused pages, reduced motion and raster lifetime");
    }
}

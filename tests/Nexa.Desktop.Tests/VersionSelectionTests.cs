using Nexa.Desktop.Ui;
using Nexa.Services.Composition;
using Nexa.Services.Minecraft;
using Nexa.UI.Next;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void VersionSelectionUsesDirectoryQualifiedLaunch()
    {
        RecordingStartRoute recording = new();
        using MinecraftRuntime runtime = CreateRecordingRuntime(recording);
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([Instance("same"), Instance("chosen")]),
            runtime, addProfile: true, ownsMinecraftRuntime: false);
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        XsrUiShell shell = fixture.Shell;
        XsrUiSize size = new(850, 500);
        XsrUiEntityId entry = FindByKey(shell, shell.Render(size), "InstanceListButton").Entity;
        AssertTrue(shell.Renderer.Focus(entry));
        AssertTrue(shell.Renderer.HandleKey(XsrUiKey.Enter));
        XsrUiScene scene = shell.Render(size);
        AssertEqual("当前目录：", FindByKey(shell, scene, "LibraryDirectoryCaption").Text);
        XsrUiEntityId chosen = FindByKey(shell, scene, "LibraryRow:version:chosen").Entity;
        AssertTrue(shell.Renderer.Focus(chosen));
        AssertTrue(shell.Renderer.HandleKey(XsrUiKey.Enter));
        fixture.Controller.Versions.WaitUntilIdle().GetAwaiter().GetResult();
        scene = shell.Render(size);
        // Selecting a version immediately returns to the launch page; the entry regains focus.
        AssertTrue(SpinWait.SpinUntil(() => shell.Stage.Navigation.Current != fixture.Controller.Versions.Page, TimeSpan.FromSeconds(2)));
        AssertEqual("chosen", ReadCell(fixture.Store, LaunchPageState.SelectedInstanceKey));
        AssertEqual(entry, shell.Renderer.Focused);
        AssertEqual("chosen", FindByKey(shell, scene, "VersionName").Text);
        string original = ReadCell(fixture.Store, LaunchPageState.InstanceDirectoryKey);
        string second = Path.Combine(fixture.TemporaryDirectory, "second");
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(second, "keep.txt"), "keep");
        // Re-enter the version page. The browse entry opens the chooser with the manual path
        // editor (no native picker in the test host); directories later reopen the list.
        AssertTrue(shell.Renderer.Activate(entry));
        scene = shell.Render(size);
        AssertTrue(shell.Renderer.Activate(FindByKey(shell, scene, "LibraryAddDirectory").Entity));
        scene = shell.Render(size);
        shell.Renderer.SetTextInputValue(FindByKey(shell, scene, "LibraryDirectoryInput").Entity, second);
        AssertTrue(shell.Renderer.Activate(FindByKey(shell, scene, "LibraryAddPath").Entity));
        fixture.Controller.Versions.WaitUntilIdle().GetAwaiter().GetResult();
        scene = shell.Render(size);
        AssertEqual("second", FindByKey(shell, scene, "LibraryDirectoryName").Text);
        AssertEqual("same", ReadCell(fixture.Store, LaunchPageState.SelectedInstanceKey));
        AssertEqual(second, ReadCell(fixture.Store, LaunchPageState.InstanceDirectoryKey));
        AssertTrue(shell.Renderer.Activate(FindByKey(shell, scene, "LibraryChooseDirectory").Entity));
        scene = shell.Render(size);
        AssertTrue(FindByKey(shell, scene, "LibraryRow:directory:" + second).IsSelected);
        AssertTrue(shell.Renderer.Activate(FindByKey(shell, scene, "LibraryRow:directory:" + original).Entity));
        fixture.Controller.Versions.WaitUntilIdle().GetAwaiter().GetResult();
        scene = shell.Render(size);
        AssertEqual("chosen", ReadCell(fixture.Store, LaunchPageState.SelectedInstanceKey));
        AssertEqual(original, ReadCell(fixture.Store, LaunchPageState.InstanceDirectoryKey));
        // Forgetting only removes registration; it never selects the removed row or deletes files.
        AssertTrue(shell.Renderer.Activate(FindByKey(shell, scene, "LibraryChooseDirectory").Entity));
        scene = shell.Render(size);
        AssertTrue(shell.Renderer.Activate(FindByKey(shell, scene, "LibraryDirectoryForget:directory:" + second).Entity));
        fixture.Controller.Versions.WaitUntilIdle().GetAwaiter().GetResult();
        scene = shell.Render(size);
        AssertFalse(HasKey(shell, scene, "LibraryRow:directory:" + second));
        AssertTrue(File.Exists(Path.Combine(second, "keep.txt")));
        Emit(fixture.Intents, "ui.page.back");
        AssertEqual(entry, shell.Renderer.Focused);
        scene = shell.Render(size);
        AssertEqual("chosen", FindByKey(shell, scene, "VersionName").Text);
        Emit(fixture.Intents, "ui.launch.primary");
        AssertTrue(SpinWait.SpinUntil(() => recording.LastCommand is not null, TimeSpan.FromSeconds(2)));
        AssertEqual("chosen", recording.LastCommand!.InstanceId);
        AssertEqual(original, recording.LastCommand.MinecraftRootDirectory);
    }

    private static void VersionListKeepsCompactGeometryAndIcons()
    {
        MinecraftInstanceDescriptor[] instances = [.. Enum.GetValues<MinecraftVersionKind>().Select(kind =>
        {
            MinecraftInstanceDescriptor instance = Instance(kind.ToString());
            return instance with { Version = instance.Version with { Kind = kind } };
        })];
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource(instances));
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        XsrUiShell shell = fixture.Shell;
        Emit(fixture.Intents, "ui.launch.instances");
        foreach (XsrUiSize size in new[] { new XsrUiSize(810, 470), new(850, 500), new(1280, 800) })
        {
            foreach (bool expanded in new[] { false, true })
            {
                shell.SetNavigationExpanded(expanded);
                XsrUiScene scene = shell.Render(size);
                XsrUiRect content = FindByKey(shell, scene, "LibraryContent").Rect;
                AssertContains(new(0, 0, size.Width, size.Height), content);
                AssertContains(content, FindByKey(shell, scene, "LibraryDirectoryBar").Rect);
                AssertEqual(40d, FindByKey(shell, scene, "LibraryDirectoryBar").Rect.Height);
                AssertFalse(HasKey(shell, scene, "LibrarySelection"));
                AssertFalse(HasKey(shell, scene, "LibraryDirectoryHint"));
                AssertFalse(HasKey(shell, scene, "LibraryDirectoryPath"));
                XsrUiRect scrollRect = FindByKey(shell, scene, "LibraryVersionRows").Rect;
                AssertTrue(scrollRect.Height > 100);
                XsrUiScroll scroll = shell.Tree.GetComponent<XsrUiScroll>(FindByKey(shell, scene, "LibraryVersionRows").Entity)!;
                AssertTrue(FindByKey(shell, scene, "LibraryVersionRows").Scroll!.Value.CanScrollVertically);
                AssertTrue(shell.Renderer.PointerScroll(new(scrollRect.X + 50, scrollRect.Y + 40), 180));
                AssertTrue(scroll.OffsetY > 0);
                scroll.OffsetY = 0;
                shell.Tree.MarkDirty(FindByKey(shell, scene, "LibraryVersionRows").Entity, XsrUiDirtyKinds.Layout);
            }
        }
        XsrUiScene current = shell.Render(new(850, 500));
        foreach (MinecraftInstanceDescriptor instance in instances)
        {
            XsrUiEntityId icon = FindEntity(shell, "LibraryRowIcon:version:" + instance.Id);
            AssertEqual(VersionSelectionController.VersionIcon(instance.Version.Kind), shell.Tree.GetComponent<XsrUiImage>(icon)!.Source);
        }
        AssertEqual(12, instances.Select(instance => VersionSelectionController.VersionIcon(instance.Version.Kind)).Distinct().Count());
        string selection = ReadCell(fixture.Store, LaunchPageState.SelectedInstanceKey);
        shell.Renderer.SetTextInputValue(FindByKey(shell, current, "LibrarySearch").Entity, "Fabric");
        current = shell.Render(new(850, 500));
        AssertTrue(HasKey(shell, current, "LibraryRow:version:Fabric"));
        AssertFalse(HasKey(shell, current, "LibraryRow:version:Release"));
        AssertEqual(selection, ReadCell(fixture.Store, LaunchPageState.SelectedInstanceKey));
        shell.Renderer.SetTextInputValue(FindByKey(shell, current, "LibrarySearch").Entity, "no such version");
        current = shell.Render(new(850, 500));
        AssertEqual("无匹配版本", FindByKey(shell, current, "LibraryEmpty").Text);
    }

    private static void VersionDirectoryPickerDiscardsLateResult()
    {
        DeferredDirectoryPicker effects = new();
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([Instance("same")]), directoryEffects: effects);
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        Emit(fixture.Intents, "ui.launch.instances");
        XsrUiScene scene = fixture.Shell.Render(new(850, 500));
        string original = ReadCell(fixture.Store, LaunchPageState.InstanceDirectoryKey);
        string another = Path.Combine(fixture.TemporaryDirectory, "late");
        Directory.CreateDirectory(another);
        AssertTrue(fixture.Shell.Renderer.Activate(FindByKey(fixture.Shell, scene, "LibraryAddDirectory").Entity));
        Emit(fixture.Intents, "ui.page.back");
        effects.Completion.SetResult(another);
        fixture.Controller.Versions.WaitUntilIdle().GetAwaiter().GetResult();
        AssertEqual(original, ReadCell(fixture.Store, LaunchPageState.InstanceDirectoryKey));
    }

    private sealed class DeferredDirectoryPicker : IVersionDirectoryEffects
    {
        public TaskCompletionSource<string?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string?> PickDirectoryAsync() => Completion.Task;
    }
}

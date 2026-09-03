using PCL.Desktop.Ui;
using PCL.Services.Composition;
using PCL.Services.Foundation;
using PCL.Services.Minecraft.Launch;
using PCL.UI.Next;
using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Desktop.Tests;

internal static partial class Program
{
    public static void Main()
    {
        foreach ((string name, Action body) in TestCases)
        {
            body();
            Console.WriteLine($"PASS: {name}");
        }

        Console.WriteLine($"Desktop composition tests passed: {TestCases.Length}.");
    }

    private static readonly (string Name, Action Body)[] TestCases =
    [
        ("launch page composes with bound summaries and status", LaunchPageComposesWithBoundSummaries),
        ("navigation intents route between launch and placeholder pages", NavigationIntentsRouteBetweenPages),
        ("launch intent without instances reports the idle status", LaunchIntentWithoutInstancesReportsIdleStatus),
    ];

    private static (XsrUiShell Shell, DesktopUiIntentSink Intents, XsrStateStore Store, LaunchPageController Controller) ComposeLaunchPage()
    {
        XsrStateStore store = FoundationState.CreateBuilder().Build();
        XsrUiShell shell = XsrUiShellComposer.Compose(store);
        DesktopUiIntentSink intents = new();
        MinecraftRuntime minecraft = MinecraftRuntimeComposer.Compose();
        LaunchPageController controller = new(shell, intents, minecraft, store, Path.Combine(Path.GetTempPath(), "nexa-launch-test", "minecraft"));
        controller.Attach();
        return (shell, intents, store, controller);
    }

    private static void LaunchPageComposesWithBoundSummaries()
    {
        (XsrUiShell shell, _, XsrStateStore store, _) = ComposeLaunchPage();
        XsrUiScene scene = shell.Render(new XsrUiSize(1024, 700));

        // The launch page is the initial route: its state-bound texts resolve through the host
        // store cells and the launch command is present.
        AssertTrue(scene.Nodes.Any(node => node.Label == "启动游戏" && node.Text == "启动"));
        AssertEqual(
            "未选择账户",
            scene.Nodes.First(node => node.Label == "账户摘要").Text);
        AssertEqual(
            "就绪",
            scene.Nodes.First(node => node.Label == "启动状态").Text);
        AssertEqual("就绪", ReadCell(store, LaunchPageStateComposition.StatusKey));
    }

    private static void NavigationIntentsRouteBetweenPages()
    {
        (XsrUiShell shell, DesktopUiIntentSink intents, _, _) = ComposeLaunchPage();

        Emit(intents, "ui.navigation.settings");
        XsrUiScene placeholder = shell.Render(new XsrUiSize(1024, 700));
        AssertTrue(placeholder.Nodes.Any(node => node.Text == "该分区将在后续单元中迁移。"));
        AssertFalse(placeholder.Nodes.Any(node => node.Label == "启动游戏"));

        Emit(intents, "ui.navigation.launch");
        XsrUiScene launch = shell.Render(new XsrUiSize(1024, 700));
        AssertTrue(launch.Nodes.Any(node => node.Label == "启动游戏" && node.Text == "启动"));
        AssertFalse(launch.Nodes.Any(node => node.Text == "该分区将在后续单元中迁移。"));
    }

    private static void LaunchIntentWithoutInstancesReportsIdleStatus()
    {
        (XsrUiShell _, DesktopUiIntentSink intents, XsrStateStore store, _) = ComposeLaunchPage();

        // No instance directory exists under the test root, so the start intent reports the
        // idle status without dispatching any command.
        Emit(intents, "ui.launch.start");
        AssertEqual("未找到可启动的实例", ReadCell(store, LaunchPageStateComposition.StatusKey));
    }

    private static void Emit(DesktopUiIntentSink intents, string command) =>
        intents.Emit(XsrSemanticId.Parse(command), default, XsrCorrelationId.Create());

    private static string ReadCell(XsrStateStore store, XsrSemanticId key) =>
        (string?)store.ReadAppliedValue(store.Resolve(key)) ?? string.Empty;
}

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
        ("launch page replicates the legacy card layout with bound facts", LaunchPageReplicatesLegacyLayout),
        ("launch page matches legacy geometry across wide, default, and minimum windows", LaunchPageMatchesLegacyGeometry),
        ("navigation intents route between launch and placeholder pages", NavigationIntentsRouteBetweenPages),
        ("launch intent without instances reports the idle status", LaunchIntentWithoutInstancesReportsIdleStatus),
    ];

    private static (XsrUiShell Shell, DesktopUiIntentSink Intents, XsrStateStore Store, LaunchPageController Controller) ComposeLaunchPage()
    {
        XsrStateStore store = FoundationState.CreateBuilder().Build();
        DesktopUiIntentSink intents = new();
        XsrUiShell shell = PxmlShellComposer.Compose(store, intentSink: intents);
        MinecraftRuntime minecraft = MinecraftRuntimeComposer.Compose();
        LaunchPageController controller = new(shell, intents, minecraft, store, Path.Combine(Path.GetTempPath(), "nexa-launch-test", "minecraft"));
        controller.Attach();
        controller.WaitUntilIdle().GetAwaiter().GetResult();
        return (shell, intents, store, controller);
    }

    private static void LaunchPageReplicatesLegacyLayout()
    {
        (XsrUiShell shell, _, XsrStateStore store, _) = ComposeLaunchPage();
        XsrUiScene scene = shell.Render(new XsrUiSize(1280, 800));

        // The launch page replicates the legacy experimental home: an account card (账户 +
        // 实验 badge + name + summary), a version card with the picker row and accent launch
        // button, and the community about card.
        AssertTrue(scene.Nodes.Any(node => node.Label == "LaunchButton" && node.Text == "下载游戏"));
        AssertEqual("账户", scene.Nodes.First(node => node.Label == "AccountHeader").Text);
        AssertEqual("实验", scene.Nodes.First(node => node.Label == "AccountBadgeText").Text);
        AssertEqual("版本", scene.Nodes.First(node => node.Label == "VersionHeader").Text);
        AssertTrue(scene.Nodes.Any(node => node.Label == "AboutTitle" && node.Text == "关于 PCL N Edition"));

        // Facts flow from host state cells.
        AssertEqual(ReadCell(store, LaunchPageStateComposition.ProfileNameKey),
            scene.Nodes.First(node => node.Label == "AccountName").Text);
        AssertEqual(ReadCell(store, LaunchPageStateComposition.InstanceSummaryKey),
            scene.Nodes.First(node => node.Label == "VersionName").Text);

        // Legacy typography carries through the scene: section headers 12 px semibold, the
        // instance name 16 px semibold.
        XsrUiSceneNode header = scene.Nodes.First(node => node.Label == "AccountHeader");
        AssertEqual(12, header.VisualStyle.FontSize);
        AssertEqual(600, header.VisualStyle.FontWeight);
        XsrUiSceneNode versionName = scene.Nodes.First(node => node.Label == "VersionName");
        AssertEqual(16, versionName.VisualStyle.FontSize);
        AssertEqual(600, versionName.VisualStyle.FontWeight);
        AssertEqual(
            XsrUiTextAlignment.Center,
            scene.Nodes.First(node => node.Label == "LaunchButton").VisualStyle.TextAlignment);

        // No instance under the test root: the button offers the download action and the
        // picker row explains how to select or install a version.
        AssertEqual("未找到可启动的游戏版本", ReadCell(store, LaunchPageStateComposition.InstanceSummaryKey));
        AssertEqual("使用右上角按钮选择或安装版本", ReadCell(store, LaunchPageStateComposition.InstanceDetailKey));
        AssertEqual("下载游戏", scene.Nodes.First(node => node.Label == "LaunchButton").Text);
        AssertEqual("就绪", ReadCell(store, LaunchPageStateComposition.StatusKey));
    }

    private static void LaunchPageMatchesLegacyGeometry()
    {
        (XsrUiShell shell, _, _, _) = ComposeLaunchPage();

        AssertLegacyGeometry(shell.Render(new XsrUiSize(1280, 800)), contentWidth: 1176, contentHeight: 700);
        AssertLegacyGeometry(shell.Render(new XsrUiSize(850, 500)), contentWidth: 746, contentHeight: 400);
        AssertLegacyGeometry(shell.Render(new XsrUiSize(810, 470)), contentWidth: 706, contentHeight: 370);
    }

    private static void AssertLegacyGeometry(
        XsrUiScene scene,
        double contentWidth,
        double contentHeight)
    {
        const double contentX = 76;
        const double contentY = 76;
        const double columnGap = 16;
        const double rightCardGap = 12;
        const double versionCardHeight = 176;
        double distributableWidth = contentWidth - columnGap;
        double expectedAccountWidth = Math.Min(360, distributableWidth * 0.92 / (0.92 + 1.35));
        double expectedRightWidth = distributableWidth - expectedAccountWidth;

        XsrUiRect account = scene.Nodes.First(node => node.Label == "CardAccount").Rect;
        XsrUiRect version = scene.Nodes.First(node => node.Label == "CardVersion").Rect;
        XsrUiRect about = scene.Nodes.First(node => node.Label == "CardAbout").Rect;
        XsrUiRect accountBadge = scene.Nodes.First(node => node.Label == "AccountBadge").Rect;
        XsrUiRect accountHeader = scene.Nodes.First(node => node.Label == "AccountHeaderRow").Rect;
        XsrUiRect accountContent = scene.Nodes.First(node => node.Label == "AccountContent").Rect;
        XsrUiRect accountSummary = scene.Nodes.First(node => node.Label == "AccountSummary").Rect;

        AssertRectClose(
            new XsrUiRect(contentX, contentY, expectedAccountWidth, contentHeight),
            account);
        AssertRectClose(
            new XsrUiRect(
                contentX + expectedAccountWidth + columnGap,
                contentY,
                expectedRightWidth,
                versionCardHeight),
            version);
        AssertRectClose(
            new XsrUiRect(
                version.X,
                contentY + versionCardHeight + rightCardGap,
                expectedRightWidth,
                contentHeight - versionCardHeight - rightCardGap),
            about);

        AssertClose(accountContent.X + accountContent.Width, accountBadge.X + accountBadge.Width);
        AssertClose(16, accountHeader.Height);
        AssertClose(18, accountSummary.Height);
        AssertClose(accountContent.Y + accountContent.Height, accountSummary.Y + accountSummary.Height);
        AssertTrue(version.Y + version.Height <= about.Y);
    }

    private static void NavigationIntentsRouteBetweenPages()
    {
        (XsrUiShell shell, DesktopUiIntentSink intents, _, _) = ComposeLaunchPage();

        Emit(intents, "ui.navigation.settings");
        XsrUiScene placeholder = shell.Render(new XsrUiSize(1280, 800));
        AssertTrue(placeholder.Nodes.Any(node => node.Text == "该分区将在后续单元中迁移。"));
        AssertFalse(placeholder.Nodes.Any(node => node.Label == "LaunchButton"));

        Emit(intents, "ui.navigation.launch");
        XsrUiScene launch = shell.Render(new XsrUiSize(1280, 800));
        AssertTrue(launch.Nodes.Any(node => node.Label == "LaunchButton"));
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

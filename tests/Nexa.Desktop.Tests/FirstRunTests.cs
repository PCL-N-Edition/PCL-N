using Nexa.Desktop.Ui;
using Nexa.Services.Composition;
using Nexa.Services.Files;
using Nexa.Services.Setup;
using Nexa.UI.Next;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void FirstRunKeepsDraftsAndCommitsOnlyAtFinish()
    {
        string temp = Path.Combine(Path.GetTempPath(), "nexa-setup-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            foreach (bool finish in new[] { false, true })
            {
                var context = new XsrUiRuntimeContext();
                XsrStateStoreBuilder builder = new(); LaunchPageState.DeclareState(builder);
                var store = builder.Build(context.StateBridge);
                var intents = new DesktopUiIntentSink();
                var shell = PxmlShellComposer.Compose(store, context, intentSink: intents);
                shell.Renderer.ReducedMotion = true;
                string chosen = Path.Combine(temp, finish ? "finish" : "cancel");
                string locator = Path.Combine(temp, "storage.json");
                var service = new FirstRunService(Path.Combine(temp, "initial"), locator);
                bool closed = false;
                using var page = new FirstRunController(shell, intents, store, FirstRunRuntimeComposer.Compose(service), service.Read(), () => Task.FromResult<string?>(chosen), () => closed = true);
                void Click(string command) { Emit(intents, "ui.setup." + command); shell.Render(new(760, 500)); }
                shell.Render(new(760, 500));
                AssertFalse(File.Exists(locator));
                Click("next"); Click("browse"); shell.Render(new(760, 500));
                Click("next");
                AssertEqual(2, page.Step);
                var scene = shell.Render(new(760, 500));
                AssertTrue(FindByKey(shell, scene, "SetupPrivate").Text!.StartsWith('✓'));
                Click("share"); Click("back");
                scene = shell.Render(new(760, 500));
                AssertEqual(chosen, FindByKey(shell, scene, "SetupPath").Text);
                Click("next"); Click("next");
                scene = shell.Render(new(760, 500));
                AssertTrue(FindByKey(shell, scene, "SetupSummary").Text!.Contains("共享基本使用数据", StringComparison.Ordinal));
                var next = FindByKey(shell, scene, "SetupNext");
                AssertEqual(XsrUiTextAlignment.Center, shell.Tree.GetComponent<XsrUiVisualStyle>(next.Entity)!.TextAlignment);
                AssertTrue(next.Rect.Y + next.Rect.Height <= 500);
                AssertFalse(File.Exists(locator));
                if (finish)
                {
                    Click("next");
                    AssertTrue(SpinWait.SpinUntil(() => { shell.Render(new(760, 500)); return closed; }, TimeSpan.FromSeconds(10)));
                    AssertTrue(page.Completed);
                    AssertEqual(chosen, LauncherStorageLocation.Read(locator));
                }
            }
            AssertFalse(Directory.Exists(Path.Combine(temp, "cancel")));
        }
        finally { Directory.Delete(temp, true); }
    }
}

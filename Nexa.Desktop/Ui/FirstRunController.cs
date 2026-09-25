using Nexa.Pxml;
using Nexa.Services.Composition;
using Nexa.Services.Setup;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Ui;

internal static class FirstRunState
{
    public static readonly XsrSemanticId Wake = XsrSemanticId.Parse("setup.presentation.wake");
    public static void Declare(XsrStateStoreBuilder builder) => builder.Cell<long>(Wake, "Nexa.Desktop.Setup");
}

/// <summary>Presentation-only setup draft. Persistence crosses sealed Service routes.</summary>
internal sealed class FirstRunController : IDisposable
{
    private readonly XsrUiShell _shell;
    private readonly DesktopUiIntentSink _intents;
    private readonly FirstRunRuntime _runtime;
    private readonly Func<Task<string?>> _pickDirectory;
    private readonly Action _close;
    private readonly FirstRunStatus _status;
    private readonly XsrStateStore _store;
    private long _wake;
    private readonly Dictionary<string, XsrUiEntityId> _nodes = [];
    private readonly Queue<string> _pending = [];
    private readonly CancellationTokenSource _stop = new();
    private Task<string?>? _picking;
    private Task<XsrResult>? _saving;
    private int _pickStep;
    private string _directory;
    private bool _consent;
    private bool _disposed;
    internal int Step { get; private set; }
    internal bool Completed { get; private set; }
    internal XsrUiEntityId Page { get; }

    public FirstRunController(XsrUiShell shell, DesktopUiIntentSink intents, XsrStateStore store,
        FirstRunRuntime runtime, FirstRunStatus status, Func<Task<string?>> pickDirectory, Action close)
    {
        _shell = shell; _intents = intents; _runtime = runtime; _status = status;
        _store = store;
        _pickDirectory = pickDirectory; _close = close; _directory = status.DataDirectory;
        _consent = status.TelemetryRequired;
        using var stream = typeof(FirstRunController).Assembly.GetManifestResourceStream("Nexa.Desktop.Ui.FirstRunPage.pxml")!;
        using var reader = new StreamReader(stream);
        var host = shell.Tree.Create("setup-loader");
        Page = PxmlUiLoader.Load(PxmlCompiler.Compile(PxmlParser.Parse(reader.ReadToEnd())), shell.Tree, store, host);
        shell.Tree.Detach(Page); shell.Tree.Destroy(host);
        shell.Tree.Walk(Page, entity => { _nodes[shell.Tree.Name(entity)] = entity; return true; });
        shell.Tree.GetComponent<XsrUiElement>(shell.Navigation)!.IsVisible = false;
        shell.Tree.GetComponent<XsrUiElement>(shell.Content)!.Padding = default;
        shell.Stage.Navigation.Replace(Page);
        foreach (var (name, entity) in _nodes)
        {
            if (!name.StartsWith("Setup", StringComparison.Ordinal)) continue;
            bool button = shell.Tree.GetComponent<XsrUiInput>(entity) is not null;
            Style(name, button ? DesktopUiPalette.CapsuleBackground : XsrUiColor.Transparent,
                button ? DesktopUiPalette.CapsuleForeground : new(52, 61, 74), name == "SetupTitle" ? 32 : 15, name == "SetupTitle" ? 600 : 400);
            if (shell.Tree.GetComponent<XsrUiText>(entity) is { } text) { text.MaxLines = 0; text.TrimOverflow = true; }
            shell.Tree.GetComponent<XsrUiVisualStyle>(entity)!.WrapText = !button;
        }
        Style("SetupStep", XsrUiColor.Transparent, new(122, 138, 153), 13);
        Style("SetupDescription", XsrUiColor.Transparent, new(96, 108, 124), 16);
        Style("SetupError", XsrUiColor.Transparent, new(173, 48, 48), 14);
        Style("SetupNext", new(11, 91, 203), new(255, 255, 255), 15, 600);
        shell.Tree.SetComponent(_nodes["SetupTitle"], new XsrUiTransition());
        shell.Tree.SetComponent(_nodes["SetupDescription"], new XsrUiTransition());
        _intents.IntentEmitted += OnIntent;
        shell.Renderer.FramePreparing += OnFrame;
        Update();
    }

    private void OnIntent(object? sender, DesktopUiIntentEventArgs args)
    {
        if (!_disposed && args.Intent.Command.Value.StartsWith("ui.setup.", StringComparison.Ordinal)) _pending.Enqueue(args.Intent.Command.Value);
    }

    private void OnFrame(object? sender, EventArgs args)
    {
        if (_disposed || Completed) return;
        if (_picking is { IsCompleted: true } picked)
        {
            _picking = null;
            if (Step == _pickStep)
            {
                if (picked.IsCompletedSuccessfully && picked.Result is { } directory) _directory = directory;
                else if (picked.IsFaulted) Error("无法打开文件夹选择器，请重试。");
                Update();
            }
        }
        if (_saving is { IsCompleted: true } saved)
        {
            _saving = null;
            if (saved.IsCompletedSuccessfully && saved.Result.IsSuccess)
            { Completed = true; _close(); return; }
            Error(saved.IsCompletedSuccessfully ? saved.Result.Error?.Message ?? "未能保存设置。" : "未能保存设置，请重试。");
            Update();
        }
        while (_pending.TryDequeue(out string? command))
        {
            if (_saving is not null || _picking is not null) continue;
            Error("");
            switch (command)
            {
                case "ui.setup.back" when Step > 0: Step--; break;
                case "ui.setup.next" when Step < 3: Step++; break;
                case "ui.setup.next":
                    if (_runtime.Commands.TryResolve(FirstRunContract.Complete, out var route))
                    {
                        _saving = _runtime.Commands.Dispatch(route, new FirstRunCompleteCommand(_directory, _consent), cancellationToken: _stop.Token).Completion;
                        WakeWhenCompleted(_saving);
                    }
                    break;
                case "ui.setup.browse" when Step == 1 && !_status.LocationLocked:
                    _pickStep = Step; _picking = PickAsync(); WakeWhenCompleted(_picking); break;
                case "ui.setup.private" when Step == 2 && !_status.TelemetryRequired: _consent = false; break;
                case "ui.setup.share" when Step == 2: _consent = true; break;
            }
            Update();
        }
    }

    private async Task<string?> PickAsync() => await _pickDirectory().ConfigureAwait(false);
    private void WakeWhenCompleted(Task task) => _ = task.ContinueWith(_ =>
    {
        if (!_disposed) _store.Publish(_store.Resolve(FirstRunState.Wake), Interlocked.Increment(ref _wake));
    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private void Update()
    {
        string[] labels = ["欢迎", "数据位置", "数据与诊断", "完成"];
        string[] titles = ["欢迎使用 NexaCL", "为数据选择一个位置", "选择诊断信息的共享方式", "准备好了"];
        if (_status.TelemetryRequired) titles[2] = "帮助改进测试版本";
        string[] descriptions = ["先完成几项设置，然后开始你的 Minecraft。",
            "账户、设置、缓存和日志将保存在这里。游戏目录单独管理。" + (_status.LocationLocked ? "\n此位置由环境变量指定。" : "\n更换位置时，请选择一个空文件夹。"),
            "必要遥测始终启用，用于统计应用运行和更新结果。\n可选诊断信息用于用户体验改进计划，包含脱敏错误堆栈、耗时、资源占用、算法指标和功能使用情况，可在设置中关闭。不上传账户、路径或日志正文。",
            "确认以下选择。开始使用后，NexaCL 会自动重新打开。"];
        if (_status.TelemetryRequired) descriptions[2] = "必要遥测始终启用。CI、Alpha 和 Beta 还必须启用诊断信息，用于用户体验改进计划。\n诊断包含脱敏错误堆栈、耗时、资源占用、算法指标及功能使用情况，不上传账户、路径或日志正文。";
        SetText("SetupStep", $"{Step + 1} / 4 · {labels[Step]}"); SetText("SetupTitle", titles[Step]); SetText("SetupDescription", descriptions[Step]);
        SetText("SetupPath", _directory); SetText("SetupSummary", $"数据位置\n{_directory}\n\n必要遥测：已启用\n诊断信息：{(_consent ? "已启用" : "已关闭")}");
        Show("SetupDirectory", Step == 1); Show("SetupConsent", Step == 2); Show("SetupSummary", Step == 3); Show("SetupBack", Step > 0);
        Show("SetupBrowse", !_status.LocationLocked);
        Show("SetupPrivate", !_status.TelemetryRequired);
        SetText("SetupPrivate", (!_consent ? "✓  " : "    ") + "仅共享必要遥测");
        SetText("SetupShare", (_consent ? "✓  " : "    ") + "同时共享诊断信息");
        foreach (string key in new[] { "SetupPrivate", "SetupShare" })
            Style(key, ((key == "SetupShare") == _consent) ? DesktopUiPalette.CapsuleBackground : new(243, 246, 250), new(52, 61, 74), 15);
        SetText("SetupNext", _saving is not null ? "正在保存…" : Step == 3 ? "开始使用" : "继续");
        foreach (string key in new[] { "SetupBack", "SetupNext", "SetupBrowse", "SetupPrivate", "SetupShare" })
            _shell.Tree.GetComponent<XsrUiInput>(_nodes[key])!.Enabled = _saving is null && _picking is null;
        if (_status.TelemetryRequired)
        {
            SetText("SetupShare", "✓  测试版本必须启用");
            _shell.Tree.GetComponent<XsrUiInput>(_nodes["SetupShare"])!.Enabled = false;
        }
        foreach (string key in new[] { "SetupTitle", "SetupDescription" })
            _shell.Tree.GetComponent<XsrUiTransition>(_nodes[key])!.Key = Step.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _shell.Tree.MarkDirty(Page, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }
    private void SetText(string key, string value)
    {
        var node = _nodes[key]; _shell.Tree.GetComponent<XsrUiText>(node)!.Content = value;
        if (_shell.Tree.GetComponent<XsrUiSemantic>(node) is { } semantic) semantic.Label = value;
        _shell.Tree.MarkDirty(node, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }
    private void Show(string key, bool value) => _shell.Tree.GetComponent<XsrUiElement>(_nodes[key])!.IsVisible = value;
    private void Error(string message) { SetText("SetupError", message); Show("SetupError", message.Length > 0); }
    private void Style(string key, XsrUiColor background, XsrUiColor foreground, double size, double weight = 400) =>
        _shell.Tree.SetComponent(_nodes[key], new XsrUiVisualStyle
        {
            Background = background,
            Foreground = foreground,
            FontSize = size,
            FontWeight = weight,
            TextAlignment = _shell.Tree.GetComponent<XsrUiInput>(_nodes[key]) is null ? XsrUiTextAlignment.Start : XsrUiTextAlignment.Center,
            WrapText = _shell.Tree.GetComponent<XsrUiInput>(_nodes[key]) is null,
            CornerRadius = key == "SetupNext" ? 22 : 10,
            Hover = DesktopUiPalette.CapsuleHover,
            Surface = XsrUiSurfaceKind.Solid
        });
    public void Dispose()
    {
        _disposed = true; _stop.Cancel();
        _intents.IntentEmitted -= OnIntent; _shell.Renderer.FramePreparing -= OnFrame;
    }
}

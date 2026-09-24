using Nexa.Services.Tasks;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Ui;

/// <summary>Desktop-owned presentation cells for the task bubble.</summary>
internal static class TaskBubbleState
{
    internal const string OwnerName = "Nexa.Desktop.TaskBubble";

    /// <summary>Wake revision: timer and controller threads ask for a frame through this cell.</summary>
    public static readonly XsrSemanticId Revision = XsrSemanticId.Parse("ui.tasks.bubble.revision");

    public static void DeclareState(XsrStateStoreBuilder builder) => builder.Cell<long>(Revision, OwnerName);
}

/// <summary>
/// Render-thread projection of the task center summary into the bottom-right progress
/// bubble. The compact shell overlay shows task count and a fine progress track, and yields
/// while the task page is open. All tree mutations happen at
/// <see cref="XsrUiRenderer.FramePreparing"/>; worker threads only publish the wake cell.
/// </summary>
internal sealed class DesktopTaskBubblePresenter : IDisposable
{
    private const double BubbleSize = 48;
    private const double BubbleWidth = 208;
    private const double DockInset = 18;
    private static readonly TimeSpan ExitSettle = TimeSpan.FromMilliseconds(360);
    private static readonly XsrSemanticId OpenCommand = XsrSemanticId.Parse("ui.tasks.open");

    private readonly XsrUiShell _shell;
    private readonly XsrStateStore _store;
    private readonly XsrStateId _summaryId;
    private readonly XsrStateId _wakeState;
    private readonly XsrUiEntityId _root;
    private readonly XsrUiEntityId _fill;
    private readonly XsrUiEntityId _label;
    private readonly XsrUiEntityId _details;
    private readonly XsrUiEntityId _launch;
    private readonly XsrUiOverlayMotion _motion;
    private readonly TimeProvider _timeProvider;
    private long _wakeRevision;
    private volatile bool _exitPending;
    private ITimer? _exitTimer;
    private bool _pageVisible;
    private bool _closing;
    private int _lastAnnouncedPercent = -1;
    private double _lastTarget = -1d;
    private bool _disposed;

    public DesktopTaskBubblePresenter(XsrUiShell shell, XsrStateStore store, TimeProvider? timeProvider = null)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _summaryId = store.Resolve(TaskCenterStateContract.SummaryKey);
        _wakeState = store.Resolve(TaskBubbleState.Revision);

        // Children of a plain element overlap the full content rect in attach order, so the
        // track sits below the caption beside the icon.
        _root = shell.Tree.Create("task-bubble");
        shell.Tree.SetComponent(_root, new XsrUiElement
        {
            Width = BubbleWidth,
            Height = BubbleSize,
            Margin = new XsrUiThickness(0, 0, DockInset, DockInset),
            HorizontalAlignment = XsrUiAlignment.End,
            VerticalAlignment = XsrUiAlignment.End,
            IsVisible = false,
        });
        shell.Tree.SetComponent(_root, new XsrUiInput { Focusable = true, Clickable = true });
        shell.Tree.SetComponent(_root, new XsrUiCommandBinding(OpenCommand));
        shell.Tree.SetComponent(_root, new XsrUiSemantic(XsrUiSemanticRole.Button, "打开任务中心"));
        DesktopBubbleLayout.Register(shell, _root, 1);
        _motion = shell.Tree.GetComponent<XsrUiOverlayMotion>(_root)!;
        shell.Tree.SetComponent(_root, _motion);
        shell.Tree.SetComponent(_root, DesktopBubbleLayout.Style());

        _details = shell.Tree.Create("task-bubble-details");
        shell.Tree.Attach(_details, _root);
        shell.Tree.SetComponent(_details, new XsrUiElement { IsVisible = false });
        _launch = CreateDock("launch-bubble", 208, 48, 18, "ui.launch.restore", "返回正在启动");
        shell.Tree.SetComponent(_launch, new XsrUiImage("lucide/play"));
        shell.Tree.SetComponent(_launch, new XsrUiText("返回正在启动"));
        XsrUiEntityId track = shell.Tree.Create("task-bubble-track");
        shell.Tree.Attach(track, _details);
        shell.Tree.SetComponent(track, new XsrUiElement { Width = 136, Height = 3, Margin = new XsrUiThickness(16, 35, 0, 0), HorizontalAlignment = XsrUiAlignment.Start, VerticalAlignment = XsrUiAlignment.Start });
        shell.Tree.SetComponent(track, Style(new(220, 230, 244), XsrUiColor.Transparent, XsrUiColor.Transparent, 1.5));
        _fill = shell.Tree.Create("task-bubble-fill");
        shell.Tree.Attach(_fill, track);
        shell.Tree.SetComponent(_fill, new XsrUiElement());
        shell.Tree.SetComponent(_fill, new XsrUiProgress
        {
            Anchor = XsrUiProgressFillAnchor.Leading,
        });
        shell.Tree.SetComponent(_fill, Style(
            background: new XsrUiColor(32, 110, 224),
            foreground: XsrUiColor.Transparent,
            border: XsrUiColor.Transparent,
            cornerRadius: 1.5));

        shell.Tree.SetComponent(_root, new XsrUiImage("lucide/list-checks"));
        _label = _root;
        shell.Tree.SetComponent(_label, new XsrUiText("任务"));
        shell.Stage.Show(_root);
        _shell.Renderer.FramePreparing += OnFramePreparing;
    }

    private XsrUiEntityId CreateDock(string name, double width, double height, double bottom, string command, string label)
    {
        var entity = _shell.Tree.Create(name);
        _shell.Tree.SetComponent(entity, new XsrUiElement { Width = width, Height = height, HorizontalAlignment = XsrUiAlignment.End, VerticalAlignment = XsrUiAlignment.End, Margin = new(0, 0, DockInset, bottom), IsVisible = false });
        _shell.Tree.SetComponent(entity, new XsrUiInput { Focusable = true, Clickable = true });
        _shell.Tree.SetComponent(entity, new XsrUiCommandBinding(XsrSemanticId.Parse(command)));
        _shell.Tree.SetComponent(entity, new XsrUiSemantic(XsrUiSemanticRole.Button, label));
        _shell.Tree.SetComponent(entity, DesktopBubbleLayout.Style());
        DesktopBubbleLayout.Register(_shell, entity, 0);
        _shell.Stage.Show(entity);
        return entity;
    }
    internal XsrUiEntityId Root => _root;

    internal XsrUiEntityId FillEntity => _fill;

    /// <summary>Called by the task center controller: the bubble yields while the page is open.</summary>
    public void SetPageVisible(bool visible)
    {
        if (_pageVisible == visible)
        {
            return;
        }

        _pageVisible = visible;
        RequestFrame();
    }

    private void RequestFrame()
    {
        if (_disposed)
        {
            return;
        }

        long revision = Interlocked.Increment(ref _wakeRevision);
        try
        {
            _store.Publish(_wakeState, revision);
        }
        catch (ObjectDisposedException)
        {
            // Host teardown won the race with the controller thread.
        }
    }

    private void OnFramePreparing(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (_exitPending)
        {
            _exitPending = false;
            _closing = false;
            _motion.IsClosing = false;
            SetVisible(false);
            SetEnabled(true);
        }

        TaskCenterSummary summary =
            (TaskCenterSummary?)_store.ReadAppliedValue(_summaryId) ?? new TaskCenterSummary(0, 0, 0, 0, 0);
        Reconcile(summary);
        DesktopBubbleLayout.Arrange(_shell);
    }

    private void Reconcile(TaskCenterSummary summary)
    {
        bool wanted = summary.VisibleCount > 0 && !_pageVisible;
        bool launching = _store.TryResolve(LaunchPageState.LaunchingVisibleKey, out var launchState)
            && _store.ReadAppliedValue(launchState) is true;
        DesktopBubbleLayout.SetVisible(_shell, _store, _launch, launching);
        var rootInput = _shell.Tree.GetComponent<XsrUiInput>(_root)!;
        bool expanded = wanted && (rootInput.IsHovered || rootInput.IsFocused);
        bool showDetails = expanded && (rootInput.CapsuleExpansionProgress > .95 || _shell.Renderer.ReducedMotion);
        var details = _shell.Tree.GetComponent<XsrUiElement>(_details)!;
        if (details.IsVisible != showDetails)
        {
            details.IsVisible = showDetails;
            _shell.Tree.MarkDirty(_root, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
        }
        if (wanted && _closing)
        {
            _exitTimer?.Dispose(); _exitTimer = null;
            _exitPending = false; _closing = false; _motion.IsClosing = false;
            SetEnabled(true);
            _shell.Tree.MarkDirty(_root, XsrUiDirtyKinds.Paint);
        }
        string caption = summary.ActiveCount > 0 ? $"{summary.ActiveCount} 项任务 · {(int)Math.Round(summary.Progress * 100)}%" : $"{summary.VisibleCount} 项任务记录";
        var label = _shell.Tree.GetComponent<XsrUiText>(_label)!;
        if (label.Content != caption) { label.Content = caption; _shell.Tree.MarkDirty(_label, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint); }
        if (wanted && !_closing && !IsVisible(_root))
        {
            SetVisible(true);
        }
        else if (!wanted && IsVisible(_root) && !_closing)
        {
            BeginClose();
        }

        // With no active task the bubble reads as filled (completion is acknowledged by the
        // user on the page, not by a timeout); with active work it follows aggregated progress.
        // Only touch the tree when the target actually moved: FramePreparing fires every frame,
        // and an unconditional MarkDirty here keeps the whole tree perpetually dirty, which
        // spins the render loop and freezes the UI.
        double target = summary.ActiveCount > 0 ? Math.Clamp(summary.Progress, 0d, 1d) : 1d;
        if (Math.Abs(target - _lastTarget) > 0.00005
            && _shell.Tree.GetComponent<XsrUiProgress>(_fill) is { } fill)
        {
            _lastTarget = target;
            fill.SetTarget(target);
            _shell.Tree.MarkDirty(_fill, XsrUiDirtyKinds.Layout);
        }

        int percent = (int)Math.Round(target * 100d);
        if (percent != _lastAnnouncedPercent
            && _shell.Tree.GetComponent<XsrUiSemantic>(_root) is { } semantic)
        {
            _lastAnnouncedPercent = percent;
            semantic.Label = summary.ActiveCount > 0
                ? $"任务进行中 {percent}%，打开任务中心"
                : "查看任务记录，打开任务中心";
        }
    }

    private void BeginClose()
    {
        _closing = true;
        _motion.IsClosing = true;
        SetEnabled(false);
        _shell.Tree.MarkDirty(_root, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
        if (_shell.Renderer.ReducedMotion)
        {
            _exitPending = false;
            _closing = false;
            _motion.IsClosing = false;
            SetVisible(false);
            SetEnabled(true);
            return;
        }

        _exitTimer?.Dispose();
        _exitTimer = _timeProvider.CreateTimer(
            static state =>
            {
                DesktopTaskBubblePresenter owner = (DesktopTaskBubblePresenter)state!;
                owner._exitPending = true;
                owner.RequestFrame();
            },
            this,
            ExitSettle,
            Timeout.InfiniteTimeSpan);
    }

    private void SetVisible(bool visible)
    {
        XsrUiElement element = _shell.Tree.GetComponent<XsrUiElement>(_root) ?? new XsrUiElement();
        if (element.IsVisible == visible)
        {
            return;
        }

        element.IsVisible = visible;
        _shell.Tree.SetComponent(_root, element);
        _shell.Tree.MarkDirty(_root, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }

    private void SetEnabled(bool enabled)
    {
        if (_shell.Tree.GetComponent<XsrUiInput>(_root) is { } input)
        {
            input.Enabled = enabled;
        }
    }

    private bool IsVisible(XsrUiEntityId entity) =>
        (_shell.Tree.GetComponent<XsrUiElement>(entity) ?? new XsrUiElement()).IsVisible;

    private static XsrUiVisualStyle Style(
        XsrUiColor background, XsrUiColor foreground, XsrUiColor border, double cornerRadius,
        double borderWidth = 0, XsrUiColor hover = default) => new()
        {
            Surface = background.Alpha == 0 ? XsrUiSurfaceKind.None : XsrUiSurfaceKind.Solid,
            Background = background,
            Foreground = foreground,
            Border = border,
            BorderWidth = borderWidth,
            Hover = hover,
            CornerRadius = cornerRadius,
        };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shell.Renderer.FramePreparing -= OnFramePreparing;
        _exitTimer?.Dispose();
        _shell.Tree.Destroy(_details);
        _shell.Tree.Destroy(_launch);
        if (_shell.Tree.IsAlive(_root))
        {
            _shell.Tree.Destroy(_root);
        }
    }
}

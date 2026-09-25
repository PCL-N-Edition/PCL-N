using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using Nexa.Pxml;
using Nexa.Services.Composition;
using Nexa.Services.Tasks;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Ui;

/// <summary>
/// Owns the task center page: opening it from the bubble (or anywhere), reconciling one card
/// per task entry at frame time, and dispatching card intents through the typed task routes.
/// The page is pushed onto the shell navigation stack, so the shared back affordance pops it
/// and the bubble reclaims the corner as soon as the page leaves the stage.
/// </summary>
internal sealed class TaskCenterController : IDisposable
{
    private const string PageResource = "Ui.TaskCenterPage.pxml";
    private const string CardResource = "Ui.TaskCard.pxml";

    private static readonly XsrSemanticId DetailsCommand = XsrSemanticId.Parse("ui.tasks.card.details");
    private readonly ConcurrentQueue<string> _detailToggles = new();
    private static readonly XsrSemanticId OpenCommand = XsrSemanticId.Parse("ui.tasks.open");
    private static readonly XsrSemanticId CardCancelCommand = XsrSemanticId.Parse("ui.tasks.card.cancel");
    private static readonly XsrSemanticId CardDismissCommand = XsrSemanticId.Parse("ui.tasks.card.dismiss");
    private static readonly XsrSemanticId ClearFinishedCommand = XsrSemanticId.Parse("ui.tasks.clear-finished");

    private static readonly XsrUiColor CardBackground = new(255, 255, 255);
    private static readonly XsrUiColor CardBorder = new(228, 233, 240);
    private static readonly XsrUiColor TitleInk = new(40, 48, 60);
    private static readonly XsrUiColor SecondaryInk = new(112, 124, 138);
    private static readonly XsrUiColor RunningAccent = new(19, 112, 243);
    private static readonly XsrUiColor FinishedAccent = new(34, 128, 84);
    private static readonly XsrUiColor FailedAccent = new(196, 64, 54);
    private static readonly XsrUiColor CanceledAccent = new(112, 124, 138);
    private static readonly XsrUiColor HoverTint = new(19, 112, 243, 22);

    private readonly XsrUiShell _shell;
    private readonly DesktopUiIntentSink _intents;
    private readonly XsrCommandRouter _commands;
    private readonly XsrStateStore _store;
    private readonly DesktopTaskBubblePresenter _bubble;
    private readonly XsrStateId _entriesId;
    private readonly XsrStateId _summaryId;
    private readonly XsrUiEntityId _page;
    private readonly Dictionary<string, XsrUiEntityId> _entities;
    private readonly PxmlHostIr _cardTemplate;
    private readonly Dictionary<string, PresentedCard> _cards = [];
    private readonly ConcurrentQueue<(string TaskId, XsrSemanticId Command)> _dispatches = [];
    private bool _dispatchedThisFrame;
    private bool _staged;
    private bool _disposed;

    private sealed class PresentedCard(
        XsrUiEntityId root,
        XsrUiEntityId icon,
        XsrUiEntityId title,
        XsrUiEntityId stage,
        XsrUiEntityId percent,
        XsrUiEntityId fill,
        XsrUiEntityId files,
        XsrUiEntityId speed,
        XsrUiEntityId error,
        XsrUiEntityId action,
        XsrUiEntityId steps,
        List<XsrUiEntityId> stepRows)
    {
        public bool Expanded { get; set; }
        public XsrUiEntityId Details { get; init; }
        public TaskCenterEntry? Value { get; set; }
        public XsrUiEntityId Root { get; } = root;
        public XsrUiEntityId Icon { get; } = icon;
        public XsrUiEntityId Title { get; } = title;
        public XsrUiEntityId Stage { get; } = stage;
        public XsrUiEntityId Percent { get; } = percent;
        public XsrUiEntityId Fill { get; } = fill;
        public XsrUiEntityId Files { get; } = files;
        public XsrUiEntityId Speed { get; } = speed;
        public XsrUiEntityId Error { get; } = error;
        public XsrUiEntityId Action { get; } = action;
        public XsrUiEntityId Steps { get; } = steps;
        public List<XsrUiEntityId> StepRows { get; } = stepRows;
        public bool HasSteps { get; set; }
        public double LastFillTarget { get; set; } = -1d;
    }

    public TaskCenterController(
        XsrUiShell shell,
        DesktopUiIntentSink intents,
        XsrCommandRouter commands,
        XsrStateStore store,
        DesktopTaskBubblePresenter bubble)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _intents = intents ?? throw new ArgumentNullException(nameof(intents));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _bubble = bubble ?? throw new ArgumentNullException(nameof(bubble));
        _entriesId = store.Resolve(TaskCenterStateContract.EntriesKey);
        _summaryId = store.Resolve(TaskCenterStateContract.SummaryKey);
        _cardTemplate = LoadTemplate(CardResource);
        (_page, _entities) = LoadPage();

        _intents.IntentEmitted += OnIntentEmitted;
        _shell.Renderer.FramePreparing += OnFramePreparing;
    }

    internal XsrUiEntityId Page => _page;

    internal bool IsStaged => _staged;

    internal XsrUiEntityId CardAction(string taskId) =>
        _cards.TryGetValue(taskId, out PresentedCard? card) ? card.Action : default;

    private (XsrUiEntityId Page, Dictionary<string, XsrUiEntityId> Entities) LoadPage()
    {
        PxmlHostIr ir = PxmlCompiler.Compile(PxmlParser.Parse(ReadEmbeddedResource(PageResource)));
        XsrUiEntityId host = _shell.Tree.Create("task-center-page-loader");
        XsrUiEntityId page = PxmlUiLoader.Load(ir, _shell.Tree, _store, host);
        _shell.Tree.Detach(page);
        _shell.Tree.Destroy(host);
        Dictionary<string, XsrUiEntityId> entities = [];
        _shell.Tree.Walk(page, entity =>
        {
            string key = _shell.Tree.Name(entity);
            if (key.Length > 0)
            {
                entities[key] = entity;
            }

            return true;
        });
        Style(entities["TaskCenterTitle"], XsrUiColor.Transparent, TitleInk, XsrUiColor.Transparent, 0, fontSize: 28, fontWeight: 650);
        Style(entities["TaskCenterSummary"], XsrUiColor.Transparent, SecondaryInk, XsrUiColor.Transparent, 0, fontSize: 14);
        Style(entities["TaskCenterClear"], new(239, 244, 251), new(48, 87, 145), XsrUiColor.Transparent, 18, hover: HoverTint, fontSize: 13, fontWeight: 550);
        _shell.Tree.GetComponent<XsrUiVisualStyle>(entities["TaskCenterClear"])!.TextAlignment = XsrUiTextAlignment.Center;
        Style(entities["TaskCenterEmptyTitle"], XsrUiColor.Transparent, TitleInk, XsrUiColor.Transparent, 0, fontSize: 18, fontWeight: 600);
        Style(entities["TaskCenterEmptyHint"], XsrUiColor.Transparent, SecondaryInk, XsrUiColor.Transparent, 0, fontSize: 13);
        Style(entities["TaskCenterEmptyIcon"], XsrUiColor.Transparent, new(144, 159, 181), XsrUiColor.Transparent, 0);
        _shell.Tree.SetComponent(entities["TaskCenterList"], new XsrUiScrollGesture());
        return (page, entities);
    }

    private void OnIntentEmitted(object? sender, DesktopUiIntentEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (e.Intent.Command == OpenCommand)
        {
            Open();
            return;
        }

        if (e.Intent.Command == ClearFinishedCommand)
        {
            Dispatch(TaskCenterRoutes.ClearFinished, new TaskCenterClearCommand());
            return;
        }

        if (e.Intent.Command != CardCancelCommand && e.Intent.Command != CardDismissCommand && e.Intent.Command != DetailsCommand)
        {
            return;
        }

        // The card carries the entry identity: walk from the source button to its card root.
        XsrUiEntityId ancestor = e.Intent.Source;
        while (ancestor.IsAssigned && _shell.Tree.IsAlive(ancestor))
        {
            if (TryFindCardByRoot(ancestor, out PresentedCard? card) && card is not null)
            {
                if (e.Intent.Command == DetailsCommand)
                { _detailToggles.Enqueue(card.Value!.TaskId); return; }
                if (e.Intent.Command == CardCancelCommand)
                {
                    Dispatch(TaskCenterRoutes.Cancel, new TaskCenterCancelCommand(card.Value!.TaskId));
                }
                else
                {
                    Dispatch(TaskCenterRoutes.Dismiss, new TaskCenterDismissCommand(card.Value!.TaskId));
                }

                return;
            }

            ancestor = _shell.Tree.Parent(ancestor);
        }
    }

    private bool TryFindCardByRoot(XsrUiEntityId entity, out PresentedCard? card)
    {
        foreach (PresentedCard candidate in _cards.Values)
        {
            if (candidate.Root.Equals(entity))
            {
                card = candidate;
                return true;
            }
        }

        card = null;
        return false;
    }

    private void Open()
    {
        if (_staged || _disposed)
        {
            return;
        }

        _shell.Stage.Navigation.Push(_page);
        _staged = true;
        _bubble.SetPageVisible(true);
        _shell.Renderer.Focus(_entities["TaskCenterClear"]);
    }

    private void Dispatch<TCommand>(XsrSemanticId route, TCommand command) where TCommand : notnull
    {
        // Intents arrive off the render thread; route them at the next frame alongside the
        // card reconciliation so cancel/dismiss always lands with a fresh view.
        string taskId = command switch
        {
            TaskCenterCancelCommand cancel => cancel.TaskId,
            TaskCenterDismissCommand dismiss => dismiss.TaskId,
            _ => string.Empty,
        };
        _dispatches.Enqueue((taskId, route));
        _dispatchedThisFrame = false;
    }

    private void OnFramePreparing(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        while (_detailToggles.TryDequeue(out string? taskId))
            if (_cards.TryGetValue(taskId, out var card) && card.Value is { } entry)
            { card.Expanded = !card.Expanded; ReconcileSteps(card, entry); }
        DrainDispatches();
        TrackStagedState();
        TaskCenterSummary summary =
            (TaskCenterSummary?)_store.ReadAppliedValue(_summaryId) ?? new TaskCenterSummary(0, 0, 0, 0, 0);
        XsrCollectionSnapshot<TaskCenterEntry> entries = _store.ReadCollection<TaskCenterEntry>(_entriesId);
        ReconcileHeader(summary, entries);
        if (_staged)
        {
            ReconcileCards(entries.Items);
        }
    }

    private void DrainDispatches()
    {
        if (_dispatchedThisFrame)
        {
            return;
        }

        _dispatchedThisFrame = true;
        while (_dispatches.TryDequeue(out (string TaskId, XsrSemanticId Command) pending))
        {
            if (pending.Command == TaskCenterRoutes.Cancel)
            {
                if (_commands.TryResolve(TaskCenterRoutes.Cancel, out XsrCommandId cancelId))
                {
                    _ = _commands.Dispatch(cancelId, new TaskCenterCancelCommand(pending.TaskId));
                }
            }
            else if (pending.Command == TaskCenterRoutes.Dismiss)
            {
                if (_commands.TryResolve(TaskCenterRoutes.Dismiss, out XsrCommandId dismissId))
                {
                    _ = _commands.Dispatch(dismissId, new TaskCenterDismissCommand(pending.TaskId));
                }
            }
            else if (pending.Command == TaskCenterRoutes.ClearFinished)
            {
                if (_commands.TryResolve(TaskCenterRoutes.ClearFinished, out XsrCommandId clearId))
                {
                    _ = _commands.Dispatch(clearId, new TaskCenterClearCommand());
                }
            }
        }
    }

    private void TrackStagedState()
    {
        bool staged = _shell.Stage.Navigation.Current.Equals(_page);
        if (staged == _staged)
        {
            return;
        }

        _staged = staged;
        _bubble.SetPageVisible(staged);
        if (!staged)
        {
            // The page left the stage (back or destination switch); drop its cards so the
            // next open starts from the current entries.
            foreach (PresentedCard card in _cards.Values)
            {
                if (_shell.Tree.IsAlive(card.Root))
                {
                    _shell.Tree.Destroy(card.Root);
                }
            }

            _cards.Clear();
        }
    }

    private void ReconcileHeader(TaskCenterSummary summary, XsrCollectionSnapshot<TaskCenterEntry> entries)
    {
        string header;
        if (summary.VisibleCount == 0)
        {
            header = "暂无任务";
        }
        else if (summary.ActiveCount == 0)
        {
            header = entries.Items.Any(static entry => entry.State == TaskCenterEntryState.Failed)
                ? "有任务未成功"
                : "没有进行中的任务";
        }
        else
        {
            header = string.Create(
                CultureInfo.CurrentCulture,
                $"{summary.ActiveCount} 个任务进行中 · {(int)Math.Round(summary.Progress * 100d)}%");
            if (summary.SpeedBytesPerSecond > 0)
            {
                header += $" · {FormatSpeed(summary.SpeedBytesPerSecond)}";
            }

            if (summary.RemainingFiles > 0)
            {
                header += $" · 剩余 {summary.RemainingFiles} 个文件";
            }
        }

        SetText(_entities["TaskCenterSummary"], header);
        bool hasFinished = entries.Items.Any(static entry => entry.IsTerminal);
        SetVisible(_entities["TaskCenterClear"], hasFinished);
        SetVisible(_entities["TaskCenterEmpty"], summary.VisibleCount == 0);
        SetVisible(_entities["TaskCenterList"], summary.VisibleCount > 0);
    }

    private void ReconcileCards(IReadOnlyList<TaskCenterEntry> entries)
    {
        HashSet<string> requested = [.. entries.Select(static entry => entry.TaskId)];
        foreach (KeyValuePair<string, PresentedCard> pair in _cards.ToArray())
        {
            if (!requested.Contains(pair.Key) && _shell.Tree.IsAlive(pair.Value.Root))
            {
                _shell.Tree.Destroy(pair.Value.Root);
                _cards.Remove(pair.Key);
            }
        }

        foreach (TaskCenterEntry entry in entries)
        {
            if (!_cards.TryGetValue(entry.TaskId, out PresentedCard? card))
            {
                card = CreateCard(entry);
                _cards.Add(entry.TaskId, card);
            }

            UpdateCard(card, entry);
        }
    }

    private PresentedCard CreateCard(TaskCenterEntry entry)
    {
        PxmlIrNode rootNode = _cardTemplate.Root with { Key = $"task-card:{entry.TaskId}" };
        XsrUiEntityId root = PxmlUiLoader.Load(
            new PxmlHostIr(rootNode), _shell.Tree, _store, _entities["TaskCenterList"]);
        Dictionary<string, XsrUiEntityId> entities = [];
        _shell.Tree.Walk(root, entity =>
        {
            string key = _shell.Tree.Name(entity);
            if (key.Length > 0)
            {
                entities[key] = entity;
            }

            return true;
        });

        // The template root was renamed to the per-entry key, so style the root directly.
        Style(root, CardBackground, TitleInk, CardBorder, XsrUiCornerRadii.Surface, borderWidth: 1);
        Style(entities["TaskCardIcon"], XsrUiColor.Transparent, RunningAccent, XsrUiColor.Transparent, 0);
        Style(entities["TaskCardTitle"], XsrUiColor.Transparent, TitleInk, XsrUiColor.Transparent, 0, fontSize: 17, fontWeight: 600);
        Style(entities["TaskCardStage"], XsrUiColor.Transparent, SecondaryInk, XsrUiColor.Transparent, 0, fontSize: 13);
        Style(entities["TaskCardPercent"], XsrUiColor.Transparent, TitleInk, XsrUiColor.Transparent, 0, fontSize: 13, fontWeight: 600);
        Style(entities["TaskCardFill"], RunningAccent, XsrUiColor.Transparent, XsrUiColor.Transparent, 2);
        Style(entities["TaskCardFiles"], XsrUiColor.Transparent, SecondaryInk, XsrUiColor.Transparent, 0, fontSize: 12);
        Style(entities["TaskCardSpeed"], XsrUiColor.Transparent, SecondaryInk, XsrUiColor.Transparent, 0, fontSize: 12);
        Style(entities["TaskCardError"], XsrUiColor.Transparent, FailedAccent, XsrUiColor.Transparent, 0, fontSize: 12);
        Style(entities["TaskCardDetails"], XsrUiColor.Transparent, SecondaryInk, XsrUiColor.Transparent, 6, hover: HoverTint, fontSize: 12);
        Style(entities["TaskCardTrack"], new(236, 240, 246), XsrUiColor.Transparent, XsrUiColor.Transparent, 2);
        _shell.Tree.GetComponent<XsrUiVisualStyle>(entities["TaskCardError"])!.WrapText = true;
        Style(entities["TaskCardAction"], XsrUiColor.Transparent, SecondaryInk, XsrUiColor.Transparent, XsrUiCornerRadii.Pill(30), hover: HoverTint);

        return new PresentedCard(
            root,
            entities["TaskCardIcon"],
            entities["TaskCardTitle"],
            entities["TaskCardStage"],
            entities["TaskCardPercent"],
            entities["TaskCardFill"],
            entities["TaskCardFiles"],
            entities["TaskCardSpeed"],
            entities["TaskCardError"],
            entities["TaskCardAction"],
            entities["TaskCardSteps"],
            [])
        { Details = entities["TaskCardDetails"] };
    }

    private void UpdateCard(PresentedCard card, TaskCenterEntry entry)
    {
        // SetComponent dirties the entity as Structure unconditionally, so restyling per
        // frame spins the render loop while the page is staged (the install-start freeze).
        // Entries are records: an unchanged entry skips the whole restyle.
        if (card.Value == entry)
        {
            return;
        }

        card.Value = entry;
        XsrUiColor accent = entry.State switch
        {
            TaskCenterEntryState.Finished => FinishedAccent,
            TaskCenterEntryState.Failed => FailedAccent,
            TaskCenterEntryState.Canceled or TaskCenterEntryState.Paused => CanceledAccent,
            _ => RunningAccent,
        };
        if (_shell.Tree.GetComponent<XsrUiImage>(card.Icon) is { } icon)
        {
            icon.Source = entry.State switch
            {
                TaskCenterEntryState.Finished => "lucide/circle-check",
                TaskCenterEntryState.Failed => "lucide/circle-x",
                TaskCenterEntryState.Canceled => "lucide/circle-minus",
                TaskCenterEntryState.Paused => "lucide/pause",
                _ => "lucide/loader-circle",
            };
        }

        Style(card.Icon, XsrUiColor.Transparent, accent, XsrUiColor.Transparent, 0, MarkDirty: false);
        Style(card.Fill, accent, XsrUiColor.Transparent, XsrUiColor.Transparent, 2, MarkDirty: false);
        Style(card.Percent, XsrUiColor.Transparent, accent, XsrUiColor.Transparent, 0, fontSize: 13, fontWeight: 600, MarkDirty: false);
        // Same guard as the bubble: FramePreparing fires every frame, so only a moved target
        // may dirty the tree — an unconditional mark spins the render loop.
        double fillTarget = Math.Clamp(entry.Progress, 0d, 1d);
        if (Math.Abs(fillTarget - card.LastFillTarget) > 0.00005
            && _shell.Tree.GetComponent<XsrUiProgress>(card.Fill) is { } fill)
        {
            card.LastFillTarget = fillTarget;
            fill.SetTarget(fillTarget);
            _shell.Tree.MarkDirty(card.Fill, XsrUiDirtyKinds.Layout);
        }
        SetText(card.Title, entry.Title);
        SetText(card.Stage, entry.IsTerminal ? entry.Detail : $"{entry.Stage} · {entry.Detail}");
        SetText(card.Percent, entry.State switch { TaskCenterEntryState.Finished => "已完成", TaskCenterEntryState.Failed => "失败", TaskCenterEntryState.Canceled => "已取消", TaskCenterEntryState.Paused => "已暂停", _ => $"{(int)Math.Round(Math.Clamp(entry.Progress, 0d, 1d) * 100d)}%" });
        SetVisible(_shell.Tree.Parent(card.Fill), !entry.IsTerminal);
        SetVisible(_shell.Tree.Parent(card.Files), entry.TotalFiles > 0 || entry.SpeedBytesPerSecond > 0 && !entry.IsTerminal);
        SetText(card.Files, entry.TotalFiles > 0
            ? string.Create(CultureInfo.CurrentCulture, $"{entry.CompletedFiles}/{entry.TotalFiles} 个文件")
            : string.Empty);
        SetText(card.Speed, entry.SpeedBytesPerSecond > 0 && !entry.IsTerminal
            ? FormatSpeed(entry.SpeedBytesPerSecond)
            : string.Empty);
        SetText(card.Error, entry.ErrorMessage ?? string.Empty);
        SetVisible(card.Error, entry.State == TaskCenterEntryState.Failed && !string.IsNullOrEmpty(entry.ErrorMessage));

        // The trailing action flips between cancel (running) and dismiss (terminal).
        XsrUiSemantic? actionSemantic = _shell.Tree.GetComponent<XsrUiSemantic>(card.Action);
        if (actionSemantic is not null)
        {
            actionSemantic.Label = entry.IsTerminal ? "移除任务" : "取消任务";
        }

        if (_shell.Tree.GetComponent<XsrUiCommandBinding>(card.Action) is { } binding)
        {
            binding.Command = entry.IsTerminal ? CardDismissCommand : CardCancelCommand;
        }

        SetVisible(card.Action, entry.IsTerminal || entry.CanCancel);
        SetEnabled(card.Action, entry.IsTerminal || entry.CanCancel);
        ReconcileSteps(card, entry);
    }

    private void ReconcileSteps(PresentedCard card, TaskCenterEntry entry)
    {
        IReadOnlyList<TaskCenterStep> steps = entry.Steps ?? [];
        bool wantsSteps = steps.Count > 1 && card.Expanded;
        SetVisible(card.Details, steps.Count > 1);
        SetText(card.Details, card.Expanded ? "收起步骤" : "查看步骤");
        _shell.Tree.GetComponent<XsrUiSemantic>(card.Details)!.Label = card.Expanded ? "收起任务步骤" : "展开任务步骤";
        if (card.HasSteps != wantsSteps || wantsSteps && card.StepRows.Count != steps.Count)
        {
            foreach (XsrUiEntityId row in card.StepRows)
            {
                if (_shell.Tree.IsAlive(row))
                {
                    _shell.Tree.Destroy(row);
                }
            }

            card.StepRows.Clear();
            card.HasSteps = wantsSteps;
            for (int i = 0; wantsSteps && i < steps.Count; i++)
            {
                XsrUiEntityId row = _shell.Tree.Create($"task-card-step:{entry.TaskId}:{i}");
                _shell.Tree.Attach(row, card.Steps);
                _shell.Tree.SetComponent(row, new XsrUiElement { Height = 22 });
                _shell.Tree.SetComponent(row, new XsrUiText(string.Empty));
                card.StepRows.Add(row);
            }
        }

        for (int i = 0; i < card.StepRows.Count && i < steps.Count; i++)
        {
            TaskCenterStep step = steps[i];
            XsrUiEntityId row = card.StepRows[i];
            XsrUiColor ink = step.State switch
            {
                TaskCenterEntryState.Finished => FinishedAccent,
                TaskCenterEntryState.Running => RunningAccent,
                TaskCenterEntryState.Failed => FailedAccent,
                _ => CanceledAccent,
            };
            Style(row, XsrUiColor.Transparent, ink, XsrUiColor.Transparent, 0, fontSize: 12, MarkDirty: false);
            SetText(row, step.State switch
            {
                TaskCenterEntryState.Finished => $"✓ {step.Name}",
                TaskCenterEntryState.Running => $"● {step.Name} · {step.Detail}",
                TaskCenterEntryState.Failed => $"✕ {step.Name} · {step.Detail}",
                _ => $"· {step.Name}",
            });
        }

        SetVisible(card.Steps, wantsSteps);
    }

    private void SetText(XsrUiEntityId entity, string content)
    {
        if (_shell.Tree.GetComponent<XsrUiText>(entity) is not { } text)
        {
            return;
        }

        if (text.Content == content)
        {
            return;
        }

        text.Content = content;
        _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }

    private void SetVisible(XsrUiEntityId entity, bool visible)
    {
        XsrUiElement element = _shell.Tree.GetComponent<XsrUiElement>(entity) ?? new XsrUiElement();
        if (element.IsVisible == visible)
        {
            return;
        }

        element.IsVisible = visible;
        _shell.Tree.SetComponent(entity, element);
        _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }

    private void SetEnabled(XsrUiEntityId entity, bool enabled)
    {
        if (_shell.Tree.GetComponent<XsrUiInput>(entity) is { } input)
        {
            input.Enabled = enabled;
        }
    }

    private void Style(
        XsrUiEntityId entity,
        XsrUiColor background,
        XsrUiColor foreground,
        XsrUiColor border,
        double cornerRadius,
        double borderWidth = 0,
        XsrUiColor hover = default,
        double fontSize = 0,
        double fontWeight = 400,
        bool MarkDirty = true)
    {
        _shell.Tree.SetComponent(entity, new XsrUiVisualStyle
        {
            Surface = background.Alpha == 0 ? XsrUiSurfaceKind.None : XsrUiSurfaceKind.Solid,
            Background = background,
            Foreground = foreground,
            Border = border,
            BorderWidth = borderWidth,
            Hover = hover,
            CornerRadius = cornerRadius,
            FontSize = fontSize,
            FontWeight = fontWeight,
        });
        if (MarkDirty)
        {
            _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
        }
    }

    internal static string FormatSpeed(long bytesPerSecond)
    {
        if (bytesPerSecond <= 0)
        {
            return "0 B/s";
        }

        double value = bytesPerSecond;
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s"];
        int unit = 0;
        while (value >= 1024d && unit < units.Length - 1)
        {
            value /= 1024d;
            unit++;
        }

        return string.Create(
            CultureInfo.CurrentCulture,
            $"{value.ToString(unit == 0 ? "F0" : "F1", CultureInfo.CurrentCulture)} {units[unit]}");
    }

    private static PxmlHostIr LoadTemplate(string suffix)
    {
        Assembly assembly = typeof(TaskCenterController).Assembly;
        string resource = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(suffix, StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing embedded task template '{suffix}'.");
        using StreamReader reader = new(stream);
        return PxmlCompiler.Compile(PxmlParser.Parse(reader.ReadToEnd()));
    }

    private static string ReadEmbeddedResource(string suffix)
    {
        Assembly assembly = typeof(TaskCenterController).Assembly;
        string resource = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(suffix, StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing embedded resource '{suffix}'.");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _intents.IntentEmitted -= OnIntentEmitted;
        _shell.Renderer.FramePreparing -= OnFramePreparing;
        foreach (PresentedCard card in _cards.Values)
        {
            if (_shell.Tree.IsAlive(card.Root))
            {
                _shell.Tree.Destroy(card.Root);
            }
        }

        _cards.Clear();
        if (_shell.Tree.IsAlive(_page))
        {
            _shell.Tree.Destroy(_page);
        }
    }
}

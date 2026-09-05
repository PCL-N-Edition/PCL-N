using System.Collections.Concurrent;
using System.Reflection;
using PCL.Pxml;
using PCL.UI.Next;
using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Desktop.Ui;

/// <summary>
/// Render-thread projection of <see cref="DesktopFeedbackService"/> into the shared UI.Next
/// stage. Worker/timer callbacks only publish a wake revision; PXML entities are reconciled at
/// <see cref="XsrUiRenderer.FramePreparing"/>.
/// </summary>
internal sealed class DesktopFeedbackPresenter : IDisposable
{
    private const string NotificationResource = "Ui.Notification.pxml";
    private const string DialogResource = "Ui.Dialog.pxml";
    private static readonly TimeSpan ExitSettle = TimeSpan.FromMilliseconds(360);
    private static readonly XsrSemanticId NotificationDismiss =
        XsrSemanticId.Parse("ui.feedback.notification.dismiss");
    private static readonly XsrSemanticId NotificationOpen =
        XsrSemanticId.Parse("ui.feedback.notification.open");
    private static readonly XsrSemanticId DialogAccept =
        XsrSemanticId.Parse("ui.feedback.dialog.accept");
    private static readonly XsrSemanticId DialogCancel =
        XsrSemanticId.Parse("ui.feedback.dialog.cancel");

    private sealed class PresentedNotification(
        DesktopNotification value,
        XsrUiEntityId root,
        XsrUiEntityId close,
        XsrUiOverlayMotion motion)
    {
        public DesktopNotification Value { get; } = value;
        public XsrUiEntityId Root { get; } = root;
        public XsrUiEntityId Close { get; } = close;
        public XsrUiOverlayMotion Motion { get; } = motion;
        public bool IsClosing { get; set; }
        public ITimer? Cleanup { get; set; }
    }

    private sealed class PresentedDialog(
        DesktopDialog value,
        XsrUiEntityId root,
        XsrUiEntityId card,
        XsrUiEntityId accept,
        XsrUiEntityId cancel,
        XsrUiOverlayMotion scrimMotion,
        XsrUiOverlayMotion cardMotion,
        XsrUiEntityId previousFocus,
        bool previousFocusVisible)
    {
        public DesktopDialog Value { get; set; } = value;
        public XsrUiEntityId Root { get; } = root;
        public XsrUiEntityId Card { get; } = card;
        public XsrUiEntityId Accept { get; } = accept;
        public XsrUiEntityId Cancel { get; } = cancel;
        public XsrUiOverlayMotion ScrimMotion { get; } = scrimMotion;
        public XsrUiOverlayMotion CardMotion { get; } = cardMotion;
        public XsrUiEntityId PreviousFocus { get; } = previousFocus;
        public bool PreviousFocusVisible { get; } = previousFocusVisible;
        public bool IsClosing { get; set; }
        public ITimer? Cleanup { get; set; }
    }

    private readonly XsrUiShell _shell;
    private readonly DesktopUiIntentSink _intents;
    private readonly DesktopFeedbackService _service;
    private readonly XsrStateStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly PxmlHostIr _notificationTemplate;
    private readonly PxmlHostIr _dialogTemplate;
    private readonly XsrUiEntityId _notificationHost;
    private readonly Dictionary<Guid, PresentedNotification> _notifications = [];
    private readonly Dictionary<XsrUiEntityId, Guid> _notificationRoots = [];
    private readonly ConcurrentQueue<Guid> _notificationCleanup = new();
    private readonly ConcurrentQueue<Guid> _dialogCleanup = new();
    private readonly XsrStateId _wakeState;
    private PresentedDialog? _dialog;
    private long _wakeRevision;
    private bool _disposed;

    public DesktopFeedbackPresenter(
        XsrUiShell shell,
        DesktopUiIntentSink intents,
        DesktopFeedbackService service,
        XsrStateStore store,
        TimeProvider? timeProvider = null)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _intents = intents ?? throw new ArgumentNullException(nameof(intents));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _notificationTemplate = LoadTemplate(NotificationResource);
        _dialogTemplate = LoadTemplate(DialogResource);
        _wakeState = store.Resolve(DesktopFeedbackState.Revision);

        _notificationHost = shell.Tree.Create("notification-host");
        shell.Tree.SetComponent(_notificationHost, new XsrUiElement
        {
            Width = 360,
            MaxHeight = 440,
            Margin = new XsrUiThickness(XsrUiShell.ExpandedRailWidth + 18, 18, 18, 18),
            HorizontalAlignment = XsrUiAlignment.Start,
            VerticalAlignment = XsrUiAlignment.End,
            IsVisible = false,
        });
        shell.Tree.SetComponent(_notificationHost, new XsrUiStackPanel(XsrUiOrientation.Vertical)
        {
            Spacing = 10,
        });
        shell.Tree.SetComponent(_notificationHost, new XsrUiScroll
        {
            StickToEnd = true,
        });
        shell.Stage.Show(_notificationHost);

        _service.Changed += OnServiceChanged;
        _intents.IntentEmitted += OnIntent;
        _shell.Renderer.FramePreparing += OnFramePreparing;
        RequestFrame();
    }

    internal XsrUiEntityId NotificationHost => _notificationHost;

    internal int PresentedNotificationCount => _notifications.Count;

    internal XsrUiEntityId PresentedDialogRoot => _dialog?.Root ?? default;

    private void OnServiceChanged(object? sender, EventArgs e) => RequestFrame();

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
            // Host teardown won the race with a timer callback.
        }
    }

    private void OnFramePreparing(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        DrainCleanup();
        DesktopFeedbackSnapshot snapshot = _service.Snapshot();
        ReconcileNotifications(snapshot.Notifications);
        ReconcileDialog(snapshot.Dialog);
    }

    private void ReconcileNotifications(IReadOnlyList<DesktopNotification> requested)
    {
        HashSet<Guid> requestedIds = [.. requested.Select(notification => notification.Id)];
        foreach (PresentedNotification current in _notifications.Values.ToArray())
        {
            if (!requestedIds.Contains(current.Value.Id) && !current.IsClosing)
            {
                BeginNotificationClose(current);
            }
        }

        bool added = false;
        foreach (DesktopNotification notification in requested)
        {
            if (_notifications.ContainsKey(notification.Id))
            {
                continue;
            }

            PresentedNotification presented = CreateNotification(notification);
            _notifications.Add(notification.Id, presented);
            _notificationRoots.Add(presented.Root, notification.Id);
            added = true;
        }

        SetNotificationHostVisible(_notifications.Count > 0);

        if (added && _shell.Tree.GetComponent<XsrUiScroll>(_notificationHost) is { } scroll)
        {
            // New notifications belong at the visible bottom edge; users can still wheel back
            // through older permanent errors when the bounded stack overflows.
            scroll.OffsetY = double.PositiveInfinity;
            _shell.Tree.MarkDirty(_notificationHost, XsrUiDirtyKinds.Layout);
        }
    }

    private PresentedNotification CreateNotification(DesktopNotification notification)
    {
        PxmlIrNode rootNode = _notificationTemplate.Root with
        {
            Key = $"notification:{notification.Id:N}",
        };
        XsrUiEntityId root = PxmlUiLoader.Load(
            new PxmlHostIr(rootNode),
            _shell.Tree,
            _store,
            _notificationHost);
        Dictionary<string, XsrUiEntityId> entities = Index(root);
        string level = notification.Level.ToString();
        string icon = notification.Level switch
        {
            DesktopNotificationLevel.Info => "lucide/info",
            DesktopNotificationLevel.Warn => "lucide/triangle-alert",
            DesktopNotificationLevel.Error => "lucide/circle-x",
            _ => throw new ArgumentOutOfRangeException(nameof(notification)),
        };
        (XsrUiColor accent, XsrUiColor background, XsrUiColor border, XsrUiColor hover) =
            NotificationPalette(notification.Level);

        XsrUiText levelText = _shell.Tree.GetComponent<XsrUiText>(entities["NotificationLevel"])!;
        levelText.Content = level;
        XsrUiText messageText = _shell.Tree.GetComponent<XsrUiText>(entities["NotificationMessage"])!;
        messageText.Content = notification.Message;
        messageText.MaxLines = 2;
        messageText.TrimOverflow = true;
        _shell.Tree.GetComponent<XsrUiImage>(entities["NotificationIcon"])!.Source = icon;
        // The status root announces the complete level and message once. Its decorative icon
        // and visible copy stay out of the native accessibility tree to avoid duplicate speech.
        _shell.Tree.SetComponent<XsrUiSemantic>(entities["NotificationIcon"], null);
        _shell.Tree.SetComponent<XsrUiSemantic>(entities["NotificationLevel"], null);
        _shell.Tree.SetComponent<XsrUiSemantic>(entities["NotificationMessage"], null);
        _shell.Tree.GetComponent<XsrUiSemantic>(entities["NotificationClose"])!.Label = $"关闭 {level} 通知";
        _shell.Tree.GetComponent<XsrUiSemantic>(root)!.Label =
            $"{level}：{notification.Message}。按下可查看完整内容";
        _shell.Tree.SetComponent(root, new XsrUiInput
        {
            Focusable = true,
            Clickable = true,
        });
        _shell.Tree.SetComponent(root, new XsrUiCommandBinding(NotificationOpen));
        _shell.Tree.SetComponent(root, new XsrUiLiveRegion(
            notification.Level == DesktopNotificationLevel.Info
                ? XsrUiLiveSetting.Polite
                : XsrUiLiveSetting.Assertive));
        XsrUiOverlayMotion motion = new(XsrUiOverlayMotionKind.Notification);
        _shell.Tree.SetComponent(root, motion);

        SetStyle(root, background, new XsrUiColor(52, 61, 74), border,
            XsrUiCornerRadii.Surface, borderWidth: 1, hover: hover);
        SetStyle(entities["NotificationAccent"], accent, accent, XsrUiColor.Transparent,
            XsrUiCornerRadii.Pill(4));
        SetStyle(entities["NotificationIcon"], XsrUiColor.Transparent, accent, XsrUiColor.Transparent, 0);
        SetStyle(entities["NotificationLevel"], XsrUiColor.Transparent, accent,
            XsrUiColor.Transparent, 0, fontSize: 13, fontWeight: 600);
        SetStyle(entities["NotificationMessage"], XsrUiColor.Transparent, new XsrUiColor(52, 61, 74),
            XsrUiColor.Transparent, 0, fontSize: 13, wrap: true);
        SetStyle(entities["NotificationClose"], XsrUiColor.Transparent, accent,
            XsrUiColor.Transparent, XsrUiCornerRadii.Pill(30), hover: hover);
        return new PresentedNotification(notification, root, entities["NotificationClose"], motion);
    }

    private void BeginNotificationClose(PresentedNotification notification)
    {
        notification.IsClosing = true;
        notification.Motion.IsClosing = true;
        SetEnabled(notification.Root, false);
        if (_shell.Tree.GetComponent<XsrUiInput>(notification.Close) is { } close)
        {
            close.Enabled = false;
        }
        _shell.Tree.MarkDirty(notification.Root, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
        _shell.Tree.MarkDirty(notification.Close, XsrUiDirtyKinds.Paint);
        if (_shell.Renderer.ReducedMotion)
        {
            FinalizeNotification(notification.Value.Id);
            return;
        }

        notification.Cleanup = _timeProvider.CreateTimer(
            static state =>
            {
                (DesktopFeedbackPresenter owner, Guid id) = ((DesktopFeedbackPresenter, Guid))state!;
                owner._notificationCleanup.Enqueue(id);
                owner.RequestFrame();
            },
            (this, notification.Value.Id),
            ExitSettle,
            Timeout.InfiniteTimeSpan);
    }

    private void FinalizeNotification(Guid id)
    {
        if (!_notifications.Remove(id, out PresentedNotification? notification))
        {
            return;
        }

        notification.Cleanup?.Dispose();
        _notificationRoots.Remove(notification.Root);
        if (_shell.Tree.IsAlive(notification.Root))
        {
            _shell.Tree.Destroy(notification.Root);
        }
        SetNotificationHostVisible(_notifications.Count > 0);
    }

    private void ReconcileDialog(DesktopDialog? requested)
    {
        if (requested is null)
        {
            if (_dialog is { IsClosing: false } current)
            {
                BeginDialogClose(current);
            }
            return;
        }

        if (_dialog is null)
        {
            _dialog = CreateDialog(requested);
            return;
        }

        if (_dialog.Value.Id != requested.Id)
        {
            FinalizeDialog(_dialog.Value.Id);
            _dialog = CreateDialog(requested);
            return;
        }

        if (!_dialog.IsClosing && _dialog.Value != requested)
        {
            _dialog.Value = requested;
            UpdateDialog(_dialog, requested);
        }
    }

    private PresentedDialog CreateDialog(DesktopDialog dialog)
    {
        XsrUiEntityId temporaryHost = _shell.Tree.Create("dialog-template-host");
        XsrUiEntityId root = PxmlUiLoader.Load(_dialogTemplate, _shell.Tree, _store, temporaryHost);
        _shell.Tree.Detach(root);
        _shell.Tree.Destroy(temporaryHost);
        Dictionary<string, XsrUiEntityId> entities = Index(root);
        // The full-window scrim and decorative title glyph are not separate narration stops.
        // The Dialog node names the surface and the message/button semantics remain readable.
        _shell.Tree.SetComponent<XsrUiSemantic>(root, null);
        _shell.Tree.SetComponent<XsrUiSemantic>(entities["DialogIcon"], null);
        _shell.Tree.SetComponent<XsrUiSemantic>(entities["DialogTitle"], null);
        _shell.Tree.SetComponent<XsrUiSemantic>(entities["DialogMessage"], null);
        XsrUiEntityId previousFocus = _shell.Renderer.Focused;
        bool focusVisible = previousFocus.IsAssigned
            && _shell.Tree.IsAlive(previousFocus)
            && _shell.Tree.GetComponent<XsrUiInput>(previousFocus)?.IsFocusVisible == true;

        XsrUiOverlayMotion scrimMotion = new(XsrUiOverlayMotionKind.DialogScrim);
        XsrUiOverlayMotion cardMotion = new(XsrUiOverlayMotionKind.Dialog);
        _shell.Tree.SetComponent(root, scrimMotion);
        _shell.Tree.SetComponent(root, new XsrUiDismissBinding(DialogCancel));
        _shell.Tree.SetComponent(entities["DialogCard"], cardMotion);
        _shell.Tree.SetComponent(entities["DialogCard"], new XsrUiLiveRegion(XsrUiLiveSetting.Assertive));
        _shell.Tree.GetComponent<XsrUiScroll>(entities["DialogMessageViewport"])!
            .ShowsVerticalIndicator = true;

        SetStyle(root, new XsrUiColor(20, 28, 40, 92), new XsrUiColor(52, 61, 74),
            XsrUiColor.Transparent, 0);
        SetStyle(entities["DialogCard"], new XsrUiColor(255, 255, 255, 252),
            new XsrUiColor(52, 61, 74), new XsrUiColor(218, 225, 235), 20, borderWidth: 1);
        SetStyle(entities["DialogIcon"], XsrUiColor.Transparent, new XsrUiColor(19, 112, 243),
            XsrUiColor.Transparent, 0);
        SetStyle(entities["DialogTitle"], XsrUiColor.Transparent, new XsrUiColor(38, 47, 60),
            XsrUiColor.Transparent, 0, fontSize: 20, fontWeight: 600);
        SetStyle(entities["DialogMessage"], XsrUiColor.Transparent, new XsrUiColor(91, 105, 122),
            XsrUiColor.Transparent, 0, fontSize: 14, wrap: true);
        SetStyle(entities["DialogCancel"], new XsrUiColor(238, 242, 247), new XsrUiColor(52, 61, 74),
            XsrUiColor.Transparent, XsrUiCornerRadii.Pill(38), hover: new XsrUiColor(224, 230, 238),
            fontSize: 14, fontWeight: 600, centered: true);
        SetStyle(entities["DialogAccept"], new XsrUiColor(11, 91, 203), new XsrUiColor(255, 255, 255),
            XsrUiColor.Transparent, XsrUiCornerRadii.Pill(38), hover: new XsrUiColor(19, 112, 243),
            fontSize: 14, fontWeight: 600, centered: true);

        PresentedDialog presented = new(dialog, root, entities["DialogCard"], entities["DialogAccept"],
            entities["DialogCancel"], scrimMotion, cardMotion, previousFocus, focusVisible);
        UpdateDialog(presented, dialog);
        _shell.Stage.Show(root, modal: true);
        _shell.Renderer.Focus(presented.Accept, focusVisible);
        return presented;
    }

    private void UpdateDialog(PresentedDialog presented, DesktopDialog dialog)
    {
        Dictionary<string, XsrUiEntityId> entities = Index(presented.Root);
        SetText(entities["DialogTitle"], dialog.Title);
        SetText(entities["DialogMessage"], dialog.Message);
        SetText(presented.Accept, dialog.AcceptLabel);
        bool hasCancel = !string.IsNullOrWhiteSpace(dialog.CancelLabel);
        if (hasCancel)
        {
            SetText(presented.Cancel, dialog.CancelLabel!);
        }
        SetVisible(presented.Cancel, hasCancel);
        _shell.Tree.GetComponent<XsrUiSemantic>(presented.Card)!.Label = $"{dialog.Title}。{dialog.Message}";
        _shell.Tree.GetComponent<XsrUiSemantic>(presented.Accept)!.Label = dialog.AcceptLabel;
        _shell.Tree.GetComponent<XsrUiSemantic>(presented.Cancel)!.Label = dialog.CancelLabel ?? string.Empty;
        _shell.Tree.MarkDirty(presented.Card, XsrUiDirtyKinds.Paint);
        _shell.Tree.MarkDirty(presented.Accept, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
        _shell.Tree.MarkDirty(presented.Cancel, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }

    private void BeginDialogClose(PresentedDialog dialog)
    {
        dialog.IsClosing = true;
        dialog.ScrimMotion.IsClosing = true;
        dialog.CardMotion.IsClosing = true;
        _shell.Tree.SetComponent<XsrUiDismissBinding>(dialog.Root, null);
        SetEnabled(dialog.Accept, false);
        SetEnabled(dialog.Cancel, false);
        _shell.Tree.MarkDirty(dialog.Root, XsrUiDirtyKinds.Paint);
        _shell.Tree.MarkDirty(dialog.Card, XsrUiDirtyKinds.Paint);
        if (_shell.Renderer.ReducedMotion)
        {
            FinalizeDialog(dialog.Value.Id);
            return;
        }

        dialog.Cleanup = _timeProvider.CreateTimer(
            static state =>
            {
                (DesktopFeedbackPresenter owner, Guid id) = ((DesktopFeedbackPresenter, Guid))state!;
                owner._dialogCleanup.Enqueue(id);
                owner.RequestFrame();
            },
            (this, dialog.Value.Id),
            ExitSettle,
            Timeout.InfiniteTimeSpan);
    }

    private void FinalizeDialog(Guid id)
    {
        PresentedDialog? dialog = _dialog;
        if (dialog is null || dialog.Value.Id != id)
        {
            return;
        }

        _dialog = null;
        dialog.Cleanup?.Dispose();
        if (_shell.Tree.IsAlive(dialog.Root))
        {
            _shell.Stage.Dismiss(dialog.Root);
            _shell.Tree.Destroy(dialog.Root);
        }
        if (dialog.PreviousFocus.IsAssigned && _shell.Tree.IsAlive(dialog.PreviousFocus))
        {
            _shell.Renderer.Focus(dialog.PreviousFocus, dialog.PreviousFocusVisible);
        }
    }

    private void OnIntent(object? sender, DesktopUiIntentEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (e.Intent.Command == NotificationDismiss)
        {
            if (TryResolveNotification(e.Intent.Source, out Guid id))
            {
                _service.DismissNotification(id);
            }
            return;
        }

        if (e.Intent.Command == NotificationOpen)
        {
            if (TryResolveNotification(e.Intent.Source, out Guid id)
                && _notifications.TryGetValue(id, out PresentedNotification? notification))
            {
                string level = notification.Value.Level.ToString();
                _service.ShowMessageDialog(
                    $"notification.detail.{id:N}",
                    $"{level} 通知",
                    notification.Value.Message);
            }
            return;
        }

        if (_dialog is { IsClosing: false } dialog)
        {
            if (e.Intent.Command == DialogAccept)
            {
                _service.ResolveDialog(dialog.Value.Id, accepted: true);
            }
            else if (e.Intent.Command == DialogCancel)
            {
                _service.ResolveDialog(dialog.Value.Id, accepted: false);
            }
        }
    }

    private void DrainCleanup()
    {
        while (_notificationCleanup.TryDequeue(out Guid notificationId))
        {
            FinalizeNotification(notificationId);
        }
        while (_dialogCleanup.TryDequeue(out Guid dialogId))
        {
            FinalizeDialog(dialogId);
        }
    }

    private Dictionary<string, XsrUiEntityId> Index(XsrUiEntityId root)
    {
        Dictionary<string, XsrUiEntityId> result = [];
        _shell.Tree.Walk(root, entity =>
        {
            string name = _shell.Tree.Name(entity);
            if (name.Length > 0)
            {
                result[name] = entity;
            }
            return true;
        });
        return result;
    }

    private bool TryResolveNotification(XsrUiEntityId source, out Guid id)
    {
        XsrUiEntityId current = source;
        while (current.IsAssigned && _shell.Tree.IsAlive(current))
        {
            if (_notificationRoots.TryGetValue(current, out id))
            {
                return true;
            }
            current = _shell.Tree.Parent(current);
        }

        id = default;
        return false;
    }

    private void SetText(XsrUiEntityId entity, string content)
    {
        XsrUiText text = _shell.Tree.GetComponent<XsrUiText>(entity)!;
        if (!string.Equals(text.Content, content, StringComparison.Ordinal))
        {
            text.Content = content;
            _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
        }
    }

    private void SetEnabled(XsrUiEntityId entity, bool enabled)
    {
        if (_shell.Tree.GetComponent<XsrUiInput>(entity) is { } input)
        {
            input.Enabled = enabled;
            _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
        }
    }

    private void SetVisible(XsrUiEntityId entity, bool visible)
    {
        XsrUiElement element = _shell.Tree.GetComponent<XsrUiElement>(entity)
            ?? new XsrUiElement();
        if (element.IsVisible == visible)
        {
            return;
        }

        element.IsVisible = visible;
        _shell.Tree.SetComponent(entity, element);
        _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }

    private void SetNotificationHostVisible(bool visible) => SetVisible(_notificationHost, visible);

    private void SetStyle(
        XsrUiEntityId entity,
        XsrUiColor background,
        XsrUiColor foreground,
        XsrUiColor border,
        double cornerRadius,
        double borderWidth = 0,
        XsrUiColor hover = default,
        double fontSize = 0,
        double fontWeight = 400,
        bool wrap = false,
        bool centered = false)
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
            WrapText = wrap,
            TextAlignment = centered ? XsrUiTextAlignment.Center : XsrUiTextAlignment.Start,
        });
    }

    private static (XsrUiColor Accent, XsrUiColor Background, XsrUiColor Border, XsrUiColor Hover)
        NotificationPalette(DesktopNotificationLevel level) => level switch
        {
            DesktopNotificationLevel.Info => (
                new XsrUiColor(19, 112, 243),
                new XsrUiColor(244, 248, 255, 252),
                new XsrUiColor(184, 214, 255),
                new XsrUiColor(19, 112, 243, 24)),
            DesktopNotificationLevel.Warn => (
                new XsrUiColor(190, 124, 0),
                new XsrUiColor(255, 249, 235, 252),
                new XsrUiColor(245, 211, 137),
                new XsrUiColor(190, 124, 0, 24)),
            DesktopNotificationLevel.Error => (
                new XsrUiColor(207, 47, 54),
                new XsrUiColor(255, 244, 245, 252),
                new XsrUiColor(244, 184, 188),
                new XsrUiColor(207, 47, 54, 24)),
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown notification level."),
        };

    private static PxmlHostIr LoadTemplate(string suffix)
    {
        Assembly assembly = typeof(DesktopFeedbackPresenter).Assembly;
        string resource = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(suffix, StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing embedded feedback template '{suffix}'.");
        using StreamReader reader = new(stream);
        return PxmlCompiler.Compile(PxmlParser.Parse(reader.ReadToEnd()));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _service.Changed -= OnServiceChanged;
        _intents.IntentEmitted -= OnIntent;
        _shell.Renderer.FramePreparing -= OnFramePreparing;
        foreach (PresentedNotification notification in _notifications.Values)
        {
            notification.Cleanup?.Dispose();
            if (_shell.Tree.IsAlive(notification.Root))
            {
                _shell.Tree.Destroy(notification.Root);
            }
        }
        _notifications.Clear();
        _notificationRoots.Clear();
        if (_dialog is { } dialog)
        {
            dialog.Cleanup?.Dispose();
            if (_shell.Tree.IsAlive(dialog.Root))
            {
                _shell.Stage.Dismiss(dialog.Root);
                _shell.Tree.Destroy(dialog.Root);
            }
            _dialog = null;
        }
        if (_shell.Tree.IsAlive(_notificationHost))
        {
            _shell.Stage.Dismiss(_notificationHost);
            _shell.Tree.Destroy(_notificationHost);
        }
    }
}

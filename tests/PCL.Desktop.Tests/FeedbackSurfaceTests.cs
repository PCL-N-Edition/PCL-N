using PCL.Desktop.Ui;
using PCL.UI.Next;

namespace PCL.Desktop.Tests;

internal static partial class Program
{
    private static void NotificationLevelsKeepExactLifetimes()
    {
        using FeedbackClock clock = new();
        using DesktopFeedbackService service = new(clock);
        Guid info = service.Info("info");
        Guid warn = service.Warn("warn");
        Guid error = service.Error("error");

        AssertEqual(TimeSpan.FromSeconds(5), DesktopFeedbackService.DurationFor(DesktopNotificationLevel.Info));
        AssertEqual(TimeSpan.FromSeconds(15), DesktopFeedbackService.DurationFor(DesktopNotificationLevel.Warn));
        AssertEqual<TimeSpan?>(null, DesktopFeedbackService.DurationFor(DesktopNotificationLevel.Error));
        AssertEqual(3, service.Snapshot().Notifications.Count);

        clock.Advance(TimeSpan.FromMilliseconds(4_999));
        AssertTrue(service.Snapshot().Notifications.Any(item => item.Id == info));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        AssertFalse(service.Snapshot().Notifications.Any(item => item.Id == info));
        AssertTrue(service.Snapshot().Notifications.Any(item => item.Id == warn));
        AssertTrue(service.Snapshot().Notifications.Any(item => item.Id == error));

        clock.Advance(TimeSpan.FromMilliseconds(9_999));
        AssertTrue(service.Snapshot().Notifications.Any(item => item.Id == warn));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        AssertFalse(service.Snapshot().Notifications.Any(item => item.Id == warn));
        AssertTrue(service.Snapshot().Notifications.Any(item => item.Id == error));

        // Error has no automatic expiry, but the exact same manual-dismiss contract applies.
        clock.Advance(TimeSpan.FromDays(1));
        AssertTrue(service.Snapshot().Notifications.Any(item => item.Id == error));
        AssertTrue(service.DismissNotification(error));
        AssertEqual(0, service.Snapshot().Notifications.Count);
    }

    private static void NotificationsShareLowerLeftSurfaceAndCloseManually()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        fixture.Shell.Renderer.ReducedMotion = true;
        XsrUiSize size = new(810, 470);
        fixture.Feedback.Info("information");
        fixture.Feedback.Warn("warning content that wraps onto a second line without forcing every notice to be tall");
        fixture.Feedback.Error("failure");

        XsrUiScene scene = fixture.Shell.Render(size);
        XsrUiSceneNode host = FindByKey(fixture.Shell, scene, "notification-host");
        XsrUiSceneNode[] notifications = [.. scene.Nodes.Where(node => node.Role == XsrUiSemanticRole.Status)];
        AssertEqual(3, notifications.Length);
        double expectedX = XsrUiShell.CollapsedRailWidth + 18;
        AssertClose(expectedX, host.Rect.X);
        AssertClose(size.Height - 18, host.Rect.Y + host.Rect.Height);
        AssertTrue(notifications.All(node => Math.Abs(node.Rect.X - expectedX) < .001));
        XsrUiSceneNode compact = notifications.Single(node => node.Label!.StartsWith("Info：", StringComparison.Ordinal));
        XsrUiSceneNode wrapped = notifications.Single(node => node.Label!.StartsWith("Warn：", StringComparison.Ordinal));
        XsrUiSceneNode compactError = notifications.Single(node => node.Label!.StartsWith("Error：", StringComparison.Ordinal));
        AssertTrue(wrapped.Rect.Height > compact.Rect.Height);
        AssertClose(compact.Rect.Height, compactError.Rect.Height);
        fixture.Shell.SetNavigationExpanded(true);
        scene = fixture.Shell.Render(size);
        host = FindByKey(fixture.Shell, scene, "notification-host");
        AssertClose(XsrUiShell.ExpandedRailWidth + 18, host.Rect.X);
        fixture.Shell.SetRailPresentationProgress(.5);
        scene = fixture.Shell.Render(size);
        host = FindByKey(fixture.Shell, scene, "notification-host");
        AssertClose(XsrUiShell.RailWidthFor(.5) + 18, host.Rect.X);
        fixture.Shell.SetRailPresentationProgress(1);
        scene = fixture.Shell.Render(size);
        XsrUiSceneNode summary = scene.Nodes.First(node =>
            fixture.Shell.Tree.Name(node.Entity) == "NotificationMessage");
        AssertEqual(2, summary.TextMaxLines);
        AssertTrue(summary.TextTrimsOverflow);

        AssertNotificationPresentation(
            notifications.Single(node => node.Label!.StartsWith("Info：", StringComparison.Ordinal)),
            XsrUiLiveSetting.Polite,
            new XsrUiColor(244, 248, 255, 252));
        AssertNotificationPresentation(
            notifications.Single(node => node.Label!.StartsWith("Warn：", StringComparison.Ordinal)),
            XsrUiLiveSetting.Assertive,
            new XsrUiColor(255, 249, 235, 252));
        AssertNotificationPresentation(
            notifications.Single(node => node.Label!.StartsWith("Error：", StringComparison.Ordinal)),
            XsrUiLiveSetting.Assertive,
            new XsrUiColor(255, 244, 245, 252));

        foreach (string level in new[] { "Info", "Warn", "Error" })
        {
            scene = fixture.Shell.Render(size);
            XsrUiSceneNode close = scene.Nodes.Single(node =>
                node.Role == XsrUiSemanticRole.Button
                && node.Label == $"关闭 {level} 通知");
            AssertTrue(fixture.Shell.Renderer.Activate(close.Entity));
            scene = fixture.Shell.Render(size);
            AssertFalse(scene.Nodes.Any(node =>
                node.Role == XsrUiSemanticRole.Status
                && node.Label!.StartsWith(level + "：", StringComparison.Ordinal)));
        }

        AssertEqual(0, fixture.Feedback.Snapshot().Notifications.Count);
        AssertEqual(0, fixture.FeedbackPresenter.PresentedNotificationCount);
        AssertFalse(HasKey(fixture.Shell, fixture.Shell.Render(size), "notification-host"));
    }

    private static void NotificationSummaryOpensCompleteScrollableDialog()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        fixture.Shell.Renderer.ReducedMotion = true;
        XsrUiSize size = new(810, 470);
        string complete = string.Join('\n', Enumerable.Range(1, 40).Select(index =>
            $"第 {index} 行：这是一段必须在详情对话框中完整保留的通知内容。"));
        fixture.Feedback.Error(complete);

        XsrUiScene scene = fixture.Shell.Render(size);
        XsrUiSceneNode notice = scene.Nodes.Single(node => node.Role == XsrUiSemanticRole.Status);
        XsrUiSceneNode summary = FindByKey(fixture.Shell, scene, "NotificationMessage");
        AssertEqual(complete, summary.Text);
        AssertEqual(2, summary.TextMaxLines);
        AssertTrue(summary.TextTrimsOverflow);
        AssertTrue(fixture.Shell.Renderer.Activate(notice.Entity));

        scene = fixture.Shell.Render(size);
        XsrUiSceneNode message = FindByKey(fixture.Shell, scene, "DialogMessage");
        XsrUiSceneNode viewport = FindByKey(fixture.Shell, scene, "DialogMessageViewport");
        XsrUiSceneNode accept = FindByKey(fixture.Shell, scene, "DialogAccept");
        AssertEqual(complete, message.Text);
        AssertEqual("OK", accept.Text);
        AssertFalse(HasKey(fixture.Shell, scene, "DialogCancel"));
        AssertTrue(viewport.Scroll is { ShowsVerticalIndicator: true, CanScrollVertically: true });
        AssertTrue(viewport.Scroll!.Value.ContentHeight > viewport.Scroll.Value.ViewportHeight);

        XsrUiPoint overMessage = new(viewport.Rect.X + 20, viewport.Rect.Y + 20);
        AssertTrue(fixture.Shell.Renderer.PointerScroll(overMessage, 12.5));
        scene = fixture.Shell.Render(size);
        viewport = FindByKey(fixture.Shell, scene, "DialogMessageViewport");
        AssertClose(12.5, viewport.Scroll!.Value.OffsetY);

        AssertTrue(fixture.Shell.Renderer.Activate(FindByKey(fixture.Shell, scene, "DialogAccept").Entity));
        scene = fixture.Shell.Render(size);
        AssertFalse(HasKey(fixture.Shell, scene, "DialogCard"));
    }

    private static void NotificationOverflowRecoversWithoutDisappearing()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        fixture.Shell.Renderer.ReducedMotion = true;
        XsrUiSize size = new(810, 470);
        Guid[] ids = [.. Enumerable.Range(1, 7).Select(index => fixture.Feedback.Error($"error-{index}"))];

        XsrUiScene scene = fixture.Shell.Render(size);
        XsrUiSceneNode host = FindByKey(fixture.Shell, scene, "notification-host");
        AssertTrue(host.Scroll is { OffsetY: > 0 });

        foreach (Guid id in ids.Take(4))
        {
            AssertTrue(fixture.Feedback.DismissNotification(id));
            scene = fixture.Shell.Render(size);
        }

        XsrUiSceneNode[] remaining = [.. scene.Nodes.Where(node => node.Role == XsrUiSemanticRole.Status)];
        AssertEqual(3, remaining.Length);
        host = FindByKey(fixture.Shell, scene, "notification-host");
        AssertClose(0, host.Scroll!.Value.OffsetY);
        AssertTrue(remaining.All(node => node.Rect.Y >= host.Rect.Y
            && node.Rect.Y + node.Rect.Height <= host.Rect.Y + host.Rect.Height));
        AssertTrue(remaining.Any(node => node.Label!.Contains("error-7", StringComparison.Ordinal)));

        foreach (Guid id in ids.Skip(4))
        {
            AssertTrue(fixture.Feedback.DismissNotification(id));
        }
        scene = fixture.Shell.Render(size);
        AssertFalse(HasKey(fixture.Shell, scene, "notification-host"));
    }

    private static void ClosingNotificationReflowsImmediately()
    {
        using FeedbackClock clock = new();
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]), timeProvider: clock);
        XsrUiSize size = new(810, 470);
        _ = fixture.Feedback.Error("first");
        _ = fixture.Feedback.Error("second");
        Guid closing = fixture.Feedback.Error("third");

        XsrUiScene before = fixture.Shell.Render(size);
        double firstY = before.Nodes.Single(node => node.Role == XsrUiSemanticRole.Status
            && node.Label!.Contains("first", StringComparison.Ordinal)).Rect.Y;
        AssertTrue(fixture.Feedback.DismissNotification(closing));

        XsrUiScene after = fixture.Shell.Render(size);
        XsrUiSceneNode first = after.Nodes.Single(node => node.Role == XsrUiSemanticRole.Status
            && node.Label!.Contains("first", StringComparison.Ordinal));
        XsrUiSceneNode second = after.Nodes.Single(node => node.Role == XsrUiSemanticRole.Status
            && node.Label!.Contains("second", StringComparison.Ordinal));
        AssertTrue(first.Rect.Y > firstY + 80);
        AssertClose(10, second.Rect.Y - (first.Rect.Y + first.Rect.Height));
    }

    private static void DialogStaysInsideWindowAndRestoresFocus()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        fixture.Shell.Renderer.ReducedMotion = true;
        XsrUiSize size = new(810, 470);
        XsrUiScene scene = fixture.Shell.Render(size);
        XsrUiEntityId previousFocus = FindByKey(fixture.Shell, scene, "LaunchButton").Entity;
        AssertTrue(fixture.Shell.Renderer.Focus(previousFocus, showIndicator: true));
        bool? resolved = null;
        fixture.Feedback.ShowDialog(
            "test.dialog",
            "需要确认",
            "此对话框必须留在应用窗口内。",
            "继续",
            "取消",
            accepted => resolved = accepted);

        scene = fixture.Shell.Render(size);
        XsrUiSceneNode layer = FindByKey(fixture.Shell, scene, "DialogLayer");
        XsrUiSceneNode card = FindByKey(fixture.Shell, scene, "DialogCard");
        XsrUiSceneNode accept = FindByKey(fixture.Shell, scene, "DialogAccept");
        XsrUiSceneNode pageButton = FindByKey(fixture.Shell, scene, "LaunchButton");
        AssertRectClose(new XsrUiRect(0, 0, size.Width, size.Height), layer.Rect);
        AssertContains(layer.Rect, card.Rect);
        AssertEqual(XsrUiSemanticRole.None, layer.Role);
        AssertEqual(XsrUiSemanticRole.Dialog, card.Role);
        AssertTrue(card.Label!.Contains("此对话框必须留在应用窗口内", StringComparison.Ordinal));
        AssertEqual(XsrUiSemanticRole.None, FindByKey(fixture.Shell, scene, "DialogIcon").Role);
        AssertEqual(XsrUiSemanticRole.None, FindByKey(fixture.Shell, scene, "DialogTitle").Role);
        AssertEqual(XsrUiSemanticRole.None, FindByKey(fixture.Shell, scene, "DialogMessage").Role);
        AssertEqual(XsrUiLiveSetting.Assertive, card.LiveSetting);
        AssertEqual(accept.Entity, fixture.Shell.Renderer.Focused);
        AssertFalse(pageButton.IsAccessible);
        AssertFalse(pageButton.IsClickable);
        AssertEqual(layer.Entity, fixture.Shell.Renderer.HitTest(new XsrUiPoint(4, 4)));

        AssertTrue(fixture.Shell.Renderer.HandleKey(XsrUiKey.Escape));
        AssertEqual(false, resolved);
        scene = fixture.Shell.Render(size);
        AssertFalse(HasKey(fixture.Shell, scene, "DialogCard"));
        AssertEqual(previousFocus, fixture.Shell.Renderer.Focused);
        AssertTrue(FindByKey(fixture.Shell, scene, "LaunchButton").IsFocusVisible);
    }

    private static void NotificationTimersStayOffTheRenderTree()
    {
        using FeedbackClock clock = new();
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]), timeProvider: clock);
        fixture.Shell.Renderer.ReducedMotion = true;
        fixture.Feedback.Info("expires from a timer");
        XsrUiScene scene = fixture.Shell.Render(new XsrUiSize(810, 470));
        AssertTrue(scene.Nodes.Any(node => node.Role == XsrUiSemanticRole.Status));
        XsrUiEntityId[] dirtBeforeWorker = [.. fixture.Shell.Tree.DirtyEntities()];

        Task.Run(() => clock.Advance(TimeSpan.FromSeconds(5))).GetAwaiter().GetResult();

        AssertTrue(dirtBeforeWorker.SequenceEqual(fixture.Shell.Tree.DirtyEntities()));
        AssertTrue(fixture.Shell.StateBridge!.PendingCount > 0);
        scene = fixture.Shell.Render(new XsrUiSize(810, 470));
        AssertFalse(scene.Nodes.Any(node => node.Role == XsrUiSemanticRole.Status));
    }

    private static void AssertNotificationPresentation(
        XsrUiSceneNode notification,
        XsrUiLiveSetting liveSetting,
        XsrUiColor background)
    {
        AssertEqual(liveSetting, notification.LiveSetting);
        AssertEqual(background, notification.VisualStyle.Background);
        AssertEqual(XsrUiOverlayMotionKind.Notification, notification.OverlayMotion);
    }

    /// <summary>Deterministic multi-timer clock for exact notification lifetime contracts.</summary>
    private sealed class FeedbackClock : TimeProvider, IDisposable
    {
        private readonly List<FeedbackTimer> _timers = [];

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            FeedbackTimer timer = new(callback, state, dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            foreach (FeedbackTimer timer in _timers.ToArray())
            {
                timer.Advance(elapsed);
            }
        }

        public void Dispose()
        {
            foreach (FeedbackTimer timer in _timers)
            {
                timer.Dispose();
            }
            _timers.Clear();
        }

        private sealed class FeedbackTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) : ITimer
        {
            private TimeSpan _remaining = dueTime;
            private TimeSpan _period = period;
            private bool _active = dueTime != Timeout.InfiniteTimeSpan;
            private bool _disposed;

            public void Advance(TimeSpan elapsed)
            {
                if (_disposed || !_active)
                {
                    return;
                }

                _remaining -= elapsed;
                while (!_disposed && _active && _remaining <= TimeSpan.Zero)
                {
                    if (_period == Timeout.InfiniteTimeSpan)
                    {
                        _active = false;
                    }
                    else
                    {
                        _remaining += _period;
                    }
                    callback(state);
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed)
                {
                    return false;
                }

                _remaining = dueTime;
                _period = period;
                _active = dueTime != Timeout.InfiniteTimeSpan;
                return true;
            }

            public void Dispose()
            {
                _disposed = true;
                _active = false;
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}

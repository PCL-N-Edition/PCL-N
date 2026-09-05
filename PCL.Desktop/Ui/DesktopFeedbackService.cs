using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Desktop.Ui;

/// <summary>The three product feedback levels and no implicit fourth level.</summary>
internal enum DesktopNotificationLevel
{
    Info = 1,
    Warn = 2,
    Error = 3,
}

/// <summary>One immutable notification request owned by the Desktop feedback service.</summary>
internal sealed record DesktopNotification(Guid Id, DesktopNotificationLevel Level, string Message);

/// <summary>One immutable modal decision request. The callback never runs under the service lock.</summary>
internal sealed record DesktopDialog(
    Guid Id,
    string Key,
    string Title,
    string Message,
    string AcceptLabel,
    string CancelLabel,
    Action<bool> Resolve);

/// <summary>Thread-safe point-in-time feedback state consumed at the render boundary.</summary>
internal sealed record DesktopFeedbackSnapshot(
    IReadOnlyList<DesktopNotification> Notifications,
    DesktopDialog? Dialog);

/// <summary>
/// Thread-safe product feedback service. It owns notification lifetimes and modal decisions but
/// never touches UI.Next: a Desktop presenter projects snapshots into PXML on the render thread.
/// </summary>
internal sealed class DesktopFeedbackService : IDisposable
{
    internal static readonly TimeSpan InfoDuration = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan WarnDuration = TimeSpan.FromSeconds(15);

    private sealed class NotificationEntry(DesktopNotification notification)
    {
        public DesktopNotification Notification { get; } = notification;

        public ITimer? Timer { get; set; }
    }

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly List<NotificationEntry> _notifications = [];
    private DesktopDialog? _dialog;
    private bool _disposed;

    public DesktopFeedbackService(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised after a snapshot changes. The callback may run on any thread.</summary>
    public event EventHandler? Changed;

    public Guid Info(string message) => Notify(DesktopNotificationLevel.Info, message);

    public Guid Warn(string message) => Notify(DesktopNotificationLevel.Warn, message);

    public Guid Error(string message) => Notify(DesktopNotificationLevel.Error, message);

    public Guid Notify(DesktopNotificationLevel level, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (!Enum.IsDefined(level))
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown notification level.");
        }

        Guid id = Guid.NewGuid();
        NotificationEntry entry = new(new DesktopNotification(id, level, message));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _notifications.Add(entry);
        }

        TimeSpan? duration = DurationFor(level);
        if (duration is { } due)
        {
            ITimer? timer = _timeProvider.CreateTimer(
                static state =>
                {
                    (DesktopFeedbackService owner, Guid notificationId) =
                        ((DesktopFeedbackService, Guid))state!;
                    _ = owner.DismissNotification(notificationId);
                },
                (this, id),
                due,
                Timeout.InfiniteTimeSpan);
            lock (_gate)
            {
                if (!_disposed && _notifications.Contains(entry))
                {
                    entry.Timer = timer;
                    timer = null;
                }
            }

            timer?.Dispose();
        }

        RaiseChanged();
        return id;
    }

    /// <summary>Removes one notification, including permanent errors, through one path.</summary>
    public bool DismissNotification(Guid id)
    {
        NotificationEntry? removed = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            int index = _notifications.FindIndex(entry => entry.Notification.Id == id);
            if (index >= 0)
            {
                removed = _notifications[index];
                _notifications.RemoveAt(index);
            }
        }

        if (removed is null)
        {
            return false;
        }

        removed.Timer?.Dispose();
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// Shows or refreshes one keyed dialog. Refreshing the same key preserves its identity;
    /// replacing a different dialog cancels the older request so no waiter is stranded.
    /// </summary>
    public Guid ShowDialog(
        string key,
        string title,
        string message,
        string acceptLabel,
        string cancelLabel,
        Action<bool> resolve)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(acceptLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(cancelLabel);
        ArgumentNullException.ThrowIfNull(resolve);

        DesktopDialog? replaced = null;
        Guid id;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_dialog is { } current && string.Equals(current.Key, key, StringComparison.Ordinal))
            {
                id = current.Id;
                // A same-key refresh updates presentation while retaining the original
                // resolution callback; repeated state projection must never strand a waiter.
                resolve = current.Resolve;
            }
            else
            {
                replaced = _dialog;
                id = Guid.NewGuid();
            }

            _dialog = new DesktopDialog(id, key, title, message, acceptLabel, cancelLabel, resolve);
        }

        replaced?.Resolve(false);
        RaiseChanged();
        return id;
    }

    /// <summary>Resolves the active dialog and invokes its typed decision outside the lock.</summary>
    public bool ResolveDialog(Guid id, bool accepted)
    {
        DesktopDialog? resolved;
        lock (_gate)
        {
            if (_disposed || _dialog?.Id != id)
            {
                return false;
            }

            resolved = _dialog;
            _dialog = null;
        }

        RaiseChanged();
        resolved.Resolve(accepted);
        return true;
    }

    /// <summary>Dismisses a dialog whose underlying state already resolved, without re-callback.</summary>
    public bool DismissDialog(Guid id)
    {
        lock (_gate)
        {
            if (_disposed || _dialog?.Id != id)
            {
                return false;
            }

            _dialog = null;
        }

        RaiseChanged();
        return true;
    }

    public DesktopFeedbackSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new DesktopFeedbackSnapshot(
                [.. _notifications.Select(entry => entry.Notification)],
                _dialog);
        }
    }

    internal static TimeSpan? DurationFor(DesktopNotificationLevel level) => level switch
    {
        DesktopNotificationLevel.Info => InfoDuration,
        DesktopNotificationLevel.Warn => WarnDuration,
        DesktopNotificationLevel.Error => null,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown notification level."),
    };

    public void Dispose()
    {
        NotificationEntry[] entries;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            entries = [.. _notifications];
            _notifications.Clear();
            _dialog = null;
        }

        foreach (NotificationEntry entry in entries)
        {
            entry.Timer?.Dispose();
        }

        Changed = null;
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>Host-state wake revision used only to marshal feedback changes to a render frame.</summary>
internal static class DesktopFeedbackState
{
    public const string Owner = "PCL.Desktop.Feedback";
    public static readonly XsrSemanticId Revision = XsrSemanticId.Parse("ui.feedback.revision");

    public static void DeclareState(XsrStateStoreBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Cell<long>(Revision, Owner);
    }
}

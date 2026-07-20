// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using CommunityToolkit.Mvvm.Messaging;
using PCL.Desktop.Features.Tasks.Views;
using PCL.Desktop.Messaging;

namespace PCL.Desktop.Session;

/// <summary>In-memory background download / install task snapshots for the task manager + FAB.</summary>
public sealed class TaskSessionStore
{
    private readonly IMessenger _messenger;
    private readonly Dictionary<string, TaskManagerEntrySnapshot> _snapshots = new(StringComparer.Ordinal);
    private int _sequence;
    private bool _isTaskManagerVisible;

    public TaskSessionStore(IMessenger messenger)
    {
        _messenger = messenger;
    }

    public IReadOnlyDictionary<string, TaskManagerEntrySnapshot> Snapshots => _snapshots;

    public bool IsTaskManagerVisible
    {
        get => _isTaskManagerVisible;
        set
        {
            if (_isTaskManagerVisible == value)
                return;
            _isTaskManagerVisible = value;
            PublishProgress();
        }
    }

    public int NextSequence() => Interlocked.Increment(ref _sequence);

    public void Upsert(string taskId, TaskManagerEntrySnapshot snapshot)
    {
        _snapshots[taskId] = snapshot;
        PublishProgress();
    }

    public bool TryGet(string taskId, out TaskManagerEntrySnapshot snapshot) =>
        _snapshots.TryGetValue(taskId, out snapshot!);

    public void Remove(string taskId)
    {
        _snapshots.Remove(taskId);
        PublishProgress();
    }

    public void Clear()
    {
        _snapshots.Clear();
        PublishProgress();
    }

    public bool HasActiveTask =>
        _snapshots.Values.Any(static snapshot =>
            snapshot.State is TaskManagerTaskState.Waiting or TaskManagerTaskState.Running);

    public bool HasVisibleTask =>
        _snapshots.Values.Any(static snapshot =>
            snapshot.State is TaskManagerTaskState.Waiting or TaskManagerTaskState.Running or
                TaskManagerTaskState.Failed or TaskManagerTaskState.Canceled);

    public double AverageActiveProgress()
    {
        TaskManagerEntrySnapshot[] active = _snapshots.Values
            .Where(static snapshot => snapshot.State is TaskManagerTaskState.Waiting or TaskManagerTaskState.Running)
            .ToArray();
        TaskManagerEntrySnapshot[] source = active.Length == 0 ? _snapshots.Values.ToArray() : active;
        return source.Length == 0
            ? 1d
            : source.Average(static snapshot => Math.Clamp(snapshot.Progress, 0d, 1d));
    }

    public void PublishProgress()
    {
        double progress = HasActiveTask ? AverageActiveProgress() : HasVisibleTask ? 1d : 0d;
        _messenger.Send(new TaskProgressChangedMessage(
            HasVisibleTask,
            HasActiveTask,
            progress,
            IsTaskManagerVisible));
    }
}

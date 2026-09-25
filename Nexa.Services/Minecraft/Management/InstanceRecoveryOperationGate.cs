namespace Nexa.Services.Minecraft.Management;

/// <summary>Background snapshots yield to foreground operations; ordinary operations remain concurrent.</summary>
internal static class InstanceRecoveryOperationGate
{
    internal sealed class Entry
    {
        internal int References, Operations, WaitingRestores;
        internal Lease? Capture, Restore;
        internal TaskCompletionSource Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<string, Entry> Entries = new(MinecraftLibraryService.PathComparer);

    internal sealed class Lease : IDisposable
    {
        private readonly string _root;
        private readonly Entry _entry;
        private readonly LeaseKind _kind;
        private int _disposed;
        private readonly CancellationTokenSource _stop = new();
        internal CancellationToken Token => _stop.Token;
        internal Lease(string root, Entry entry, LeaseKind kind) { _root = root; _entry = entry; _kind = kind; }
        internal void Cancel()
        {
            try { _stop.Cancel(); } catch (ObjectDisposedException) { }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (Sync)
            {
                if (_kind == LeaseKind.Capture) _entry.Capture = null;
                else if (_kind == LeaseKind.Restore) _entry.Restore = null;
                else _entry.Operations--;
                Pulse(_entry);
                ReleaseReference(_root, _entry);
            }
            _stop.Dispose();
        }
    }

    internal enum LeaseKind { Operation, Capture, Restore }

    private static void Pulse(Entry entry)
    {
        var changed = entry.Changed;
        entry.Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        changed.TrySetResult();
    }

    private static string Normalize(string root) => Path.IsPathFullyQualified(root)
        ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
        : throw new ArgumentException("游戏根目录必须为绝对路径。", nameof(root));

    private static Entry Retain(string root)
    {
        if (!Entries.TryGetValue(root, out var entry)) Entries.Add(root, entry = new());
        entry.References++;
        return entry;
    }

    private static void ReleaseReference(string root, Entry entry)
    {
        if (--entry.References == 0) Entries.Remove(root);
    }

    internal static Lease? TryCapture(string root)
    {
        root = Normalize(root);
        lock (Sync)
        {
            var entry = Retain(root);
            if (entry.References != 1 || entry.Operations != 0 || entry.Capture is not null || entry.Restore is not null || entry.WaitingRestores != 0) { ReleaseReference(root, entry); return null; }
            return entry.Capture = new Lease(root, entry, LeaseKind.Capture);
        }
    }

    internal static async ValueTask<Lease> EnterOperationAsync(string root, CancellationToken token = default)
    {
        root = Normalize(root);
        Entry entry;
        lock (Sync) entry = Retain(root);
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                Lease? capture;
                Task changed;
                lock (Sync)
                {
                    if (entry.Capture is null && entry.Restore is null
                        && (entry.WaitingRestores == 0 || entry.Operations > 0))
                    {
                        entry.Operations++;
                        return new Lease(root, entry, LeaseKind.Operation);
                    }
                    capture = entry.Capture;
                    changed = entry.Changed.Task;
                }
                capture?.Cancel();
                await changed.WaitAsync(token).ConfigureAwait(false);
            }
        }
        catch
        {
            lock (Sync) ReleaseReference(root, entry);
            throw;
        }
    }

    internal static async ValueTask<Lease> EnterRestoreAsync(string root, CancellationToken token = default)
    {
        root = Normalize(root);
        Entry entry;
        lock (Sync) { entry = Retain(root); entry.WaitingRestores++; }
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                Lease? capture;
                Task changed;
                lock (Sync)
                {
                    if (entry.Capture is null && entry.Restore is null && entry.Operations == 0)
                    {
                        entry.WaitingRestores--;
                        return entry.Restore = new Lease(root, entry, LeaseKind.Restore);
                    }
                    capture = entry.Capture;
                    changed = entry.Changed.Task;
                }
                capture?.Cancel();
                await changed.WaitAsync(token).ConfigureAwait(false);
            }
        }
        catch
        {
            lock (Sync)
            {
                entry.WaitingRestores--;
                Pulse(entry);
                ReleaseReference(root, entry);
            }
            throw;
        }
    }

}

namespace Nexa.Services.Minecraft.Management;

/// <summary>Background snapshots yield to foreground operations; ordinary operations remain concurrent.</summary>
internal static class InstanceRecoveryOperationGate
{
    internal sealed class Entry
    {
        internal int References, Operations;
        internal Lease? Capture;
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<string, Entry> Entries = new(MinecraftLibraryService.PathComparer);

    internal sealed class Lease : IDisposable
    {
        private readonly string _root;
        private readonly Entry _entry;
        private readonly bool _capture;
        private int _disposed;
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token => _stop.Token;
        internal Task Released => _released.Task;
        internal Lease(string root, Entry entry, bool capture) { _root = root; _entry = entry; _capture = capture; }
        internal void Cancel()
        {
            try { _stop.Cancel(); } catch (ObjectDisposedException) { }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (Sync)
            {
                if (_capture) _entry.Capture = null; else _entry.Operations--;
                ReleaseReference(_root, _entry);
            }
            _released.TrySetResult();
            _stop.Dispose();
        }
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
            if (entry.References != 1 || entry.Operations != 0 || entry.Capture is not null) { ReleaseReference(root, entry); return null; }
            return entry.Capture = new Lease(root, entry, true);
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
                Lease capture;
                lock (Sync)
                {
                    if (entry.Capture is null)
                    {
                        entry.Operations++;
                        return new Lease(root, entry, false);
                    }
                    capture = entry.Capture;
                }
                capture.Cancel();
                await capture.Released.WaitAsync(token).ConfigureAwait(false);
            }
        }
        catch
        {
            lock (Sync) ReleaseReference(root, entry);
            throw;
        }
    }
}

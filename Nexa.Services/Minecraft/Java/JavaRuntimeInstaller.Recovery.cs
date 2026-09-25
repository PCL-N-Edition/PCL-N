namespace Nexa.Services.Minecraft.Java;

public sealed partial class JavaRuntimeInstaller
{
    public async Task<Nexa.Xsr.XsrResult> RecoverAsync(string root, CancellationToken token = default)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        lock (_gate)
        {
            if (_stopping) return Nexa.Xsr.XsrResult.Failure(Nexa.Xsr.XsrRuntimeErrors.Cancelled());
            _roots.Add(root);
        }
        using var queue = _tasks?.Begin(new("java-install:recovery:" + Guid.NewGuid().ToString("N"), "恢复 Java 安装", ["恢复 Java"], CanCancel: false));
        await _recoveryGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            foreach (var journal in await PendingAsync(root, token).ConfigureAwait(false))
            {
                lock (_gate) if (_stopping) { queue?.Paused(); return Nexa.Xsr.XsrResult.Failure(Nexa.Xsr.XsrRuntimeErrors.Cancelled()); }
                if (journal.CancelRequested)
                {
                    string path = Path.Combine(root, ".nexa-java.lock");
                    Nexa.Services.Minecraft.Management.RecoveryBlobStore.CheckLinks(path);
                    using var lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    await journal.CancelAsync(MatchesAsync).ConfigureAwait(false);
                    continue;
                }
                await InstallAsync(journal.Intent.Component, root, cancellationToken: token).ConfigureAwait(false);
            }
            queue?.Complete(); return Nexa.Xsr.XsrResult.Success();
        }
        catch (OperationCanceledException) { queue?.Paused(); return Nexa.Xsr.XsrResult.Failure(Nexa.Xsr.XsrRuntimeErrors.Cancelled()); }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        { queue?.Fail("Java 安装恢复需要处理。"); return Nexa.Xsr.XsrResult.Failure(MinecraftErrors.InvalidRequest(error.Message)); }
        finally { _recoveryGate.Release(); }
    }

    private static async Task CancelPendingAsync(string root)
    {
        if (!Directory.Exists(root)) return;
        string path = Path.Combine(root, ".nexa-java.lock");
        Nexa.Services.Minecraft.Management.RecoveryBlobStore.CheckLinks(path);
        using var lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        foreach (var journal in await PendingAsync(root, CancellationToken.None).ConfigureAwait(false))
            await journal.CancelAsync(MatchesAsync).ConfigureAwait(false);
    }

    private static async Task<JavaInstallJournal?> FindPendingAsync(string root, string component, CancellationToken token)
        => (await PendingAsync(root, token).ConfigureAwait(false)).FirstOrDefault(journal => journal.Intent.Component == component);

    private static async Task<IReadOnlyList<JavaInstallJournal>> PendingAsync(string root, CancellationToken token)
    {
        string directory = Path.Combine(root, JavaInstallJournal.DirectoryName);
        Nexa.Services.Minecraft.Management.RecoveryBlobStore.CheckLinks(directory);
        if (!Directory.Exists(directory)) return [];
        List<JavaInstallJournal> pending = [];
        int count = 0;
        foreach (string stage in Directory.EnumerateDirectories(directory))
        {
            if (++count > 4096) throw new InvalidDataException("Java 恢复记录过多。");
            if (!Guid.TryParseExact(Path.GetFileName(stage), "N", out _)) continue;
            var journal = await JavaInstallJournal.OpenAsync(root, stage, token).ConfigureAwait(false);
            if (!journal.Completed && !journal.Canceled) pending.Add(journal);
            if (pending.Count > 64) throw new InvalidDataException("Java 恢复任务过多。");
        }
        return pending;
    }
}

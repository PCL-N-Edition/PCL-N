using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Process;
using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Management;

public sealed record InstanceRecoveryRestoreCommand(string InstanceDirectory, Guid BaselineRevision,
    string Fingerprint, IReadOnlyList<InstanceRecoveryChange> Changes);

public sealed partial class InstanceRecoveryService
{
    public Task<XsrResult> RestoreAsync(InstanceRecoveryRestoreCommand command, CancellationToken token = default) => Task.Run(async () =>
    {
        try
        {
            if (!Path.IsPathFullyQualified(command.InstanceDirectory) || command.BaselineRevision == Guid.Empty
                || command.Changes.Count is 0 or > RecoverySnapshotStore.MaxFiles)
                throw new InvalidDataException("请选择需要恢复的更改。");
            string instance = Path.TrimEndingDirectorySeparator(Path.GetFullPath(command.InstanceDirectory));
            var versions = Directory.GetParent(instance);
            if (versions?.Name != "versions" || versions.Parent is null) throw new InvalidDataException("实例目录无效。");
            string root = versions.Parent.FullName;
            using var exclusive = await InstanceRecoveryOperationGate.EnterRestoreAsync(root, token).ConfigureAwait(false);
            using var instanceLease = AcquireRestoreLease(instance);
            var comparison = await ReadInternalAsync(new(instance), true, token).ConfigureAwait(false);
            if (!comparison.IsSuccess || comparison.Value!.BaselineRevision != command.BaselineRevision
                || comparison.Value.Fingerprint != command.Fingerprint)
                throw new InvalidDataException("恢复范围已变化，请刷新更改清单后重试。");
            var selected = command.Changes.ToHashSet();
            if (selected.Count != command.Changes.Count || selected.Any(item => !comparison.Value.Changes.Contains(item)))
                throw new InvalidDataException("所选更改不属于当前恢复预览。");

            string transactions = Path.Combine(instance, "Nexa", "Recovery", "transactions");
            RecoveryBlobStore.CheckLinks(transactions);
            if (Directory.Exists(transactions))
            {
                int count = 0;
                foreach (string directory in Directory.EnumerateDirectories(transactions))
                {
                    if (++count > 1024) throw new InvalidDataException("恢复记录过多，请先处理历史事务。");
                    RecoveryBlobStore.CheckLinks(directory);
                    string marker = Path.Combine(directory, "transaction.json");
                    if (!File.Exists(marker))
                    {
                        if (File.Exists(Path.Combine(directory, "apply.json"))) throw new IOException("存在未完成的恢复记录。");
                        continue; // Preparation alone never changes live files.
                    }
                    var record = JsonNode.Parse(await RecoveryFileTransaction.ReadRecordAsync(marker, 4096, token).ConfigureAwait(false));
                    if (record?["phase"]?.GetValue<string>() is not ("committed" or "rolled-back"))
                        throw new IOException("存在未完成的恢复记录，请先撤回该事务。");
                }
            }
            // Validate both isolated and shared directories before staging any data.
            RecoverySnapshot? snapshot;
            try { snapshot = await new RecoverySnapshotStore(instance, instance).ReadAsync(token).ConfigureAwait(false); }
            catch (InvalidDataException) { snapshot = await new RecoverySnapshotStore(instance, root).ReadAsync(token).ConfigureAwait(false); }
            if (snapshot is null) throw new InvalidDataException("成功快照不存在。");
            Task Validate(CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                if (store.TryResolve(MinecraftProcessStateComposition.SessionsKey, out var id)
                    && store.ReadCollection<MinecraftProcessSnapshot>(id, cancellationToken: ct).Items.Any(item =>
                        item.State is MinecraftProcessState.Created or MinecraftProcessState.Running
                        && (MinecraftLibraryService.PathComparer.Equals(item.InstanceDirectory, instance)
                            || MinecraftLibraryService.PathComparer.Equals(item.GameDirectory, snapshot.GameDirectory)
                            || item.InstanceDirectory is { } active && MinecraftLibraryService.PathComparer.Equals(Directory.GetParent(active)?.Parent?.FullName, root))))
                    throw new IOException("请先结束此游戏目录中的游戏进程再恢复快照。");
                return Task.CompletedTask;
            }
            await Validate(token).ConfigureAwait(false);
            var prepared = await RecoveryRestorePreparation.PrepareAsync(instance, snapshot.GameDirectory, command.BaselineRevision, token).ConfigureAwait(false);
            var snapshotStore = new RecoverySnapshotStore(instance, snapshot.GameDirectory);
            var files = new List<RecoveryFileEdit>();
            var budget = new RecoveryByteBudget(RecoveryBlobStore.MaxTransactionBytes);
            foreach (var change in selected.Where(item => item.SettingKey is null))
            {
                var source = new RecoverySource(change.Area ?? throw new InvalidDataException("恢复条目缺少区域。"), change.Path);
                string path = snapshotStore.ResolveSource(source);
                var before = await RecoveryFileTransaction.ObserveAsync(path, budget, token).ConfigureAwait(false);
                var after = prepared.Snapshot.Files.FirstOrDefault(item =>
                    MinecraftLibraryService.PathComparer.Equals(snapshotStore.ResolveSource(item.Source), path))?.Blob;
                files.Add(new(source, before, after));
            }
            var settingsPlan = settings.PlanRecoverySettings(instance, prepared.Snapshot.SettingsDocument,
                selected.Where(item => item.SettingKey is not null).Select(item => item.SettingKey!).ToArray());
            // Hash again after preparation: never silently incorporate an external edit into the transaction.
            comparison = await ReadInternalAsync(new(instance), true, token).ConfigureAwait(false);
            if (!comparison.IsSuccess || comparison.Value!.Fingerprint != command.Fingerprint)
                throw new IOException("准备恢复期间文件或设置发生变化，请刷新后重试。");
            await RecoveryTransactionCoordinator.ExecuteAsync(prepared, files, settingsPlan, settings, Validate, restoreLeaseHeld: true, token: token).ConfigureAwait(false);
            CleanupCompletedRestore(prepared.Directory);
            return XsrResult.Success();
        }
        catch (OperationCanceledException) { return XsrResult.Failure(XsrRuntimeErrors.Cancelled()); }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            log?.Warn("Recovery", $"恢复未完成：{error.GetType().Name}");
            return XsrResult.Failure(MinecraftErrors.InvalidRequest(error is InvalidDataException or IOException
                ? error.Message : "恢复未完成，已保留快照及事务备份。"));
        }
    }, token);

    private void CleanupCompletedRestore(string directory)
    {
        try
        {
            RecoveryBlobStore.CheckLinks(directory);
            if (Directory.EnumerateDirectories(directory).Any()) return;
            string[] files = Directory.EnumerateFiles(directory).Take(RecoverySnapshotStore.MaxFiles * 2 + 6).ToArray();
            bool Known(string file)
            {
                string name = Path.GetFileName(file);
                if (name is "prepared.json" or "apply.json" or "file-plan.json" or "settings-plan.json" or "transaction.json") return true;
                string hash = Path.GetFileNameWithoutExtension(name);
                return Path.GetExtension(name) is ".data" or ".before" && hash.Length == 64
                    && hash.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
            }
            if (files.Length >= RecoverySnapshotStore.MaxFiles * 2 + 6 || files.Any(file => !Known(file))) return;
            foreach (string file in files) RecoveryBlobStore.CheckLinks(file);
            foreach (string file in files.Where(file => Path.GetFileName(file) != "transaction.json")) File.Delete(file);
            File.Delete(Path.Combine(directory, "transaction.json"));
            Directory.Delete(directory, recursive: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { log?.Warn("Recovery", "恢复已经完成，部分暂存文件将在下次启动清理。"); }
    }
}

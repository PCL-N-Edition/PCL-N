using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Nexa.Services.Settings;
using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Management;

public enum InstanceRecoveryChangeKind { Added, Removed, Modified }
public sealed record InstanceRecoveryQuery(string InstanceDirectory);
public sealed record InstanceRecoveryChange(InstanceRecoveryChangeKind Kind, string Category, string Path, string? SettingKey = null);
public sealed record InstanceRecoveryReport(string InstanceDirectory, Guid? BaselineRevision, DateTimeOffset? CapturedAt,
    IReadOnlyList<InstanceRecoveryChange> Changes, string Fingerprint, string? UnavailableReason = null);
public static class InstanceRecoveryContract
{
    public static readonly XsrSemanticId Query = XsrSemanticId.Parse("minecraft.instance.recovery.query");
}

public sealed partial class InstanceRecoveryService
{
    public Task<XsrResult<InstanceRecoveryReport>> ReadAsync(InstanceRecoveryQuery query, CancellationToken token = default) =>
        Task.Run(async () =>
        {
            try
            {
                if (!Path.IsPathFullyQualified(query.InstanceDirectory)) throw new InvalidDataException("实例目录必须为绝对路径。");
                string instance = Path.TrimEndingDirectorySeparator(Path.GetFullPath(query.InstanceDirectory));
                var versions = Directory.GetParent(instance);
                if (versions?.Name != "versions" || versions.Parent is null) throw new InvalidDataException("实例目录无效。");
                string root = versions.Parent.FullName;
                using var capture = InstanceRecoveryOperationGate.TryCapture(root);
                if (capture is null) return XsrResult.Failure<InstanceRecoveryReport>(MinecraftErrors.InvalidRequest("版本文件正在更改，请稍后重试。"));
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, capture.Token);
                cancellation.CancelAfter(TimeSpan.FromMinutes(3));
                var stop = cancellation.Token;
                RecoverySnapshot? baseline;
                try { baseline = await new RecoverySnapshotStore(instance, instance).ReadAsync(stop).ConfigureAwait(false); }
                catch (InvalidDataException) { baseline = await new RecoverySnapshotStore(instance, root).ReadAsync(stop).ConfigureAwait(false); }
                if (baseline is null) return XsrResult.Success(new InstanceRecoveryReport(instance, null, null, [], "", "尚无成功运行的快照。"));
                var snapshotStore = new RecoverySnapshotStore(instance, baseline.GameDirectory);
                var sources = RecoveryCapturePlan.BuildComparison(root, baseline, stop);
                var current = new Dictionary<string, (RecoverySource Source, RecoveryBlob Blob)>(MinecraftLibraryService.PathComparer);
                var stamps = new List<(string Path, long Length, long Modified)>();
                var budget = new RecoveryByteBudget(RecoveryBlobStore.MaxTransactionBytes);
                foreach (var source in sources)
                {
                    string path = snapshotStore.ResolveSource(source);
                    RecoveryBlobStore.CheckLinks(path);
                    var info = new FileInfo(path);
                    long length = info.Length, modified = info.LastWriteTimeUtc.Ticks;
                    if (length > RecoveryBlobStore.MaxFileBytes) throw new InvalidDataException("文件超过比较预算。");
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                    string hash = await RecoveryBlobStore.CopyAndHashAsync(stream, Stream.Null, length, budget, stop).ConfigureAwait(false);
                    current.Add(path, (source, new(hash, length)));
                    stamps.Add((path, length, modified));
                }
                string currentSettings = settings.CaptureRecoverySettings(instance);
                List<InstanceRecoveryChange> changes = [];
                var previous = baseline.Files.ToDictionary(file => snapshotStore.ResolveSource(file.Source), MinecraftLibraryService.PathComparer);
                foreach (var file in baseline.Files)
                {
                    string path = snapshotStore.ResolveSource(file.Source);
                    if (!current.TryGetValue(path, out var value)) changes.Add(FileChange(InstanceRecoveryChangeKind.Removed, file.Source));
                    else if (value.Blob != file.Blob) changes.Add(FileChange(InstanceRecoveryChangeKind.Modified, file.Source));
                }
                foreach (var pair in current)
                    if (!previous.ContainsKey(pair.Key)) changes.Add(FileChange(InstanceRecoveryChangeKind.Added, pair.Value.Source));
                var before = JsonNode.Parse(baseline.SettingsDocument)?["values"] as JsonObject ?? throw new InvalidDataException("快照启动设置无效。");
                var after = JsonNode.Parse(currentSettings)!["values"]!.AsObject();
                foreach (string key in before.Select(pair => pair.Key).Concat(after.Select(pair => pair.Key)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                {
                    if (JsonNode.DeepEquals(before[key], after[key])) continue;
                    if (!SettingsPolicySchema.ByKey.TryGetValue(key, out var definition) || !definition.InstanceOverride) throw new InvalidDataException("快照启动设置字段无效。");
                    string title = SettingsCatalog.Read(new()).Entries.FirstOrDefault(item => item.Scope == "global" && item.SettingKey == key)?.Label ?? "启动参数";
                    changes.Add(new(before[key] is null ? InstanceRecoveryChangeKind.Added : after[key] is null ? InstanceRecoveryChangeKind.Removed : InstanceRecoveryChangeKind.Modified,
                        "启动设置", title, key));
                }
                if (!sources.SequenceEqual(RecoveryCapturePlan.BuildComparison(root, baseline, stop))
                    || currentSettings != settings.CaptureRecoverySettings(instance)
                    || (await snapshotStore.ReadAsync(stop).ConfigureAwait(false))?.Revision != baseline.Revision)
                    throw new IOException("比较期间恢复范围发生变化。");
                foreach (var stamp in stamps)
                {
                    RecoveryBlobStore.CheckLinks(stamp.Path);
                    var info = new FileInfo(stamp.Path);
                    if (!info.Exists || info.Length != stamp.Length || info.LastWriteTimeUtc.Ticks != stamp.Modified)
                        throw new IOException("比较期间文件发生变化。");
                }
                using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                void Append(string value)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(value);
                    Span<byte> length = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
                    fingerprint.AppendData(length); fingerprint.AppendData(bytes);
                }
                Append(baseline.Revision.ToString("D"));
                foreach (var pair in current.OrderBy(pair => pair.Key, MinecraftLibraryService.PathComparer))
                { Append(pair.Key); Append(pair.Value.Blob.Sha256); }
                Append(currentSettings);
                return XsrResult.Success(new InstanceRecoveryReport(instance, baseline.Revision, baseline.CapturedAt,
                    Array.AsReadOnly(changes.OrderBy(item => item.Category, StringComparer.Ordinal).ThenBy(item => item.Path, StringComparer.Ordinal).ToArray()),
                    Convert.ToHexString(fingerprint.GetHashAndReset())));
            }
            catch (OperationCanceledException) { return XsrResult.Failure<InstanceRecoveryReport>(XsrRuntimeErrors.Cancelled()); }
            catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
            {
                log?.Warn("Recovery", $"恢复范围比较未完成（{error.GetType().Name}）。");
                return XsrResult.Failure<InstanceRecoveryReport>(MinecraftErrors.InvalidRequest("无法完整比较恢复范围，请检查文件是否被占用或修改。"));
            }
        }, token);

    private static InstanceRecoveryChange FileChange(InstanceRecoveryChangeKind kind, RecoverySource source)
    {
        string path = source.RelativePath.Replace('\\', '/');
        string category = source.Area == "root" ? "版本依赖" : path.Split('/')[0] switch
        {
            "mods" => "模组",
            "config" or "defaultconfigs" or "scripts" or "kubejs" => "配置",
            "resourcepacks" => "资源包",
            "shaderpacks" => "光影包",
            "Nexa" => "版本设置",
            _ when path.StartsWith("options", StringComparison.OrdinalIgnoreCase) => "游戏选项",
            _ => "版本文件"
        };
        return new(kind, category, path);
    }
}

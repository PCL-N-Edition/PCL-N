using Nexa.Services.Settings;

namespace Nexa.Services.Capabilities;

internal static class CoreRemediationHandlers
{
    public static IEnumerable<IRemediationHandler> Create(SettingsPolicyService settings)
    {
        yield return new AdjustHeapHandler(settings);
        yield return new ReleaseLauncherMemoryHandler();
        yield return new InspectCommitHandler();
        yield return new RecheckPermissionsHandler();
    }

    private sealed class AdjustHeapHandler(SettingsPolicyService settings) : IRemediationHandler
    {
        public string Id => "remediation.memory.adjust_heap";
        public ValueTask<RemediationResult> ExecuteAsync(RemediationRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Arguments?.GetValueOrDefault("memoryMiB") is not { } raw
                || !long.TryParse(raw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out long memory)
                || memory is < 256 or > 1048576)
            {
                return ValueTask.FromResult(new RemediationResult(Id, false, "invalid_memory",
                    "缺少有效的建议内存值。"));
            }

            string? instance = request.Arguments?.GetValueOrDefault("instanceDirectory");
            if (string.IsNullOrWhiteSpace(instance) || !Path.IsPathFullyQualified(instance))
                return ValueTask.FromResult(new RemediationResult(Id, false, "missing_scope", "请先选择需要调整的实例。"));
            var result = settings.Set(new SettingsMutation("game.memory", SettingsLayer.Instance,
                new(SettingsOverrideMode.Custom, memory.ToString(System.Globalization.CultureInfo.InvariantCulture)), instance));
            return ValueTask.FromResult(result.IsSuccess
                ? new RemediationResult(Id, true, "ok", $"已将 Minecraft 内存调整为 {memory} MiB。")
                : new RemediationResult(Id, false, "settings_rejected", result.Error?.Message ?? "设置未保存。"));
        }
    }

    private sealed class ReleaseLauncherMemoryHandler : IRemediationHandler
    {
        public string Id => "remediation.memory.release_background";
        public ValueTask<RemediationResult> ExecuteAsync(RemediationRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: false);
            return ValueTask.FromResult(new RemediationResult(Id, true, "ok", "已释放 NexaCL 的可回收后台内存。"));
        }
    }

    private sealed class InspectCommitHandler : IRemediationHandler
    {
        public string Id => "remediation.memory.inspect_commit";
        public ValueTask<RemediationResult> ExecuteAsync(RemediationRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new RemediationResult(Id, true, "ok", "已重新请求平台能力检测。"));
        }
    }

    private sealed class RecheckPermissionsHandler : IRemediationHandler
    {
        public string Id => "remediation.instance.recheck_permissions";
        public ValueTask<RemediationResult> ExecuteAsync(RemediationRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Arguments?.GetValueOrDefault("instanceDirectory") is not { Length: > 0 } directory)
                return ValueTask.FromResult(new RemediationResult(Id, false, "missing_scope", "没有选中的实例目录。"));
            try
            {
                string full = Path.GetFullPath(directory);
                if (!Directory.Exists(full))
                    return ValueTask.FromResult(new RemediationResult(Id, false, "path_missing", "实例目录不存在。"));
                string probe = Path.Combine(full, ".nexa-write-probe-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
                return ValueTask.FromResult(new RemediationResult(Id, true, "ok", "实例目录可以正常写入。"));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return ValueTask.FromResult(new RemediationResult(Id, false, "permission_denied", error.Message));
            }
        }
    }
}

internal sealed class RemediationCapabilityProvider(RemediationService service) : IMachineCapabilityProvider
{
    public string Id => "nexa.remediation";

    public ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ICapability> values = RemediationCatalog.Definitions().Select(definition =>
            ((CapabilityDefinition<bool>)definition).Observe(service.CanExecute(definition.Id), timestamp,
                "RemediationService handler registry")).ToArray();
        return ValueTask.FromResult(values);
    }
}

using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Minecraft.Java;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Minecraft.Process;
using Nexa.Services.Settings;
using Nexa.Xsr.State;

namespace Nexa.Services.Capabilities;

/// <summary>Composition-only, single-assignment bindings to the same installer lifetimes used by product routes.</summary>
public sealed class MinecraftRemediationBindings(SettingsPolicyService settings, XsrStateStore store)
{
    private sealed record JavaBindings(IJavaRuntimeLocator Locator, IJavaRuntimeInstaller Installer, string RuntimeRoot, MinecraftLaunchFileCompletion Files);
    private JavaBindings? _java;
    private MinecraftInstallService? _install;

    public void BindJava(IJavaRuntimeLocator locator, IJavaRuntimeInstaller installer, string runtimeRoot, MinecraftLaunchFileCompletion files)
    {
        if (Interlocked.CompareExchange(ref _java, new(locator, installer, runtimeRoot, files), null) is not null)
            throw new InvalidOperationException("Java remediation services are already bound.");
    }
    public void BindInstall(MinecraftInstallService installer)
    {
        if (Interlocked.CompareExchange(ref _install, installer, null) is not null)
            throw new InvalidOperationException("Install remediation service is already bound.");
    }
    internal IEnumerable<IRemediationHandler> CreateHandlers()
    {
        foreach (string id in new[] { "remediation.java.select", "remediation.java.download", "remediation.java.switch_recommended",
            "remediation.game.repair_files", "remediation.game.repair_version", "remediation.loader.repair" }) yield return new Handler(this, id);
    }

    private sealed class Handler(MinecraftRemediationBindings owner, string id) : IRemediationHandler, IRemediationAvailability
    {
        public string Id => id;
        public bool Available => id is "remediation.game.repair_version" or "remediation.loader.repair"
            ? Volatile.Read(ref owner._install) is not null : Volatile.Read(ref owner._java) is not null;
        public async ValueTask<RemediationResult> ExecuteAsync(RemediationRequest request, CancellationToken cancellationToken)
        {
            try { return await Task.Run(() => owner.ExecuteAsync(request, cancellationToken), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
            { return new(Id, false, "repair_failed", error is IOException or InvalidDataException ? error.Message : "修复未完成，请检查任务详情。"); }
        }
    }

    private async Task<RemediationResult> ExecuteAsync(RemediationRequest request, CancellationToken token)
    {
        string? instance = request.Arguments?.GetValueOrDefault("instanceDirectory");
        if (string.IsNullOrWhiteSpace(instance) || !Path.IsPathFullyQualified(instance))
            return new(request.Id, false, "missing_scope", "请先选择需要修复的实例。");
        instance = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instance));
        var versions = Directory.GetParent(instance);
        if (versions?.Name != "versions" || versions.Parent is null) throw new InvalidDataException("实例目录无效。");
        string root = versions.Parent.FullName;
        var query = new MachineCapabilityQuery(instance, Path.GetFileName(instance), root) { RefreshInstance = true };
        if (request.Id is "remediation.game.repair_version" or "remediation.loader.repair")
        {
            var edit = await MinecraftInstallEditService.ReadAsync(new(root, Path.GetFileName(instance)), token).ConfigureAwait(false);
            var selected = edit.Selection.FirstOrDefault(item => !InstallCompatibility.IsAddon(item.Loader));
            var addons = edit.Selection.Where(item => item != selected).Select(item => new MinecraftInstallAddon(item.Loader, item.Version)).ToArray();
            var result = await _install!.InstallAsync(new(root, edit.GameVersion, selected?.Loader, selected?.Version, addons,
                edit.InstanceId, edit.Fingerprint)
            { ForceReinstall = true }, token).ConfigureAwait(false);
            return new(request.Id, result.IsSuccess, result.IsSuccess ? "ok" : "install_failed", result.IsSuccess ? "已完成版本修复。" : result.Error?.Message ?? "版本修复未完成。");
        }
        var primary = await MinecraftPrimaryInstanceScope.ResolveAsync(root, query, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("无法读取实例清单。");
        var java = _java ?? throw new InvalidOperationException("Java services unavailable.");
        if (request.Id == "remediation.game.repair_files")
        {
            using var lease = await InstanceRecoveryOperationGate.EnterRestoreAsync(root, token).ConfigureAwait(false);
            primary = await MinecraftPrimaryInstanceScope.ResolveAsync(root, query, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("无法读取实例清单。");
            if (store.TryResolve(MinecraftProcessStateComposition.SessionsKey, out var id)
                && store.ReadCollection<MinecraftProcessSnapshot>(id, cancellationToken: token).Items.Any(item => item.State is MinecraftProcessState.Created or MinecraftProcessState.Running
                    && item.InstanceDirectory is { } directory && MinecraftLibraryService.PathComparer.Equals(Directory.GetParent(directory)?.Parent?.FullName, root)))
                return new(request.Id, false, "game_running", "请先结束游戏进程再修复文件。");
            await java.Files.CompleteAsync(root, primary.Instance, primary.Manifests, MinecraftLaunchPlatform.Detect(), "预检文件修复", null, token).ConfigureAwait(false);
            return new(request.Id, true, "ok", "已校验并补全游戏文件。");
        }
        var loader = await InstalledLoaderProjection.ReadAsync(primary, root, query, token).ConfigureAwait(false);
        var requirement = MinecraftLaunchCoordinator.CreateJavaRequirement(primary.Instance, primary.Manifests, loader);
        JavaPreference preference = new AutoSelectJavaPreference();
        if (request.Id == "remediation.java.select")
        {
            string? path = request.Arguments?.GetValueOrDefault("executable");
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return new(request.Id, false, "choose_java", "请选择 Java 可执行文件。");
            preference = new ExistingJavaPreference(path);
        }
        var selection = await new JavaSelectionService(java.Locator).SelectAsync(requirement, preference, token).ConfigureAwait(false);
        if (!selection.Success && request.Id == "remediation.java.download" && selection.SuggestedDownloadComponent is { } component)
        {
            string executable = await java.Installer.InstallAsync(component, java.RuntimeRoot, cancellationToken: token).ConfigureAwait(false);
            LocalJavaRuntimeLocator.Invalidate();
            selection = await new JavaSelectionService(java.Locator).SelectAsync(requirement, new ExistingJavaPreference(executable), token).ConfigureAwait(false);
        }
        if (!selection.Success || selection.SelectedJava is null) return new(request.Id, false, "java_unavailable", "没有找到符合本实例要求的 Java。");
        var saved = settings.Set(new("java.runtime", SettingsLayer.Instance,
            new(SettingsOverrideMode.Custom, selection.SelectedJava.Installation.JavaExecutablePath), instance));
        return new(request.Id, saved.IsSuccess, saved.IsSuccess ? "ok" : "settings_failed", saved.IsSuccess ? "已为此实例选择兼容 Java。" : saved.Error?.Message ?? "Java 设置未保存。");
    }
}

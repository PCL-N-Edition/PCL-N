using Nexa.Services.Composition;
using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Install;
using Nexa.UI.Next.Backend.Avalonia;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Ui;

/// <summary>Converts native paths to Service queries and user-confirmed commands.</summary>
internal sealed class MinecraftDropController : IDisposable
{
    private readonly AvaloniaUiPlatformActions _platform;
    private readonly MinecraftFolderImportRuntime _imports;
    private readonly XsrCommandRouter _library;
    private readonly XsrStateStore _store;
    private readonly DesktopFeedbackService _feedback;
    private readonly CancellationTokenSource _lifetime = new();
    private int _busy;
    private bool _disposed;
    private Guid _jarDialog;

    public MinecraftDropController(AvaloniaUiPlatformActions platform, MinecraftFolderImportRuntime imports,
        XsrCommandRouter library, XsrStateStore store, DesktopFeedbackService feedback)
    {
        _platform = platform; _imports = imports; _library = library; _store = store; _feedback = feedback;
        platform.FilesDropped += OnDrop;
    }

    private void OnDrop(IReadOnlyList<string> paths)
    {
        if (_disposed) return;
        if (paths.Count != 1) { _feedback.Error("请每次拖入一个文件或文件夹。"); return; }
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        var snapshot = (MinecraftLibrarySnapshot?)_store.ReadAppliedValue(_store.Resolve(MinecraftLibraryService.StateKey));
        _ = InspectAsync(paths[0], snapshot?.RootDirectory, snapshot?.SelectedInstanceId ?? "");
    }

    private async Task InspectAsync(string path, string? target, string instanceId)
    {
        bool awaitingChoice = false;
        try
        {
            if (!_imports.Queries.TryResolve(MinecraftFolderImportContract.Inspect, out var route)) return;
            var result = await _imports.Queries.QueryAsync<MinecraftFolderInspectQuery, MinecraftFolderInspection>(route, new(path), cancellationToken: _lifetime.Token).ConfigureAwait(false);
            if (_disposed) return;
            if (!result.IsSuccess) { _feedback.Error(result.Error?.Message ?? "无法读取拖入的文件夹。"); return; }
            var item = result.Value!;
            if (item.Kind is MinecraftFolderKind.Modpack or MinecraftFolderKind.Jar && target is null)
            { _feedback.Error("请先选择游戏目录，再拖入文件。"); return; }
            if (item.Modpack is { } pack && target is not null)
            {
                _jarDialog = _feedback.ShowDialog("modpack.install", "安装 " + pack.Name,
                    $"{pack.Format} · {pack.Version}\nMinecraft {pack.Game}" + (pack.Loader is null ? "" : $" · {pack.Loader} {pack.Build}")
                    + $"\n安装到：{target}\n独立版本：{pack.InstanceId}\n必选文件 {pack.RequiredFiles} 个，可选文件 {pack.OptionalFiles} 个。默认仅安装必选客户端文件。",
                    "开始安装", "取消", accepted =>
                    {
                        if (accepted) _ = ExecuteModpackAsync(pack, target, false);
                        else Interlocked.Exchange(ref _busy, 0);
                    }, pack.OptionalFiles > 0 ? "包含可选文件" : null, pack.OptionalFiles > 0 ? () =>
                    {
                        _feedback.DismissDialog(_jarDialog);
                        _ = ExecuteModpackAsync(pack, target, true);
                    }
                : null);
                awaitingChoice = true;
                return;
            }
            if (item.Jar is { } jar && target is not null)
            {
                _jarDialog = _feedback.ShowDialog("jar.use", "如何使用 " + item.Name,
                    string.IsNullOrEmpty(instanceId) ? "当前没有选择版本。添加模组或修改核心前，请先选择版本。" : $"当前版本：{instanceId}。模组将添加到此版本实际使用的 mods 目录。",
                    "添加模组", "取消", accepted =>
                    {
                        if (accepted) _ = ExecuteJarAsync(jar, target, instanceId, LocalJarAction.Mod);
                        else Interlocked.Exchange(ref _busy, 0);
                    }, "其他用途", () => ShowOtherJarUses(jar, target, instanceId));
                awaitingChoice = true;
                return;
            }
            if (item.Kind == MinecraftFolderKind.Unsupported)
            { _feedback.Error("未识别到游戏目录、版本目录、JAR 或受支持的整合包。"); return; }
            if (item.Kind == MinecraftFolderKind.Version && target is null) return;
            bool root = item.Kind == MinecraftFolderKind.GameRoot;
            _feedback.ShowDialog("folder.import", root ? "添加游戏目录" : "导入 " + item.Name,
                root ? item.Path : $"复制到 {target}。源文件保留，同名版本不会覆盖。共享的库和资源不包含在此次复制中。",
                root ? "添加目录" : "导入", "取消", confirmed =>
                {
                    if (confirmed && !_disposed) _ = ExecuteAsync(item, target);
                    else Interlocked.Exchange(ref _busy, 0);
                });
            awaitingChoice = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        { if (!_disposed) _feedback.Error("无法识别拖入内容：" + error.Message); }
        finally { if (!awaitingChoice) Interlocked.Exchange(ref _busy, 0); }
    }

    private async Task ExecuteModpackAsync(MinecraftModpackPreview pack, string root, bool includeOptional)
    {
        try
        {
            if (_disposed || !_imports.Commands.TryResolve(MinecraftModpackContract.Install, out var route)) return;
            var result = await _imports.Commands.Dispatch(route, new MinecraftModpackCommand(pack, root, includeOptional), cancellationToken: _lifetime.Token).Completion.ConfigureAwait(false);
            if (!_disposed && !result.IsSuccess && result.Error?.Code != XsrRuntimeErrors.Cancelled().Code)
                _feedback.Error(result.Error?.Message ?? "整合包安装未完成。");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        { if (!_disposed) _feedback.Error("整合包安装未完成：" + error.Message); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private void ShowOtherJarUses(LocalJarArtifact jar, string root, string instanceId)
    {
        if (_disposed) return;
        _feedback.DismissDialog(_jarDialog);
        _jarDialog = _feedback.ShowDialog("jar.other", "选择 JAR 用途",
            "核心补丁会合并归档内容并备份原核心，只支持拥有独立核心的版本。加载器安装会执行此安装器，创建独立版本。",
            "Patch 版本核心", "取消", accepted =>
            {
                if (accepted) _ = ExecuteJarAsync(jar, root, instanceId, LocalJarAction.CorePatch);
                else Interlocked.Exchange(ref _busy, 0);
            }, "安装加载器", () =>
            {
                _feedback.DismissDialog(_jarDialog);
                _ = ExecuteJarAsync(jar, root, instanceId, LocalJarAction.Loader);
            });
    }

    private async Task ExecuteJarAsync(LocalJarArtifact jar, string root, string instanceId, LocalJarAction action)
    {
        try
        {
            if (_disposed || !_imports.Commands.TryResolve(MinecraftLocalJarContract.Import, out var route)) return;
            var result = await _imports.Commands.Dispatch(route, new MinecraftLocalJarCommand(jar, root, instanceId, action), cancellationToken: _lifetime.Token).Completion.ConfigureAwait(false);
            if (!_disposed && !result.IsSuccess && result.Error?.Code != XsrRuntimeErrors.Cancelled().Code)
                _feedback.Error(result.Error?.Message ?? "JAR 导入未完成。");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        { if (!_disposed) _feedback.Error("JAR 导入未完成：" + error.Message); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private async Task ExecuteAsync(MinecraftFolderInspection item, string? target)
    {
        try
        {
            XsrResult result;
            if (item.Kind == MinecraftFolderKind.GameRoot)
            {
                if (!_library.TryResolve(MinecraftLibraryRoutes.Directory, out var route)) return;
                result = await _library.Dispatch(route, new MinecraftLibraryDirectoryCommand(item.Path, true), cancellationToken: _lifetime.Token).Completion.ConfigureAwait(false);
            }
            else
            {
                if (!_imports.Commands.TryResolve(MinecraftFolderImportContract.Import, out var route)) return;
                result = await _imports.Commands.Dispatch(route, new MinecraftFolderImportCommand(item.Path, target!), cancellationToken: _lifetime.Token).Completion.ConfigureAwait(false);
            }
            if (!_disposed && !result.IsSuccess && result.Error?.Code != XsrRuntimeErrors.Cancelled().Code)
                _feedback.Error(result.Error?.Message ?? "导入未完成。");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        { if (!_disposed) _feedback.Error("导入未完成：" + error.Message); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    public void Dispose()
    {
        _disposed = true;
        _platform.FilesDropped -= OnDrop;
        _lifetime.Cancel();
        // Pending queries/commands own cancellation registrations until completion.
    }
}

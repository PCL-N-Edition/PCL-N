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

    public MinecraftDropController(AvaloniaUiPlatformActions platform, MinecraftFolderImportRuntime imports,
        XsrCommandRouter library, XsrStateStore store, DesktopFeedbackService feedback)
    {
        _platform = platform; _imports = imports; _library = library; _store = store; _feedback = feedback;
        platform.FilesDropped += OnDrop;
    }

    private void OnDrop(IReadOnlyList<string> paths)
    {
        if (_disposed) return;
        if (paths.Count != 1) { _feedback.Error("请每次拖入一个游戏文件夹或版本文件夹。"); return; }
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        var snapshot = (MinecraftLibrarySnapshot?)_store.ReadAppliedValue(_store.Resolve(MinecraftLibraryService.StateKey));
        _ = InspectAsync(paths[0], snapshot?.RootDirectory);
    }

    private async Task InspectAsync(string path, string? target)
    {
        bool awaitingChoice = false;
        try
        {
            if (!_imports.Queries.TryResolve(MinecraftFolderImportContract.Inspect, out var route)) return;
            var result = await _imports.Queries.QueryAsync<MinecraftFolderInspectQuery, MinecraftFolderInspection>(route, new(path), cancellationToken: _lifetime.Token).ConfigureAwait(false);
            if (_disposed) return;
            if (!result.IsSuccess) { _feedback.Error(result.Error?.Message ?? "无法读取拖入的文件夹。"); return; }
            var item = result.Value!;
            if (item.Kind == MinecraftFolderKind.Unsupported)
            { _feedback.Error("未找到游戏目录或版本描述文件。当前拖入入口支持游戏文件夹和版本文件夹。"); return; }
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

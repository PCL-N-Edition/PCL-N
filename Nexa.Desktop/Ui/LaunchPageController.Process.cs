using Nexa.Pxml;
using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Crash;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Process;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Ui;

internal sealed partial class LaunchPageController
{
    private readonly Dictionary<Guid, (XsrUiEntityId Power, XsrUiEntityId Logs)> _processDocks = [];
    private readonly HashSet<Guid> _shownFailures = [];
    private XsrUiEntityId _javaChoicePage;
    private int _showJavaChoices, _returnFromJavaChoice;
    private bool _processPresentationInitialized;
    private long _processRevision = -1;

    private void ShowJavaVersionChoice()
    {
        Interlocked.Exchange(ref _showJavaChoices, 1);
        Publish(LaunchPageState.LaunchingStageKey, "选择 Java 版本");
    }

    private void ProjectProcessFeedback()
    {
        if (Interlocked.Exchange(ref _returnFromJavaChoice, 0) != 0 && _shell.Stage.Navigation.Current == _javaChoicePage)
        { _shell.Stage.Navigation.Pop(); _returnFocus.TryPop(out _); UpdateTitleBar(); }
        if (Interlocked.Exchange(ref _showJavaChoices, 0) != 0)
        {
            if (!_javaChoicePage.IsAssigned)
            {
                var parent = _shell.Tree.Create("java-choice-loader");
                var template = PxmlCompiler.Compile(PxmlParser.Parse(ReadEmbeddedResource("Ui.JavaChoicePage.pxml")));
                _javaChoicePage = PxmlUiLoader.Load(template, _shell.Tree, _store, parent);
                _shell.Tree.Detach(_javaChoicePage);
                _shell.Tree.Destroy(parent);
                _shell.Tree.Walk(_javaChoicePage, entity =>
                {
                    _shell.Tree.SetComponent(entity, new XsrUiVisualStyle
                    {
                        Foreground = PrimaryText,
                        FontSize = 15,
                        Background = _shell.Tree.GetComponent<XsrUiInput>(entity) is null ? XsrUiColor.Transparent : DesktopUiPalette.CapsuleBackground,
                        Hover = DesktopUiPalette.CapsuleHover,
                        CornerRadius = 12
                    });
                    return true;
                });
            }
            var choices = _store.ReadAppliedValue(_store.Resolve(MinecraftLaunchProgressState.JavaChoicesKey)) as IReadOnlyList<int>;
            _shell.Tree.Walk(_javaChoicePage, entity =>
            {
                string name = _shell.Tree.Name(entity);
                if (name.StartsWith("JavaChoice", StringComparison.Ordinal) && int.TryParse(name[10..], out int major))
                    _shell.Tree.GetComponent<XsrUiElement>(entity)!.IsVisible = choices is null || choices.Contains(major);
                return true;
            });
            _shell.Tree.MarkDirty(_javaChoicePage, XsrUiDirtyKinds.Layout);
            DismissAcquisitionDialog();
            OpenSubpage(_javaChoicePage, _launchingPage);
        }
        if (!_processPresentationInitialized && _pageEntities.TryGetValue("LaunchButtonProgress", out var fill))
        {
            _processPresentationInitialized = true;
            _shell.Tree.SetComponent(fill, new XsrUiVisualStyle { Surface = XsrUiSurfaceKind.Solid, Background = LaunchButtonBackground, CornerRadius = 20 });
            var text = _pageEntities["LaunchButtonText"];
            _shell.Tree.SetComponent(text, new XsrUiVisualStyle { Foreground = new(255, 255, 255), FontSize = 15, FontWeight = 600 });
        }
        if (!_store.TryResolve(MinecraftProcessStateComposition.SessionsKey, out var sessionsId)) return;
        var sessions = _store.ReadCollection<MinecraftProcessSnapshot>(sessionsId);
        if (_processRevision != sessions.Revision)
        {
            _processRevision = sessions.Revision;
            var running = sessions.Items
                .Where(item => item.State is MinecraftProcessState.Created or MinecraftProcessState.Running).ToArray();
            foreach (var id in _processDocks.Keys.Except(running.Select(item => item.SessionId)).ToArray())
            {
                var dock = _processDocks[id];
                DesktopBubbleLayout.SetVisible(_shell, _store, dock.Power, false, destroy: true);
                DesktopBubbleLayout.SetVisible(_shell, _store, dock.Logs, false, destroy: true);
                _processDocks.Remove(id);
            }
            for (int i = 0; i < running.Length; i++)
            {
                var session = running[i];
                if (!_processDocks.TryGetValue(session.SessionId, out var dock))
                {
                    dock = (CreateProcessButton(session, "power", "结束游戏", "stop"),
                        CreateProcessButton(session, "menu", "日志（尚未实现）", "logs"));
                    _shell.Tree.GetComponent<XsrUiInput>(dock.Logs)!.Clickable = false;
                    _processDocks.Add(session.SessionId, dock);
                }

            }
        }
        DesktopBubbleLayout.Arrange(_shell);
        if (_store.TryResolve(MinecraftProcessStateComposition.FailuresKey, out var failuresId))
        {
            var failures = _store.ReadCollection<MinecraftProcessFailure>(failuresId).Items;
            _shownFailures.IntersectWith(failures.Select(failure => failure.SessionId));
            foreach (var failure in failures)
                if (!_shownFailures.Contains(failure.SessionId) && TryShowCrashDialog(failure))
                { _shownFailures.Add(failure.SessionId); break; }
        }
    }

    private XsrUiEntityId CreateProcessButton(MinecraftProcessSnapshot session, string icon, string label, string action)
    {
        var entity = _shell.Tree.Create("process-" + action + "-" + session.SessionId);
        _shell.Tree.SetComponent(entity, new XsrUiElement
        {
            Width = 280,
            Height = 48,
            HorizontalAlignment = XsrUiAlignment.End,
            VerticalAlignment = XsrUiAlignment.End,
            Margin = new(0, 0, 76, 18)
        });
        _shell.Tree.SetComponent(entity, new XsrUiInput { Clickable = true, Focusable = true });
        _shell.Tree.SetComponent(entity, new XsrUiSemantic(XsrUiSemanticRole.Button, session.InstanceId + " · " + label));
        _shell.Tree.SetComponent(entity, new XsrUiCommandBinding(XsrSemanticId.Parse("ui.process." + action + "." + session.SessionId.ToString("N"))));
        _shell.Tree.SetComponent(entity, DesktopBubbleLayout.Style());
        DesktopBubbleLayout.Register(_shell, entity, 2);
        _shell.Tree.SetComponent(entity, new XsrUiImage("lucide/" + icon));
        _shell.Tree.SetComponent(entity, new XsrUiText(session.InstanceId + " · " + label));
        _shell.Stage.Show(entity);
        return entity;
    }

    private bool HandleProcessIntent(DesktopUiIntentEventArgs e)
    {
        string command = e.Intent.Command.Value;
        if (command.StartsWith("ui.java.major.", StringComparison.Ordinal) && int.TryParse(command[14..], out int major))
        {
            _ = SelectJavaMajorAsync(major); return true;
        }
        if (command.StartsWith("ui.process.stop.", StringComparison.Ordinal) && Guid.TryParseExact(command[16..], "N", out var id))
        {
            _feedback.ShowDialog("minecraft.stop." + id, "结束游戏？", "未保存的游戏进度可能丢失。", "结束游戏", "取消",
                confirmed => { if (confirmed) _ = StopProcessAsync(id); });
            return true;
        }
        return command.StartsWith("ui.process.logs.", StringComparison.Ordinal);
    }

    private async Task StopProcessAsync(Guid id)
    {
        if (!_minecraft.Commands.TryResolve(MinecraftRouteIds.ProcessCancel, out var route)) return;
        try
        {
            var result = await Task.Run(async () => await _minecraft.Commands.Dispatch(route, new MinecraftCancelProcessCommand(id), cancellationToken: _lifetimeCancellation.Token).Completion.ConfigureAwait(false)).ConfigureAwait(false);
            if (!_disposed && !result.IsSuccess) _feedback.Error(result.Error?.Message ?? "无法结束游戏。");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException)
        { if (!_disposed) _feedback.Error("无法结束游戏：" + ex.Message); }
    }

    private async Task SelectJavaMajorAsync(int major)
    {
        if (Interlocked.Exchange(ref _pickingJava, 1) != 0) return;
        try
        {
            if (!_minecraft.Commands.TryResolve(MinecraftRouteIds.JavaVersionSelect, out var route)) return;
            _feedback.Info($"正在查找 Java {major}，缺失时将自动下载。");
            var result = await _minecraft.Commands.Dispatch(route, new MinecraftSelectJavaVersionCommand(major), cancellationToken: _lifetimeCancellation.Token).Completion.ConfigureAwait(false);
            if (!_disposed && !result.IsSuccess) _feedback.Error(result.Error?.Message ?? "无法使用所选 Java。");
            else if (!_disposed)
            {
                Interlocked.Exchange(ref _returnFromJavaChoice, 1);
                Publish(LaunchPageState.LaunchingStageKey, "正在准备 Java");
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException)
        { if (!_disposed) _feedback.Error("Java 选择失败：" + ex.Message); }
        finally { Interlocked.Exchange(ref _pickingJava, 0); }
    }

    private static string CrashSummary(MinecraftLaunchFaultReport report) => report.Code switch
    {
        MinecraftLaunchFaultCode.OutOfMemory => "游戏内存不足。请关闭占用内存的应用，并检查内存分配。",
        MinecraftLaunchFaultCode.JavaRuntimeIncompatible => "Java 与当前版本不兼容。请选择兼容的 Java 版本。",
        MinecraftLaunchFaultCode.MissingModDependency => "缺少模组依赖。请检查模组要求。",
        MinecraftLaunchFaultCode.ModConflict => "检测到模组冲突。请检查近期添加或更新的模组。",
        MinecraftLaunchFaultCode.GraphicsInitializationFailed => "图形初始化失败。请检查显卡驱动。",
        MinecraftLaunchFaultCode.MainClassMissing or MinecraftLaunchFaultCode.ClasspathDependencyMissing => "游戏文件可能不完整。请修复版本文件。",
        MinecraftLaunchFaultCode.NativeLibraryFailed => "本地库加载失败。请检查系统架构与版本文件。",
        _ => "尚无法确定具体原因。以下是本次运行记录中的错误信息。",
    };
}

using Nexa.Services.Minecraft.Install;
using Nexa.UI.Next;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class LaunchPageController
{
    private bool _editingInstall;
    private MinecraftInstallEditPlan? _editPlan;
    private string? _editPlanKey;
    private MinecraftInstallEditSnapshot? _installEdit;
    private Task<XsrResult<MinecraftInstallEditSnapshot>>? _installEditRead;

    private void OpenInstallEditor(XsrUiEntityId source)
    {
        if (_installCatalogQueries is null || !_installCatalogQueries.TryResolve(MinecraftInstallEditContract.Query, out XsrQueryId route))
        { _feedback.Warn("无法读取版本修改信息。"); return; }
        string root = ReadCell(LaunchPageState.InstanceDirectoryKey);
        string instance = ReadCell(LaunchPageState.SelectedInstanceKey);
        if (string.IsNullOrWhiteSpace(instance)) return;
        ResetInstallSelection();
        _editingInstall = true;
        SetInstallEditorLabels(true);
        _shell.Renderer.SetTextInputValue(_javaInstallEntities["JavaInstallVersionInput"], instance);
        OpenSubpage(_javaInstallPage, source);
        _installEditRead = _installCatalogQueries.QueryAsync<MinecraftInstallEditQuery, MinecraftInstallEditSnapshot>(route,
            new(root, instance), cancellationToken: _lifetimeCancellation.Token).AsTask();
    }

    private void SetInstallEditorLabels(bool editing)
    {
        _shell.Tree.GetComponent<XsrUiSemantic>(_javaInstallPage)!.Label = editing ? "修改版本" : "安装 Java 版";
        _editPlan = null;
        _editPlanKey = null;
        var button = _javaInstallEntities["JavaInstallStart"];
        _shell.Tree.GetComponent<XsrUiInput>(button)!.Enabled = !editing;
        _shell.Tree.GetComponent<XsrUiText>(button)!.Content = editing ? "保存修改" : "开始安装";
        _shell.Tree.GetComponent<XsrUiSemantic>(button)!.Label = editing ? "保存版本修改" : "开始安装 Java 版 Minecraft";
        _shell.Tree.GetComponent<XsrUiInput>(_javaInstallEntities["JavaInstallVersionInput"])!.Enabled = !editing;
        _shell.Tree.MarkDirty(_javaInstallPage, XsrUiDirtyKinds.Paint | XsrUiDirtyKinds.Layout);
    }

    private void ProjectInstallEditor()
    {
        if (!_editingInstall || _installEditRead is not { IsCompleted: true } read) return;
        _installEditRead = null;
        if (!read.IsCompletedSuccessfully || !read.Result.IsSuccess)
        { _feedback.Warn("无法读取版本信息，请返回后重试。"); return; }
        _installEdit = read.Result.Value!;
        _installGameChosen = true;
        _selectedInstallVersion = _installEdit.GameVersion;
        foreach (var selection in _installEdit.Selection) _selectedInstallBuilds[selection.Loader] = selection.Version;
        _selectedInstallLoader = _installEdit.Selection.Count > 0 ? _installEdit.Selection[0].Loader.ToString() : "原版 Minecraft";
        PrefetchInstallCatalog(_installEdit.GameVersion);
        UpdateJavaInstallSubpageVisibility("JavaMinecraftPage");
        _catalogRevision = -1;
        _catalogFirst = -1;
        UpdateTitleBar();
    }
    private void ProjectInstallEditPlan()
    {
        if (_installEdit is null || _installCatalogQueries is null
            || !_installCatalogQueries.TryResolve(MinecraftInstallEditPlanContract.Query, out XsrQueryId route)) return;
        string key = string.Join(";", _selectedInstallBuilds.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value));
        if (key == _editPlanKey) return;
        var pending = _installCatalogQueries.QueryAsync<MinecraftInstallEditPlanQuery, MinecraftInstallEditPlan>(route,
            new(_installEdit, _selectedInstallBuilds.Select(pair => new InstallBuildSelection(pair.Key, pair.Value)).ToArray()));
        if (!pending.IsCompletedSuccessfully) return;
        var completed = pending.Result;
        if (!completed.IsSuccess) return;
        _editPlan = completed.Value!;
        _editPlanKey = key;
        var button = _javaInstallEntities["JavaInstallStart"];
        _shell.Tree.GetComponent<XsrUiText>(button)!.Content = _editPlan.ActionLabel;
        _shell.Tree.GetComponent<XsrUiSemantic>(button)!.Label = _editPlan.ActionLabel;
        _shell.Tree.GetComponent<XsrUiInput>(button)!.Enabled = _editPlan.Kind != MinecraftInstallEditKind.Unchanged;
        _shell.Tree.MarkDirty(button, XsrUiDirtyKinds.Paint | XsrUiDirtyKinds.Layout);
    }

}

using Nexa.Services.Minecraft.Management;
using Nexa.UI.Next;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private string _modCategory = "all";
    private bool _checkModUpdates;
    private bool _checkingModUpdates;
    private XsrUiEntityId _modUpdateButton;
    private readonly Dictionary<XsrUiEntityId, string> _modCategoryOptions = [];
    private XsrUiEntityId _modCategoryTrack;

    private static bool MatchesModCategory(InstanceContentEntry item, string category) => category switch
    {
        "enabled" => item.Enabled == true,
        "disabled" => item.Enabled == false,
        "updates" => item.UpdateAvailable == true,
        "problems" => item.PackageProblem.Length > 0,
        "unchecked" => item.Enabled is not null && (item.PackageReadable is null || item.UpdateAvailable is null),
        _ => true
    };

    private void BuildModCategories()
    {
        _modCategoryOptions.Clear();
        var row = Stack(_sections, "ManagementModCategories", XsrUiOrientation.Horizontal, 12);
        var track = Stack(row, "ManagementModCategoryTrack", XsrUiOrientation.Horizontal, 0);
        _modCategoryTrack = track;
        Style(track, new(241, 245, 250), Ink, 9);
        var thumb = Element(track, "ManagementModCategoryThumb", XsrUiSemanticRole.None, null);
        _shell.Tree.GetComponent<XsrUiElement>(thumb)!.IsVisible = false;
        Style(thumb, White, Ink, 7);
        _shell.Tree.SetComponent(thumb, new XsrUiTransition());
        _shell.Tree.SetComponent(track, new XsrUiSegmentedTrack(thumb));
        foreach (var (key, label) in new[] { ("all", "全部"), ("enabled", "已启用"), ("disabled", "已禁用"),
            ("updates", "可更新"), ("problems", "包异常"), ("unchecked", "未检测") })
        {
            var option = ActionButton(track, "ManagementModCategory." + key, label, ManagementAction, 72);
            _shell.Tree.SetComponent(option, new XsrUiSelection());
            _modCategoryOptions[option] = key;
            _managementActions[option] = () =>
            {
                _modCategory = key;
                UpdateModCategories();
                ApplyContentFilter();
                _shell.Tree.GetComponent<XsrUiScroll>(_sections)!.OffsetY = 0;
                UpdateContentWindow();
            };
        }
        _shell.Tree.GetComponent<XsrUiElement>(track)!.Width = 432;
        _modUpdateButton = ActionButton(row, "Management.检查更新", _checkingModUpdates ? "检查中…" : "检查更新", ManagementAction, 84);
        _managementActions[_modUpdateButton] = () =>
        {
            if (_managementRead is not null || _checkingModUpdates) return;
            _checkingModUpdates = true;
            _shell.Tree.SetComponent(_modUpdateButton, new XsrUiText("检查中…"));
            _checkModUpdates = true;
            CancelManagementRead();
        };
        UpdateModCategories();
    }

    private void UpdateModCategories()
    {
        foreach (var (entity, category) in _modCategoryOptions)
        {
            bool selected = category == _modCategory;
            _shell.Tree.GetComponent<XsrUiSelection>(entity)!.IsSelected = selected;
            Style(entity, XsrUiColor.Transparent, selected ? Blue : Ink, 7, 12, selected ? 600 : 450);
            _shell.Tree.GetComponent<XsrUiVisualStyle>(entity)!.TextAlignment = XsrUiTextAlignment.Center;
            if (selected) _shell.Tree.GetComponent<XsrUiSegmentedTrack>(_modCategoryTrack)!.Selected = entity;
        }
    }
}

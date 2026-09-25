using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private static readonly XsrSemanticId ArgumentAdd = XsrSemanticId.Parse("ui.settings.argument.add");
    private static readonly XsrSemanticId ArgumentRemove = XsrSemanticId.Parse("ui.settings.argument.remove");
    private sealed record ShiftSelector(Editor Editor, XsrUiEntityId Track, Dictionary<XsrUiEntityId, string> Options);
    private sealed class ArgumentEditor(SettingsCatalogEntry entry, XsrUiEntityId rows)
    {
        public SettingsCatalogEntry Entry { get; } = entry;
        public XsrUiEntityId Rows { get; } = rows;
        public List<XsrUiEntityId> Inputs { get; } = [];
        public bool Initialized { get; set; }
    }
    private readonly Dictionary<XsrUiEntityId, ShiftSelector> _selectors = [];
    private readonly Dictionary<XsrUiEntityId, ArgumentEditor> _argumentEditors = [];
    private readonly Dictionary<XsrUiEntityId, (ArgumentEditor Editor, int Index)> _argumentActions = [];

    private XsrUiEntityId ActionButton(XsrUiEntityId parent, string name, string text, XsrSemanticId command, double width)
    {
        var button = Element(parent, name, XsrUiSemanticRole.Button, text, width, 34);
        _shell.Tree.SetComponent(button, new XsrUiText(text));
        _shell.Tree.SetComponent(button, new XsrUiInput { Focusable = true, Clickable = true });
        _shell.Tree.SetComponent(button, new XsrUiCommandBinding(command));
        Style(button, new(242, 245, 249), Blue, 9, 12, 500);
        _shell.Tree.GetComponent<XsrUiVisualStyle>(button)!.TextAlignment = XsrUiTextAlignment.Center;
        return button;
    }

    private void BuildShiftSelector(XsrUiEntityId parent, SettingsCatalogEntry entry)
    {
        var track = Stack(parent, "SettingsSelector." + entry.SettingKey, XsrUiOrientation.Horizontal, 0);
        Style(track, new(241, 245, 250), Ink, 9);
        _shell.Tree.GetComponent<XsrUiElement>(track)!.Padding = new(3, 3, 3, 3);
        var thumb = Element(track, "SettingsSelectorThumb." + entry.SettingKey, XsrUiSemanticRole.None, null);
        _shell.Tree.GetComponent<XsrUiElement>(thumb)!.IsVisible = false;
        Style(thumb, White, Ink, 7);
        _shell.Tree.SetComponent(thumb, new XsrUiTransition());
        _shell.Tree.SetComponent(track, new XsrUiSegmentedTrack(thumb));
        Dictionary<XsrUiEntityId, string> options = [];
        string[] values = entry.Definition!.Kind == SettingsValueKind.Boolean ? ["false", "true"] : entry.Definition.Choices.Split('|');
        double width = 6;
        foreach (string value in values)
        {
            string label = value switch { "true" => "开启", "false" => "关闭", "fullscreen" => "全屏", "windowed" => "窗口", _ => value };
            double optionWidth = Math.Max(48, label.Length * 13 + 24);
            var option = ActionButton(track, "SettingsOption." + entry.SettingKey + "." + value, label, Choice, optionWidth);
            _shell.Tree.SetComponent(option, new XsrUiSelection());
            options[option] = value; width += optionWidth;
        }
        _shell.Tree.GetComponent<XsrUiElement>(track)!.Width = width;
        var editor = new Editor(entry, default, options.Keys.First(), default);
        _editors[editor.Button] = editor;
        _selectors[editor.Button] = new(editor, track, options);
    }

    private void UpdateShiftSelectors()
    {
        foreach (var selector in _selectors.Values)
        {
            string raw = _values?.Values.FirstOrDefault(item => item.Key == selector.Editor.Entry.SettingKey)?.Value.Value ?? selector.Options.Values.First();
            foreach (var option in selector.Options)
            {
                bool selected = option.Value == raw;
                _choices[option.Key] = (selector.Editor, option.Value);
                _shell.Tree.GetComponent<XsrUiSelection>(option.Key)!.IsSelected = selected;
                Style(option.Key, XsrUiColor.Transparent, selected ? Blue : Ink, 7, 12, selected ? 600 : 450);
                _shell.Tree.GetComponent<XsrUiVisualStyle>(option.Key)!.TextAlignment = XsrUiTextAlignment.Center;
                if (selected) _shell.Tree.GetComponent<XsrUiSegmentedTrack>(selector.Track)!.Selected = option.Key;
            }
        }
    }

    private void BuildArgumentEditor(XsrUiEntityId parent, SettingsCatalogEntry entry)
    {
        var panel = Stack(parent, "SettingsArguments." + entry.SettingKey, XsrUiOrientation.Horizontal, 8);
        _shell.Tree.GetComponent<XsrUiElement>(panel)!.Weight = 1;
        _shell.Tree.GetComponent<XsrUiElement>(panel)!.Padding = new(0, 6, 0, 6);
        var rows = Stack(panel, "SettingsArgumentRows." + entry.SettingKey, XsrUiOrientation.Vertical, 6);
        _shell.Tree.GetComponent<XsrUiElement>(rows)!.Weight = 1;
        var actions = Stack(panel, "SettingsArgumentActions", XsrUiOrientation.Vertical, 6);
        _shell.Tree.GetComponent<XsrUiElement>(actions)!.VerticalAlignment = XsrUiAlignment.Start;
        var apply = ActionButton(actions, "SettingsEdit." + entry.SettingKey, "应用", Edit, 44);
        var add = ActionButton(actions, "SettingsArgumentAdd." + entry.SettingKey, "添加", ArgumentAdd, 44);
        var editor = new ArgumentEditor(entry, rows);
        _argumentEditors[apply] = editor;
        _argumentActions[add] = (editor, -1);
        _editors[apply] = new(entry, default, apply, default);
        BuildArgumentRows(editor, [""]);
    }

    private string[] ReadArgumentDrafts(ArgumentEditor editor) => editor.Inputs
        .Select(input => _shell.Tree.GetComponent<XsrUiTextInput>(input)!.ReadDraft()).ToArray();

    private void BuildArgumentRows(ArgumentEditor editor, IReadOnlyList<string> values)
    {
        foreach (var key in _argumentActions.Where(pair => pair.Value.Editor == editor && pair.Value.Index >= 0).Select(pair => pair.Key).ToArray()) _argumentActions.Remove(key);
        foreach (var child in _shell.Tree.Children(editor.Rows).ToArray()) _shell.Tree.Destroy(child);
        editor.Inputs.Clear();
        for (int i = 0; i < Math.Max(1, values.Count); i++)
        {
            var row = Stack(editor.Rows, "SettingsArgumentRow", XsrUiOrientation.Horizontal, 6);
            var input = Element(row, "SettingsArgument." + editor.Entry.SettingKey + "." + i,
                XsrUiSemanticRole.TextInput, editor.Entry.Label + " " + (i + 1), height: 30);
            var layout = _shell.Tree.GetComponent<XsrUiElement>(input)!; layout.Weight = 1; layout.Padding = new(8, 0, 8, 0);
            _shell.Tree.SetComponent(input, new XsrUiTextInput { Placeholder = "输入参数" });
            _shell.Tree.SetComponent(input, new XsrUiInput { Focusable = true, Clickable = true });
            Style(input, new(244, 247, 251), Ink, 7, 12);
            _shell.Renderer.SetTextInputValue(input, i < values.Count ? values[i] : "");
            editor.Inputs.Add(input);
            var remove = ActionButton(row, "SettingsArgumentRemove." + editor.Entry.SettingKey + "." + i, "×", ArgumentRemove, 28);
            _shell.Tree.GetComponent<XsrUiSemantic>(remove)!.Label = "删除参数 " + (i + 1);
            _argumentActions[remove] = (editor, i);
        }
        _shell.Tree.MarkDirty(editor.Rows, XsrUiDirtyKinds.Layout);
    }

    private void UpdateArgumentEditors()
    {
        foreach (var editor in _argumentEditors.Values)
        {
            if (editor.Initialized || _values is null) continue;
            var value = _values.Values.First(item => item.Key == editor.Entry.SettingKey);
            BuildArgumentRows(editor, value.ArgumentRows);
            editor.Initialized = true;
        }
    }

    private void HandleArgumentIntent(DesktopUiIntent intent)
    {
        if (!_argumentActions.TryGetValue(intent.Source, out var action)) return;
        var values = ReadArgumentDrafts(action.Editor).ToList();
        if (intent.Command == ArgumentAdd) values.Add("");
        else if (action.Index >= 0 && action.Index < values.Count) values.RemoveAt(action.Index);
        BuildArgumentRows(action.Editor, values);
        action.Editor.Initialized = true;
        _shell.Renderer.Focus(action.Editor.Inputs[intent.Command == ArgumentAdd ? action.Editor.Inputs.Count - 1 : Math.Min(action.Index, action.Editor.Inputs.Count - 1)]);
    }
}


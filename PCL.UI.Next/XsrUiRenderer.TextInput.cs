namespace PCL.UI.Next;

public sealed partial class XsrUiRenderer
{
    public bool InsertText(string text)
    {
        if (!CanEdit(_focused, out XsrUiTextInput? input)) return false;
        input.ReplaceSelection(text);
        _tree.MarkDirty(_focused, XsrUiDirtyKinds.Paint);
        return true;
    }

    public bool EditText(XsrUiTextEdit action, bool extendSelection = false)
    {
        if (!CanEdit(_focused, out XsrUiTextInput? input)) return false;
        int start = Math.Min(input.SelectionStart, input.SelectionEnd);
        int end = Math.Max(input.SelectionStart, input.SelectionEnd);
        int target = input.SelectionEnd;
        switch (action)
        {
            case XsrUiTextEdit.SelectAll:
                input.SelectionStart = 0;
                input.SelectionEnd = input.ReadDraft().Length;
                break;
            case XsrUiTextEdit.Backspace:
            case XsrUiTextEdit.Delete:
                if (start == end)
                {
                    input.SelectionStart = action == XsrUiTextEdit.Backspace ? input.Previous(start) : start;
                    input.SelectionEnd = action == XsrUiTextEdit.Delete ? input.Next(end) : end;
                }
                input.ReplaceSelection(string.Empty);
                break;
            default:
                target = action switch
                {
                    XsrUiTextEdit.Home => 0,
                    XsrUiTextEdit.End => input.ReadDraft().Length,
                    XsrUiTextEdit.Left => !extendSelection && start != end ? start : input.Previous(target),
                    XsrUiTextEdit.Right => !extendSelection && start != end ? end : input.Next(target),
                    _ => target,
                };
                input.SelectionEnd = target;
                if (!extendSelection) input.SelectionStart = target;
                break;
        }
        input.Preedit = string.Empty;
        _tree.MarkDirty(_focused, XsrUiDirtyKinds.Paint);
        return true;
    }

    /// <summary>Programmatic draft initialization/clearing at the render-thread product boundary.</summary>
    public bool SetTextInputValue(XsrUiEntityId entity, string value)
    {
        if (!_tree.IsAlive(entity) || _tree.GetComponent<XsrUiTextInput>(entity) is not { } input) return false;
        input.SetValue(value);
        _tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
        return true;
    }

    public bool SetTextSelection(XsrUiEntityId entity, int start, int end)
    {
        if (!CanEdit(entity, out XsrUiTextInput? input)) return false;
        input.SelectionStart = input.Boundary(start);
        input.SelectionEnd = input.Boundary(end);
        _tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
        return true;
    }

    public bool SetTextPreedit(XsrUiEntityId entity, string? value)
    {
        if (!CanEdit(entity, out XsrUiTextInput? input)) return false;
        input.Preedit = value is { Length: > 2048 } ? value[..2048] : value ?? string.Empty;
        _tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
        return true;
    }

    public string? CopySelectedText()
    {
        if (!CanEdit(_focused, out XsrUiTextInput? input) || input.IsPassword) return null;
        int start = Math.Min(input.SelectionStart, input.SelectionEnd);
        int end = Math.Max(input.SelectionStart, input.SelectionEnd);
        return input.ReadDraft()[start..end];
    }

    private bool CanEdit(XsrUiEntityId entity, out XsrUiTextInput input)
    {
        input = _tree.IsAlive(entity) ? _tree.GetComponent<XsrUiTextInput>(entity)! : null!;
        return input is not null && IsInVisibleTree(entity) && IsEnabled(_tree.GetComponent<XsrUiInput>(entity));
    }
}

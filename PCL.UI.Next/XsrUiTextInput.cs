using System.Globalization;

namespace PCL.UI.Next;

/// <summary>Ephemeral form draft. Secrets are never copied into render-scene snapshots.</summary>
public sealed class XsrUiTextInput
{
    private string _value = string.Empty;
    internal string Preedit { get; set; } = string.Empty;
    public string Placeholder { get; set; } = string.Empty;
    public bool IsPassword { get; init; }
    public int SelectionStart { get; internal set; }
    public int SelectionEnd { get; internal set; }

    /// <summary>Read only at the trusted product submit boundary, never for scene projection.</summary>
    public string ReadDraft() => _value;

    internal void SetValue(string value)
    {
        _value = Sanitize(value);
        SelectionStart = SelectionEnd = _value.Length;
        Preedit = string.Empty;
    }

    internal void ReplaceSelection(string value)
    {
        int start = Math.Min(SelectionStart, SelectionEnd);
        int end = Math.Max(SelectionStart, SelectionEnd);
        string insertion = Sanitize(value);
        string updated = Sanitize(_value[..start] + insertion + _value[end..]);
        _value = updated;
        SelectionStart = SelectionEnd = Boundary(Math.Min(start + insertion.Length, updated.Length));
        Preedit = string.Empty;
    }

    internal int Boundary(int index) => index <= 0 ? 0 : index >= _value.Length ? _value.Length
        : StringInfo.ParseCombiningCharacters(_value).LastOrDefault(position => position <= index);

    internal int Previous(int index) => StringInfo.ParseCombiningCharacters(_value).LastOrDefault(position => position < index);
    internal int Next(int index) => StringInfo.ParseCombiningCharacters(_value).FirstOrDefault(position => position > index, _value.Length);

    internal XsrUiTextInputSnapshot Snapshot() => new(
        IsPassword ? new string('•', _value.Length) : _value,
        Placeholder, IsPassword, SelectionStart, SelectionEnd,
        IsPassword ? new string('•', Preedit.Length) : Preedit);

    private static string Sanitize(string value)
    {
        string singleLine = string.Concat(value.Where(character => !char.IsControl(character)));
        if (singleLine.Length <= 2048) return singleLine;
        int end = StringInfo.ParseCombiningCharacters(singleLine).Last(position => position <= 2048);
        return singleLine[..end];
    }
}

/// <summary>Display-only text editing facts; passwords and their preedit are already masked.</summary>
public readonly record struct XsrUiTextInputSnapshot(
    string DisplayText, string Placeholder, bool IsPassword,
    int SelectionStart, int SelectionEnd, string Preedit);

public enum XsrUiTextEdit
{
    Left, Right, Home, End, Backspace, Delete, SelectAll,
}

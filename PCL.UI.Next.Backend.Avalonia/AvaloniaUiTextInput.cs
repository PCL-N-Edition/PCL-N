using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using PCL.UI.Next;

namespace PCL.UI.Next.Backend.Avalonia;

internal sealed record AvaloniaUiTextInputActions(
    Action<XsrUiEntityId, string> SetValue,
    Action<XsrUiEntityId, int, int> Select,
    Action<XsrUiEntityId, string?> Preedit);

public sealed partial class AvaloniaUiSceneSurface
{
    private XsrUiEntityId _textSelecting;
    private int _textAnchor;

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Text is not null && _shell.Renderer.InsertText(e.Text))
        {
            CommitScene();
            e.Handled = true;
        }
    }

    private bool HandleTextEditingKey(KeyEventArgs e)
    {
        if (e.Key == Key.Tab && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            bool moved = _shell.Renderer.FocusPrevious();
            if (moved) CommitScene();
            return moved;
        }
        if (!_controls.TryGetValue(_shell.Renderer.Focused, out AvaloniaUiSceneNodeControl? control)
            || control.Node.TextInput is null) return false;
        bool command = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        if (command && e.Key is Key.C or Key.X or Key.V)
        {
            _ = TransferClipboard(e.Key, control.Node.Entity);
            return true;
        }
        XsrUiTextEdit? edit = e.Key switch
        {
            Key.Left => XsrUiTextEdit.Left,
            Key.Right => XsrUiTextEdit.Right,
            Key.Home => XsrUiTextEdit.Home,
            Key.End => XsrUiTextEdit.End,
            Key.Back => XsrUiTextEdit.Backspace,
            Key.Delete => XsrUiTextEdit.Delete,
            Key.A when command => XsrUiTextEdit.SelectAll,
            _ => null,
        };
        if (edit is null) return false;
        bool handled = _shell.Renderer.EditText(edit.Value, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        if (handled) CommitScene();
        return handled;
    }

    private async Task TransferClipboard(Key key, XsrUiEntityId target)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try
        {
            if (key == Key.V)
            {
                string? text = await clipboard.TryGetTextAsync();
                if (!_disposed && _shell.Renderer.Focused == target && text is not null)
                    _shell.Renderer.InsertText(text);
            }
            else if (_shell.Renderer.CopySelectedText() is { Length: > 0 } selection)
            {
                await clipboard.SetTextAsync(selection);
                if (key == Key.X && !_disposed && _shell.Renderer.Focused == target
                    && _shell.Renderer.CopySelectedText() == selection)
                    _shell.Renderer.InsertText(string.Empty);
            }
            if (!_disposed) CommitScene();
        }
        catch (Exception failure) when (failure is InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            // Clipboard denial is a native effect failure; preserve the draft and never log it.
        }
    }

    private void BeginTextSelection(Point position, int clickCount)
    {
        if (!_controls.TryGetValue(_shell.Renderer.Focused, out AvaloniaUiSceneNodeControl? control)
            || control.Node.TextInput is null || !control.Bounds.Contains(position)) return;
        _textSelecting = control.Node.Entity;
        _textAnchor = control.TextPositionAt(position.X - control.Bounds.X);
        if (clickCount >= 2) _shell.Renderer.EditText(XsrUiTextEdit.SelectAll);
        else _shell.Renderer.SetTextSelection(_textSelecting, _textAnchor, _textAnchor);
        CommitScene();
    }

    private bool ExtendTextSelection(Point position)
    {
        if (!_textSelecting.IsAssigned || !_controls.TryGetValue(_textSelecting, out AvaloniaUiSceneNodeControl? control)) return false;
        _shell.Renderer.SetTextSelection(_textSelecting, _textAnchor, control.TextPositionAt(position.X - control.Bounds.X));
        CommitScene();
        return true;
    }
}

internal sealed partial class AvaloniaUiSceneNodeControl
{
    private AvaloniaUiTextInputActions? _textActions;
    private SceneInputMethodClient? _inputMethod;
    private double _textOffset;
    internal Rect TextCursorRectangle { get; private set; }

    private void InitializeTextInput(AvaloniaUiTextInputActions? actions)
    {
        _textActions = actions;
        TextInputMethodClientRequested += (_, e) =>
        {
            if (_node.TextInput is null) return;
            _inputMethod ??= new SceneInputMethodClient(this);
            e.Client = _inputMethod;
        };
    }

    private void UpdateTextInput(XsrUiTextInputSnapshot? before, XsrUiTextInputSnapshot? after)
    {
        if (after is not { } input) return;
        TextInputOptions.SetIsSensitive(this, input.IsPassword);
        TextInputOptions.SetShowSuggestions(this, !input.IsPassword);
        TextInputOptions.SetMultiline(this, false);
        if (before == after) return;
        _inputMethod?.NotifyChanged();
        if (!input.IsPassword && ControlAutomationPeer.FromElement(this) is { } peer)
            peer.RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, before?.DisplayText, input.DisplayText);
    }

    internal void SetTextFromAutomation(string value)
    {
        if (!IsEnabled) throw new InvalidOperationException("The text field is disabled.");
        _textActions?.SetValue(_node.Entity, value);
    }

    private FormattedText FormatInput(string text, IBrush? foreground = null) => new(
        text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface(FontFamily.Default), _node.VisualStyle.FontSize > 0 ? _node.VisualStyle.FontSize : 14,
        foreground ?? (IBrush?)Brush(_node.VisualStyle.Foreground) ?? Brushes.Black);

    internal int TextPositionAt(double x)
    {
        string text = _node.TextInput?.DisplayText ?? string.Empty;
        double target = x - 12 + _textOffset;
        int best = 0;
        double distance = Math.Abs(target);
        foreach (int end in StringInfo.ParseCombiningCharacters(text).Skip(1).Append(text.Length))
        {
            double next = Math.Abs(FormatInput(text[..end]).WidthIncludingTrailingWhitespace - target);
            if (next < distance) { best = end; distance = next; }
        }
        return best;
    }

    private void DrawTextInput(DrawingContext context, Rect rect, XsrUiVisualStyleSnapshot style, XsrUiTextInputSnapshot input)
    {
        string text = input.DisplayText;
        int caret = Math.Clamp(input.SelectionEnd, 0, text.Length);
        string presented = input.Preedit.Length == 0 ? text : text[..caret] + input.Preedit + text[caret..];
        double cursor = FormatInput(presented[..(caret + input.Preedit.Length)]).WidthIncludingTrailingWhitespace;
        double available = Math.Max(1, rect.Width - 24);
        _textOffset = Math.Clamp(_textOffset, Math.Max(0, cursor - available), Math.Max(0, cursor));
        double x = 12 - _textOffset;
        FormattedText formatted = FormatInput(presented.Length == 0 ? input.Placeholder : presented,
            presented.Length == 0 ? Brush(new XsrUiColor(112, 122, 138)) : null);
        double y = (rect.Height - formatted.Height) / 2;
        Rect cursorRect = new(x + cursor, Math.Max(4, y), 1, Math.Min(22, rect.Height - 8));
        if (cursorRect != TextCursorRectangle) { TextCursorRectangle = cursorRect; _inputMethod?.NotifyCursorChanged(); }
        using (context.PushClip(new Rect(10, 0, Math.Max(0, rect.Width - 20), rect.Height)))
        {
            int start = Math.Min(input.SelectionStart, input.SelectionEnd), end = Math.Max(input.SelectionStart, input.SelectionEnd);
            if (_node.IsFocused && start != end)
            {
                double left = FormatInput(text[..start]).WidthIncludingTrailingWhitespace;
                double right = FormatInput(text[..end]).WidthIncludingTrailingWhitespace;
                context.DrawRectangle(new SolidColorBrush(Color.FromArgb(55, 11, 91, 203)), null,
                    new Rect(x + left, 7, right - left, Math.Max(0, rect.Height - 14)));
            }
            context.DrawText(formatted, new Point(x, y));
            if (_node.IsFocused)
            {
                context.DrawRectangle(Brush(new XsrUiColor(11, 91, 203)), null, cursorRect);
                if (input.Preedit.Length > 0)
                {
                    double from = FormatInput(text[..caret]).WidthIncludingTrailingWhitespace;
                    context.DrawLine(new Pen(Brush(style.Foreground), 1), new Point(x + from, rect.Height - 7), new Point(x + cursor, rect.Height - 7));
                }
            }
        }
        if (_node.IsFocused)
            context.DrawRectangle(null, new Pen(Brush(new XsrUiColor(11, 91, 203)), 1),
                new RoundedRect(rect.Deflate(.5), new CornerRadius(style.CornerRadius)));
    }

    private sealed class SceneInputMethodClient(AvaloniaUiSceneNodeControl owner) : TextInputMethodClient
    {
        public override Visual TextViewVisual => owner;
        public override bool SupportsPreedit => true;
        public override bool SupportsSurroundingText => owner.Node.TextInput?.IsPassword != true;
        public override string SurroundingText => SupportsSurroundingText ? owner.Node.TextInput?.DisplayText ?? string.Empty : string.Empty;
        public override Rect CursorRectangle => owner.TextCursorRectangle;
        public override TextSelection Selection
        {
            get => new(owner.Node.TextInput?.SelectionStart ?? 0, owner.Node.TextInput?.SelectionEnd ?? 0);
            set => owner._textActions?.Select(owner.Node.Entity, value.Start, value.End);
        }
        public override void SetPreeditText(string? text) => owner._textActions?.Preedit(owner.Node.Entity, text);
        internal void NotifyChanged() { RaiseSurroundingTextChanged(); RaiseSelectionChanged(); }
        internal void NotifyCursorChanged() => RaiseCursorRectangleChanged();
    }
}

internal sealed class AvaloniaUiSceneTextAutomationPeer(AvaloniaUiSceneNodeControl owner)
    : AvaloniaUiSceneNodeAutomationPeer(owner), IValueProvider
{
    public bool IsReadOnly => !SceneOwner.Node.IsEnabled;
    public string Value => SceneOwner.Node.TextInput is { IsPassword: false } input ? input.DisplayText : string.Empty;
    public void SetValue(string? value) => SceneOwner.SetTextFromAutomation(value ?? string.Empty);
    protected override string? GetPlaceholderTextCore() => SceneOwner.Node.TextInput?.Placeholder;
}

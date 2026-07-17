// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace PCL.Desktop.Controls.Legacy;

/// <summary>
/// Lightweight CommonMark-style renderer used by launcher dialogs. It keeps the
/// complete source while formatting headings, lists, quotes, code, emphasis,
/// links and tables without introducing a browser surface into the desktop shell.
/// </summary>
public sealed class MyMarkdownViewer : TextBlock
{
    private static readonly Regex HeadingRegex = new(@"^(?<marks>#{1,6})\s+(?<text>.*)$");
    private static readonly Regex BulletRegex = new(@"^(?<indent>\s*)[-+*]\s+(?<text>.*)$");
    private static readonly Regex OrderedRegex = new(@"^(?<indent>\s*)(?<number>\d+)[.)]\s+(?<text>.*)$");
    private static readonly Regex LinkRegex = new(@"\A(?<image>!)?\[(?<label>[^\]]*)\]\((?<url>[^)]+)\)");
    private static readonly FontFamily MonospaceFont = new("Cascadia Mono, Consolas, monospace");

    public static readonly StyledProperty<string> MarkdownProperty =
        AvaloniaProperty.Register<MyMarkdownViewer, string>(nameof(Markdown), string.Empty);

    public MyMarkdownViewer()
    {
        TextWrapping = TextWrapping.Wrap;
        FontSize = 15d;
        this.GetObservable(MarkdownProperty).Subscribe(RenderMarkdown);
    }

    public string Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private void RenderMarkdown(string? markdown)
    {
        InlineCollection inlines = Inlines!;
        inlines.Clear();
        string[] lines = (markdown ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        bool fencedCode = false;

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) ||
                trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                fencedCode = !fencedCode;
                if (index < lines.Length - 1)
                    inlines.Add(new LineBreak());
                continue;
            }

            if (fencedCode)
            {
                inlines.Add(new Run(line) { FontFamily = MonospaceFont });
                AddLineBreak(inlines, index, lines.Length);
                continue;
            }

            Match heading = HeadingRegex.Match(line);
            if (heading.Success)
            {
                int level = heading.Groups["marks"].Length;
                double size = level switch
                {
                    1 => 25d,
                    2 => 21d,
                    3 => 18d,
                    _ => 16d
                };
                AddInlineMarkdown(
                    inlines,
                    heading.Groups["text"].Value,
                    fontSize: size,
                    fontWeight: FontWeight.SemiBold);
                AddLineBreak(inlines, index, lines.Length);
                continue;
            }

            if (IsHorizontalRule(line))
            {
                inlines.Add(new Run("────────────────────────"));
                AddLineBreak(inlines, index, lines.Length);
                continue;
            }

            Match bullet = BulletRegex.Match(line);
            if (bullet.Success)
            {
                string text = bullet.Groups["text"].Value;
                string marker = "• ";
                if (text.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase))
                {
                    marker = "☑ ";
                    text = text[4..];
                }
                else if (text.StartsWith("[ ] ", StringComparison.Ordinal))
                {
                    marker = "☐ ";
                    text = text[4..];
                }
                inlines.Add(new Run(NormalizeIndent(bullet.Groups["indent"].Value) + marker));
                AddInlineMarkdown(inlines, text);
                AddLineBreak(inlines, index, lines.Length);
                continue;
            }

            Match ordered = OrderedRegex.Match(line);
            if (ordered.Success)
            {
                inlines.Add(new Run(
                    NormalizeIndent(ordered.Groups["indent"].Value) +
                    ordered.Groups["number"].Value + ". "));
                AddInlineMarkdown(inlines, ordered.Groups["text"].Value);
                AddLineBreak(inlines, index, lines.Length);
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                string quote = trimmed.Length > 1 ? trimmed[1..].TrimStart() : string.Empty;
                inlines.Add(new Run("│ ") { FontWeight = FontWeight.SemiBold });
                AddInlineMarkdown(inlines, quote, fontStyle: FontStyle.Italic);
                AddLineBreak(inlines, index, lines.Length);
                continue;
            }

            bool tableRow = trimmed.StartsWith('|') && trimmed.EndsWith('|');
            AddInlineMarkdown(inlines, line, fontFamily: tableRow ? MonospaceFont : null);
            AddLineBreak(inlines, index, lines.Length);
        }
    }

    private static void AddInlineMarkdown(
        InlineCollection inlines,
        string text,
        double? fontSize = null,
        FontWeight? fontWeight = null,
        FontStyle? fontStyle = null,
        FontFamily? fontFamily = null)
    {
        int position = 0;
        while (position < text.Length)
        {
            Match link = LinkRegex.Match(text[position..]);
            if (link.Success)
            {
                string label = link.Groups["label"].Value;
                string url = link.Groups["url"].Value;
                string display = link.Groups["image"].Success
                    ? $"🖼 {label} ({url})"
                    : string.IsNullOrWhiteSpace(label) || string.Equals(label, url, StringComparison.Ordinal)
                        ? url
                        : $"{label} ({url})";
                Run run = CreateRun(display, fontSize, fontWeight, fontStyle, fontFamily);
                run.TextDecorations = Avalonia.Media.TextDecorations.Underline;
                inlines.Add(run);
                position += link.Length;
                continue;
            }

            if (TryAddDelimited(inlines, text, ref position, "**", FontWeight.Bold, fontSize, fontStyle, fontFamily) ||
                TryAddDelimited(inlines, text, ref position, "__", FontWeight.Bold, fontSize, fontStyle, fontFamily) ||
                TryAddDelimited(inlines, text, ref position, "~~", fontWeight, fontSize, fontStyle, fontFamily, strike: true) ||
                TryAddDelimited(inlines, text, ref position, "`", fontWeight, fontSize, fontStyle, MonospaceFont) ||
                TryAddDelimited(inlines, text, ref position, "*", fontWeight, fontSize, FontStyle.Italic, fontFamily))
            {
                continue;
            }

            int next = FindNextMarker(text, position + 1);
            string plain = text[position..next];
            inlines.Add(CreateRun(plain, fontSize, fontWeight, fontStyle, fontFamily));
            position = next;
        }
    }

    private static bool TryAddDelimited(
        InlineCollection inlines,
        string text,
        ref int position,
        string delimiter,
        FontWeight? tokenWeight,
        double? fontSize,
        FontStyle? tokenStyle,
        FontFamily? fontFamily,
        bool strike = false)
    {
        if (!text.AsSpan(position).StartsWith(delimiter, StringComparison.Ordinal))
            return false;
        int end = text.IndexOf(delimiter, position + delimiter.Length, StringComparison.Ordinal);
        if (end <= position + delimiter.Length)
            return false;

        Run run = CreateRun(
            text[(position + delimiter.Length)..end],
            fontSize,
            tokenWeight,
            tokenStyle,
            fontFamily);
        if (strike)
            run.TextDecorations = Avalonia.Media.TextDecorations.Strikethrough;
        inlines.Add(run);
        position = end + delimiter.Length;
        return true;
    }

    private static Run CreateRun(
        string text,
        double? fontSize,
        FontWeight? fontWeight,
        FontStyle? fontStyle,
        FontFamily? fontFamily)
    {
        Run run = new(text);
        if (fontSize is { } size)
            run.FontSize = size;
        if (fontWeight is { } weight)
            run.FontWeight = weight;
        if (fontStyle is { } style)
            run.FontStyle = style;
        if (fontFamily is not null)
            run.FontFamily = fontFamily;
        return run;
    }

    private static int FindNextMarker(string text, int start)
    {
        int next = text.Length;
        foreach (string marker in new[] { "![", "[", "**", "__", "~~", "`", "*" })
        {
            int found = text.IndexOf(marker, start, StringComparison.Ordinal);
            if (found >= 0)
                next = Math.Min(next, found);
        }
        return next;
    }

    private static bool IsHorizontalRule(string line)
    {
        string compact = line.Replace(" ", string.Empty, StringComparison.Ordinal);
        return compact.Length >= 3 &&
               (compact.All(static character => character == '-') ||
                compact.All(static character => character == '*') ||
                compact.All(static character => character == '_'));
    }

    private static string NormalizeIndent(string indent) =>
        new(' ', Math.Min(12, indent.Replace("\t", "    ", StringComparison.Ordinal).Length));

    private static void AddLineBreak(InlineCollection inlines, int index, int count)
    {
        if (index < count - 1)
            inlines.Add(new LineBreak());
    }
}

// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace PCL.Desktop.Controls.Legacy;

/// <summary>Visual and behavioral role of an action in a launcher message dialog.</summary>
public enum MyMsgDialogButtonRole
{
    Secondary = 0,
    Primary = 1,
    Destructive = 2
}

/// <summary>A single message-dialog action.</summary>
public sealed record MyMsgDialogButton(
    string Text,
    int Result,
    MyMsgDialogButtonRole Role = MyMsgDialogButtonRole.Secondary,
    Action? Action = null)
{
    public string Text { get; } = string.IsNullOrWhiteSpace(Text)
        ? throw new ArgumentException("Dialog button text cannot be empty.", nameof(Text))
        : Text;
}

/// <summary>
/// Common model for launcher message dialogs. A dialog may expose one independent action on
/// the left and up to three ordered actions on the right.
/// </summary>
public sealed class MyMsgDialogModel
{
    public MyMsgDialogModel(
        string title,
        string content,
        MyMsgDialogButton? leftButton = null,
        IEnumerable<MyMsgDialogButton>? rightButtons = null,
        bool isWarning = false)
    {
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Content = content ?? throw new ArgumentNullException(nameof(content));
        LeftButton = leftButton;
        MyMsgDialogButton[] actions = rightButtons?.ToArray() ?? [];
        if (actions.Length > 3)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rightButtons),
                actions.Length,
                "A dialog can contain at most three right-side buttons.");
        }

        RightButtons = actions;
        IsWarning = isWarning;
    }

    public string Title { get; }

    public string Content { get; }

    public MyMsgDialogButton? LeftButton { get; }

    public IReadOnlyList<MyMsgDialogButton> RightButtons { get; }

    public bool IsWarning { get; }

    internal static MyMsgDialogModel CreateLegacy(
        string title,
        string content,
        string primaryButton,
        string secondaryButton = "",
        string thirdButton = "",
        bool isWarning = false,
        Action? primaryAction = null,
        Action? secondaryAction = null,
        Action? thirdAction = null)
    {
        List<MyMsgDialogButton> rightButtons =
        [
            new(
                primaryButton,
                1,
                isWarning
                    ? MyMsgDialogButtonRole.Destructive
                    : string.IsNullOrWhiteSpace(secondaryButton)
                        ? MyMsgDialogButtonRole.Secondary
                        : MyMsgDialogButtonRole.Primary,
                primaryAction)
        ];
        AddOptional(rightButtons, secondaryButton, 2, secondaryAction);
        AddOptional(rightButtons, thirdButton, 3, thirdAction);
        return new MyMsgDialogModel(title, content, rightButtons: rightButtons, isWarning: isWarning);
    }

    private static void AddOptional(
        List<MyMsgDialogButton> actions,
        string text,
        int result,
        Action? action)
    {
        if (!string.IsNullOrWhiteSpace(text))
            actions.Add(new MyMsgDialogButton(text, result, MyMsgDialogButtonRole.Secondary, action));
    }
}

/// <summary>Maps the common model onto the fixed, plugin-stable message-dialog button slots.</summary>
internal static class MyMsgDialogPresenter
{
    public static void Apply(
        Control owner,
        MyMsgDialogModel model,
        TextBlock? title,
        Rectangle? titleLine,
        MyButton? leftButton,
        params MyButton?[] rightButtons)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(model);
        if (rightButtons.Length < 3)
            throw new ArgumentException("Three right-side button slots are required.", nameof(rightButtons));

        if (title is not null)
        {
            title.Text = model.Title;
            IBrush titleBrush = model.IsWarning
                ? LegacyResourceResolver.Brush(owner, "ColorBrushRedLight", "#ff4c4c")
                : LegacyResourceResolver.Brush(owner, "ColorBrush2", "#3a3a3a");
            title.Foreground = titleBrush;
            if (titleLine is not null)
                titleLine.Fill = titleBrush;
        }

        ApplyButton(leftButton, model.LeftButton);
        for (int index = 0; index < rightButtons.Length; index++)
        {
            MyMsgDialogButton? action = index < model.RightButtons.Count
                ? model.RightButtons[index]
                : null;
            ApplyButton(rightButtons[index], action);
        }
    }

    public static MyMsgDialogButton? GetAction(MyButton? button) =>
        button?.Tag as MyMsgDialogButton;

    private static void ApplyButton(MyButton? button, MyMsgDialogButton? action)
    {
        if (button is null)
            return;

        button.Tag = action;
        button.IsVisible = action is not null;
        if (action is null)
            return;

        button.Text = action.Text;
        button.ColorType = action.Role switch
        {
            MyMsgDialogButtonRole.Primary => MyButton.ColorState.Highlight,
            MyMsgDialogButtonRole.Destructive => MyButton.ColorState.Red,
            _ => MyButton.ColorState.Normal
        };
    }
}

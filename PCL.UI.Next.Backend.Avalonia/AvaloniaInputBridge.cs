// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using PCL.UI.Next;
using AvaloniaKey = Avalonia.Input.Key;

namespace PCL.UI.Next.Backend.Avalonia;

/// <summary>Normalizes Avalonia pointer/key events into the backend-neutral input queue.</summary>
public sealed class AvaloniaInputBridge : IDisposable
{
    private readonly Control _source;
    private readonly UiInputRuntime _input;
    private readonly UiInputRootId _inputRoot;
    private readonly HashSet<int> _intentionalCaptureReleases = [];
    private bool _disposed;

    public AvaloniaInputBridge(Control source, UiInputRuntime input, UiInputRootId inputRoot)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        if (!input.InputRoots.IsAlive(inputRoot))
            throw new InvalidOperationException("Input root is stale or invalid: " + inputRoot);
        _inputRoot = inputRoot;
        _source.PointerMoved += OnPointerMoved;
        _source.PointerPressed += OnPointerPressed;
        _source.PointerReleased += OnPointerReleased;
        _source.PointerExited += OnPointerExited;
        _source.PointerCaptureLost += OnPointerCaptureLost;
        _source.PointerWheelChanged += OnPointerWheelChanged;
        _source.KeyDown += OnKeyDown;
        _source.KeyUp += OnKeyUp;
    }

    /// <summary>Signals the host that an event requested a reactive Runtime frame.</summary>
    public event Action? InputQueued;

    public void Dispose()
    {
        if (_disposed)
            return;
        _source.PointerMoved -= OnPointerMoved;
        _source.PointerPressed -= OnPointerPressed;
        _source.PointerReleased -= OnPointerReleased;
        _source.PointerExited -= OnPointerExited;
        _source.PointerCaptureLost -= OnPointerCaptureLost;
        _source.PointerWheelChanged -= OnPointerWheelChanged;
        _source.KeyDown -= OnKeyDown;
        _source.KeyUp -= OnKeyUp;
        InputQueued = null;
        _disposed = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        _ = sender;
        Point point = e.GetPosition(_source);
        PointerPoint current = e.GetCurrentPoint(_source);
        EnqueuePointer(
            e.Pointer,
            UiPointerEventKind.Move,
            point,
            UiPointerButton.None,
            Buttons(current.Properties),
            Modifiers(e.KeyModifiers));
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _ = sender;
        _source.Focus();
        Point point = e.GetPosition(_source);
        PointerPoint current = e.GetCurrentPoint(_source);
        EnqueuePointer(
            e.Pointer,
            UiPointerEventKind.Down,
            point,
            ChangedButton(current.Properties.PointerUpdateKind),
            Buttons(current.Properties),
            Modifiers(e.KeyModifiers));
        e.Pointer.Capture(_source);
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _ = sender;
        Point point = e.GetPosition(_source);
        PointerPoint current = e.GetCurrentPoint(_source);
        EnqueuePointer(
            e.Pointer,
            UiPointerEventKind.Up,
            point,
            ChangedButton(current.Properties.PointerUpdateKind),
            Buttons(current.Properties),
            Modifiers(e.KeyModifiers));
        if (ReferenceEquals(e.Pointer.Captured, _source))
        {
            _intentionalCaptureReleases.Add(e.Pointer.Id);
            e.Pointer.Capture(null);
        }
        e.Handled = true;
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        _ = sender;
        Point point = e.GetPosition(_source);
        EnqueuePointer(
            e.Pointer,
            UiPointerEventKind.Move,
            point,
            UiPointerButton.None,
            UiPointerButtons.None,
            Modifiers(e.KeyModifiers));
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _ = sender;
        if (_intentionalCaptureReleases.Remove(e.Pointer.Id))
            return;
        EnqueuePointer(
            e.Pointer,
            UiPointerEventKind.Cancel,
            default,
            UiPointerButton.None,
            UiPointerButtons.None,
            UiInputModifiers.None);
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        _ = sender;
        Point point = e.GetPosition(_source);
        _input.EnqueueWheel(
            _inputRoot,
            new UiPoint((float)point.X, (float)point.Y),
            new UiPoint((float)e.Delta.X, (float)e.Delta.Y),
            Modifiers(e.KeyModifiers));
        InputQueued?.Invoke();
        e.Handled = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        _ = sender;
        UiKey key = MapKey(e.Key);
        if (key == UiKey.None)
            return;
        _input.EnqueueKey(_inputRoot, UiKeyEventKind.Down, key, Modifiers(e.KeyModifiers));
        InputQueued?.Invoke();
        e.Handled = true;
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        _ = sender;
        UiKey key = MapKey(e.Key);
        if (key == UiKey.None)
            return;
        _input.EnqueueKey(_inputRoot, UiKeyEventKind.Up, key, Modifiers(e.KeyModifiers));
        InputQueued?.Invoke();
        e.Handled = true;
    }

    private void EnqueuePointer(
        IPointer pointer,
        UiPointerEventKind kind,
        Point point,
        UiPointerButton changedButton,
        UiPointerButtons buttons,
        UiInputModifiers modifiers)
    {
        _input.EnqueuePointer(
            _inputRoot,
            kind,
            new UiPoint((float)point.X, (float)point.Y),
            unchecked((int)pointer.Id),
            changedButton,
            buttons,
            modifiers);
        InputQueued?.Invoke();
    }

    private static UiPointerButtons Buttons(PointerPointProperties properties)
    {
        UiPointerButtons buttons = UiPointerButtons.None;
        if (properties.IsLeftButtonPressed) buttons |= UiPointerButtons.Primary;
        if (properties.IsRightButtonPressed) buttons |= UiPointerButtons.Secondary;
        if (properties.IsMiddleButtonPressed) buttons |= UiPointerButtons.Middle;
        if (properties.IsXButton1Pressed) buttons |= UiPointerButtons.X1;
        if (properties.IsXButton2Pressed) buttons |= UiPointerButtons.X2;
        return buttons;
    }

    private static UiPointerButton ChangedButton(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => UiPointerButton.Primary,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => UiPointerButton.Secondary,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => UiPointerButton.Middle,
        PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton1Released => UiPointerButton.X1,
        PointerUpdateKind.XButton2Pressed or PointerUpdateKind.XButton2Released => UiPointerButton.X2,
        _ => UiPointerButton.None
    };

    private static UiInputModifiers Modifiers(KeyModifiers modifiers)
    {
        UiInputModifiers result = UiInputModifiers.None;
        if ((modifiers & KeyModifiers.Shift) != 0) result |= UiInputModifiers.Shift;
        if ((modifiers & KeyModifiers.Control) != 0) result |= UiInputModifiers.Control;
        if ((modifiers & KeyModifiers.Alt) != 0) result |= UiInputModifiers.Alt;
        if ((modifiers & KeyModifiers.Meta) != 0) result |= UiInputModifiers.Meta;
        return result;
    }

    private static UiKey MapKey(AvaloniaKey key) => key switch
    {
        AvaloniaKey.Tab => UiKey.Tab,
        AvaloniaKey.Enter => UiKey.Enter,
        AvaloniaKey.Space => UiKey.Space,
        AvaloniaKey.Escape => UiKey.Escape,
        AvaloniaKey.Left => UiKey.Left,
        AvaloniaKey.Up => UiKey.Up,
        AvaloniaKey.Right => UiKey.Right,
        AvaloniaKey.Down => UiKey.Down,
        AvaloniaKey.Home => UiKey.Home,
        AvaloniaKey.End => UiKey.End,
        AvaloniaKey.PageUp => UiKey.PageUp,
        AvaloniaKey.PageDown => UiKey.PageDown,
        AvaloniaKey.Back => UiKey.Backspace,
        AvaloniaKey.Delete => UiKey.Delete,
        AvaloniaKey.F1 => UiKey.F1,
        AvaloniaKey.F2 => UiKey.F2,
        AvaloniaKey.F3 => UiKey.F3,
        AvaloniaKey.F4 => UiKey.F4,
        AvaloniaKey.F5 => UiKey.F5,
        AvaloniaKey.F6 => UiKey.F6,
        AvaloniaKey.F7 => UiKey.F7,
        AvaloniaKey.F8 => UiKey.F8,
        AvaloniaKey.F9 => UiKey.F9,
        AvaloniaKey.F10 => UiKey.F10,
        AvaloniaKey.F11 => UiKey.F11,
        AvaloniaKey.F12 => UiKey.F12,
        >= AvaloniaKey.A and <= AvaloniaKey.Z =>
            (UiKey)((int)UiKey.A + ((int)key - (int)AvaloniaKey.A)),
        >= AvaloniaKey.D0 and <= AvaloniaKey.D9 =>
            (UiKey)((int)UiKey.Digit0 + ((int)key - (int)AvaloniaKey.D0)),
        _ => UiKey.None
    };
}

// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia;
using PCL.UI.Next;

namespace PCL.UI.Next.Backend.Avalonia;

/// <summary>Avalonia platform adapter for retained ECS render commits.</summary>
public sealed class AvaloniaUiBackend : IUiBackend, INativeHostBackend
{
    private readonly Action _invalidate;
    private bool _initialized;
    private readonly Canvas _nativeLayer = new();
    private readonly Dictionary<NativeHostHandle, NativeEntry> _nativeHosts = [];
    private int _nextNativeHost = 1;

    public AvaloniaUiBackend(AvaloniaTextEngine textEngine)
    {
        Surface = new PclUiSurface(textEngine ?? throw new ArgumentNullException(nameof(textEngine)));
        View = new Grid();
        View.Children.Add(Surface);
        _nativeLayer.SetValue(Panel.ZIndexProperty, 1);
        View.Children.Add(_nativeLayer);
        _invalidate = Surface.InvalidateVisual;
    }

    public PclUiSurface Surface { get; }
    public Grid View { get; }
    public int NativeHostCount => _nativeHosts.Count;

    public event Action<NativeHostEvent>? NativeHostEventRaised;

    public UiBackendCapabilities Capabilities =>
        UiBackendCapabilities.Clip | UiBackendCapabilities.NativeTextInput;

    public void Initialize(in UiBackendContext context)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_initialized)
            throw new InvalidOperationException("Backend is already initialized.");
        Surface.Initialize(in context);
        _initialized = true;
    }

    public void Commit(in UiCommitBatch batch)
    {
        Dispatcher.UIThread.VerifyAccess();
        EnsureInitialized();
        Surface.Apply(in batch);
    }

    public void RequestFrame()
    {
        EnsureInitialized();
        if (Dispatcher.UIThread.CheckAccess())
            _invalidate();
        else
            Dispatcher.UIThread.Post(_invalidate, DispatcherPriority.Render);
    }

    public NativeHostHandle CreateNativeHost(in NativeHostDescriptor descriptor)
    {
        Dispatcher.UIThread.VerifyAccess();
        EnsureInitialized();
        NativeHostHandle handle = new(_nextNativeHost++, 1);
        TextBox textBox = new()
        {
            PasswordChar = descriptor.Kind == UiNativeHostKind.PasswordBox ? '●' : '\0'
        };
        NativeEntry entry = new(handle, descriptor.Owner, textBox);
        textBox.TextChanged += entry.OnTextChanged = (_, _) =>
        {
            if (entry.IsApplying)
                return;
            RaiseNativeEvent(entry, NativeHostEventKind.ValueChanged, textBox.Text);
        };
        textBox.PropertyChanged += entry.OnPropertyChanged = (_, e) =>
        {
            if (entry.IsApplying ||
                (e.Property != TextBox.SelectionStartProperty && e.Property != TextBox.SelectionEndProperty))
                return;
            RaiseNativeEvent(entry, NativeHostEventKind.SelectionChanged, textBox.Text);
        };
        textBox.GotFocus += entry.OnGotFocus = (_, _) => RaiseNativeEvent(entry, NativeHostEventKind.GotFocus, textBox.Text);
        textBox.LostFocus += entry.OnLostFocus = (_, _) => RaiseNativeEvent(entry, NativeHostEventKind.LostFocus, textBox.Text);
        textBox.KeyDown += entry.OnKeyDown = (_, e) =>
        {
            if (e.Key == Key.Enter && !textBox.AcceptsReturn)
                RaiseNativeEvent(entry, NativeHostEventKind.Submitted, textBox.Text);
        };
        _nativeHosts.Add(handle, entry);
        _nativeLayer.Children.Add(textBox);
        ApplyState(entry, NativeHostMutationFlags.All, descriptor.State);
        return handle;
    }

    public void UpdateNativeHost(NativeHostHandle handle, in NativeHostMutation mutation)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_nativeHosts.TryGetValue(handle, out NativeEntry? entry))
            throw new InvalidOperationException("Native-host handle is stale or invalid: " + handle);
        ApplyState(entry, mutation.Flags, mutation.State);
    }

    public void DestroyNativeHost(NativeHostHandle handle)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_nativeHosts.Remove(handle, out NativeEntry? entry))
            return;
        TextBox textBox = entry.Control;
        textBox.TextChanged -= entry.OnTextChanged;
        textBox.PropertyChanged -= entry.OnPropertyChanged;
        textBox.GotFocus -= entry.OnGotFocus;
        textBox.LostFocus -= entry.OnLostFocus;
        textBox.KeyDown -= entry.OnKeyDown;
        _nativeLayer.Children.Remove(textBox);
    }

    private static void ApplyState(
        NativeEntry entry,
        NativeHostMutationFlags flags,
        NativeHostVisualState state)
    {
        TextBox control = entry.Control;
        entry.IsApplying = true;
        try
        {
            if ((flags & NativeHostMutationFlags.Bounds) != 0)
            {
                Canvas.SetLeft(control, state.Bounds.X);
                Canvas.SetTop(control, state.Bounds.Y);
                control.Width = state.Bounds.Width;
                control.Height = state.Bounds.Height;
            }
            if ((flags & NativeHostMutationFlags.Value) != 0 && control.Text != state.Value)
                control.Text = state.Value;
            if ((flags & NativeHostMutationFlags.Placeholder) != 0)
                control.PlaceholderText = state.Placeholder;
            if ((flags & NativeHostMutationFlags.Selection) != 0)
            {
                control.SelectionStart = state.SelectionStart;
                control.SelectionEnd = state.SelectionEnd;
            }
            if ((flags & NativeHostMutationFlags.Visibility) != 0)
                control.IsVisible = state.IsVisible;
            if ((flags & NativeHostMutationFlags.Enabled) != 0)
                control.IsEnabled = state.IsEnabled;
            if ((flags & NativeHostMutationFlags.ReadOnly) != 0)
                control.IsReadOnly = state.IsReadOnly;
            if ((flags & NativeHostMutationFlags.AcceptsReturn) != 0)
                control.AcceptsReturn = state.AcceptsReturn;
            if ((flags & NativeHostMutationFlags.Focus) != 0 && state.IsFocused && !control.IsFocused)
                control.Focus();
        }
        finally
        {
            entry.IsApplying = false;
        }
    }

    private void RaiseNativeEvent(NativeEntry entry, NativeHostEventKind kind, string? value)
    {
        NativeHostEvent nativeEvent = new(
            entry.Handle,
            kind,
            UiTimestamp.Zero,
            value,
            entry.Control.SelectionStart,
            entry.Control.SelectionEnd);
        NativeHostEventRaised?.Invoke(nativeEvent);
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException("Backend must be initialized before use.");
    }

    private sealed class NativeEntry(
        NativeHostHandle handle,
        UiEntity owner,
        TextBox control)
    {
        public NativeHostHandle Handle { get; } = handle;
        public UiEntity Owner { get; } = owner;
        public TextBox Control { get; } = control;
        public bool IsApplying { get; set; }
        public EventHandler<TextChangedEventArgs>? OnTextChanged { get; set; }
        public EventHandler<AvaloniaPropertyChangedEventArgs>? OnPropertyChanged { get; set; }
        public EventHandler<FocusChangedEventArgs>? OnGotFocus { get; set; }
        public EventHandler<FocusChangedEventArgs>? OnLostFocus { get; set; }
        public EventHandler<KeyEventArgs>? OnKeyDown { get; set; }
    }
}

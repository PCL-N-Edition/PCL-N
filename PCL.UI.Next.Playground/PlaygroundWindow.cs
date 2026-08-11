// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using PCL.UI.Next.Backend.Avalonia;

namespace PCL.UI.Next.Playground;

/// <summary>Interactive smoke window for the retained ECS UI runtime.</summary>
public sealed class PlaygroundWindow : Window, IDisposable
{
    private const int CounterSlice = 1;
    private const int DetailsSlice = 2;
    private const int StatusSlice = 3;

    private static readonly UiCommand IncrementCommand = new(1001, "Increment");
    private static readonly UiCommand ToggleDetailsCommand = new(1002, "Toggle details");
    private static readonly UiCommand ToggleThemeCommand = new(1003, "Toggle theme");
    private static readonly UiCommand MotionCommand = new(1004, "Retarget motion");
    private static readonly UiCommand ReducedMotionCommand = new(1005, "Reduced motion");
    private static readonly UiCommand ResetCommand = new(1006, "Reset");

    private static readonly UiClass RootClass = new(5001, "Playground.Root");
    private static readonly UiClass SubtitleClass = new(5002, "Playground.Subtitle");
    private static readonly UiClass ActionClass = new(5003, "Playground.Action");
    private static readonly UiClass CardClass = new(5004, "Playground.Card");
    private static readonly UiClass AccentCardClass = new(5005, "Playground.AccentCard");
    private static readonly UiClass MutedCardClass = new(5006, "Playground.MutedCard");
    private static readonly UiClass DemoTargetClass = new(5007, "Playground.MotionTarget");

    private readonly UiWorld _world;
    private readonly UiInteractiveRuntime _runtime;
    private readonly UiRenderingRuntime _rendering;
    private readonly AvaloniaTextEngine _textEngine;
    private readonly AvaloniaUiBackend _backend;
    private readonly PresentationStore _presentation;
    private readonly BlueprintInstantiator _blueprints;
    private readonly UiScopeId _windowScope;
    private readonly UiInputRootId _inputRoot;
    private readonly AvaloniaInputBridge _inputBridge;
    private readonly DispatcherTimer _timer;
    private readonly IDisposable _resetShortcut;
    private int _counter;
    private bool _detailsVisible = true;
    private bool _darkTheme;
    private bool _motionRight;
    private bool _reducedMotion;
    private UiEntity _motionTarget;
    private bool _disposed;

    public PlaygroundWindow()
    {
        Title = "PCL.UI.Next Rendering Playground";
        Width = 1120;
        Height = 760;
        MinWidth = 820;
        MinHeight = 600;
        Background = new SolidColorBrush(Color.FromRgb(18, 22, 29));

        UiSize viewport = new(1120, 760);
        _world = new UiWorld(new StopwatchUiClock());
        _textEngine = new AvaloniaTextEngine();
        _runtime = new UiInteractiveRuntime(_world, _textEngine, viewport);
        ConfigureStyles(_runtime.Styles);
        UiScopeId applicationScope = _world.CreateRootScope();
        _windowScope = _world.CreateScope(applicationScope);
        _backend = new AvaloniaUiBackend(_textEngine);
        _rendering = new UiRenderingRuntime(_world, _backend, _windowScope, viewport);
        _presentation = new PresentationStore();
        _presentation.Set(CounterSlice, _counter);
        _presentation.Set(DetailsSlice, _detailsVisible);
        _presentation.Set(StatusSlice, "Ready · click a button, resize the window, use Tab/Enter, or press F5");
        _blueprints = new BlueprintInstantiator(_world, _presentation);

        _inputRoot = _runtime.Input.InputRoots.Register(_windowScope);
        _resetShortcut = _runtime.Input.Shortcuts.Register(
            _windowScope,
            new UiKeyGesture(UiKey.F5),
            ResetCommand);

        BlueprintInstance instance = _blueprints.Instantiate(Ui.Compile(BuildContent()), _windowScope);
        _motionTarget = FindEntityWithClass(instance, DemoTargetClass);

        Content = _backend.Surface;
        _inputBridge = new AvaloniaInputBridge(_backend.Surface, _runtime.Input, _inputRoot);
        _inputBridge.InputQueued += PumpFrame;
        SizeChanged += OnSizeChanged;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000d / 60d) };
        _timer.Tick += OnTick;
        _timer.Start();
        PumpFrame();
    }

    protected override void OnClosed(EventArgs e)
    {
        DisposeRuntime();
        base.OnClosed(e);
    }

    public void Dispose() => DisposeRuntime();

    private static UiNode BuildContent()
    {
        UiSelector<string> counterText = UiSelectors.String(
            6001,
            CounterSlice,
            static store => "Command counter: " + store.Get<int>(CounterSlice));
        UiSelector<bool> details = UiSelectors.Bool(
            6002,
            DetailsSlice,
            static store => store.Get<bool>(DetailsSlice));
        UiSelector<string> status = UiSelectors.String(
            6003,
            StatusSlice,
            static store => store.Get<string>(StatusSlice));

        UiGridDefinition cards = new(
            [UiGridTrack.Star(1f, min: 180f), UiGridTrack.Star(2f, min: 260f), UiGridTrack.Star(1f, min: 180f)],
            [UiGridTrack.Auto()]);

        return Ui.Column(
                Ui.Text("PCL.UI.Next · Retained Rendering Playground")
                    .Class(UiClass.PageTitle),
                Ui.Text("This window is rendered by ECS entities → incremental layout → animation → retained RenderScene → minimal commits → one Avalonia surface. The paragraph wraps against the parent constraint when the window is resized.")
                    .Class(SubtitleClass)
                    .WrapText(1000),
                Ui.Row(
                        ActionButton("Increment", IncrementCommand),
                        ActionButton("Toggle structure", ToggleDetailsCommand),
                        ActionButton("Switch theme", ToggleThemeCommand),
                        ActionButton("Retarget spring", MotionCommand),
                        ActionButton("Reduced motion", ReducedMotionCommand))
                    .Gap(10)
                    .Height(UiLength.Pixels(46)),
                Ui.Grid(
                        cards,
                        Card("Fixed + Star Grid", "Track 1: 1*", CardClass).GridCell(0, 0),
                        Card("Animated retained layer", "Hover buttons; resize for FLIP", AccentCardClass)
                            .Class(DemoTargetClass)
                            .AnimateLayout(UiMotion.Layout)
                            .GridCell(0, 1),
                        Card("Text shaping", "Avalonia TextLayout handle", MutedCardClass).GridCell(0, 2))
                    .Gap(12)
                    .Height(UiLength.Pixels(118)),
                Ui.If(
                    details,
                    Ui.Container(
                            Ui.Column(
                                    Ui.Text("Reactive structural branch mounted")
                                        .Class(UiClass.Body),
                                    Ui.Text("The alternate subtree is destroyed and recreated generation-safely; RenderDiff emits only the required create/destroy mutations.")
                                        .Class(SubtitleClass)
                                        .WrapText(900))
                                .Gap(6))
                        .Class(CardClass)
                        .Height(UiLength.Pixels(88)),
                    Ui.Container(
                            Ui.Text("Structural branch is currently unmounted.").Class(SubtitleClass))
                        .Class(MutedCardClass)
                        .Height(UiLength.Pixels(52))),
                Ui.Overlay(
                        Ui.Container().Class(CardClass),
                        Ui.Absolute(
                            Ui.Container().Class(AccentCardClass)
                                .Width(UiLength.Pixels(160)).Height(UiLength.Pixels(48)).At(0, 8),
                            Ui.Container().Class(MutedCardClass)
                                .Width(UiLength.Pixels(210)).Height(UiLength.Pixels(48)).At(180, 8),
                            Ui.Text("Absolute / Overlay geometry").Class(UiClass.Body).At(18, 22)))
                    .Height(UiLength.Pixels(68)),
                Ui.Text().BindText(counterText).Class(UiClass.Body),
                Ui.Text().BindText(status).Class(SubtitleClass).WrapText(1000))
            .Class(RootClass)
            .Gap(14)
            .Padding(new UiThickness(28))
            .Width(UiLength.Percent(1f))
            .Height(UiLength.Percent(1f));
    }

    private static UiNode ActionButton(string label, UiCommand command) =>
        Ui.Button(label)
            .Class(ActionClass)
            .Command(command)
            .Width(UiLength.Pixels(170))
            .Height(UiLength.Pixels(44))
            .Transition(UiAnimationProperty.ScaleX, UiMotion.Hover)
            .Transition(UiAnimationProperty.ScaleY, UiMotion.Hover)
            .Transition(UiAnimationProperty.Opacity, UiMotion.FastFade);

    private static UiNode Card(string title, string subtitle, UiClass styleClass) =>
        Ui.Container(
                Ui.Column(
                        Ui.Text(title).Class(UiClass.Body),
                        Ui.Text(subtitle).Class(SubtitleClass).WrapText(300))
                    .Gap(7))
            .Class(styleClass)
            .Padding(new UiThickness(14));

    private static void ConfigureStyles(UiStyleSheet styles)
    {
        styles.Add(new UiStyleRule(
            RootClass,
            default(UiStyleValues)
                .WithBackground(UiColor.FromRgb(25, 30, 39))
                .WithForeground(UiColor.FromRgb(235, 239, 246))));
        styles.Add(new UiStyleRule(
            SubtitleClass,
            default(UiStyleValues)
                .WithForeground(UiColor.FromRgb(163, 174, 190))
                .WithFontSize(14f)));
        styles.Add(new UiStyleRule(
            ActionClass,
            default(UiStyleValues)
                .WithBackground(UiColor.FromRgb(51, 62, 79))
                .WithForeground(UiColor.FromRgb(241, 245, 250))
                .WithCornerRadius(9f)
                .WithScale(1f)));
        styles.Add(new UiStyleRule(
            ActionClass,
            default(UiStyleValues)
                .WithBackground(UiColor.FromRgb(67, 84, 109))
                .WithScale(1.025f),
            requiredState: InteractionState.Hovered,
            priority: 20));
        styles.Add(new UiStyleRule(
            ActionClass,
            default(UiStyleValues).WithScale(0.965f),
            requiredState: InteractionState.Pressed,
            priority: 30));
        styles.Add(new UiStyleRule(
            ActionClass,
            default(UiStyleValues).WithBackground(UiColor.FromRgb(70, 123, 235)),
            requiredState: InteractionState.Focused,
            priority: 25));
        styles.Add(new UiStyleRule(
            CardClass,
            default(UiStyleValues)
                .WithBackground(UiColor.FromRgb(38, 46, 59))
                .WithCornerRadius(12f)));
        styles.Add(new UiStyleRule(
            AccentCardClass,
            default(UiStyleValues)
                .WithBackground(UiColor.FromRgb(43, 78, 145))
                .WithCornerRadius(12f)));
        styles.Add(new UiStyleRule(
            MutedCardClass,
            default(UiStyleValues)
                .WithBackground(UiColor.FromRgb(46, 51, 62))
                .WithCornerRadius(12f)));
    }

    private UiEntity FindEntityWithClass(BlueprintInstance instance, UiClass styleClass)
    {
        for (int i = 0; i < instance.Blueprint.NodeCount; i++)
        {
            UiEntity entity = instance.EntityAt(i);
            if (_world.Entities.IsAlive(entity) &&
                _world.Components.TryGet(entity, out StyleClassSet classes) &&
                classes.Contains(styleClass.Id))
            {
                return entity;
            }
        }
        throw new InvalidOperationException("Playground motion target was not instantiated.");
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        _ = sender;
        _runtime.SetViewport(new UiSize(
            Math.Max(0f, (float)e.NewSize.Width),
            Math.Max(0f, (float)e.NewSize.Height)));
        SetStatus("Viewport resized · constrained text + Grid + FLIP recomputed in one frame");
        PumpFrame();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (_world.Scheduler.NeedsFrame)
            PumpFrame();
    }

    private void PumpFrame()
    {
        if (_disposed || !_world.Scheduler.NeedsFrame)
            return;
        _world.Update();
        ProcessCommands();
        Title = $"PCL.UI.Next Playground · frame {_world.FrameIndex} · " +
                $"nodes {_rendering.Scene.NodeCount} · commits {_backend.Surface.CommitCount}";
    }

    private void ProcessCommands()
    {
        while (_runtime.Input.Commands.TryDequeue(out UiCommandInvocation invocation))
        {
            if (invocation.Command == IncrementCommand)
            {
                _presentation.Set(CounterSlice, ++_counter);
                SetStatus($"Command routed from entity {invocation.Source.Index} via {invocation.Trigger}");
            }
            else if (invocation.Command == ToggleDetailsCommand)
            {
                _detailsVisible = !_detailsVisible;
                _presentation.Set(DetailsSlice, _detailsVisible);
                SetStatus("Structural reconcile switched the mounted branch");
            }
            else if (invocation.Command == ToggleThemeCommand)
            {
                _darkTheme = !_darkTheme;
                ApplyThemeVariant();
                SetStatus("Theme tokens invalidated only dependent styled entities");
            }
            else if (invocation.Command == MotionCommand)
            {
                _motionRight = !_motionRight;
                UiAnimationSpec spec = new(UiMotion.SpringExpressive);
                _runtime.Animation.Retarget(
                    _motionTarget,
                    UiAnimationProperty.TranslateX,
                    _motionRight ? 42f : 0f,
                    in spec);
                SetStatus("Spring channel retargeted from current value + velocity");
            }
            else if (invocation.Command == ReducedMotionCommand)
            {
                _reducedMotion = !_reducedMotion;
                _runtime.Animation.SetReducedMotion(_reducedMotion);
                SetStatus("Reduced motion: " + (_reducedMotion ? "ON" : "OFF"));
            }
            else if (invocation.Command == ResetCommand)
            {
                ResetPlayground();
            }
        }
    }

    private void ApplyThemeVariant()
    {
        _runtime.Theme.Set(
            UiThemeTokens.TextPrimary,
            _darkTheme ? UiColor.FromRgb(242, 245, 250) : UiColor.FromRgb(31, 35, 41));
        _runtime.Theme.Set(
            UiThemeTokens.Surface,
            _darkTheme ? UiColor.FromRgb(48, 57, 72) : UiColor.FromRgb(245, 247, 250));
        _runtime.Theme.Set(
            UiThemeTokens.SurfaceHover,
            _darkTheme ? UiColor.FromRgb(64, 77, 98) : UiColor.FromRgb(232, 237, 244));
    }

    private void ResetPlayground()
    {
        _counter = 0;
        _detailsVisible = true;
        _motionRight = false;
        _reducedMotion = false;
        _presentation.Set(CounterSlice, _counter);
        _presentation.Set(DetailsSlice, _detailsVisible);
        _runtime.Animation.SetReducedMotion(false);
        UiAnimationSpec spec = new(UiMotion.SpringExpressive);
        _runtime.Animation.Retarget(_motionTarget, UiAnimationProperty.TranslateX, 0f, in spec);
        SetStatus("Reset by F5 shortcut through the scope-aware command registry");
    }

    private void SetStatus(string value) => _presentation.Set(StatusSlice, value);

    private void DisposeRuntime()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        SizeChanged -= OnSizeChanged;
        _inputBridge.InputQueued -= PumpFrame;
        _inputBridge.Dispose();
        _resetShortcut.Dispose();
        _rendering.Dispose();
        _runtime.Dispose();
        _textEngine.Dispose();
    }

}

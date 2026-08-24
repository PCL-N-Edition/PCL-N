// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Theme;

namespace PCL.Desktop.Controls.Motion;

/// <summary>
/// White transition surface with a circular transparent aperture. The same
/// reversible component is used for OOBE handoff and ultra-low-power focus changes.
/// </summary>
public sealed class CircularRevealOverlay : Grid
{
    private readonly RevealMask _mask;
    private readonly Image _icon;
    private DispatcherTimer? _timer;
    private int _animationGeneration;
    private double _radius;

    public CircularRevealOverlay()
    {
        IsVisible = false;
        IsHitTestVisible = true;
        ClipToBounds = true;
        _mask = new RevealMask();
        _icon = new Image
        {
            Width = 112d,
            Height = 112d,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Source = new Bitmap(AssetLoader.Open(new Uri("avares://PCL.Desktop/Assets/icon.png")))
        };
        Children.Add(_mask);
        Children.Add(_icon);
        SizeChanged += (_, _) =>
        {
            if (_radius >= MaximumRadius - 1d)
                SetRadius(MaximumRadius);
        };
    }

    public double RevealRadius => _radius;

    public bool IsCovered => IsVisible && _radius <= 0.5d && Opacity >= 0.99d;

    private double MaximumRadius =>
        Math.Sqrt(Bounds.Width * Bounds.Width + Bounds.Height * Bounds.Height) / 2d + 4d;

    public void PrepareCovered(bool showIcon = true)
    {
        CancelAnimation();
        IsVisible = true;
        Opacity = 1d;
        _icon.Opacity = showIcon ? 1d : 0d;
        SetRadius(0d);
    }

    public void PrepareRevealed()
    {
        CancelAnimation();
        _icon.Opacity = 0d;
        SetRadius(MaximumRadius);
        Opacity = 0d;
        IsVisible = false;
    }

    public Task CoverAsync(bool showIcon, CancellationToken cancellationToken = default) =>
        RunSequenceAsync(cover: true, showIcon, cancellationToken);

    public Task RevealAsync(CancellationToken cancellationToken = default) =>
        RunSequenceAsync(cover: false, showIcon: false, cancellationToken);

    private async Task RunSequenceAsync(
        bool cover,
        bool showIcon,
        CancellationToken cancellationToken)
    {
        int generation = CancelAnimation();
        IsVisible = true;
        IsHitTestVisible = true;

        bool reducedMotion = ControlVisualHelpers.ReduceMotionPreferred();
        if (reducedMotion)
        {
            SetRadius(0d);
            _icon.Opacity = showIcon ? 1d : 0d;
            double targetOpacity = cover ? 1d : 0d;
            if (cover && Opacity >= 0.99d)
                Opacity = 0d;
            await AnimateAsync(
                    Opacity,
                    targetOpacity,
                    MotionTokens.ReducedMotionFadeMs,
                    value => Opacity = value,
                    generation,
                    cancellationToken)
                .ConfigureAwait(true);
        }
        else if (cover)
        {
            Opacity = 1d;
            _icon.Opacity = 0d;
            double startRadius = _radius <= 0.5d ? MaximumRadius : _radius;
            SetRadius(startRadius);
            await AnimateAsync(
                    startRadius,
                    0d,
                    MotionTokens.CircularCoverMs,
                    SetRadius,
                    generation,
                    cancellationToken)
                .ConfigureAwait(true);
            if (showIcon)
            {
                await AnimateAsync(
                        _icon.Opacity,
                        1d,
                        MotionTokens.TransitionIconFadeMs,
                        value => _icon.Opacity = value,
                        generation,
                        cancellationToken)
                    .ConfigureAwait(true);
            }
        }
        else
        {
            Opacity = 1d;
            await AnimateAsync(
                    _icon.Opacity,
                    0d,
                    MotionTokens.TransitionIconFadeMs,
                    value => _icon.Opacity = value,
                    generation,
                    cancellationToken)
                .ConfigureAwait(true);
            await AnimateAsync(
                    _radius,
                    MaximumRadius,
                    MotionTokens.CircularRevealMs,
                    SetRadius,
                    generation,
                    cancellationToken)
                .ConfigureAwait(true);
        }

        if (generation != _animationGeneration)
            return;
        if (!cover)
        {
            Opacity = 0d;
            IsVisible = false;
            IsHitTestVisible = false;
        }
    }

    private Task AnimateAsync(
        double from,
        double to,
        int durationMs,
        Action<double> apply,
        int generation,
        CancellationToken cancellationToken)
    {
        if (durationMs <= 0 || Math.Abs(to - from) < 0.001d)
        {
            apply(to);
            return Task.CompletedTask;
        }

        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Stopwatch stopwatch = Stopwatch.StartNew();
        CancellationTokenRegistration registration = cancellationToken.Register(() =>
            Dispatcher.UIThread.Post(() =>
            {
                if (generation == _animationGeneration)
                    CancelAnimation();
                completion.TrySetCanceled(cancellationToken);
            }));
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16d) };
        _timer.Tick += (_, _) =>
        {
            if (generation != _animationGeneration || cancellationToken.IsCancellationRequested)
            {
                _timer?.Stop();
                registration.Dispose();
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            double progress = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / durationMs, 0d, 1d);
            // Critically damped visual settle: monotonic cubic, never overshoots.
            double eased = 1d - Math.Pow(1d - progress, 3d);
            apply(from + (to - from) * eased);
            if (progress < 1d)
                return;

            _timer?.Stop();
            apply(to);
            registration.Dispose();
            completion.TrySetResult();
        };
        _timer.Start();
        return completion.Task;
    }

    private int CancelAnimation()
    {
        _animationGeneration++;
        _timer?.Stop();
        _timer = null;
        return _animationGeneration;
    }

    private void SetRadius(double value)
    {
        _radius = Math.Clamp(value, 0d, Math.Max(0d, MaximumRadius));
        _mask.Radius = _radius;
    }

    private sealed class RevealMask : Control
    {
        private double _radius;

        public double Radius
        {
            get => _radius;
            set
            {
                if (Math.Abs(_radius - value) < 0.001d)
                    return;
                _radius = value;
                InvalidateVisual();
            }
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            Rect bounds = new(Bounds.Size);
            if (bounds.Width <= 0d || bounds.Height <= 0d)
                return;
            EllipseGeometry aperture = new(new Rect(
                bounds.Center.X - Radius,
                bounds.Center.Y - Radius,
                Radius * 2d,
                Radius * 2d));
            CombinedGeometry mask = new(
                GeometryCombineMode.Exclude,
                new RectangleGeometry(bounds),
                aperture);
            context.DrawGeometry(Brushes.White, null, mask);
        }
    }
}

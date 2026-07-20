// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PCL.Desktop.Theme;

namespace PCL.Desktop.Controls.Legacy;

public class MyPageRight : ContentControl, IDisposable
{
    public enum PageStates
    {
        Empty,
        LoaderWait,
        LoaderEnter,
        LoaderStayForce,
        LoaderStay,
        LoaderExit,
        ContentEnter,
        ContentStay,
        ContentExit,
        PageExit
    }

    public static readonly StyledProperty<MyScrollViewer?> PanScrollProperty =
        AvaloniaProperty.Register<MyPageRight, MyScrollViewer?>(nameof(PanScroll));

    private Func<CancellationToken, Task>? _pageLoader;
    private Action? _pageLoaderFinished;
    private CancellationTokenSource? _pageLoaderCancellation;
    private Control? _pageLoaderPanel;
    private Control? _pageContentPanel;
    private Control? _pageAlwaysPanel;
    private LoaderRunState _pageLoaderState = LoaderRunState.Waiting;
    private bool _pageLoaderAutoRun;

    protected override Type StyleKeyOverride => typeof(ContentControl);

    public int PageUuid { get; } = Random.Shared.Next();

    public List<Control> DisabledPageAnimControls { get; } = [];

    public MyScrollViewer? PanScroll
    {
        get
        {
            MyScrollViewer? scroll = GetValue(PanScrollProperty);
            if (scroll is not null)
                return scroll;

            scroll = ResolveCopiedPageScrollViewer();
            return scroll;
        }
        set => SetValue(PanScrollProperty, value);
    }

    public PageStates PageState { get; set; } = PageStates.Empty;

    public event Action? PageEnter;

    public event Action? PageExit;

    private enum LoaderRunState
    {
        Waiting,
        Loading,
        Finished,
        Failed
    }

    public void PageLoaderInit(
        MyLoading loaderUi,
        Control panLoader,
        Control panContent,
        Control? panAlways,
        Func<CancellationToken, Task> realLoader,
        Action? finishedInvoke = null,
        bool autoRun = true)
    {
        _pageLoader = realLoader;
        _pageLoaderFinished = finishedInvoke;
        _pageLoaderPanel = panLoader;
        _pageContentPanel = panContent;
        _pageAlwaysPanel = panAlways;
        _pageLoaderAutoRun = autoRun;
        _pageLoaderState = LoaderRunState.Waiting;

        loaderUi.Text = "正在加载";
        panLoader.IsVisible = false;
        panContent.IsVisible = false;
        if (panAlways is not null)
            panAlways.IsVisible = false;

        if (autoRun)
            PageLoaderRestart();
    }

    public void PageLoaderRestart(object? input = null, bool isForceRestart = true)
    {
        if (!_pageLoaderAutoRun || _pageLoader is null)
            return;

        _pageLoaderCancellation?.Cancel();
        _pageLoaderCancellation?.Dispose();
        _pageLoaderCancellation = new CancellationTokenSource();

        _pageLoaderState = LoaderRunState.Loading;
        HandleLoaderStarted();
        _ = RunPageLoaderAsync(_pageLoaderCancellation);
    }

    private async Task RunPageLoaderAsync(CancellationTokenSource cancellation)
    {
        CancellationToken cancellationToken = cancellation.Token;
        try
        {
            await _pageLoader!(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                _pageLoaderFinished?.Invoke();
                _pageLoaderState = LoaderRunState.Finished;
                HandleLoaderFinished();
            });
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ReferenceEquals(_pageLoaderCancellation, cancellation))
                    _pageLoaderState = LoaderRunState.Waiting;
            });
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                _pageLoaderState = LoaderRunState.Failed;
                HandleLoaderFailed();
            });
        }
    }

    public void PageOnEnter()
    {
        PageEnter?.Invoke();
        switch (PageState)
        {
            case PageStates.Empty:
                EnterFromEmpty(includeAlways: true);
                break;
            case PageStates.ContentExit:
                EnterFromEmpty(includeAlways: false);
                break;
            case PageStates.PageExit:
            case PageStates.LoaderExit:
                // Interrupted exit: cancel exit tween and enter fresh (do not leave opacity 0).
                ModAnimation.AniStop("PageRight PageChange " + PageUuid, finish: false);
                EnsureContentPresentationVisible();
                EnterFromEmpty(includeAlways: true);
                break;
            case PageStates.ContentStay:
            case PageStates.ContentEnter:
                // Cached / already-visible page: never re-zero opacities (empty flash + lag).
                ModAnimation.AniStop("PageRight PageChange " + PageUuid, finish: false);
                EnsureContentPresentationVisible();
                PageState = PageStates.ContentStay;
                break;
            case PageStates.LoaderWait:
            case PageStates.LoaderEnter:
            case PageStates.LoaderStay:
            case PageStates.LoaderStayForce:
                break;
        }
    }

    /// <summary>
    /// Force content to a settled, visible presentation (used after interrupt / re-show).
    /// </summary>
    public void EnsureContentPresentationVisible()
    {
        if (_pageAlwaysPanel is not null)
        {
            _pageAlwaysPanel.IsVisible = true;
            _pageAlwaysPanel.Opacity = 1d;
        }

        Control? content = GetContentTarget();
        if (content is not null)
        {
            content.IsVisible = true;
            content.Opacity = 1d;
            foreach (Control control in GetAllAnimControls(content, ignoreInvisibility: true))
            {
                control.IsVisible = true;
                control.Opacity = control is TextBlock ? Math.Max(control.Opacity, 0.55d) : 1d;
                control.IsHitTestVisible = true;
                if (control.RenderTransform is TranslateTransform t)
                {
                    t.X = 0d;
                    t.Y = 0d;
                }
            }
        }

        if (_pageLoaderPanel is not null && PageState is PageStates.LoaderEnter or PageStates.LoaderStay or PageStates.LoaderStayForce)
        {
            _pageLoaderPanel.IsVisible = true;
            _pageLoaderPanel.Opacity = 1d;
        }
    }

    public void PageOnExit()
    {
        PageExit?.Invoke();
        switch (PageState)
        {
            case PageStates.ContentEnter:
            case PageStates.ContentStay:
                PageState = PageStates.PageExit;
                TriggerExitAnimation(_pageAlwaysPanel, GetContentTarget());
                break;
            case PageStates.LoaderEnter:
            case PageStates.LoaderStayForce:
            case PageStates.LoaderStay:
                PageState = PageStates.PageExit;
                TriggerExitAnimation(_pageAlwaysPanel, _pageLoaderPanel);
                break;
            case PageStates.LoaderWait:
                PageState = PageStates.PageExit;
                TriggerExitAnimation(_pageAlwaysPanel);
                break;
            case PageStates.LoaderExit:
            case PageStates.ContentExit:
                PageState = PageStates.PageExit;
                if (_pageAlwaysPanel is not null)
                    TriggerExitAnimation(_pageAlwaysPanel, GetContentTarget());
                break;
        }
    }

    public void PageOnForceExit()
    {
        _pageLoaderCancellation?.Cancel();
        PageState = PageStates.Empty;
        // Drop mid-flight enter/exit without running finish callbacks (force-hide follows).
        ModAnimation.AniStop("PageRight PageChange " + PageUuid, finish: false);
        if (_pageContentPanel is not null)
            _pageContentPanel.IsVisible = false;
        if (_pageLoaderPanel is not null)
            _pageLoaderPanel.IsVisible = false;
        if (_pageAlwaysPanel is not null)
            _pageAlwaysPanel.IsVisible = false;
        if (_pageContentPanel is null && Content is Control content)
            content.IsVisible = false;
    }

    public void PageOnContentExit()
    {
        switch (PageState)
        {
            case PageStates.ContentEnter:
            case PageStates.ContentStay:
                PageState = PageStates.ContentExit;
                TriggerExitAnimation(GetContentTarget());
                break;
            case PageStates.LoaderExit:
                PageState = PageStates.ContentExit;
                break;
            case PageStates.LoaderEnter:
            case PageStates.LoaderStayForce:
            case PageStates.LoaderStay:
                PageState = PageStates.ContentExit;
                TriggerExitAnimation(_pageLoaderPanel);
                break;
            case PageStates.LoaderWait:
            case PageStates.Empty:
                PageOnEnter();
                break;
        }
    }

    public virtual void Dispose()
    {
        _pageLoaderCancellation?.Cancel();
        _pageLoaderCancellation?.Dispose();
        _pageLoaderCancellation = null;
        GC.SuppressFinalize(this);
    }

    public void TriggerEnterAnimation(params Control?[] elements)
    {
        Control[] realElements = elements.OfType<Control>().ToArray();
        foreach (Control element in realElements)
        {
            element.IsVisible = true;
            foreach (Control control in GetAllAnimControls(element, ignoreInvisibility: true))
            {
                control.IsHitTestVisible = true;
                if (control.RenderTransform is TranslateTransform)
                    control.RenderTransform = null;
            }
        }

        List<ModAnimation.AniData> animations = [];
        int delay = 0;
        int animatedCount = 0;
        foreach (Control element in realElements)
        {
            foreach (Control control in GetAllAnimControls(element))
            {
                if (DisabledPageAnimControls.Contains(control))
                    continue;
                if (control is MyExtraTextButton extraTextButton)
                {
                    extraTextButton.Show = true;
                    continue;
                }

                // Apple-style materialize: fade + rise. Cap stagger so pages stay snappy;
                // excess children appear settled (still "have motion" via the host fade).
                if (ControlVisualHelpers.ReduceMotionPreferred() ||
                    animatedCount >= MotionTokens.PageEnterMaxChildren)
                {
                    control.Opacity = 1d;
                    if (control.RenderTransform is TranslateTransform settle)
                    {
                        settle.X = 0d;
                        settle.Y = 0d;
                    }
                    continue;
                }

                control.Opacity = 0d;
                control.RenderTransform = new TranslateTransform(0d, MotionTokens.PageEnterOffsetY);
                animations.Add(ModAnimation.AaOpacity(
                    control,
                    1d,
                    MotionTokens.PageEnterOpacityMs,
                    delay,
                    new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak)));
                animations.Add(ModAnimation.AaTranslateY(
                    control,
                    -MotionTokens.PageEnterOffsetY,
                    MotionTokens.PageEnterSlideMs,
                    delay,
                    new ModAnimation.AniEaseOutFluent()));
                delay += MotionTokens.PageStaggerMs;
                animatedCount++;
            }
        }

        Control? scrollBar = GetFirstScrollBar(realElements);
        if (scrollBar is not null && !ControlVisualHelpers.ReduceMotionPreferred())
        {
            if (scrollBar.RenderTransform is not TranslateTransform)
                scrollBar.RenderTransform = new TranslateTransform(10d, 0d);
            animations.Add(ModAnimation.AaTranslateX(
                scrollBar,
                -((TranslateTransform)scrollBar.RenderTransform).X,
                MotionTokens.PageEnterSlideMs,
                0,
                new ModAnimation.AniEaseOutFluent()));
        }

        // Soft settle on next after-tick (engine defers after-chain one frame).
        animations.Add(ModAnimation.AaCode(() =>
        {
            foreach (Control element in realElements)
            {
                foreach (Control control in GetAllAnimControls(element, ignoreInvisibility: true))
                {
                    if (control.Opacity < 0.999d)
                        control.Opacity = 1d;
                    if (control.RenderTransform is TranslateTransform t &&
                        (Math.Abs(t.X) > 0.01d || Math.Abs(t.Y) > 0.01d))
                    {
                        t.X = 0d;
                        t.Y = 0d;
                    }
                }
            }

            PageOnEnterAnimationFinished();
        }, after: true));
        if (animations.Count <= 1)
        {
            // Nothing to tween (reduced motion or only finish callback).
            EnsureContentPresentationVisible();
            PageOnEnterAnimationFinished();
            return;
        }

        // finishPrevious:false — rapid page re-entry must not drain a long after-chain.
        ModAnimation.AniStart(animations, "PageRight PageChange " + PageUuid, refreshTime: true, finishPrevious: false);
    }

    public void TriggerExitAnimation(params Control?[] elements)
    {
        Control[] realElements = elements.OfType<Control>().ToArray();
        List<ModAnimation.AniData> animations = [];
        int delay = 0;
        foreach (Control element in realElements)
        {
            foreach (Control control in GetAllAnimControls(element))
            {
                if (DisabledPageAnimControls.Contains(control))
                    continue;
                if (control is MyExtraTextButton extraTextButton)
                {
                    extraTextButton.Show = false;
                    continue;
                }

                control.IsHitTestVisible = false;
                // Exit mirrors enter path (down + fade) for spatial consistency.
                animations.Add(ModAnimation.AaOpacity(
                    control,
                    -control.Opacity,
                    MotionTokens.NavCrossfadeOutMs,
                    delay,
                    new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak)));
                animations.Add(ModAnimation.AaTranslateY(
                    control,
                    MotionTokens.PageEnterOffsetY * 0.75d,
                    MotionTokens.NavCrossfadeOutMs,
                    delay,
                    new ModAnimation.AniEaseInFluent(ModAnimation.AniEasePower.Weak)));
                delay += 12;
            }
        }

        Control? scrollBar = GetFirstScrollBar(realElements);
        if (scrollBar is not null)
        {
            if (scrollBar.RenderTransform is not TranslateTransform)
                scrollBar.RenderTransform = new TranslateTransform();
            animations.Add(ModAnimation.AaTranslateX(
                scrollBar,
                10d - ((TranslateTransform)scrollBar.RenderTransform).X,
                90,
                0,
                new ModAnimation.AniEaseInFluent()));
        }

        animations.Add(ModAnimation.AaCode(() =>
        {
            foreach (Control element in realElements)
                element.IsVisible = false;
            PageOnExitAnimationFinished();
        }, after: true));
        ModAnimation.AniStart(animations, "PageRight PageChange " + PageUuid);
    }

    private void PageOnEnterAnimationFinished()
    {
        PageState = PageState switch
        {
            PageStates.ContentEnter => PageStates.ContentStay,
            PageStates.LoaderEnter => PageStates.LoaderStayForce,
            _ => PageState
        };
        if (PageState == PageStates.LoaderStayForce)
            ModAnimation.AniStart(ModAnimation.AaCode(PageOnLoaderStayFinished, 400), "PageRight PageChange " + PageUuid);
    }

    private void PageOnExitAnimationFinished()
    {
        switch (PageState)
        {
            case PageStates.PageExit:
                PageState = PageStates.Empty;
                break;
            case PageStates.ContentExit:
                PageOnEnter();
                break;
            case PageStates.LoaderExit:
                PageState = PageStates.ContentEnter;
                TriggerEnterAnimation(GetContentTarget());
                break;
        }
    }

    private void PageOnLoaderWaitFinished()
    {
        if (PageState != PageStates.LoaderWait)
            return;

        switch (_pageLoaderState)
        {
            case LoaderRunState.Loading:
            case LoaderRunState.Failed:
                PageState = PageStates.LoaderEnter;
                TriggerEnterAnimation(GetHiddenAlwaysPanel(), _pageLoaderPanel);
                break;
            case LoaderRunState.Finished:
            case LoaderRunState.Waiting:
                PageState = PageStates.ContentEnter;
                TriggerEnterAnimation(GetHiddenAlwaysPanel(), GetContentTarget());
                break;
        }
    }

    private void PageOnLoaderStayFinished()
    {
        if (PageState != PageStates.LoaderStayForce)
            return;

        if (_pageLoaderState == LoaderRunState.Finished)
        {
            PageState = PageStates.LoaderExit;
            TriggerExitAnimation(_pageLoaderPanel);
        }
        else
        {
            PageState = PageStates.LoaderStay;
        }
    }

    private void EnterFromEmpty(bool includeAlways)
    {
        switch (_pageLoaderState)
        {
            case LoaderRunState.Loading:
                PageState = PageStates.LoaderWait;
                ModAnimation.AniStart(ModAnimation.AaCode(PageOnLoaderWaitFinished, 400), "PageRight PageChange " + PageUuid);
                break;
            case LoaderRunState.Failed:
                PageState = PageStates.LoaderEnter;
                TriggerEnterAnimation(includeAlways ? _pageAlwaysPanel : null, _pageLoaderPanel);
                break;
            case LoaderRunState.Finished:
            case LoaderRunState.Waiting:
                PageState = PageStates.ContentEnter;
                TriggerEnterAnimation(includeAlways ? _pageAlwaysPanel : null, GetContentTarget());
                break;
        }
    }

    private void HandleLoaderStarted()
    {
        switch (PageState)
        {
            case PageStates.ContentEnter:
            case PageStates.ContentStay:
                PageState = PageStates.ContentExit;
                TriggerExitAnimation(GetContentTarget());
                break;
            case PageStates.LoaderExit:
                PageState = PageStates.ContentExit;
                break;
        }
    }

    private void HandleLoaderFinished()
    {
        switch (PageState)
        {
            case PageStates.LoaderWait:
                PageState = PageStates.ContentEnter;
                TriggerEnterAnimation(GetHiddenAlwaysPanel(), GetContentTarget());
                break;
            case PageStates.LoaderStay:
                PageState = PageStates.LoaderExit;
                TriggerExitAnimation(_pageLoaderPanel);
                break;
        }
    }

    private void HandleLoaderFailed()
    {
        if (PageState == PageStates.LoaderWait)
        {
            PageState = PageStates.LoaderEnter;
            TriggerEnterAnimation(GetHiddenAlwaysPanel(), _pageLoaderPanel);
        }
    }

    private Control? GetContentTarget() => _pageContentPanel ?? Content as Control;

    private Control? GetHiddenAlwaysPanel() => IsControlVisible(_pageAlwaysPanel) ? null : _pageAlwaysPanel;

    private MyScrollViewer? ResolveCopiedPageScrollViewer()
    {
        // Some pages reuse the legacy name "PanBack" for a non-scroll root (e.g. experimental
        // Launch homepage uses a Grid). Avalonia's FindControl<T> throws InvalidOperationException
        // when a control with that name exists but is not T — treat that as "no scroll".
        if (this.FindControl<Control>("PanBack") is MyScrollViewer namedScroll)
            return namedScroll;

        if (GetContentTarget() is { } content)
            return FindScrollViewer(content, preferPanBack: true) ?? FindScrollViewer(content, preferPanBack: false);

        return null;
    }

    private static MyScrollViewer? FindScrollViewer(Control control, bool preferPanBack)
    {
        if (control is MyScrollViewer viewer && (!preferPanBack || viewer.Name == "PanBack"))
            return viewer;

        foreach (MyScrollViewer visualViewer in control.GetVisualDescendants().OfType<MyScrollViewer>())
        {
            if (!preferPanBack || visualViewer.Name == "PanBack")
                return visualViewer;
        }

        if (control is ContentControl { Content: Control content })
            return FindScrollViewer(content, preferPanBack);

        if (control is Panel panel)
        {
            foreach (Control child in panel.Children)
            {
                MyScrollViewer? nested = FindScrollViewer(child, preferPanBack);
                if (nested is not null)
                    return nested;
            }
        }

        return null;
    }

    private static bool IsControlVisible(Control? control) => control?.IsVisible == true;

    internal static IEnumerable<Control> GetAllAnimControls(Control element, bool ignoreInvisibility = false)
    {
        if (!ignoreInvisibility && !element.IsVisible)
            yield break;

        // Leaf interactive / card units that should materialize on page enter.
        if (element is MyCard or MyHint or MyExtraTextButton or TextBlock or MyTextButton
            or MyListItem or MyButton or MyIconTextButton or MyRadioButton or MyCheckBox
            or MyRadioBox or MyExtraButton or MyLoading or MySlider or MyComboBox)
        {
            yield return element;
            yield break;
        }

        if (element is ContentControl { Content: Control content })
        {
            bool any = false;
            foreach (Control child in GetAllAnimControls(content, ignoreInvisibility))
            {
                any = true;
                yield return child;
            }

            if (!any)
                yield return element;
            yield break;
        }

        if (element is Panel panel)
        {
            bool any = false;
            foreach (Control child in panel.Children)
            {
                foreach (Control nested in GetAllAnimControls(child, ignoreInvisibility))
                {
                    any = true;
                    yield return nested;
                }
            }

            // Empty / custom-only panels still get a single fade so the page is never static.
            if (!any)
                yield return element;
            yield break;
        }

        yield return element;
    }

    private static Control? GetFirstScrollBar(IEnumerable<Control> elements)
    {
        foreach (Control element in elements)
        {
            if (TryGetVisibleVerticalScrollBar(element, out Control? directScrollBar))
                return directScrollBar;

            foreach (ScrollBar scrollBar in element.GetVisualDescendants().OfType<ScrollBar>())
            {
                if (TryGetVisibleVerticalScrollBar(scrollBar, out Control? nestedScrollBar))
                    return nestedScrollBar;
            }
        }

        return null;
    }

    private static bool TryGetVisibleVerticalScrollBar(Control control, out Control? scrollBar)
    {
        scrollBar = null;
        if (control is not ScrollBar { Orientation: Orientation.Vertical } bar || !bar.IsVisible)
            return false;

        scrollBar = bar;
        return true;
    }
}

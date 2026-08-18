// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Hosting;
using PCL.UI.Abstractions.Navigation;

namespace PCL.Desktop.Features.Community;

internal sealed class CommunityFeatureModule : IDesktopFeatureModule
{
    public string Id => "community";

    public IReadOnlyList<NavigationRouteId> Routes { get; } =
    [
        DesktopNavigationRegistry.CommunityRoute
    ];

    public void Register(IServiceCollection services)
    {
        services.AddSingleton<CommunityFavoritesStore>();
        services.AddSingleton<CommunityFeatureSurface>();
    }

    public DesktopMainPage CreateMainPage(IServiceProvider services) =>
        throw new NotSupportedException(
            "Community main page requires host bindings; use CommunityFeatureSurface via MainWindow.");

    public bool TryCreateSubPage(string subPageId, object? argument, IServiceProvider services, out Control? page)
    {
        page = null;
        return false;
    }
}

/// <summary>
/// Owns community left rail, list, detail, and favorites pages (host-scoped cache).
/// Download/open/message actions stay in MainWindow via <see cref="CommunityFeatureBindings"/>.
/// </summary>
public sealed class CommunityFeatureSurface
{
    private object? _hostToken;
    private CommunityFeatureBindings? _bindings;
    private PageCommunityLeft? _left;
    private PageCommunityRight? _right;
    private PageCommunityDetail? _detail;
    private PageCommunityFavoritesRight? _favoritesRight;

    public PageCommunityLeft? Left => _left;

    public PageCommunityRight? Right => _right;

    public PageCommunityDetail? Detail => _detail;

    public PageCommunityFavoritesRight? FavoritesRight => _favoritesRight;

    public void WireOnce(object hostToken, CommunityFeatureBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(hostToken);
        ArgumentNullException.ThrowIfNull(bindings);
        if (!ReferenceEquals(_hostToken, hostToken))
        {
            _hostToken = hostToken;
            ClearPages();
        }

        _bindings = bindings;
    }

    public DesktopMainPage CreateMainPage()
    {
        EnsureMainPages();
        PageCommunityLeft left = _left!;
        PageCommunityRight right = _right!;
        return new DesktopMainPage(
            left,
            right,
            Activated: () =>
            {
                left.TriggerShowAnimation();
                right.PageOnEnter();
            });
    }

    public PageCommunityLeft EnsureLeft()
    {
        EnsureMainPages();
        return _left!;
    }

    public PageCommunityRight EnsureRight()
    {
        EnsureMainPages();
        return _right!;
    }

    public PageCommunityDetail EnsureDetail()
    {
        CommunityFeatureBindings b = RequireBindings();
        if (_detail is not null)
            return _detail;

        PageCommunityDetail page = new(new CompositeCommunityResourceCatalog(), ownsCatalog: true, b.Favorites);
        page.BackRequested += (_, _) => b.CloseDetail();
        page.OpenWebRequested += (_, entry) => b.OpenUrl(entry.WebsiteUrl);
        page.OpenUrlRequested += (_, url) => b.OpenUrl(url);
        page.MessageRequested += (_, message) => b.ShowMessage(message.Title, message.Message);
        page.DownloadRequested += (_, request) => _ = b.DownloadAsync(request);
        _detail = page;
        return page;
    }

    public PageCommunityFavoritesRight EnsureFavorites()
    {
        CommunityFeatureBindings b = RequireBindings();
        if (_favoritesRight is not null)
            return _favoritesRight;

        PageCommunityFavoritesRight page = new(b.Favorites);
        page.OpenProjectRequested += (_, favorite) =>
            _ = b.OpenDetailAsync(
                favorite.Entry,
                favorite.Category,
                new CommunitySearchOptions(Source: favorite.Entry.Source));
        page.DownloadRequested += (_, request) => _ = b.DownloadAsync(request);
        page.InputRequested += (_, request) => b.PromptInput(request);
        page.ConfirmationRequested += (_, request) => b.Confirm(request);
        page.MessageRequested += (_, message) => b.ShowMessage(message.Title, message.Message);
        _favoritesRight = page;
        return page;
    }

    /// <summary>Right page to restore after closing detail (favorites vs list).</summary>
    public MyPageRight ResolveListRight()
    {
        EnsureMainPages();
        if (_left!.IsFavoritesSelected)
            return EnsureFavorites();
        return _right!;
    }

    private void EnsureMainPages()
    {
        CommunityFeatureBindings b = RequireBindings();
        _right ??= CreateRight(b);
        _left ??= CreateLeft(b, _right);
    }

    private void ClearPages()
    {
        _left = null;
        _right = null;
        _detail = null;
        _favoritesRight = null;
    }

    private CommunityFeatureBindings RequireBindings() =>
        _bindings ?? throw new InvalidOperationException("CommunityFeatureSurface 尚未 WireOnce。");

    private PageCommunityLeft CreateLeft(CommunityFeatureBindings b, PageCommunityRight rightPage)
    {
        PageCommunityLeft page = new();
        page.CategoryChanged += (_, category) =>
        {
            b.CategoryChanged(category);
            b.ApplyRightPage(rightPage);
            _ = rightPage.SetCategoryAsync(category);
        };
        page.RefreshRequested += (_, category) =>
        {
            if (rightPage.Category == category)
                _ = rightPage.RefreshAsync();
            else
                _ = rightPage.SetCategoryAsync(category);
        };
        page.FavoritesRequested += (_, _) =>
        {
            PageCommunityFavoritesRight favorites = EnsureFavorites();
            favorites.Refresh();
            b.ApplyRightPage(favorites);
        };
        return page;
    }

    private static PageCommunityRight CreateRight(CommunityFeatureBindings b)
    {
        PageCommunityRight page = new(new CompositeCommunityResourceCatalog(), ownsCatalog: true, b.Favorites);
        page.OpenProjectRequested += (_, entry) =>
            _ = b.OpenDetailAsync(entry, page.Category, page.CurrentSearchOptions);
        page.DownloadRequested += (_, request) => _ = b.DownloadAsync(request);
        page.InstallModPackRequested += (_, _) => _ = b.ImportModpackAsync();
        return page;
    }
}

public sealed class CommunityFeatureBindings
{
    public required CommunityFavoritesStore Favorites { get; init; }

    public required Action<MyPageRight> ApplyRightPage { get; init; }

    public required Func<CommunityResourceEntry, CommunityResourceCategory, CommunitySearchOptions, Task> OpenDetailAsync { get; init; }

    public required Func<CommunityResourceDownloadRequest, Task> DownloadAsync { get; init; }

    public required Func<Task> ImportModpackAsync { get; init; }

    public required Action CloseDetail { get; init; }

    public required Action<string?> OpenUrl { get; init; }

    public required Action<string, string> ShowMessage { get; init; }

    public required Action<CommunityFavoriteInputRequest> PromptInput { get; init; }

    public required Action<CommunityFavoriteConfirmationRequest> Confirm { get; init; }

    public required Action<CommunityResourceCategory> CategoryChanged { get; init; }
}

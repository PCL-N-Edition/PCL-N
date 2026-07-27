// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Desktop.Features.Launching.Views;

namespace PCL.Desktop.Features.Launching;

/// <summary>
/// Host-scoped cache of launch login sub-pages (profile / MS / auth / offline).
/// Profile list remains owned by the host; surface only mounts pages onto
/// <see cref="ILaunchHomeSurface"/>.
/// </summary>
public sealed class LaunchLoginSurface
{
    private object? _hostToken;
    private LaunchLoginBindings? _bindings;
    private ILaunchHomeSurface? _wiredLaunchPage;
    private PageLoginProfile? _profilePage;
    private PageLoginProfileSkin? _profileSkinPage;
    private PageLoginMs? _msPage;
    private PageLoginLittleSkin? _littleSkinPage;
    private PageLoginNCloud? _nCloudPage;
    private PageLoginAuth? _authPage;
    private PageLoginOffline? _offlinePage;

    public PageLoginProfile? ProfilePage => _profilePage;

    public PageLoginProfileSkin? ProfileSkinPage => _profileSkinPage;

    public PageLoginMs? MsPage => _msPage;

    public PageLoginLittleSkin? LittleSkinPage => _littleSkinPage;

    public PageLoginNCloud? NCloudPage => _nCloudPage;

    public PageLoginAuth? AuthPage => _authPage;

    public PageLoginOffline? OfflinePage => _offlinePage;

    public void WireOnce(object hostToken, LaunchLoginBindings bindings)
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

    public void Apply(
        ILaunchHomeSurface launchPage,
        PageLaunchLeft.LaunchLoginPageType type,
        IList<LoginProfileInfo> profiles)
    {
        ArgumentNullException.ThrowIfNull(launchPage);
        ArgumentNullException.ThrowIfNull(profiles);
        _ = RequireBindings();

        if (!ReferenceEquals(_wiredLaunchPage, launchPage))
        {
            ClearPages();
            _wiredLaunchPage = launchPage;
        }

        switch (type)
        {
            case PageLaunchLeft.LaunchLoginPageType.ProfileSkin:
                if (profiles.Count == 0)
                {
                    launchPage.SetSelectedProfilePresent(false);
                    Apply(launchPage, PageLaunchLeft.LaunchLoginPageType.Profile, profiles);
                    return;
                }

                LoginProfileInfo selectedProfile = profiles[0];
                PageLoginProfileSkin skin = EnsureProfileSkinPage(launchPage);
                skin.UseDirectAppearanceAction = launchPage is PageLaunchHomeExperimental;
                skin.SetProfile(selectedProfile);
                launchPage.SetLoginPage(skin, animate: true, PageLaunchLeft.LaunchLoginPageType.ProfileSkin);
                break;

            case PageLaunchLeft.LaunchLoginPageType.Profile:
            {
                PageLoginProfile page = EnsureProfilePage(launchPage);
                page.SetProfiles(profiles);
                launchPage.SetLoginPage(page, animate: true, PageLaunchLeft.LaunchLoginPageType.Profile);
                break;
            }

            case PageLaunchLeft.LaunchLoginPageType.Ms:
                launchPage.SetLoginPage(
                    EnsureMicrosoftLoginPage(launchPage),
                    animate: true,
                    PageLaunchLeft.LaunchLoginPageType.Ms);
                break;

            case PageLaunchLeft.LaunchLoginPageType.LittleSkin:
                launchPage.SetLoginPage(
                    EnsureLittleSkinLoginPage(launchPage),
                    animate: true,
                    PageLaunchLeft.LaunchLoginPageType.LittleSkin);
                break;

            case PageLaunchLeft.LaunchLoginPageType.NCloud:
                launchPage.SetLoginPage(
                    EnsureNCloudLoginPage(launchPage),
                    animate: true,
                    PageLaunchLeft.LaunchLoginPageType.NCloud);
                break;

            case PageLaunchLeft.LaunchLoginPageType.Auth:
                launchPage.SetLoginPage(
                    EnsureAuthLoginPage(launchPage),
                    animate: true,
                    PageLaunchLeft.LaunchLoginPageType.Auth);
                break;

            case PageLaunchLeft.LaunchLoginPageType.Offline:
            {
                PageLoginOffline offline = EnsureOfflineLoginPage(launchPage);
                offline.SetSkinSources(profiles);
                launchPage.SetLoginPage(offline, animate: true, PageLaunchLeft.LaunchLoginPageType.Offline);
                break;
            }

            default:
            {
                PageLoginProfile page = EnsureProfilePage(launchPage);
                page.SetProfiles(profiles);
                launchPage.SetLoginPage(page, animate: true, PageLaunchLeft.LaunchLoginPageType.Profile);
                break;
            }
        }
    }

    public PageLoginAuth EnsureAuthLoginPage(ILaunchHomeSurface launchPage)
    {
        LaunchLoginBindings b = RequireBindings();
        EnsureLaunchPage(launchPage);
        if (_authPage is not null)
            return _authPage;

        PageLoginAuth page = new();
        page.BackRequested += (_, _) => launchPage.RefreshPage(anim: true);
        page.ValidationFailed += (_, message) => b.AppendLog(message);
        page.RegisterLinkRequested += (_, isRegisterMode) => b.OpenAuthAccountPage(page.CurrentServer, isRegisterMode);
        page.LoginRequested += (_, request) => _ = b.StartThirdPartyLoginAsync(page, request);
        _authPage = page;
        return page;
    }

    private void ClearPages()
    {
        _wiredLaunchPage = null;
        _profilePage = null;
        _profileSkinPage = null;
        _msPage = null;
        _littleSkinPage = null;
        _nCloudPage = null;
        _authPage = null;
        _offlinePage = null;
    }

    private void EnsureLaunchPage(ILaunchHomeSurface launchPage)
    {
        if (!ReferenceEquals(_wiredLaunchPage, launchPage))
        {
            ClearPages();
            _wiredLaunchPage = launchPage;
        }
    }

    private LaunchLoginBindings RequireBindings() =>
        _bindings ?? throw new InvalidOperationException("LaunchLoginSurface 尚未 WireOnce。");

    private PageLoginProfile EnsureProfilePage(ILaunchHomeSurface launchPage)
    {
        LaunchLoginBindings b = RequireBindings();
        EnsureLaunchPage(launchPage);
        if (_profilePage is not null)
            return _profilePage;

        PageLoginProfile page = new();
        page.ProfileSelected += (_, profile) => b.OnProfileSelected(launchPage, profile);
        page.ProfileDeleteRequested += (_, profile) => b.ConfirmDeleteProfile(page, launchPage, profile);
        page.CreateProfileRequested += (_, _) => b.ShowProfileTypeSelector(launchPage);
        page.ImportExportRequested += (_, _) => b.ShowImportExportSelector(page, launchPage);
        _profilePage = page;
        return page;
    }

    private PageLoginProfileSkin EnsureProfileSkinPage(ILaunchHomeSurface launchPage)
    {
        LaunchLoginBindings b = RequireBindings();
        EnsureLaunchPage(launchPage);
        if (_profileSkinPage is not null)
            return _profileSkinPage;

        PageLoginProfileSkin page = new();
        page.ChangeProfileRequested += (_, _) =>
        {
            launchPage.SetSelectedProfilePresent(false);
            launchPage.RefreshPage(anim: true);
        };
        page.ChangeSkinRequested += (_, _) => b.OpenAppearance(page.Profile, "更换皮肤");
        page.SaveSkinRequested += (_, _) => _ = b.SaveSkinAsync(page.Profile);
        page.RefreshSkinRequested += (_, _) => _ = b.RefreshSkinAsync(page);
        page.ChangeCapeRequested += (_, _) => b.OpenAppearance(page.Profile, "更换披风");
        page.EditPasswordRequested += (_, _) => b.OpenSecurity(page.Profile);
        page.EditNameRequested += (_, _) => b.OpenNameEditor(page.Profile);
        _profileSkinPage = page;
        return page;
    }

    private PageLoginMs EnsureMicrosoftLoginPage(ILaunchHomeSurface launchPage)
    {
        LaunchLoginBindings b = RequireBindings();
        EnsureLaunchPage(launchPage);
        if (_msPage is not null)
            return _msPage;

        PageLoginMs page = new();
        page.BackRequested += (_, _) => launchPage.RefreshPage(anim: true);
        page.PurchaseRequested += (_, _) => b.OpenUrl(
            "https://www.xbox.com/zh-cn/games/store/minecraft-java-bedrock-edition-for-pc/9nxp44l49shj");
        page.WebsiteRequested += (_, _) => b.OpenUrl("https://www.minecraft.net/zh-hans");
        page.LoginRequested += (_, _) => _ = b.StartMicrosoftLoginAsync(page, launchPage);
        _msPage = page;
        return page;
    }

    private PageLoginLittleSkin EnsureLittleSkinLoginPage(ILaunchHomeSurface launchPage)
    {
        LaunchLoginBindings b = RequireBindings();
        EnsureLaunchPage(launchPage);
        if (_littleSkinPage is not null)
            return _littleSkinPage;

        PageLoginLittleSkin page = new();
        page.BackRequested += (_, _) => launchPage.RefreshPage(anim: true);
        page.WebsiteRequested += (_, _) => b.OpenUrl("https://littleskin.cn/");
        page.DocumentationRequested += (_, _) => b.OpenUrl(
            "https://manual.littlesk.in/advanced/oauth2/authorization-code-grant");
        page.LoginRequested += (_, _) => _ = b.StartLittleSkinLoginAsync(page, launchPage);
        _littleSkinPage = page;
        return page;
    }

    private PageLoginNCloud EnsureNCloudLoginPage(ILaunchHomeSurface launchPage)
    {
        LaunchLoginBindings b = RequireBindings();
        EnsureLaunchPage(launchPage);
        if (_nCloudPage is not null)
            return _nCloudPage;

        PageLoginNCloud page = new();
        page.BackRequested += (_, _) => launchPage.RefreshPage(anim: true);
        page.LoginRequested += (_, _) => _ = b.StartNCloudLoginAsync(page, launchPage);
        _nCloudPage = page;
        return page;
    }

    private PageLoginOffline EnsureOfflineLoginPage(ILaunchHomeSurface launchPage)
    {
        LaunchLoginBindings b = RequireBindings();
        EnsureLaunchPage(launchPage);
        if (_offlinePage is not null)
            return _offlinePage;

        PageLoginOffline page = new();
        page.BackRequested += (_, _) => launchPage.RefreshPage(anim: true);
        page.ValidationFailed += (_, message) => b.AppendLog(message);
        page.ProfileCreateRequested += (_, request) => b.CreateOfflineProfile(launchPage, request);
        _offlinePage = page;
        return page;
    }
}

/// <summary>Host callbacks for launch login pages.</summary>
public sealed class LaunchLoginBindings
{
    public required Action<string> AppendLog { get; init; }

    public required Action<ILaunchHomeSurface, LoginProfileInfo> OnProfileSelected { get; init; }

    public required Action<PageLoginProfile, ILaunchHomeSurface, LoginProfileInfo> ConfirmDeleteProfile { get; init; }

    public required Action<ILaunchHomeSurface> ShowProfileTypeSelector { get; init; }

    public required Action<PageLoginProfile, ILaunchHomeSurface> ShowImportExportSelector { get; init; }

    public required Action<LoginProfileInfo?, string> OpenAppearance { get; init; }

    public required Func<LoginProfileInfo?, Task> SaveSkinAsync { get; init; }

    public required Func<PageLoginProfileSkin, Task> RefreshSkinAsync { get; init; }

    public required Action<LoginProfileInfo?> OpenSecurity { get; init; }

    public required Action<LoginProfileInfo?> OpenNameEditor { get; init; }

    public required Action<string> OpenUrl { get; init; }

    public required Func<PageLoginMs, ILaunchHomeSurface, Task> StartMicrosoftLoginAsync { get; init; }

    public required Func<PageLoginLittleSkin, ILaunchHomeSurface, Task> StartLittleSkinLoginAsync { get; init; }

    public required Func<PageLoginNCloud, ILaunchHomeSurface, Task> StartNCloudLoginAsync { get; init; }

    public required Action<string, bool> OpenAuthAccountPage { get; init; }

    public required Func<PageLoginAuth, AuthLoginRequest, Task> StartThirdPartyLoginAsync { get; init; }

    public required Action<ILaunchHomeSurface, OfflineProfileCreateRequest> CreateOfflineProfile { get; init; }
}

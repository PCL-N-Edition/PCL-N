// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PCL.Desktop.Controls.Legacy;
using PCL.Core.Logging;
using PCL.Desktop.Features.Launching.Appearance;
using PCL.Desktop.Features.Launching.Views;
using PCL.Desktop.Features.Settings.Views;

namespace PCL.Desktop.Views;

public partial class MainWindow
{
    private CancellationTokenSource? _appearanceLoadCancellation;

    private async Task OpenExperimentalAppearancePageAsync(LoginProfileInfo requestedProfile)
    {
        LoginProfileInfo profile = ResolveCurrentProfile(requestedProfile);
        _appearanceLoadCancellation?.Cancel();
        _appearanceLoadCancellation = new CancellationTokenSource();
        CancellationTokenSource pageCancellation = _appearanceLoadCancellation;
        CancellationToken cancellationToken = pageCancellation.Token;
        int cancellationReleased = 0;

        PageSkinAppearanceRight page = new();
        page.PageExit += () =>
        {
            if (Interlocked.Exchange(ref cancellationReleased, 1) != 0)
                return;
            pageCancellation.Cancel();
            pageCancellation.Dispose();
            if (ReferenceEquals(_appearanceLoadCancellation, pageCancellation))
                _appearanceLoadCancellation = null;
        };
        page.LocalSkinRequested += (_, _) => _ = PickExperimentalLocalSkinAsync(profile);
        page.SkinLibraryRequested += (_, _) => OpenSkinLibraryPage(profile);
        page.SkinSelected += (_, card) => _ = ApplyAppearanceSkinAsync(profile, card);
        page.SetModel(CreateFallbackAppearanceModel(profile));
        ApplyExperimentalAppearancePage(
            page,
            GetResourceText("Appearance.Page.Title", "外观"),
            () => SelectNavRoute(LaunchRoute, animate: true));

        try
        {
            SkinAppearancePageModel model = await BuildAppearanceModelAsync(
                    profile,
                    cancellationToken)
                .ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested ||
                    this.FindControl<Border>("PanMainRight")?.Child != page)
                {
                    return;
                }

                page.SetModel(model);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            PortableLog.Warn(
                exception,
                "Appearance",
                "加载皮肤与披风历史失败，保留即时外观预览。");
        }
    }

    private void OpenSkinLibraryPage(LoginProfileInfo requestedProfile)
    {
        LoginProfileInfo profile = ResolveCurrentProfile(requestedProfile);
        _appearanceLoadCancellation?.Cancel();
        PageSkinLibraryRight page = new();
        page.SkinSelected += (_, item) => _ = ApplySkinSiteItemAsync(profile, item);
        page.OpenUrlRequested += (_, uri) => OpenExternalUrl(uri.AbsoluteUri);
        ApplyExperimentalAppearancePage(
            page,
            GetResourceText("Appearance.Library.Title", "皮肤库"),
            () => _ = OpenExperimentalAppearancePageAsync(ResolveCurrentProfile(profile)));
        page.SetCatalogs([new LittleSkinCatalog()], "littleskin");
    }

    private void ApplyExperimentalAppearancePage(
        MyPageRight page,
        string title,
        Action backAction)
    {
        if (this.FindControl<Border>("PanMainLeft") is not { } leftHost ||
            this.FindControl<Border>("PanMainRight") is not { } rightHost)
        {
            return;
        }

        ApplyExperimentalChrome(true);
        if (leftHost.Child is MyPageLeft oldLeft)
            oldLeft.TriggerHideAnimation();
        leftHost.Child = null;

        if (!ReferenceEquals(rightHost.Child, page))
        {
            if (rightHost.Child is MyPageRight oldRight)
                oldRight.PageOnExit();
            rightHost.Child = page;
        }

        rightHost.Opacity = 1d;
        _titleInnerBackAction = backAction;
        EnterTitleSubPage(title);
        RefreshBackToTopBinding();
        page.PageOnEnter();
    }

    private SkinAppearancePageModel CreateFallbackAppearanceModel(LoginProfileInfo profile)
    {
        bool slim = profile.Kind == LaunchLoginProfileKind.Offline &&
                    string.Equals(
                        LoginProfileInfo.ResolveOfflineDefaultModel(profile.Uuid),
                        "Alex",
                        StringComparison.OrdinalIgnoreCase);
        SkinAppearanceCard current = new(
            profile.Username,
            profile.DisplayInfo,
            profile.DisplaySkinAddress,
            null,
            slim,
            CanApply: false);
        SkinAppearanceCard[] otherProfiles = _loginProfiles
            .Where(candidate => !IsSameProfile(candidate, profile))
            .Where(static candidate => candidate.HasSkin)
            .Select(candidate => new SkinAppearanceCard(
                candidate.Username,
                GetResourceText("Appearance.Source.OtherProfile", "其他档案"),
                candidate.DisplaySkinAddress,
                null,
                candidate.Kind == LaunchLoginProfileKind.Offline &&
                string.Equals(
                    LoginProfileInfo.ResolveOfflineDefaultModel(candidate.Uuid),
                    "Alex",
                    StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        return new SkinAppearancePageModel(profile, current, otherProfiles, []);
    }

    private async Task<SkinAppearancePageModel> BuildAppearanceModelAsync(
        LoginProfileInfo requestedProfile,
        CancellationToken cancellationToken)
    {
        LoginProfileInfo[] profiles = _loginProfiles.ToArray();
        LoginProfileInfo profile = profiles.FirstOrDefault(candidate =>
                                       IsSameProfile(candidate, requestedProfile))
                                   ?? requestedProfile;
        if (!profiles.Any(candidate => IsSameProfile(candidate, profile)))
            profiles = [profile, .. profiles];

        Task<MinecraftProfileTextures>[] textureTasks = profiles
            .Select(candidate =>
                MinecraftProfileTextureResolver.ResolveAsync(candidate, cancellationToken))
            .ToArray();
        MinecraftProfileTextures[] textures = await Task.WhenAll(textureTasks).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        SkinAppearanceHistoryStore store = CreateAppearanceHistoryStore();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<SkinAppearanceHistoryEntry> snapshots = [];
        for (int index = 0; index < profiles.Length; index++)
        {
            string profileKey = CreateAppearanceProfileKey(profiles[index]);
            MinecraftProfileTextures texture = textures[index];
            if (!string.IsNullOrWhiteSpace(texture.SkinAddress))
            {
                snapshots.Add(new SkinAppearanceHistoryEntry(
                    profileKey,
                    profiles[index].Username,
                    AppearanceTextureKind.Skin,
                    texture.SkinAddress,
                    texture.IsSlim,
                    now));
            }

            if (!string.IsNullOrWhiteSpace(texture.CapeAddress))
            {
                snapshots.Add(new SkinAppearanceHistoryEntry(
                    profileKey,
                    profiles[index].Username,
                    AppearanceTextureKind.Cape,
                    texture.CapeAddress!,
                    texture.IsSlim,
                    now));
            }
        }

        await store.RecordAsync(snapshots, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<SkinAppearanceHistoryEntry> history = await store
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);

        int currentIndex = Array.FindIndex(
            profiles,
            candidate => IsSameProfile(candidate, profile));
        MinecraftProfileTextures currentTextures = currentIndex >= 0
            ? textures[currentIndex]
            : await MinecraftProfileTextureResolver.ResolveAsync(profile, cancellationToken)
                .ConfigureAwait(false);
        string currentKey = CreateAppearanceProfileKey(profile);

        SkinAppearanceCard current = new(
            profile.Username,
            profile.DisplayInfo,
            currentTextures.SkinAddress,
            currentTextures.CapeAddress,
            currentTextures.IsSlim,
            CanApply: false);
        SkinAppearanceCard[] skins = history
            .Where(static entry => entry.Kind == AppearanceTextureKind.Skin)
            .Where(entry =>
                !string.Equals(
                    entry.Address,
                    currentTextures.SkinAddress,
                    StringComparison.OrdinalIgnoreCase))
            .Select(entry => new SkinAppearanceCard(
                entry.DisplayName,
                string.Equals(entry.ProfileKey, currentKey, StringComparison.OrdinalIgnoreCase)
                    ? GetResourceText("Appearance.Source.Previous", "此前使用")
                    : GetResourceText("Appearance.Source.OtherProfile", "其他档案"),
                entry.Address,
                null,
                entry.IsSlim))
            .ToArray();
        SkinAppearanceCard[] capes = history
            .Where(static entry => entry.Kind == AppearanceTextureKind.Cape)
            .Select(entry => new SkinAppearanceCard(
                entry.DisplayName,
                string.Equals(entry.ProfileKey, currentKey, StringComparison.OrdinalIgnoreCase)
                    ? GetResourceText("Appearance.Source.CurrentOrPrevious", "当前或此前使用")
                    : GetResourceText("Appearance.Source.OtherProfile", "其他档案"),
                currentTextures.SkinAddress,
                entry.Address,
                currentTextures.IsSlim,
                CanApply: false))
            .ToArray();
        return new SkinAppearancePageModel(profile, current, skins, capes);
    }

    private async Task PickExperimentalLocalSkinAsync(LoginProfileInfo requestedProfile)
    {
        LoginProfileInfo profile = ResolveCurrentProfile(requestedProfile);
        if (profile.Kind == LaunchLoginProfileKind.ThirdParty)
        {
            OpenLegacyProfileAppearanceAction(profile, "更换皮肤");
            return;
        }

        string? path = await PickOpenFilePathAsync(
                "选择本地皮肤",
                new FilePickerFileType("皮肤 PNG") { Patterns = ["*.png"] })
            .ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        if (profile.Kind == LaunchLoginProfileKind.Microsoft)
        {
            byte[] bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(true);
            LoginProfileInfo? updated = await UploadMicrosoftSkinAsync(
                    profile,
                    bytes,
                    Path.GetFileName(path),
                    isSlim: false,
                    fallbackAddress: path,
                    "更换皮肤")
                .ConfigureAwait(true);
            if (updated is not null)
                await OpenExperimentalAppearancePageAsync(updated).ConfigureAwait(true);
            return;
        }

        await RecordProfileTextureSnapshotAsync(profile).ConfigureAwait(true);
        LoginProfileInfo offlineUpdated = profile with { SkinAddress = path };
        ApplyUpdatedAppearanceProfile(profile, offlineUpdated, "更新离线皮肤");
        await RecordProfileTextureSnapshotAsync(offlineUpdated).ConfigureAwait(true);
        await OpenExperimentalAppearancePageAsync(offlineUpdated).ConfigureAwait(true);
    }

    private async Task ApplyAppearanceSkinAsync(
        LoginProfileInfo requestedProfile,
        SkinAppearanceCard card)
    {
        await ApplySkinAddressAsync(
                requestedProfile,
                card.SkinAddress,
                card.IsSlim,
                "历史皮肤",
                detailsUri: null)
            .ConfigureAwait(true);
    }

    private async Task ApplySkinSiteItemAsync(
        LoginProfileInfo requestedProfile,
        SkinSiteItem item)
    {
        await ApplySkinAddressAsync(
                requestedProfile,
                item.SkinAddress,
                string.Equals(item.Model, "alex", StringComparison.OrdinalIgnoreCase),
                item.Name,
                item.DetailsUri)
            .ConfigureAwait(true);
    }

    private async Task ApplySkinAddressAsync(
        LoginProfileInfo requestedProfile,
        string address,
        bool isSlim,
        string displayName,
        Uri? detailsUri)
    {
        LoginProfileInfo profile = ResolveCurrentProfile(requestedProfile);
        if (profile.Kind == LaunchLoginProfileKind.ThirdParty)
        {
            if (detailsUri is not null)
                OpenExternalUrl(detailsUri.AbsoluteUri);
            ShowTextDialog(
                GetResourceText("Appearance.ThirdPartyAuth.Title", "需要皮肤站授权"),
                GetResourceText(
                    "Appearance.ThirdPartyAuth.Message",
                    "第三方皮肤站的角色材质接口需要独立 OAuth 授权，不能复用游戏登录令牌。" +
                    "\n\n已打开材质详情页，请在皮肤站中将它加入衣柜并应用到角色。"),
                "知道了");
            return;
        }

        if (profile.Kind == LaunchLoginProfileKind.Microsoft)
        {
            byte[]? bytes = await MySkin.LoadSkinBytesAsync(address).ConfigureAwait(true);
            if (bytes is null)
            {
                ShowTextDialog("更换皮肤", "未能下载所选皮肤材质。", "知道了");
                return;
            }

            LoginProfileInfo? updated = await UploadMicrosoftSkinAsync(
                    profile,
                    bytes,
                    "pcln-skin.png",
                    isSlim,
                    address,
                    "使用 " + displayName)
                .ConfigureAwait(true);
            if (updated is not null)
                await OpenExperimentalAppearancePageAsync(updated).ConfigureAwait(true);
            return;
        }

        await RecordProfileTextureSnapshotAsync(profile).ConfigureAwait(true);
        LoginProfileInfo offlineUpdated = profile with { SkinAddress = address };
        ApplyUpdatedAppearanceProfile(profile, offlineUpdated, "应用皮肤");
        await RecordProfileTextureSnapshotAsync(offlineUpdated).ConfigureAwait(true);
        await OpenExperimentalAppearancePageAsync(offlineUpdated).ConfigureAwait(true);
    }

    private void ApplyUpdatedAppearanceProfile(
        LoginProfileInfo original,
        LoginProfileInfo updated,
        string saveAction)
    {
        ReplaceLoginProfile(original, updated);
        _launchLoginSurface.ProfilePage?.SetProfiles(_loginProfiles, updated);
        _launchLoginSurface.ProfileSkinPage?.SetProfile(updated);
        SaveProfilesInBackground(saveAction);
        HandleStatusMessage($"已为 {updated.Username} 应用皮肤。");
    }

    private static async Task RecordProfileTextureSnapshotAsync(LoginProfileInfo profile)
    {
        try
        {
            MinecraftProfileTextures textures = await MinecraftProfileTextureResolver
                .ResolveAsync(profile)
                .ConfigureAwait(false);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string key = CreateAppearanceProfileKey(profile);
            List<SkinAppearanceHistoryEntry> entries =
            [
                new(
                    key,
                    profile.Username,
                    AppearanceTextureKind.Skin,
                    textures.SkinAddress,
                    textures.IsSlim,
                    now)
            ];
            if (!string.IsNullOrWhiteSpace(textures.CapeAddress))
            {
                entries.Add(new SkinAppearanceHistoryEntry(
                    key,
                    profile.Username,
                    AppearanceTextureKind.Cape,
                    textures.CapeAddress!,
                    textures.IsSlim,
                    now));
            }

            await CreateAppearanceHistoryStore().RecordAsync(entries).ConfigureAwait(false);
        }
        catch
        {
            // Appearance history is an optional convenience and must never block a skin change.
        }
    }

    private LoginProfileInfo ResolveCurrentProfile(LoginProfileInfo profile) =>
        _loginProfiles.FirstOrDefault(candidate => IsSameProfile(candidate, profile)) ?? profile;

    private static string CreateAppearanceProfileKey(LoginProfileInfo profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.Uuid))
            return profile.Kind + ":" + profile.Uuid.Replace("-", string.Empty);
        return profile.Kind + ":" + profile.AuthServer + ":" + profile.Username;
    }

    private static SkinAppearanceHistoryStore CreateAppearanceHistoryStore() =>
        new(Path.Combine(
            LauncherSettingsPageBinder.CreateDataDirectory(),
            "Appearance",
            "history.json"));
}

// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Text.Json;
using PCL.Application.Accounts;
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
        page.CapeSelected += (_, card) => _ = ApplyAppearanceCapeAsync(profile, card);
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

                if (profile.Kind == LaunchLoginProfileKind.LittleSkin &&
                    (!string.Equals(
                         profile.ProviderAccessToken,
                         model.Profile.ProviderAccessToken,
                         StringComparison.Ordinal) ||
                     !string.Equals(
                         profile.RefreshToken,
                         model.Profile.RefreshToken,
                         StringComparison.Ordinal)))
                {
                    AddOrUpdateLoginProfile(model.Profile);
                    _launchLoginSurface.ProfilePage?.SetProfiles(_loginProfiles, model.Profile);
                    _launchLoginSurface.ProfileSkinPage?.SetProfile(model.Profile);
                    SaveProfilesInBackground("刷新 LittleSkin 外观授权");
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

        IReadOnlyList<LittleSkinClosetItem> closetSkins = [];
        IReadOnlyList<LittleSkinClosetItem> closetCapes = [];
        if (profile.Kind == LaunchLoginProfileKind.LittleSkin)
        {
            try
            {
                (LoginProfileInfo refreshed, (
                    IReadOnlyList<LittleSkinClosetItem> Skins,
                    IReadOnlyList<LittleSkinClosetItem> Capes) closet) =
                    await InvokeLittleSkinOAuthAsync(
                            profile,
                            async (accessToken, token) =>
                            {
                                Task<IReadOnlyList<LittleSkinClosetItem>> skinsTask =
                                    _littleSkinOAuthService.GetClosetItemsAsync(
                                        accessToken,
                                        LittleSkinTextureKind.Skin,
                                        token);
                                Task<IReadOnlyList<LittleSkinClosetItem>> capesTask =
                                    _littleSkinOAuthService.GetClosetItemsAsync(
                                        accessToken,
                                        LittleSkinTextureKind.Cape,
                                        token);
                                await Task.WhenAll(skinsTask, capesTask).ConfigureAwait(false);
                                return (
                                    await skinsTask.ConfigureAwait(false),
                                    await capesTask.ConfigureAwait(false));
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                profile = refreshed;
                closetSkins = closet.Skins;
                closetCapes = closet.Capes;
            }
            catch (Exception exception) when (
                exception is HttpRequestException or
                    InvalidOperationException or
                    InvalidDataException or
                    JsonException)
            {
                PortableLog.Warn(
                    exception,
                    "LittleSkinAppearance",
                    "读取 LittleSkin 皮肤与披风衣柜失败，将继续显示本地历史。");
            }
        }

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
        IEnumerable<SkinAppearanceCard> historySkins = history
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
        SkinAppearanceCard[] skins = closetSkins
            .Select(item => new SkinAppearanceCard(
                item.Name,
                GetResourceText("Appearance.Source.LittleSkinCloset", "LittleSkin 衣柜"),
                item.TextureAddress,
                null,
                string.Equals(item.Model, "alex", StringComparison.OrdinalIgnoreCase),
                CanApply: true,
                TextureId: item.TextureId))
            .Concat(historySkins)
            .DistinctBy(static card => card.SkinAddress, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        IEnumerable<SkinAppearanceCard> historyCapes = history
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
        SkinAppearanceCard[] capes = closetCapes
            .Select(item => new SkinAppearanceCard(
                item.Name,
                GetResourceText("Appearance.Source.LittleSkinCloset", "LittleSkin 衣柜"),
                currentTextures.SkinAddress,
                item.TextureAddress,
                currentTextures.IsSlim,
                CanApply: true,
                TextureId: item.TextureId))
            .Concat(historyCapes)
            .DistinctBy(
                static card => card.CapeAddress ?? string.Empty,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new SkinAppearancePageModel(profile, current, skins, capes);
    }

    private async Task PickExperimentalLocalSkinAsync(LoginProfileInfo requestedProfile)
    {
        LoginProfileInfo profile = ResolveCurrentProfile(requestedProfile);
        if (profile.Kind == LaunchLoginProfileKind.LittleSkin)
        {
            ShowTextDialog(
                "上传 LittleSkin 皮肤",
                "本地材质上传仍由 LittleSkin 网站完成。上传并加入衣柜后，返回此页面即可直接应用。",
                "打开 LittleSkin");
            OpenExternalUrl("https://littleskin.cn/user/closet");
            return;
        }

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
                detailsUri: null,
                card.TextureId)
            .ConfigureAwait(true);
    }

    private async Task ApplyAppearanceCapeAsync(
        LoginProfileInfo requestedProfile,
        SkinAppearanceCard card)
    {
        LoginProfileInfo profile = ResolveCurrentProfile(requestedProfile);
        if (profile.Kind != LaunchLoginProfileKind.LittleSkin ||
            card.TextureId is not long textureId)
        {
            ShowTextDialog(
                "更换披风",
                "只有通过 LittleSkin OAuth 登录后，才能在启动器中直接应用衣柜披风。",
                "知道了");
            return;
        }

        await ApplyLittleSkinTextureAsync(
                profile,
                textureId,
                card.CapeAddress ?? string.Empty,
                card.Title,
                LittleSkinTextureKind.Cape)
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
                item.DetailsUri,
                item.TextureId)
            .ConfigureAwait(true);
    }

    private async Task ApplySkinAddressAsync(
        LoginProfileInfo requestedProfile,
        string address,
        bool isSlim,
        string displayName,
        Uri? detailsUri,
        long? textureId)
    {
        LoginProfileInfo profile = ResolveCurrentProfile(requestedProfile);
        if (profile.Kind == LaunchLoginProfileKind.LittleSkin)
        {
            if (textureId is not long littleSkinTextureId)
            {
                ShowTextDialog("更换皮肤", "所选材质缺少 LittleSkin TID，无法直接应用。", "知道了");
                return;
            }

            await ApplyLittleSkinTextureAsync(
                    profile,
                    littleSkinTextureId,
                    address,
                    displayName,
                    LittleSkinTextureKind.Skin)
                .ConfigureAwait(true);
            return;
        }

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

    private async Task ApplyLittleSkinTextureAsync(
        LoginProfileInfo requestedProfile,
        long textureId,
        string textureAddress,
        string displayName,
        LittleSkinTextureKind kind)
    {
        LoginProfileInfo profile = ResolveCurrentProfile(requestedProfile);
        try
        {
            await RecordProfileTextureSnapshotAsync(profile).ConfigureAwait(true);
            (LoginProfileInfo refreshed, LittleSkinPlayer player) =
                await InvokeLittleSkinOAuthAsync(
                        profile,
                        async (accessToken, cancellationToken) =>
                        {
                            IReadOnlyList<LittleSkinPlayer> players =
                                await _littleSkinOAuthService
                                    .GetPlayersAsync(accessToken, cancellationToken)
                                    .ConfigureAwait(false);
                            LittleSkinPlayer selected = players.FirstOrDefault(player =>
                                string.Equals(
                                    player.Username,
                                    profile.Username,
                                    StringComparison.OrdinalIgnoreCase))
                                ?? throw new InvalidOperationException(
                                    $"LittleSkin 账户中未找到角色 {profile.Username}。");
                            await _littleSkinOAuthService
                                .ApplyTextureAsync(
                                    accessToken,
                                    selected.PlayerId,
                                    textureId,
                                    kind,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            return selected;
                        },
                        CancellationToken.None)
                    .ConfigureAwait(true);
            _ = player;
            LoginProfileInfo updated = kind == LittleSkinTextureKind.Skin
                ? refreshed with { SkinAddress = textureAddress }
                : refreshed;
            ApplyUpdatedAppearanceProfile(
                profile,
                updated,
                kind == LittleSkinTextureKind.Cape
                    ? "应用 LittleSkin 披风"
                    : "应用 LittleSkin 皮肤",
                kind == LittleSkinTextureKind.Cape
                    ? $"已为 {updated.Username} 应用披风 {displayName}。"
                    : $"已为 {updated.Username} 应用皮肤 {displayName}。");
            await RecordProfileTextureSnapshotAsync(updated).ConfigureAwait(true);
            await OpenExperimentalAppearancePageAsync(updated).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ShowTextDialog(
                kind == LittleSkinTextureKind.Cape ? "更换披风失败" : "更换皮肤失败",
                exception.Message,
                "知道了");
        }
    }

    private void ApplyUpdatedAppearanceProfile(
        LoginProfileInfo original,
        LoginProfileInfo updated,
        string saveAction,
        string? statusMessage = null)
    {
        ReplaceLoginProfile(original, updated);
        _launchLoginSurface.ProfilePage?.SetProfiles(_loginProfiles, updated);
        _launchLoginSurface.ProfileSkinPage?.SetProfile(updated);
        SaveProfilesInBackground(saveAction);
        HandleStatusMessage(statusMessage ?? $"已为 {updated.Username} 应用皮肤。");
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

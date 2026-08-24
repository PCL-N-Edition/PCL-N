// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Net;
using System.Text.Json;
using PCL.Application.Accounts;
using PCL.Desktop.Controls.Legacy;
using PCL.Core.Logging;
using PCL.Desktop.Features.Launching;
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
            // Quiet refresh only — do not surface the launch-oriented MS relogin dialog here.
            // Upstream waits on mcLoginMsLoader; we best-effort refresh then query owned capes.
            if (profile.Kind == LaunchLoginProfileKind.Microsoft &&
                !string.IsNullOrWhiteSpace(profile.RefreshToken))
            {
                profile = await TryRefreshMicrosoftAppearanceProfileQuietAsync(
                        profile,
                        "刷新 Microsoft 外观凭据",
                        cancellationToken)
                    .ConfigureAwait(true);
                page.SetModel(CreateFallbackAppearanceModel(profile));
            }

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
        page.SetApplyAvailability(SkinSiteInteractionPolicy.CanApplyPublicTexture(profile.Kind));
        page.TextureSelected += (_, item) => _ = ApplySkinSiteItemAsync(profile, item);
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
        // Microsoft wardrobe must not claim "no owned capes" while the ownership query is still
        // outstanding (or when a previous load never completed).
        SkinCapeClosetState capeClosetState = profile.Kind == LaunchLoginProfileKind.Microsoft
            ? SkinCapeClosetState.Loading
            : SkinCapeClosetState.Loaded;
        return new SkinAppearancePageModel(profile, current, otherProfiles, [], capeClosetState);
    }

    private async Task<(
        LoginProfileInfo Profile,
        IReadOnlyList<MinecraftOwnedCape> Capes,
        SkinCapeClosetState State)> LoadMicrosoftOwnedCapesAsync(
        LoginProfileInfo profile,
        CancellationToken cancellationToken)
    {
        if (!MinecraftLaunchPlanFactory.IsAccessTokenUsable(profile.AccessToken) &&
            !string.IsNullOrWhiteSpace(profile.RefreshToken))
        {
            profile = await TryRefreshMicrosoftAppearanceProfileQuietAsync(
                    profile,
                    "刷新 Microsoft 披风凭据",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!MinecraftLaunchPlanFactory.IsAccessTokenUsable(profile.AccessToken))
            return (profile, [], SkinCapeClosetState.LoadFailed);

        try
        {
            IReadOnlyList<MinecraftOwnedCape> capes = await _minecraftCapeService
                .GetOwnedCapesAsync(profile.AccessToken, cancellationToken)
                .ConfigureAwait(false);
            return (profile, capes, SkinCapeClosetState.Loaded);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
                InvalidOperationException or
                JsonException or
                TaskCanceledException)
        {
            bool unauthorized = exception is HttpRequestException http &&
                                http.StatusCode == HttpStatusCode.Unauthorized;
            if (unauthorized && !string.IsNullOrWhiteSpace(profile.RefreshToken))
            {
                try
                {
                    LoginProfileInfo refreshed = await TryRefreshMicrosoftAppearanceProfileQuietAsync(
                            profile,
                            "刷新 Microsoft 披风凭据",
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (MinecraftLaunchPlanFactory.IsAccessTokenUsable(refreshed.AccessToken) &&
                        !string.Equals(refreshed.AccessToken, profile.AccessToken, StringComparison.Ordinal))
                    {
                        IReadOnlyList<MinecraftOwnedCape> retryCapes = await _minecraftCapeService
                            .GetOwnedCapesAsync(refreshed.AccessToken, cancellationToken)
                            .ConfigureAwait(false);
                        return (refreshed, retryCapes, SkinCapeClosetState.Loaded);
                    }

                    profile = refreshed;
                }
                catch (Exception retryException) when (
                    retryException is HttpRequestException or
                        InvalidOperationException or
                        JsonException or
                        TaskCanceledException)
                {
                    PortableLog.Warn(
                        retryException,
                        "MicrosoftAppearance",
                        "刷新后再次读取正版披风仍失败。");
                }
            }

            PortableLog.Warn(
                exception,
                "MicrosoftAppearance",
                "读取正版账户已获得的披风失败，不会显示其他账户或历史披风作为替代，也不应提示“尚未获得任何披风”。");
            return (profile, [], SkinCapeClosetState.LoadFailed);
        }
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
        long littleSkinActiveCapeTextureId = 0;
        IReadOnlyList<MinecraftOwnedCape> microsoftCapes = [];
        SkinCapeClosetState microsoftCapeClosetState = SkinCapeClosetState.Loaded;
        if (profile.Kind == LaunchLoginProfileKind.Microsoft)
        {
            (profile, microsoftCapes, microsoftCapeClosetState) =
                await LoadMicrosoftOwnedCapesAsync(profile, cancellationToken).ConfigureAwait(false);
        }

        if (profile.Kind == LaunchLoginProfileKind.LittleSkin)
        {
            try
            {
                (LoginProfileInfo refreshed, (
                    IReadOnlyList<LittleSkinClosetItem> Skins,
                    IReadOnlyList<LittleSkinClosetItem> Capes,
                    IReadOnlyList<LittleSkinPlayer> Players) closet) =
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
                                Task<IReadOnlyList<LittleSkinPlayer>> playersTask =
                                    _littleSkinOAuthService.GetPlayersAsync(accessToken, token);
                                await Task.WhenAll(skinsTask, capesTask, playersTask)
                                    .ConfigureAwait(false);
                                return (
                                    await skinsTask.ConfigureAwait(false),
                                    await capesTask.ConfigureAwait(false),
                                    await playersTask.ConfigureAwait(false));
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                profile = refreshed;
                closetSkins = closet.Skins;
                closetCapes = closet.Capes;
                littleSkinActiveCapeTextureId = closet.Players
                    .FirstOrDefault(player => string.Equals(
                        player.Username,
                        profile.Username,
                        StringComparison.OrdinalIgnoreCase))
                    ?.CapeTextureId ?? 0;
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
        if (profile.Kind == LaunchLoginProfileKind.Microsoft)
        {
            // Prefer ACTIVE owned cape; otherwise keep sessionserver CAPE URL for preview.
            string? preferredCape = MinecraftCapeService.PreferCapePreviewAddress(
                microsoftCapes,
                currentTextures.CapeAddress);
            if (!string.Equals(
                    preferredCape,
                    currentTextures.CapeAddress,
                    StringComparison.OrdinalIgnoreCase))
            {
                currentTextures = currentTextures with { CapeAddress = preferredCape };
            }
        }

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
        SkinAppearanceCard[] capes;
        if (profile.Kind == LaunchLoginProfileKind.Microsoft)
        {
            capes = microsoftCapes
                .Select(cape => new SkinAppearanceCard(
                    ResolveMicrosoftCapeDisplayName(cape.Name),
                    cape.IsActive
                        ? GetResourceText("Appearance.Source.Current", "当前使用")
                        : GetResourceText(
                            "Appearance.Source.MicrosoftOwnedCape",
                            "正版账户已获得"),
                    currentTextures.SkinAddress,
                    cape.TextureAddress,
                    currentTextures.IsSlim,
                    CanApply: !cape.IsActive,
                    MicrosoftCapeId: cape.Id))
                .ToArray();
        }
        else if (profile.Kind == LaunchLoginProfileKind.LittleSkin)
        {
            capes = closetCapes
                .Select(item => new SkinAppearanceCard(
                    item.Name,
                    item.TextureId == littleSkinActiveCapeTextureId
                        ? GetResourceText("Appearance.Source.Current", "当前使用")
                        : GetResourceText(
                            "Appearance.Source.LittleSkinCloset",
                            "LittleSkin 衣柜"),
                    currentTextures.SkinAddress,
                    item.TextureAddress,
                    currentTextures.IsSlim,
                    CanApply: item.TextureId != littleSkinActiveCapeTextureId,
                    TextureId: item.TextureId))
                .ToArray();
        }
        else
        {
            capes = historyCapes
                .DistinctBy(
                    static card => card.CapeAddress ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        SkinCapeClosetState capeClosetState = profile.Kind == LaunchLoginProfileKind.Microsoft
            ? microsoftCapeClosetState
            : SkinCapeClosetState.Loaded;
        return new SkinAppearancePageModel(profile, current, skins, capes, capeClosetState);
    }

    private async Task PickExperimentalLocalSkinAsync(LoginProfileInfo requestedProfile)
    {
        LoginProfileInfo profile = ResolveCurrentProfile(requestedProfile);
        if (profile.Kind == LaunchLoginProfileKind.Offline)
        {
            ShowTextDialog(
                "离线档案",
                "离线登录不提供修改皮肤功能。登录在线账户后可使用云端皮肤。",
                "知道了");
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

        if (profile.Kind == LaunchLoginProfileKind.LittleSkin)
        {
            bool? isSlim = await ChooseLocalSkinModelAsync().ConfigureAwait(true);
            if (isSlim is null)
                return;

            try
            {
                HandleStatusMessage("正在上传并应用 LittleSkin 皮肤…");
                LoginProfileInfo refreshed = await RefreshLittleSkinLaunchProfileAsync(
                        profile,
                        CancellationToken.None)
                    .ConfigureAwait(true);
                byte[] bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(true);
                _ = await _littleSkinOAuthService
                    .UploadMinecraftTextureAsync(
                        refreshed.AccessToken,
                        refreshed.Uuid,
                        bytes,
                        Path.GetFileName(path),
                        isSlim.Value,
                        CancellationToken.None)
                    .ConfigureAwait(true);
                LoginProfileInfo serverBacked = refreshed with { SkinAddress = null };
                MinecraftProfileTextures textures = await MinecraftProfileTextureResolver
                    .ResolveAsync(serverBacked, CancellationToken.None)
                    .ConfigureAwait(true);
                LoginProfileInfo updated = serverBacked with { SkinAddress = textures.SkinAddress };
                ApplyUpdatedAppearanceProfile(
                    profile,
                    updated,
                    "上传 LittleSkin 皮肤",
                    $"已为 {updated.Username} 上传并应用自定义皮肤。");
                await RecordProfileTextureSnapshotAsync(updated).ConfigureAwait(true);
                await OpenExperimentalAppearancePageAsync(updated).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                ShowTextDialog(
                    "上传 LittleSkin 皮肤失败",
                    exception.Message,
                    "知道了");
            }
            return;
        }

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

        if (profile.Kind == LaunchLoginProfileKind.NCloud)
        {
            try
            {
                IHostOnlineMinecraftAccountProvider? provider =
                    HostOnlineMinecraftAccountProvider.Current;
                if (provider?.IsAuthenticated != true)
                    throw new InvalidOperationException("N Cloud 账户尚未登录。");
                byte[] bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(true);
                HostOnlineSkinResult result = await provider
                    .UploadSkinAsync(bytes, isSlim: false)
                    .ConfigureAwait(true);
                await ApplyNCloudSkinResultAsync(
                        profile,
                        result,
                        "上传 N Cloud 皮肤")
                    .ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                ShowTextDialog(
                    "上传皮肤失败",
                    "未能把皮肤保存到 N Cloud。\n\n详细信息：" + exception.Message,
                    "知道了");
            }
        }
    }

    private Task<bool?> ChooseLocalSkinModelAsync()
    {
        TaskCompletionSource<bool?> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        ShowMarkdownDialog(
            "选择皮肤模型",
            "请选择这张皮肤使用的手臂模型。选错模型会导致手臂纹理错位。",
            result => completion.TrySetResult(result switch
            {
                1 => false,
                2 => true,
                _ => null
            }),
            "经典（Steve）",
            "纤细（Alex）",
            "取消");
        return completion.Task;
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
        if (profile.Kind == LaunchLoginProfileKind.Microsoft)
        {
            if (string.IsNullOrWhiteSpace(card.MicrosoftCapeId))
            {
                ShowTextDialog(
                    "更换披风",
                    "所选披风不在当前正版账户已获得的披风列表中，无法应用。",
                    "知道了");
                return;
            }

            try
            {
                profile = await TryRefreshMicrosoftAppearanceProfileQuietAsync(
                        profile,
                        "刷新 Microsoft 披风凭据",
                        CancellationToken.None)
                    .ConfigureAwait(true);
                if (!MinecraftLaunchPlanFactory.IsAccessTokenUsable(profile.AccessToken))
                {
                    ShowTextDialog(
                        "更换披风",
                        "当前正版档案的访问令牌缺失或已过期，请先重新登录后再更换披风。",
                        "知道了");
                    return;
                }

                HandleStatusMessage("正在更换正版披风…");
                await _minecraftCapeService
                    .SetActiveCapeAsync(profile.AccessToken, card.MicrosoftCapeId)
                    .ConfigureAwait(true);
                ShowTextDialog(
                    "更换披风",
                    $"已为 {profile.Username} 启用披风 {card.Title}。",
                    "知道了");
                await OpenExperimentalAppearancePageAsync(profile).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                ShowTextDialog(
                    "更换正版披风失败",
                    exception.Message,
                    "知道了");
            }
            return;
        }

        if (profile.Kind == LaunchLoginProfileKind.LittleSkin &&
            card.TextureId is long textureId)
        {
            await ApplyLittleSkinTextureAsync(
                    profile,
                    textureId,
                    card.CapeAddress ?? string.Empty,
                    card.Title,
                    LittleSkinTextureKind.Cape)
                .ConfigureAwait(true);
            return;
        }

        if (profile.Kind == LaunchLoginProfileKind.ThirdParty)
        {
            string? profileUrl = ResolveAuthServerProfileUrl(profile.AuthServer);
            if (string.IsNullOrWhiteSpace(profileUrl))
            {
                ShowTextDialog(
                    "第三方披风",
                    "第三方认证站可自由更换披风；请前往对应皮肤站选择或上传任意披风。",
                    "知道了");
            }
            else
            {
                ShowTextDialog(
                    "第三方披风",
                    "第三方认证站可自由更换披风；可在对应皮肤站选择或上传任意披风。",
                    primaryButton: "打开皮肤站",
                    secondaryButton: "知道了",
                    primaryAction: () => OpenExternalUrl(profileUrl));
            }
            return;
        }

        if (profile.Kind != LaunchLoginProfileKind.LittleSkin)
        {
            ShowTextDialog(
                "更换披风",
                "当前账户类型不支持在启动器内更换披风。",
                "知道了");
            return;
        }

        ShowTextDialog(
            "更换披风",
            "所选 LittleSkin 披风缺少衣柜材质 ID，无法直接应用。",
            "知道了");
    }

    private async Task ApplySkinSiteItemAsync(
        LoginProfileInfo requestedProfile,
        SkinSiteItem item)
    {
        LoginProfileInfo profile = ResolveCurrentProfile(requestedProfile);
        if (!SkinSiteInteractionPolicy.CanApplyPublicTexture(profile.Kind))
        {
            ShowTextDialog(
                item.TextureKind == SkinSiteTextureKind.Cape
                    ? GetResourceText("Appearance.Library.ApplyBlocked.CapeTitle", "使用公开披风")
                    : GetResourceText("Appearance.Library.ApplyBlocked.SkinTitle", "使用公开皮肤"),
                GetResourceText(
                    "Appearance.Library.ApplyBlocked.Message",
                    "正版与 N Cloud 档案不能直接使用皮肤站中的公开材质。"),
                GetResourceText("Common.Action.Confirm", "好"));
            return;
        }

        if (item.TextureKind == SkinSiteTextureKind.Cape)
        {
            if (profile.Kind == LaunchLoginProfileKind.LittleSkin)
            {
                await ApplyLittleSkinTextureAsync(
                        profile,
                        item.TextureId,
                        item.SkinAddress,
                        item.Name,
                        LittleSkinTextureKind.Cape)
                    .ConfigureAwait(true);
                return;
            }

            if (profile.Kind == LaunchLoginProfileKind.ThirdParty)
            {
                ShowTextDialog(
                    GetResourceText("Appearance.Library.ThirdPartyCape.Title", "第三方披风"),
                    GetResourceText(
                        "Appearance.Library.ThirdPartyCape.Message",
                        "请在皮肤站中将该披风加入衣柜，并应用到当前角色。"),
                    primaryButton: GetResourceText("Appearance.Library.Details", "查看详情"),
                    secondaryButton: GetResourceText("Common.Action.Confirm", "好"),
                    primaryAction: () => OpenExternalUrl(item.DetailsUri.AbsoluteUri));
                return;
            }

            ShowTextDialog(
                GetResourceText("Appearance.Library.UnsupportedCape.Title", "使用披风"),
                GetResourceText(
                    "Appearance.Library.UnsupportedCape.Message",
                    "当前档案不能直接应用皮肤站中的披风。"),
                GetResourceText("Common.Action.Confirm", "好"));
            return;
        }

        await ApplySkinAddressAsync(
                profile,
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
        if (profile.Kind == LaunchLoginProfileKind.NCloud)
        {
            try
            {
                IHostOnlineMinecraftAccountProvider? provider =
                    HostOnlineMinecraftAccountProvider.Current;
                if (provider?.IsAuthenticated != true)
                    throw new InvalidOperationException("N Cloud 账户尚未登录。");

                HostOnlineSkinResult result;
                if (textureId is long siteTextureId)
                {
                    // Skin-site selections remain references. The plugin/service stores
                    // only the site identity and texture id, never a duplicate PNG.
                    result = await provider
                        .UseSkinSiteTextureAsync(
                            "littleskin",
                            siteTextureId.ToString(
                                System.Globalization.CultureInfo.InvariantCulture),
                            isSlim)
                        .ConfigureAwait(true);
                }
                else
                {
                    byte[]? bytes = await MySkin
                        .LoadSkinBytesAsync(address)
                        .ConfigureAwait(true);
                    if (bytes is null)
                        throw new InvalidOperationException("无法读取所选皮肤材质。");
                    result = await provider
                        .UploadSkinAsync(bytes, isSlim)
                        .ConfigureAwait(true);
                }

                await ApplyNCloudSkinResultAsync(
                        profile,
                        result,
                        "应用 " + displayName)
                    .ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                ShowTextDialog(
                    "更换皮肤失败",
                    "未能更新 N Cloud 皮肤。\n\n详细信息：" + exception.Message,
                    "知道了");
            }
            return;
        }

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
            string detailsUrl = detailsUri?.AbsoluteUri ?? string.Empty;
            ShowTextDialog(
                GetResourceText("Appearance.ThirdPartyAuth.Title", "需要皮肤站授权"),
                GetResourceText(
                    "Appearance.ThirdPartyAuth.Message",
                    "第三方皮肤站的角色材质接口需要独立 OAuth 授权，不能复用游戏登录令牌。" +
                    "\n\n请在皮肤站中将材质加入衣柜并应用到角色。"),
                primaryButton: string.IsNullOrWhiteSpace(detailsUrl) ? "知道了" : "查看详情",
                secondaryButton: string.IsNullOrWhiteSpace(detailsUrl) ? string.Empty : "知道了",
                primaryAction: string.IsNullOrWhiteSpace(detailsUrl)
                    ? null
                    : () => OpenExternalUrl(detailsUrl));
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

        ShowTextDialog(
            "离线档案",
            "离线登录不提供修改皮肤功能。登录在线账户后可使用云端皮肤。",
            "知道了");
    }

    private async Task ApplyNCloudSkinResultAsync(
        LoginProfileInfo profile,
        HostOnlineSkinResult result,
        string action)
    {
        await RecordProfileTextureSnapshotAsync(profile).ConfigureAwait(true);
        LoginProfileInfo updated = profile with { SkinAddress = result.SkinAddress };
        ApplyUpdatedAppearanceProfile(profile, updated, action);
        await RecordProfileTextureSnapshotAsync(updated).ConfigureAwait(true);
        string storageDetail = string.Equals(
            result.SourceKind,
            "site",
            StringComparison.OrdinalIgnoreCase)
            ? "已保存皮肤站引用，未重复存储材质。"
            : string.IsNullOrWhiteSpace(result.Sha1)
                ? "皮肤已保存到 N Cloud。"
                : $"皮肤已按 SHA-1 去重保存（{result.Sha1}）。";
        ShowTextDialog(action, storageDetail, "知道了");
        await OpenExperimentalAppearancePageAsync(updated).ConfigureAwait(true);
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
                                .EnsureClosetTextureAsync(
                                    accessToken,
                                    textureId,
                                    displayName,
                                    kind,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            await _littleSkinOAuthService
                                .ApplyTextureAsync(
                                    accessToken,
                                    selected.PlayerId,
                                    textureId,
                                    kind,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            IReadOnlyList<LittleSkinPlayer> updatedPlayers =
                                await _littleSkinOAuthService
                                    .GetPlayersAsync(accessToken, cancellationToken)
                                    .ConfigureAwait(false);
                            LittleSkinPlayer updatedPlayer = updatedPlayers.FirstOrDefault(player =>
                                player.PlayerId == selected.PlayerId)
                                ?? throw new InvalidDataException("LittleSkin 未返回已更新的角色。");
                            long appliedTextureId = kind == LittleSkinTextureKind.Cape
                                ? updatedPlayer.CapeTextureId
                                : updatedPlayer.SkinTextureId;
                            if (appliedTextureId != textureId)
                            {
                                throw new InvalidDataException(
                                    "LittleSkin 返回的角色材质与所选材质不一致，请稍后重试。");
                            }
                            return updatedPlayer;
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

    /// <summary>
    /// Upstream <c>MySkin._GetCapeDisplayName</c>: map Mojang cape aliases to localized titles.
    /// </summary>
    private string ResolveMicrosoftCapeDisplayName(string capeAlias)
    {
        if (string.IsNullOrWhiteSpace(capeAlias))
            return capeAlias;

        string safeName = capeAlias
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("'", string.Empty, StringComparison.Ordinal);
        string key = "Appearance.Cape.Name." + safeName;
        string localized = GetResourceText(key, capeAlias);
        return string.Equals(localized, key, StringComparison.Ordinal) ||
               localized.StartsWith('!')
            ? capeAlias
            : localized;
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

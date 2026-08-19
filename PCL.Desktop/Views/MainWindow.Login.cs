// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PCL.Application.Accounts;
using PCL.Application.Launching;
using PCL.Application.Settings;
using PCL.Core.Logging;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Diagnostics;
using PCL.Desktop.Hosting;
using PCL.Desktop.Features.Launching;
using PCL.Desktop.Features.Launching.Appearance;
using PCL.Desktop.Features.Launching.Views;
using PCL.Desktop.Features.Settings.Views;
using PCL.Desktop.Features.Shared;
using PCL.Desktop.Localization;
using PCL.Desktop.Theme;
using PCL.Platform.Paths;

namespace PCL.Desktop.Views;

public partial class MainWindow
{
    private readonly object _profileSaveQueueLock = new();
    private Task _profileSaveQueue = Task.CompletedTask;
    private bool _profileSaveDrainRequested;
    private bool _profileSavesDrainedForClose;

    private void WireLaunchLoginSurface()
    {
        _launchLoginSurface.WireOnce(this, new LaunchLoginBindings
        {
            AppendLog = message => _launchRight?.AppendLog(message),
            OnProfileSelected = (launchPage, profile) =>
            {
                _loginProfiles.Remove(profile);
                _loginProfiles.Insert(0, profile);
                launchPage.SetSelectedProfilePresent(true);
                launchPage.RefreshPage(anim: true);
                SaveProfilesInBackground("保存账户档案选择");
                if (profile.Kind == LaunchLoginProfileKind.Microsoft)
                {
                    PublishMicrosoftProfile(profile, HostAccountSessionReason.Selected);
                }
                _launchRight?.AppendLog($"已选择账户档案 {profile.Username}。");
            },
            ConfirmDeleteProfile = (page, launchPage, profile) =>
            {
                ShowConfirmDialog(
                    "删除账户档案",
                    $"确定要删除账户档案“{profile.Username}”吗？\n\n删除后需要重新登录才能再次使用此账户。",
                    confirmed =>
                    {
                        if (confirmed)
                            RemoveLoginProfile(page, launchPage, profile);
                    },
                    "删除",
                    "取消",
                    isWarn: true);
            },
            ShowProfileTypeSelector = ShowProfileTypeSelector,
            ShowImportExportSelector = ShowProfileImportExportSelector,
            OpenAppearance = OpenProfileAppearancePage,
            SaveSkinAsync = SaveProfileSkinAsync,
            RefreshSkinAsync = RefreshProfileSkinAsync,
            OpenSecurity = OpenProfileSecurityPage,
            OpenNameEditor = OpenProfileNamePage,
            OpenUrl = OpenExternalUrl,
            StartMicrosoftLoginAsync = StartMicrosoftLoginAsync,
            StartLittleSkinLoginAsync = StartLittleSkinLoginAsync,
            StartNCloudLoginAsync = StartNCloudLoginAsync,
            OpenAuthAccountPage = OpenAuthAccountPage,
            StartThirdPartyLoginAsync = StartThirdPartyAuthLoginAsync,
            CreateOfflineProfile = CreateOfflineLoginProfile
        });
    }

    private void ApplyLaunchLoginPage(ILaunchHomeSurface launchPage, PageLaunchLeft.LaunchLoginPageType type)
    {
        WireLaunchLoginSurface();
        _launchLoginSurface.Apply(launchPage, type, _loginProfiles);
    }

    private void RemoveLoginProfile(
        PageLoginProfile page,
        ILaunchHomeSurface launchPage,
        LoginProfileInfo profile)
    {
        int removed = _loginProfiles.RemoveAll(existing => IsSameProfile(existing, profile));
        if (removed == 0)
            return;

        if (profile.Kind == LaunchLoginProfileKind.ThirdParty &&
            !string.IsNullOrWhiteSpace(profile.AuthServer) &&
            !string.IsNullOrWhiteSpace(profile.Uuid))
        {
            _ = ThirdPartyCredentialStore.DeleteAsync(profile.AuthServer, profile.Uuid);
        }

        LoginProfileInfo? selected = _loginProfiles.FirstOrDefault();
        page.SetProfiles(_loginProfiles, selected);
        launchPage.SetSelectedProfilePresent(selected is not null);
        launchPage.RefreshPage(anim: true, PageLaunchLeft.LaunchLoginPageType.Profile);
        SaveProfilesInBackground("删除账户档案");
        HandleStatusMessage($"已删除账户档案 {profile.Username}。");
    }

    private void CreateOfflineLoginProfile(ILaunchHomeSurface launchPage, OfflineProfileCreateRequest request)
    {
        string info = string.IsNullOrWhiteSpace(request.SkinSourceUuid)
            ? "离线登录"
            : $"离线登录 · 借用 {request.SkinSourceName}";
        LoginProfileInfo profile = new(
            request.Username,
            info,
            LaunchLoginProfileKind.Offline,
            Uuid: request.Uuid,
            SvgIcon: "lucide/user");

        _loginProfiles.RemoveAll(existing =>
            existing.Kind == LaunchLoginProfileKind.Offline &&
            string.Equals(existing.Uuid, profile.Uuid, StringComparison.OrdinalIgnoreCase));
        _loginProfiles.Insert(0, profile);
        _launchLoginSurface.ProfilePage?.SetProfiles(_loginProfiles, profile);
        launchPage.SetSelectedProfilePresent(true);
        launchPage.RefreshPage(anim: true);
        SaveProfilesInBackground("保存离线账户档案");
        _launchRight?.AppendLog($"已创建并选中离线档案 {profile.Username}。");
    }

    private void OpenProfileAppearancePage(LoginProfileInfo? profile, string action)
    {
        if (profile is null)
            return;

        if (profile.Kind == LaunchLoginProfileKind.ThirdParty &&
            string.Equals(action, "更换披风", StringComparison.Ordinal))
        {
            OpenAuthServerProfilePage(profile, action);
            return;
        }

        if (_launchHomeProfile.UseExperimentalFullPageHome() ||
            IsExperimentalHomepageUiEnabled())
        {
            _ = OpenExperimentalAppearancePageAsync(profile);
            return;
        }

        OpenLegacyProfileAppearanceAction(profile, action);
    }

    private void OpenLegacyProfileAppearanceAction(LoginProfileInfo profile, string action)
    {
        if (profile.Kind == LaunchLoginProfileKind.Microsoft)
        {
            // WPF: ModProfile.ChangeSkinMs — pick local PNG and upload to Minecraft services.
            _ = ChangeMicrosoftSkinAsync(profile, action);
            return;
        }

        if (profile.Kind == LaunchLoginProfileKind.ThirdParty)
        {
            OpenAuthServerProfilePage(profile, action);
            return;
        }

        if (profile.Kind == LaunchLoginProfileKind.LittleSkin)
        {
            _ = OpenExperimentalAppearancePageAsync(profile);
            return;
        }

        if (profile.Kind == LaunchLoginProfileKind.NCloud)
        {
            _ = OpenExperimentalAppearancePageAsync(profile);
            return;
        }

        // WPF offline: borrow MS profile skin or pick local file.
        ShowOfflineSkinOptions(profile, action);
    }

    private void ShowOfflineSkinOptions(LoginProfileInfo profile, string action)
    {
        List<LoginProfileInfo> msProfiles = _loginProfiles
            .Where(static p => p.Kind == LaunchLoginProfileKind.Microsoft)
            .ToList();
        if (msProfiles.Count == 0)
        {
            _ = PickOfflineSkinFileAsync(profile, action);
            return;
        }

        List<MyListItem> items =
        [
            CreateProfileTypeItem("使用本地 PNG 文件", "从磁盘选择皮肤文件作为离线外观。", "lucide/image")
        ];
        foreach (LoginProfileInfo ms in msProfiles)
        {
            items.Add(CreateProfileTypeItem(
                "借用 " + ms.Username + " 的正版皮肤",
                "使用该正版档案当前皮肤作为离线外观来源。",
                "lucide/user"));
        }

        MyMsgSelect dialog = new();
        dialog.Configure(action, items);
        ShowSelectionDialog(dialog, selectedIndex =>
        {
            if (selectedIndex is not int index)
                return;
            if (index == 0)
            {
                _ = PickOfflineSkinFileAsync(profile, action);
                return;
            }

            LoginProfileInfo source = msProfiles[index - 1];
            string skin = MySkin.ResolveSkinAddress(
                source.SkinAddress,
                source.Uuid,
                source.UsesYggdrasil ? source.AuthServer : null);
            LoginProfileInfo updated = profile with { SkinAddress = skin };
            _ = RecordProfileTextureSnapshotAsync(profile);
            ReplaceLoginProfile(profile, updated);
            _launchLoginSurface.ProfilePage?.SetProfiles(_loginProfiles, updated);
            _launchLoginSurface.ProfileSkinPage?.SetProfile(updated);
            SaveProfilesInBackground("借用正版皮肤");
            HandleStatusMessage($"已为 {updated.Username} 借用 {source.Username} 的皮肤。");
        });
    }

    private async Task PickOfflineSkinFileAsync(LoginProfileInfo profile, string action)
    {
        string? path = await PickOpenFilePathAsync(
                action,
                new FilePickerFileType("皮肤 PNG") { Patterns = ["*.png"] })
            .ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
            return;

        await RecordProfileTextureSnapshotAsync(profile).ConfigureAwait(true);
        LoginProfileInfo updated = profile with { SkinAddress = path };
        ReplaceLoginProfile(profile, updated);
        _launchLoginSurface.ProfilePage?.SetProfiles(_loginProfiles, updated);
        _launchLoginSurface.ProfileSkinPage?.SetProfile(updated);
        SaveProfilesInBackground("更新离线皮肤");
        ShowTextDialog(action, "已使用本地皮肤文件：\n" + path, "知道了");
    }

    private async Task ChangeMicrosoftSkinAsync(LoginProfileInfo profile, string action)
    {
        string? path = await PickOpenFilePathAsync(
                action,
                new FilePickerFileType("皮肤 PNG") { Patterns = ["*.png"] })
            .ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(true);
            _ = await UploadMicrosoftSkinAsync(
                    profile,
                    bytes,
                    Path.GetFileName(path),
                    isSlim: false,
                    fallbackAddress: path,
                    action)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ShowTextDialog(action, "皮肤上传失败。\n\n详细信息：" + ex.Message, "知道了");
        }
    }

    private async Task<LoginProfileInfo?> UploadMicrosoftSkinAsync(
        LoginProfileInfo profile,
        byte[] bytes,
        string fileName,
        bool isSlim,
        string fallbackAddress,
        string action)
    {
        try
        {
            profile = await RefreshMicrosoftAppearanceProfileAsync(
                    profile,
                    "刷新 Microsoft 外观凭据",
                    CancellationToken.None)
                .ConfigureAwait(true);
            if (!MinecraftLaunchPlanFactory.IsAccessTokenUsable(profile.AccessToken))
            {
                ShowTextDialog(
                    action,
                    "当前正版档案的访问令牌缺失或已过期，请先重新登录后再更换皮肤。",
                    "知道了");
                return null;
            }

            await RecordProfileTextureSnapshotAsync(profile).ConfigureAwait(true);
            HandleStatusMessage("正在上传皮肤…");
            MinecraftSkinUploadResult upload = await _minecraftSkinService
                .UploadAsync(profile.AccessToken, bytes, fileName, isSlim)
                .ConfigureAwait(true);

            LoginProfileInfo updated = profile with
            {
                SkinAddress = upload.SkinAddress ?? fallbackAddress
            };
            ReplaceLoginProfile(profile, updated);
            _launchLoginSurface.ProfilePage?.SetProfiles(_loginProfiles, updated);
            _launchLoginSurface.ProfileSkinPage?.SetProfile(updated);
            SaveProfilesInBackground("更换 Microsoft 皮肤");
            ShowTextDialog(action, "皮肤已上传并更新。", "知道了");
            return updated;
        }
        catch (Exception exception)
        {
            PortableLog.Warn(exception, "MicrosoftAppearance", "更换正版皮肤失败。");
            ShowTextDialog(action, "皮肤上传失败。\n\n详细信息：" + exception.Message, "知道了");
            return null;
        }
    }

    private async Task<LoginProfileInfo> RefreshMicrosoftAppearanceProfileAsync(
        LoginProfileInfo requestedProfile,
        string saveAction,
        CancellationToken cancellationToken)
    {
        LoginProfileInfo profile = ResolveCurrentProfile(requestedProfile);
        if (profile.Kind != LaunchLoginProfileKind.Microsoft ||
            string.IsNullOrWhiteSpace(profile.RefreshToken))
        {
            return profile;
        }

        LoginProfileInfo refreshed = await RefreshLaunchProfileAsync(profile, cancellationToken)
            .ConfigureAwait(true);
        if (refreshed == profile)
            return profile;

        ReplaceLoginProfile(profile, refreshed);
        _launchLoginSurface.ProfilePage?.SetProfiles(_loginProfiles, refreshed);
        _launchLoginSurface.ProfileSkinPage?.SetProfile(refreshed);
        SaveProfilesInBackground(saveAction);
        return refreshed;
    }

    /// <summary>
    /// Appearance-page refresh must not open the launch-oriented MS relogin dialog.
    /// Upstream waits on <c>mcLoginMsLoader</c>; we best-effort refresh and keep the
    /// previous profile when network/auth refresh fails.
    /// </summary>
    private async Task<LoginProfileInfo> TryRefreshMicrosoftAppearanceProfileQuietAsync(
        LoginProfileInfo requestedProfile,
        string saveAction,
        CancellationToken cancellationToken)
    {
        LoginProfileInfo profile = ResolveCurrentProfile(requestedProfile);
        if (profile.Kind != LaunchLoginProfileKind.Microsoft ||
            string.IsNullOrWhiteSpace(profile.RefreshToken))
        {
            return profile;
        }

        string clientId = MicrosoftMinecraftAuthService.ResolveClientId();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            if (MinecraftLaunchPlanFactory.IsAccessTokenUsable(profile.AccessToken))
                return profile;

            PortableLog.Warn(
                "MicrosoftAppearance",
                "缺少 Microsoft Client ID，且当前访问令牌不可用，无法静默刷新正版外观凭据。");
            return profile;
        }

        try
        {
            MicrosoftMinecraftLoginResult refreshed = await _microsoftAuthService
                .RefreshAsync(clientId, profile.RefreshToken, cancellationToken)
                .ConfigureAwait(false);
            LoginProfileInfo updated = profile with
            {
                Username = refreshed.Username,
                Uuid = refreshed.Uuid,
                AccessToken = refreshed.AccessToken,
                RefreshToken = refreshed.RefreshToken,
                SkinAddress = refreshed.SkinAddress ?? profile.SkinAddress,
                Info = refreshed.OwnsMinecraft ? "Microsoft 正版" : profile.Info
            };
            ReplaceLoginProfile(profile, updated);
            _launchLoginSurface.ProfilePage?.SetProfiles(_loginProfiles, updated);
            _launchLoginSurface.ProfileSkinPage?.SetProfile(updated);
            SaveProfilesInBackground(saveAction);
            return updated;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            PortableLog.Warn(
                exception,
                "MicrosoftAppearance",
                "静默刷新正版外观凭据失败，继续使用已保存的令牌读取皮肤与披风。");
            return profile;
        }
    }

    private void OpenProfileSecurityPage(LoginProfileInfo? profile)
    {
        if (profile is null)
            return;

        if (profile.Kind == LaunchLoginProfileKind.Microsoft)
        {
            const string securityUrl = "https://account.microsoft.com/security";
            ShowTextDialog(
                "修改密码",
                "将打开 Microsoft 账户安全页面。密码修改完成后，可能需要在启动器中重新登录。",
                primaryButton: "打开安全页面",
                secondaryButton: "知道了",
                primaryAction: () => OpenExternalUrl(securityUrl));
            return;
        }

        if (profile.Kind == LaunchLoginProfileKind.NCloud)
        {
            const string accountUrl = "https://pcln.top/#/account";
            ShowTextDialog(
                "管理 N Cloud 账户",
                "将打开 PCL N 在线账户页面。N Cloud 不会把账户密码交给启动器。",
                primaryButton: "打开账户页面",
                secondaryButton: "知道了",
                primaryAction: () => OpenExternalUrl(accountUrl));
            return;
        }

        if (profile.Kind is LaunchLoginProfileKind.ThirdParty or LaunchLoginProfileKind.LittleSkin)
        {
            OpenAuthServerProfilePage(profile, "修改密码");
            return;
        }

        ShowTextDialog("修改密码", "离线档案没有在线密码。若需要更换玩家名或 UUID，请新建一个离线档案。", "知道了");
    }

    private void OpenProfileNamePage(LoginProfileInfo? profile)
    {
        if (profile is null)
            return;

        if (profile.Kind == LaunchLoginProfileKind.Microsoft)
        {
            // WPF: ModProfile.EditProfileId — rename via Minecraft services API.
            ShowInputDialog(
                "修改玩家名",
                "正版玩家名 30 天内通常只能修改一次。请输入 3–16 位字母/数字/下划线。",
                profile.Username,
                "新的玩家名",
                newName =>
                {
                    if (string.IsNullOrWhiteSpace(newName))
                        return;
                    _ = RenameMicrosoftProfileAsync(profile, newName.Trim());
                });
            return;
        }

        if (profile.Kind == LaunchLoginProfileKind.NCloud)
        {
            const string accountUrl = "https://pcln.top/#/account";
            ShowTextDialog(
                "修改 N Cloud 档案",
                "将打开 PCL N 在线账户页面。修改云端玩家名后，重新登录或启动游戏即可同步。",
                primaryButton: "打开账户页面",
                secondaryButton: "知道了",
                primaryAction: () => OpenExternalUrl(accountUrl));
            return;
        }

        if (profile.Kind is LaunchLoginProfileKind.ThirdParty or LaunchLoginProfileKind.LittleSkin)
        {
            OpenAuthServerProfilePage(profile, "修改玩家名");
            return;
        }

        // WPF offline: rename + regenerate offline UUID from the new name.
        ShowInputDialog(
            "修改档案",
            "请输入新的离线玩家名（3–16 位字母、数字或下划线）。",
            profile.Username,
            "玩家名",
            newName =>
            {
                if (string.IsNullOrWhiteSpace(newName))
                    return;

                string trimmed = newName.Trim();
                if (!System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^[A-Za-z0-9_]{3,16}$"))
                {
                    ShowTextDialog("修改档案", "玩家名不合法。请使用 3–16 位字母、数字或下划线。", "知道了");
                    return;
                }

                if (string.Equals(trimmed, profile.Username, StringComparison.Ordinal))
                    return;

                string uuid = MinecraftLaunchPlanFactory.CreateOfflineUuid(trimmed, legacy: false);
                LoginProfileInfo updated = profile with
                {
                    Username = trimmed,
                    Uuid = uuid,
                    Info = string.IsNullOrWhiteSpace(profile.Info) || profile.Info.Contains("离线", StringComparison.Ordinal)
                        ? "离线"
                        : profile.Info
                };
                ReplaceLoginProfile(profile, updated);
                _launchLoginSurface.ProfilePage?.SetProfiles(_loginProfiles, updated);
                _launchLoginSurface.ProfileSkinPage?.SetProfile(updated);
                SaveProfilesInBackground("修改离线档案");
                HandleStatusMessage("已将离线档案重命名为 " + trimmed);
            });
    }

    private async Task RenameMicrosoftProfileAsync(LoginProfileInfo profile, string newUsername)
    {
        if (string.IsNullOrWhiteSpace(profile.AccessToken))
        {
            ShowTextDialog("修改玩家名", "当前正版档案缺少访问令牌，请先重新登录。", "知道了");
            return;
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(newUsername, @"^[A-Za-z0-9_]{3,16}$"))
        {
            ShowTextDialog("修改玩家名", "玩家名不合法。请使用 3–16 位字母、数字或下划线。", "知道了");
            return;
        }

        try
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(30) };
            using (HttpRequestMessage check = new(
                       HttpMethod.Get,
                       "https://api.minecraftservices.com/minecraft/profile/name/" +
                       Uri.EscapeDataString(newUsername) + "/available"))
            {
                check.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", profile.AccessToken);
                using HttpResponseMessage checkResponse = await client.SendAsync(check).ConfigureAwait(true);
                string checkBody = await checkResponse.Content.ReadAsStringAsync().ConfigureAwait(true);
                if (checkResponse.IsSuccessStatusCode)
                {
                    using JsonDocument checkDoc = JsonDocument.Parse(checkBody);
                    string status = checkDoc.RootElement.TryGetProperty("status", out JsonElement statusEl)
                        ? statusEl.GetString() ?? string.Empty
                        : string.Empty;
                    if (string.Equals(status, "DUPLICATE", StringComparison.OrdinalIgnoreCase))
                    {
                        ShowTextDialog("修改玩家名", "该玩家名已被占用。", "知道了");
                        return;
                    }

                    if (string.Equals(status, "NOT_ALLOWED", StringComparison.OrdinalIgnoreCase))
                    {
                        ShowTextDialog("修改玩家名", "该玩家名不被允许。", "知道了");
                        return;
                    }
                }
            }

            using HttpRequestMessage put = new(
                HttpMethod.Put,
                "https://api.minecraftservices.com/minecraft/profile/name/" + Uri.EscapeDataString(newUsername));
            put.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", profile.AccessToken);
            put.Content = new StringContent(string.Empty);
            using HttpResponseMessage putResponse = await client.SendAsync(put).ConfigureAwait(true);
            string putBody = await putResponse.Content.ReadAsStringAsync().ConfigureAwait(true);
            if (!putResponse.IsSuccessStatusCode)
            {
                string message = putResponse.StatusCode == System.Net.HttpStatusCode.Forbidden
                    ? "修改被拒绝（可能处于冷却期或权限不足）。"
                    : putBody;
                ShowTextDialog("修改玩家名", "修改失败。\n\n" + message, "知道了");
                return;
            }

            string finalName = newUsername;
            try
            {
                using JsonDocument result = JsonDocument.Parse(putBody);
                if (result.RootElement.TryGetProperty("name", out JsonElement nameEl) &&
                    nameEl.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(nameEl.GetString()))
                {
                    finalName = nameEl.GetString()!;
                }
            }
            catch (JsonException)
            {
            }

            LoginProfileInfo updated = profile with { Username = finalName };
            ReplaceLoginProfile(profile, updated);
            _launchLoginSurface.ProfilePage?.SetProfiles(_loginProfiles, updated);
            _launchLoginSurface.ProfileSkinPage?.SetProfile(updated);
            SaveProfilesInBackground("修改 Microsoft 玩家名");
            ShowTextDialog("修改玩家名", "玩家名已更新为：" + finalName, "知道了");
        }
        catch (Exception ex)
        {
            ShowTextDialog("修改玩家名", "修改失败。\n\n详细信息：" + ex.Message, "知道了");
        }
    }

    private void OpenAuthServerProfilePage(LoginProfileInfo profile, string action)
    {
        string? url = ResolveAuthServerProfileUrl(profile.AuthServer);
        if (string.IsNullOrWhiteSpace(url))
        {
            ShowTextDialog(action, "第三方账户的资料由认证服务器管理，但当前档案没有记录可打开的服务器地址。请到对应认证服务器的网站中修改。", "知道了");
            return;
        }

        ShowTextDialog(
            action,
            "请在此第三方账户所属的认证服务器页面中完成账户资料修改。",
            primaryButton: "打开认证服务器",
            secondaryButton: "知道了",
            primaryAction: () => OpenExternalUrl(url));
    }

    private static string? ResolveAuthServerProfileUrl(string? authServer)
    {
        string? normalized = NormalizeAuthServerUrl(authServer ?? string.Empty);
        if (normalized is null || !Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri))
            return null;

        string path = uri.AbsolutePath.TrimEnd('/');
        foreach (string suffix in new[] { "/api/yggdrasil/authserver", "/api/yggdrasil" })
        {
            if (!path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;

            string rootPath = path[..^suffix.Length].TrimEnd('/');
            UriBuilder builder = new(uri)
            {
                Path = rootPath + "/user/profile",
                Query = string.Empty,
                Fragment = string.Empty
            };
            return builder.Uri.ToString();
        }

        return uri.ToString();
    }

    private async Task SaveProfileSkinAsync(LoginProfileInfo? profile)
    {
        if (profile is null)
            return;

        if (string.IsNullOrWhiteSpace(profile.SkinAddress))
        {
            ShowTextDialog("保存皮肤", "当前档案没有可保存的皮肤资源。请先登录带有皮肤的在线档案，或在离线档案中选择一个皮肤来源。", "知道了");
            return;
        }

        string suggestedFileName = DesktopPathHelpers.SanitizeFileName(profile.Username) + "-skin.png";
        string targetPath = await PickSaveFilePathAsync(
                "保存皮肤",
                suggestedFileName,
                new FilePickerFileType("PNG 图片") { Patterns = ["*.png"] })
            .ConfigureAwait(true)
            ?? Path.Combine(DesktopPathHelpers.GetDesktopOrBaseDirectory(), suggestedFileName);

        try
        {
            byte[]? bytes = await MySkin
                .LoadSkinBytesAsync(profile.DisplaySkinAddress)
                .ConfigureAwait(true);
            if (bytes is null)
            {
                ShowTextDialog("保存皮肤", "当前皮肤资源不存在，可能已经被移动或需要重新登录后刷新。", "知道了");
                return;
            }

            await File.WriteAllBytesAsync(targetPath, bytes).ConfigureAwait(true);
            string folder = Path.GetDirectoryName(Path.GetFullPath(targetPath))
                ?? DesktopPathHelpers.GetDesktopOrBaseDirectory();
            ShowTextDialog(
                "保存完成",
                "皮肤已保存到：\n" + targetPath,
                primaryButton: "打开文件夹",
                secondaryButton: "知道了",
                primaryAction: () => OpenFolder(folder));
        }
        catch (Exception ex)
        {
            ShowTextDialog("保存失败", "未能保存皮肤。\n\n详细信息：" + ex.Message);
        }
    }

    private async Task RefreshProfileSkinAsync(PageLoginProfileSkin page)
    {
        LoginProfileInfo? profile = page.Profile;
        if (profile is null)
            return;

        try
        {
            if (profile.Kind == LaunchLoginProfileKind.Microsoft &&
                !string.IsNullOrWhiteSpace(profile.RefreshToken))
            {
                LoginProfileInfo refreshed = await RefreshLaunchProfileAsync(profile, CancellationToken.None)
                    .ConfigureAwait(true);
                AddOrUpdateLoginProfile(refreshed);
                _launchLoginSurface.ProfilePage?.SetProfiles(_loginProfiles, refreshed);
                page.SetProfile(refreshed);
                SaveProfilesInBackground("刷新 Microsoft 皮肤");
                ShowTextDialog("皮肤已刷新", "已从 Microsoft 重新获取档案与皮肤信息。", "知道了");
                return;
            }

            if (profile.Kind == LaunchLoginProfileKind.LittleSkin)
            {
                LoginProfileInfo refreshed = await RefreshLittleSkinLaunchProfileAsync(
                        profile,
                        CancellationToken.None)
                    .ConfigureAwait(true);
                MinecraftProfileTextures textures = await MinecraftProfileTextureResolver
                    .ResolveAsync(refreshed)
                    .ConfigureAwait(true);
                refreshed = refreshed with { SkinAddress = textures.SkinAddress };
                ReplaceLoginProfile(profile, refreshed);
                _launchLoginSurface.ProfilePage?.SetProfiles(_loginProfiles, refreshed);
                page.SetProfile(refreshed);
                SaveProfilesInBackground("刷新 LittleSkin 外观");
                ShowTextDialog(
                    "外观已刷新",
                    "已从 LittleSkin 重新获取当前角色的皮肤与披风信息。",
                    "知道了");
                return;
            }

            if (profile.Kind == LaunchLoginProfileKind.NCloud)
            {
                LoginProfileInfo refreshed = await RefreshLaunchProfileAsync(
                        profile,
                        CancellationToken.None)
                    .ConfigureAwait(true);
                MinecraftProfileTextures textures = await MinecraftProfileTextureResolver
                    .ResolveAsync(refreshed)
                    .ConfigureAwait(true);
                if (!string.IsNullOrWhiteSpace(textures.SkinAddress) &&
                    !textures.SkinAddress.Contains(
                        "/session/minecraft/profile/",
                        StringComparison.OrdinalIgnoreCase))
                {
                    refreshed = refreshed with { SkinAddress = textures.SkinAddress };
                }

                ReplaceLoginProfile(profile, refreshed);
                _launchLoginSurface.ProfilePage?.SetProfiles(_loginProfiles, refreshed);
                page.SetProfile(refreshed);
                SaveProfilesInBackground("刷新 N Cloud 外观");
                ShowTextDialog(
                    "外观已刷新",
                    string.IsNullOrWhiteSpace(textures.CapeAddress)
                        ? "已从 N Cloud 重新获取当前皮肤。"
                        : "已从 N Cloud 重新获取当前皮肤与披风。",
                    "知道了");
                return;
            }

            page.Reload();
            ShowTextDialog(
                "已刷新档案显示",
                profile.Kind == LaunchLoginProfileKind.Offline
                    ? "已重新加载离线档案皮肤。若使用本地 PNG，请确认文件仍然存在。"
                    : "已重新载入档案信息。第三方皮肤请在认证站修改后重新登录。",
                "知道了");
        }
        catch (Exception ex)
        {
            page.Reload();
            ShowTextDialog("刷新失败", "未能刷新皮肤。\n\n详细信息：" + ex.Message, "知道了");
        }
    }

    private void ShowProfileTypeSelector(ILaunchHomeSurface launchPage)
    {
        // Offline is always available. N Cloud is single-account: hide when a
        // profile already exists; only offer when online service is signed in.
        bool hasNCloudProfile = _loginProfiles.Any(
            static profile => profile.Kind == LaunchLoginProfileKind.NCloud);
        bool offerNCloud =
            !hasNCloudProfile &&
            HostOnlineMinecraftAccountProvider.Current?.IsAuthenticated == true;

        List<MyListItem> items =
        [
            CreateProfileTypeItem(
                "Microsoft 登录",
                "使用正版 Microsoft 账户登录，适合已购买 Minecraft 的玩家。",
                "lucide/shield-check"),
            CreateProfileTypeItem(
                "LittleSkin 登录",
                "通过浏览器 OAuth 授权，可直接管理 LittleSkin 角色、皮肤与披风。",
                "lucide/boxes"),
            CreateProfileTypeItem(
                "第三方登录",
                "使用自定义 Yggdrasil 兼容认证服务器登录。",
                "lucide/network"),
            CreateProfileTypeItem(
                "离线登录",
                "创建本地离线档案。联机服务器可能不会接受此档案。",
                "lucide/link-2-off")
        ];
        List<PageLaunchLeft.LaunchLoginPageType> targets =
        [
            PageLaunchLeft.LaunchLoginPageType.Ms,
            PageLaunchLeft.LaunchLoginPageType.LittleSkin,
            PageLaunchLeft.LaunchLoginPageType.Auth,
            PageLaunchLeft.LaunchLoginPageType.Offline
        ];

        if (offerNCloud)
        {
            items.Add(CreateProfileTypeItem(
                "N Cloud 在线账户",
                "使用已登录的在线服务账户；每个启动器仅支持一个 N Cloud 档案。",
                "lucide/cloud"));
            targets.Add(PageLaunchLeft.LaunchLoginPageType.NCloud);
        }

        MyMsgSelect dialog = new();
        dialog.Configure("选择账户类型", items);
        ShowSelectionDialog(dialog, selectedIndex =>
        {
            if (selectedIndex is not int index ||
                index < 0 ||
                index >= targets.Count)
            {
                return;
            }

            PageLaunchLeft.LaunchLoginPageType target = targets[index];
            launchPage.RefreshPage(anim: true, target);
            _launchRight?.AppendLog($"正在创建{dialog.Items[index].Title}档案。");
        });
    }

    private static MyListItem CreateProfileTypeItem(string title, string info, string icon) =>
        new()
        {
            Title = title,
            Info = info,
            SvgIcon = icon,
            LogoScale = 0.82d,
            MinHeight = 42d,
            Margin = new Thickness(0d, 2d)
        };

    private void ShowProfileImportExportSelector(PageLoginProfile page, ILaunchHomeSurface launchPage)
    {
        MyMsgSelect dialog = new();
        dialog.Configure(
            "导入或导出账户档案",
            [
                CreateProfileTypeItem(
                    "导入账户档案",
                    "从本地 JSON 文件导入账户档案，并与当前列表合并。",
                    "lucide/file-input"),
                CreateProfileTypeItem(
                    "导出账户档案",
                    "将当前账户档案保存为 JSON 文件，方便备份或转移到其他设备。",
                    "lucide/file-output")
            ]);

        ShowSelectionDialog(dialog, selectedIndex =>
        {
            if (selectedIndex == 0)
                _ = ImportProfilesAsync(page, launchPage);
            else if (selectedIndex == 1)
                _ = ExportProfilesAsync();
        });
    }

    private void ShowSelectionDialog(MyMsgSelect dialog, Action<int?> closed)
    {
        if (this.FindControl<BlurBorder>("PanMsgBackground") is not { } background ||
            this.FindControl<Grid>("PanMsg") is not { } host)
        {
            closed(null);
            return;
        }

        host.Children.Clear();
        background.IsVisible = true;
        AnimateMsgBackground(background, 90);
        dialog.Closed += (_, args) =>
        {
            host.Children.Remove(dialog);
            if (host.Children.Count == 0)
            {
                AnimateMsgBackground(background, 0, () =>
                {
                    background.Background = Brushes.Transparent;
                    background.IsVisible = false;
                });
            }
            closed(args.SelectedIndex);
        };
        host.Children.Add(dialog);
        dialog.BeginShowAnimation();
    }

    private void ShowColorDialog(SettingsColorRequestedEventArgs request)
    {
        if (this.FindControl<BlurBorder>("PanMsgBackground") is not { } background ||
            this.FindControl<Grid>("PanMsg") is not { } host)
        {
            request.Complete(null);
            return;
        }

        MyMsgColor dialog = new();
        dialog.Configure(request.Title, request.InitialColor);
        dialog.PreviewChanged += (_, color) => request.Preview(color);
        host.Children.Clear();
        background.IsVisible = true;
        AnimateMsgBackground(background, 90);
        dialog.Closed += (_, args) =>
        {
            host.Children.Remove(dialog);
            if (host.Children.Count == 0)
            {
                AnimateMsgBackground(background, 0, () =>
                {
                    background.Background = Brushes.Transparent;
                    background.IsVisible = false;
                });
            }
            request.Complete(args.Color);
        };
        host.Children.Add(dialog);
        dialog.BeginShowAnimation();
    }

    private void ShowTextDialog(
        string title,
        string caption,
        string primaryButton = "确定",
        string secondaryButton = "",
        string thirdButton = "",
        Action? primaryAction = null,
        Action? secondaryAction = null,
        Action? thirdAction = null)
    {
        if (this.FindControl<BlurBorder>("PanMsgBackground") is not { } background ||
            this.FindControl<Grid>("PanMsg") is not { } host)
        {
            _launchRight?.AppendLog($"{title}：{caption}");
            return;
        }

        MyMsgText dialog = new();
        dialog.Configure(MyMsgDialogModel.CreateLegacy(
            title,
            caption,
            primaryButton,
            secondaryButton,
            thirdButton,
            isWarning: false,
            primaryAction,
            secondaryAction,
            thirdAction));
        host.Children.Clear();
        background.IsVisible = true;
        AnimateMsgBackground(background, 90);
        dialog.Closed += (_, _) =>
        {
            host.Children.Remove(dialog);
            if (host.Children.Count == 0)
            {
                AnimateMsgBackground(background, 0, () =>
                {
                    background.Background = Brushes.Transparent;
                    background.IsVisible = false;
                });
            }
        };
        host.Children.Add(dialog);
        dialog.BeginShowAnimation();
    }

    private void ShowConfirmDialog(
        string title,
        string caption,
        Action<bool> closed,
        string primaryButton = "确定",
        string secondaryButton = "取消",
        bool isWarn = false,
        Action? primaryAction = null,
        Action? secondaryAction = null)
    {
        ShowMarkdownDialog(
            title,
            caption,
            result => closed(result == 1),
            primaryButton,
            secondaryButton,
            thirdButton: string.Empty,
            isWarn,
            primaryAction,
            secondaryAction);
    }

    private void ShowMarkdownDialog(
        string title,
        string markdown,
        Action<int> closed,
        string primaryButton,
        string secondaryButton = "",
        string thirdButton = "",
        bool isWarn = false,
        Action? primaryAction = null,
        Action? secondaryAction = null,
        Action? thirdAction = null)
    {
        if (this.FindControl<BlurBorder>("PanMsgBackground") is not { } background ||
            this.FindControl<Grid>("PanMsg") is not { } host)
        {
            closed(0);
            return;
        }

        MyMsgMarkdown dialog = new();
        dialog.Configure(MyMsgDialogModel.CreateLegacy(
            title,
            markdown,
            primaryButton,
            secondaryButton,
            thirdButton,
            isWarn,
            primaryAction,
            secondaryAction,
            thirdAction));
        host.Children.Clear();
        background.IsVisible = true;
        AnimateMsgBackground(background, 90);
        dialog.Closed += (_, args) =>
        {
            host.Children.Remove(dialog);
            if (host.Children.Count == 0)
            {
                AnimateMsgBackground(background, 0, () =>
                {
                    background.Background = Brushes.Transparent;
                    background.IsVisible = false;
                });
            }
            closed(args.Result);
        };
        host.Children.Add(dialog);
        dialog.BeginShowAnimation();
    }

    private void ShowInputDialog(
        string title,
        string caption,
        string content,
        string hintText,
        Action<string?> closed,
        bool isWarn = false,
        int maxLength = 1000)
    {
        if (this.FindControl<BlurBorder>("PanMsgBackground") is not { } background ||
            this.FindControl<Grid>("PanMsg") is not { } host)
        {
            closed(null);
            return;
        }

        MyMsgInput dialog = new();
        dialog.Configure(
            title,
            caption,
            content,
            hintText,
            "确定",
            "取消",
            isWarn,
            validateRules: null,
            maxLength);
        host.Children.Clear();
        background.IsVisible = true;
        AnimateMsgBackground(background, 90);
        dialog.Closed += (_, args) =>
        {
            host.Children.Remove(dialog);
            if (host.Children.Count == 0)
            {
                AnimateMsgBackground(background, 0, () =>
                {
                    background.Background = Brushes.Transparent;
                    background.IsVisible = false;
                });
            }
            closed(args.Result);
        };
        host.Children.Add(dialog);
        dialog.BeginShowAnimation();
    }

    private void ShowLoginDialog(MyMsgLogin dialog, Action closed)
    {
        if (this.FindControl<BlurBorder>("PanMsgBackground") is not { } background ||
            this.FindControl<Grid>("PanMsg") is not { } host)
        {
            _launchRight?.AppendLog($"{dialog.Title}：{dialog.Caption}");
            closed();
            return;
        }

        host.Children.Clear();
        background.IsVisible = true;
        AnimateMsgBackground(background, 90);
        dialog.ReopenWebpageRequested += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(dialog.Website))
                OpenExternalUrl(dialog.Website);
        };
        dialog.CopyCodeRequested += async (_, _) =>
        {
            await CopyLoginCodeAsync(dialog.UserCode).ConfigureAwait(true);
        };
        dialog.CancelRequested += (_, _) => closed();
        dialog.DragRequested += (_, e) => BeginMoveDrag(e);
        dialog.Closed += (_, _) =>
        {
            if (host.Children.Count == 0)
            {
                AnimateMsgBackground(background, 0, () =>
                {
                    background.Background = Brushes.Transparent;
                    background.IsVisible = false;
                });
            }
        };
        host.Children.Add(dialog);
    }

    private async Task CopyLoginCodeAsync(string userCode)
    {
        if (string.IsNullOrWhiteSpace(userCode))
            return;

        try
        {
            if (_clipboardWriter is not null)
            {
                await _clipboardWriter(userCode).ConfigureAwait(true);
                return;
            }

            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(userCode).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _launchRight?.AppendLog("复制登录代码失败：" + ex.Message);
        }
    }

    private async Task PrepareLoginDialogAsync(MyMsgLogin dialog)
    {
        // Device codes are short-lived. Repeat both convenience actions for every
        // newly issued code instead of relying on the first login attempt only.
        await CopyLoginCodeAsync(dialog.UserCode).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(dialog.Website))
            OpenExternalUrl(dialog.Website);
    }

    private async Task StartMicrosoftLoginAsync(PageLoginMs page, ILaunchHomeSurface launchPage)
    {
        string clientId = MicrosoftMinecraftAuthService.ResolveClientId();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            page.FinishLogin();
            _launchRight?.AppendLog("缺少 Microsoft 登录配置：PCL_MS_CLIENT_ID。");
            ShowTextDialog(
                "Microsoft 登录配置缺失",
                "缺少 Microsoft 登录配置。请为启动器提供 PCL_MS_CLIENT_ID（Microsoft OAuth 公共客户端 ID）后重试。",
                "知道了");
            return;
        }

        _microsoftLoginCancellation?.Cancel();
        _microsoftLoginCancellation?.Dispose();
        _microsoftLoginCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _microsoftLoginCancellation.Token;
        MyMsgLogin? dialog = null;
        try
        {
            _launchRight?.AppendLog("正在申请 Microsoft 设备登录代码。");
            page.UpdateProgress(0.04d);
            MicrosoftDeviceCodeInfo deviceCode = await _microsoftAuthService
                .RequestDeviceCodeAsync(clientId, cancellationToken)
                .ConfigureAwait(true);
            page.UpdateProgress(0.08d);
            dialog = new MyMsgLogin
            {
                Title = "Microsoft 正版档案登录",
                Caption = FormatMicrosoftDeviceCodeCaption(deviceCode),
                UserCode = deviceCode.UserCode,
                Website = MinecraftLaunchPlanFactory.FirstNonEmpty(deviceCode.VerificationUriComplete, deviceCode.VerificationUri)
            };
            ShowLoginDialog(dialog, () => _microsoftLoginCancellation?.Cancel());
            await PrepareLoginDialogAsync(dialog).ConfigureAwait(true);

            Progress<double> progress = new(value => page.UpdateProgress(value));
            MicrosoftMinecraftLoginResult result = await _microsoftAuthService
                .CompleteDeviceLoginAsync(clientId, deviceCode, progress, cancellationToken)
                .ConfigureAwait(true);
            if (dialog.Parent is not null)
                dialog.CloseLikeWpf();

            LoginProfileInfo profile = new(
                result.Username,
                result.OwnsMinecraft ? "Microsoft 正版" : "Microsoft 账户",
                LaunchLoginProfileKind.Microsoft,
                result.Uuid,
                SvgIcon: "lucide/badge-check",
                SkinAddress: result.SkinAddress,
                AccessToken: result.AccessToken,
                RefreshToken: result.RefreshToken);
            AddOrUpdateLoginProfile(profile);
            _launchLoginSurface.ProfilePage?.SetProfiles(_loginProfiles, profile);
            _launchLoginSurface.ProfileSkinPage?.SetProfile(profile);
            launchPage.SetSelectedProfilePresent(true);
            launchPage.RefreshPage(anim: true);
            SaveProfilesInBackground("保存 Microsoft 正版档案");
            PublishMicrosoftProfile(profile, HostAccountSessionReason.Authenticated);
            _launchRight?.AppendLog($"Microsoft 登录成功，已选中档案 {profile.Username}。");
            ShowTextDialog("登录成功", $"已添加并选中正版档案 {profile.Username}。", "知道了");
        }
        catch (OperationCanceledException)
        {
            _launchRight?.AppendLog("Microsoft 登录已取消。");
        }
        catch (Exception ex)
        {
            if (dialog?.Parent is not null)
                dialog.CloseLikeWpf();
            _launchRight?.AppendLog("Microsoft 登录失败：" + ex.Message);
            ShowTextDialog("Microsoft 登录失败", ex.Message, "知道了");
        }
        finally
        {
            page.FinishLogin();
        }
    }

    private static string FormatMicrosoftDeviceCodeCaption(MicrosoftDeviceCodeInfo deviceCode)
    {
        string website = MinecraftLaunchPlanFactory.FirstNonEmpty(deviceCode.VerificationUri, deviceCode.VerificationUriComplete);
        return string.IsNullOrWhiteSpace(website)
            ? $"请按浏览器页面提示登录 Microsoft 账户。\n\n授权码：{deviceCode.UserCode}"
            : $"请在浏览器中打开 {website}，并按页面提示登录 Microsoft 账户。\n\n授权码：{deviceCode.UserCode}";
    }

    private async Task StartThirdPartyAuthLoginAsync(PageLoginAuth page, AuthLoginRequest request)
    {
        _launchRight?.AppendLog($"正在连接第三方认证服务器：{request.Server}");
        page.UpdateProgress(0.12d);
        try
        {
            ThirdPartyAuthLoginResult result = await _thirdPartyAuthService
                .AuthenticateAsync(
                    new ThirdPartyAuthLoginRequest(
                        request.Server,
                        request.Username,
                        request.Password))
                .ConfigureAwait(true);
            page.UpdateProgress(0.8d);
            string skinAddress = MySkin.ResolveSkinAddress(
                skinAddress: null,
                uuid: result.Uuid,
                authServer: result.AuthServer);
            LoginProfileInfo profile = new(
                result.Username,
                result.AuthServerDisplayName,
                LaunchLoginProfileKind.ThirdParty,
                result.Uuid,
                SvgIcon: "lucide/key-round",
                SkinAddress: string.IsNullOrWhiteSpace(skinAddress) ? null : skinAddress,
                AuthServer: result.AuthServer,
                AccessToken: result.AccessToken,
                RefreshToken: result.RefreshToken,
                ClientToken: result.ClientToken);
            AddOrUpdateLoginProfile(profile);
            _launchLoginSurface.ProfilePage?.SetProfiles(_loginProfiles, profile);
            _launchLoginSurface.ProfileSkinPage?.SetProfile(profile);
            _launchLeft?.SetSelectedProfilePresent(true);
            _launchLeft?.RefreshPage(anim: true);
            SaveProfilesInBackground("保存第三方认证档案");

            // Encrypt password for silent re-auth / refresh when accessToken expires.
            await ThirdPartyCredentialStore.SaveAsync(
                    result.AuthServer,
                    result.Uuid,
                    request.Username,
                    request.Password,
                    result.ClientToken)
                .ConfigureAwait(true);

            _launchRight?.AppendLog($"第三方认证登录成功，已选中档案 {profile.Username}（凭据已加密保存，可用于自动刷新）。");
            ShowTextDialog("登录成功", $"已添加并选中 {profile.Username}。\n\n登录密码已加密保存在本机，启动时可自动刷新会话。", "知道了");
        }
        catch (Exception ex)
        {
            ShowTextDialog("第三方登录失败", ex.Message, "知道了");
            _launchRight?.AppendLog("第三方认证登录失败：" + ex.Message);
        }
        finally
        {
            page.FinishLogin();
        }
    }

    private void OpenAuthAccountPage(string server, bool isRegisterMode)
    {
        if (string.IsNullOrWhiteSpace(server))
        {
            ShowTextDialog("请先填写认证服务器", "填写认证服务器地址后，启动器才能打开对应的注册或找回密码页面。", "知道了");
            return;
        }

        try
        {
            string authServer = ThirdPartyAuthService.NormalizeYggdrasilServer(server);
            string root = authServer;
            const string apiSuffix = "/api/yggdrasil";
            if (root.EndsWith(apiSuffix, StringComparison.OrdinalIgnoreCase))
                root = root[..^apiSuffix.Length];
            OpenExternalUrl(root.TrimEnd('/') + (isRegisterMode ? "/auth/register" : "/auth/forgot"));
        }
        catch (Exception ex)
        {
            ShowTextDialog("认证服务器地址无效", ex.Message, "知道了");
        }
    }

    private void AddOrUpdateLoginProfile(LoginProfileInfo profile)
    {
        // Enforce single N Cloud account per launcher install.
        if (profile.Kind == LaunchLoginProfileKind.NCloud)
            _loginProfiles.RemoveAll(static existing => existing.Kind == LaunchLoginProfileKind.NCloud);

        int existingIndex = _loginProfiles.FindIndex(existing => IsSameProfile(existing, profile));
        if (existingIndex >= 0)
            _loginProfiles.RemoveAt(existingIndex);
        _loginProfiles.Insert(0, profile);
    }

    private void ReplaceLoginProfile(LoginProfileInfo original, LoginProfileInfo updated)
    {
        int existingIndex = _loginProfiles.FindIndex(existing => IsSameProfile(existing, original));
        if (existingIndex >= 0)
            _loginProfiles.RemoveAt(existingIndex);
        _loginProfiles.Insert(0, updated);
    }

    private async Task ImportProfilesAsync(PageLoginProfile page, ILaunchHomeSurface launchPage)
    {
        try
        {
            string? sourcePath = await PickOpenFilePathAsync(
                    "导入账户档案",
                    new FilePickerFileType("JSON")
                    {
                        Patterns = ["*.json"]
                    })
                .ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(sourcePath))
                return;

            using LaunchProfileStore store = new(sourcePath);
            LaunchProfileLoadResult result = await store.LoadAsync().ConfigureAwait(true);
            List<LoginProfileInfo> imported = result.Profiles.Profiles
                .Select(ToLoginProfileInfo)
                .ToList();
            int added = 0;
            int updated = 0;
            foreach (LoginProfileInfo profile in imported)
            {
                int existingIndex = _loginProfiles.FindIndex(existing => IsSameProfile(existing, profile));
                if (existingIndex >= 0)
                {
                    _loginProfiles[existingIndex] = profile;
                    updated++;
                }
                else
                {
                    _loginProfiles.Add(profile);
                    added++;
                }
            }

            page.SetProfiles(_loginProfiles, _loginProfiles.FirstOrDefault());
            launchPage.SetSelectedProfilePresent(_loginProfiles.Count > 0);
            SaveProfilesInBackground("导入账户档案");
            ShowTextDialog("导入完成", $"已导入 {added} 个新档案，更新 {updated} 个已有档案。");
        }
        catch (Exception ex)
        {
            ShowTextDialog("导入失败", "未能导入账户档案。\n\n详细信息：" + ex.Message);
        }
    }

    private async Task ExportProfilesAsync()
    {
        try
        {
            string? targetPath = await PickSaveFilePathAsync(
                    "导出账户档案",
                    $"PCLN-Profiles-{DateTime.Now:yyyyMMdd-HHmmss}.json",
                    new FilePickerFileType("JSON")
                    {
                        Patterns = ["*.json"]
                    })
                .ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(targetPath))
                return;

            using LaunchProfileStore store = new(targetPath);
            await store.SaveAsync(
                    new LaunchProfileSet
                    {
                        Profiles = _loginProfiles.Select(ToLaunchProfile).ToArray()
                    })
                .ConfigureAwait(true);
            string folder = Path.GetDirectoryName(Path.GetFullPath(targetPath))
                ?? DesktopPathHelpers.GetDesktopOrBaseDirectory();
            ShowTextDialog(
                "导出完成",
                "账户档案已导出到：\n" + targetPath,
                primaryButton: "打开文件夹",
                secondaryButton: "知道了",
                primaryAction: () => OpenFolder(folder));
        }
        catch (Exception ex)
        {
            ShowTextDialog("导出失败", "未能导出账户档案。\n\n详细信息：" + ex.Message);
        }
    }

    private async Task LoadProfilesAsync()
    {
        try
        {
            using LaunchProfileStore store = CreateLaunchProfileStore();
            LaunchProfileLoadResult result = await store.LoadAsync().ConfigureAwait(false);
            List<LoginProfileInfo> profiles = result.Profiles.Profiles
                .Select(ToLoginProfileInfo)
                .ToList();
            bool migratedNCloudEndpoint = result.Profiles.Profiles
                .Zip(profiles)
                .Any(pair =>
                    pair.First.Kind == LaunchProfileKind.NCloud &&
                    !string.Equals(
                        pair.First.AuthServer,
                        pair.Second.AuthServer,
                        StringComparison.OrdinalIgnoreCase));
            if (migratedNCloudEndpoint)
            {
                await store.SaveAsync(new LaunchProfileSet
                    {
                        Profiles = profiles.Select(ToLaunchProfile).ToArray()
                    })
                    .ConfigureAwait(false);
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _loginProfiles.Clear();
                _loginProfiles.AddRange(profiles);
                _launchLoginSurface.ProfilePage?.SetProfiles(_loginProfiles);
                _launchLeft?.SetSelectedProfilePresent(_loginProfiles.Count > 0);
                if (result.WasRecovered)
                    _launchRight?.AppendLog($"账户档案配置已重置，损坏文件已备份到：{result.BackupPath}");
                if (migratedNCloudEndpoint)
                    _launchRight?.AppendLog("已自动迁移旧版 N Cloud 认证服务器地址。");
            });

            LoginProfileInfo? microsoftProfile = profiles.FirstOrDefault(static profile =>
                profile.Kind == LaunchLoginProfileKind.Microsoft);
            if (microsoftProfile is not null)
            {
                PublishMicrosoftProfile(
                    microsoftProfile,
                    HostAccountSessionReason.Restored,
                    allowBackgroundOnlineWork: !ShouldSuppressStartupDialogs());
            }
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                _launchRight?.AppendLog("读取账户档案失败：" + ex.Message));
        }
    }

    private static bool IsSameProfile(LoginProfileInfo left, LoginProfileInfo right)
    {
        if (!string.IsNullOrWhiteSpace(left.Uuid) && !string.IsNullOrWhiteSpace(right.Uuid))
        {
            return left.Kind == right.Kind &&
                   string.Equals(left.Uuid, right.Uuid, StringComparison.OrdinalIgnoreCase) &&
                   (!left.UsesYggdrasil ||
                    string.Equals(
                        left.AuthServer,
                        right.AuthServer,
                        StringComparison.OrdinalIgnoreCase));
        }

        return left.Kind == right.Kind &&
               string.Equals(left.Username, right.Username, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.AuthServer, right.AuthServer, StringComparison.OrdinalIgnoreCase);
    }

    private void SaveProfilesInBackground(string action)
    {
        LaunchProfileSet snapshot = new()
        {
            Profiles = _loginProfiles.Select(ToLaunchProfile).ToArray()
        };

        lock (_profileSaveQueueLock)
        {
            Task predecessor = _profileSaveQueue;
            _profileSaveQueue = SaveProfilesAfterAsync(predecessor, snapshot, action);
        }
    }

    private async Task StartNCloudLoginAsync(
        PageLoginNCloud page,
        ILaunchHomeSurface launchPage)
    {
        try
        {
            if (_loginProfiles.Any(static existing => existing.Kind == LaunchLoginProfileKind.NCloud))
            {
                throw new InvalidOperationException(
                    "已存在 N Cloud 账户档案。每个启动器仅允许一个 N Cloud 账户；请先删除现有档案后再登录。");
            }

            await DesktopHost.EnsureOptionalRuntimeReadyAsync().ConfigureAwait(true);
            IHostOnlineMinecraftAccountProvider? provider =
                HostOnlineMinecraftAccountProvider.Current;
            if (provider is null)
            {
                throw new InvalidOperationException(
                    "插件侧车未就绪，无法创建 N Cloud 会话。请稍后重试或重启启动器。");
            }

            if (!provider.IsAuthenticated)
            {
                throw new InvalidOperationException(
                    "当前没有已登录的 PCL N 在线服务账户，请先在「设置 → 在线 → 账户」中连接。");
            }

            _launchRight?.AppendLog("正在创建 N Cloud 在线会话。");
            page.UpdateProgress(0.2d);
            HostOnlineMinecraftSession session = await provider
                .CreateSessionAsync()
                .ConfigureAwait(true);
            page.UpdateProgress(0.55d);

            string authServer = MinecraftLaunchPlanFactory.ResolveNCloudAuthServer(session.AuthServer);
            string? skinAddress = string.IsNullOrWhiteSpace(session.SkinAddress)
                ? null
                : session.SkinAddress.Trim();
            LoginProfileInfo profile = new(
                session.Username,
                "N Cloud 在线账户",
                LaunchLoginProfileKind.NCloud,
                session.Uuid,
                SvgIcon: "lucide/cloud",
                SkinAddress: skinAddress,
                AuthServer: authServer,
                AccessToken: session.AccessToken,
                ClientToken: session.ClientToken);

            // Resolve skin/cape texture URLs from the Yggdrasil session profile.
            MinecraftProfileTextures textures = await MinecraftProfileTextureResolver
                .ResolveAsync(profile)
                .ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(textures.SkinAddress) &&
                !textures.SkinAddress.Contains(
                    "/session/minecraft/profile/",
                    StringComparison.OrdinalIgnoreCase))
            {
                profile = profile with { SkinAddress = textures.SkinAddress };
            }
            else if (string.IsNullOrWhiteSpace(profile.SkinAddress))
            {
                profile = profile with
                {
                    SkinAddress = MySkin.ResolveSkinAddress(
                        skinAddress: null,
                        uuid: profile.Uuid,
                        authServer: profile.AuthServer)
                };
            }

            page.UpdateProgress(0.85d);
            AddOrUpdateLoginProfile(profile);
            _launchLoginSurface.ProfilePage?.SetProfiles(_loginProfiles, profile);
            _launchLoginSurface.ProfileSkinPage?.SetProfile(profile);
            launchPage.SetSelectedProfilePresent(true);
            launchPage.RefreshPage(anim: true);
            SaveProfilesInBackground("保存 N Cloud 在线档案");
            page.UpdateProgress(1d);
            _launchRight?.AppendLog($"N Cloud 登录成功，已选中档案 {profile.Username}。");
            ShowTextDialog(
                "登录成功",
                $"已添加并选中 N Cloud 在线档案 {profile.Username}。",
                "知道了");
        }
        catch (Exception exception)
        {
            _launchRight?.AppendLog("N Cloud 登录失败：" + exception.Message);
            ShowTextDialog("N Cloud 登录失败", exception.Message, "知道了");
        }
        finally
        {
            page.FinishLogin();
        }
    }

    private async Task StartLittleSkinLoginAsync(
        PageLoginLittleSkin page,
        ILaunchHomeSurface launchPage)
    {
        // Official launcher path: Device Authorization Grant + Yggdrasil OAuth APIs
        // https://manual.littlesk.in/advanced/oauth2/device-authorization-grant
        // https://manual.littlesk.in/advanced/api
        LittleSkinOAuthConfiguration configuration;
        try
        {
            configuration = LittleSkinOAuthService.ResolveConfiguration();
        }
        catch (Exception exception)
        {
            page.FinishLogin();
            _launchRight?.AppendLog("LittleSkin OAuth 配置缺失：" + exception.Message);
            ShowTextDialog(
                "LittleSkin OAuth 配置缺失",
                exception.Message +
                "\n\n启动器使用设备代码流（Device Authorization Grant）：" +
                "\n1. 在 littleskin.cn/user/oauth/manage 创建应用" +
                "\n2. 回调 URL 设为 https://open.littleskin.cn/oauth/callback" +
                "\n3. 申请设备代码流白名单（含 Yggdrasil.PlayerProfiles.Read、Yggdrasil.MinecraftToken.Create）" +
                "\n4. 配置环境变量 PCL_LITTLESKIN_CLIENT_ID",
                "知道了");
            return;
        }

        _littleSkinLoginCancellation?.Cancel();
        _littleSkinLoginCancellation?.Dispose();
        _littleSkinLoginCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _littleSkinLoginCancellation.Token;
        MyMsgLogin? dialog = null;
        try
        {
            _launchRight?.AppendLog("正在申请 LittleSkin 设备授权码（OAuth 2 设备代码流）。");
            page.UpdateProgress(0.04d);
            LittleSkinDeviceCodeInfo deviceCode = await _littleSkinOAuthService
                .RequestDeviceCodeAsync(configuration, cancellationToken)
                .ConfigureAwait(true);
            page.UpdateProgress(0.08d);

            string website = string.IsNullOrWhiteSpace(deviceCode.VerificationUriComplete)
                ? deviceCode.VerificationUri
                : deviceCode.VerificationUriComplete;
            dialog = new MyMsgLogin
            {
                Title = "LittleSkin OAuth 登录",
                Caption =
                    $"请在浏览器中打开授权页，并输入授权码完成登录。\n\n" +
                    $"授权码：{deviceCode.UserCode}\n\n" +
                    "授权后启动器会自动继续；不会接触你的 LittleSkin 密码。",
                UserCode = deviceCode.UserCode,
                Website = website
            };
            ShowLoginDialog(dialog, () => _littleSkinLoginCancellation?.Cancel());
            await PrepareLoginDialogAsync(dialog).ConfigureAwait(true);
            _launchRight?.AppendLog("正在等待 LittleSkin 设备授权完成。");

            Progress<double> progress = new(value => page.UpdateProgress(0.08d + value * 0.3d));
            LittleSkinOAuthTokens tokens = await _littleSkinOAuthService
                .WaitForDeviceAuthorizationAsync(
                    configuration,
                    deviceCode,
                    progress,
                    cancellationToken)
                .ConfigureAwait(true);
            page.UpdateProgress(0.42d);

            // API: GET …/sessionserver/session/minecraft/profile (Yggdrasil.PlayerProfiles.Read)
            IReadOnlyList<LittleSkinProfile> profiles = await _littleSkinOAuthService
                .GetProfilesAsync(tokens.AccessToken, cancellationToken)
                .ConfigureAwait(true);
            if (profiles.Count == 0)
                throw new InvalidOperationException("LittleSkin 账户下没有可用于启动游戏的角色。");

            if (dialog.Parent is not null)
                dialog.CloseLikeWpf();
            dialog = null;

            int? selectedIndex = await SelectLittleSkinProfileAsync(profiles, cancellationToken)
                .ConfigureAwait(true);
            if (selectedIndex is not int index)
            {
                _launchRight?.AppendLog("LittleSkin 登录已取消：未选择角色。");
                return;
            }

            LittleSkinProfile selected = profiles[index];
            page.UpdateProgress(0.62d);
            // API: POST …/authserver/oauth  {"uuid":"<undashed>"} (Yggdrasil.MinecraftToken.Create)
            LittleSkinMinecraftSession session = await _littleSkinOAuthService
                .CreateMinecraftSessionAsync(
                    tokens.AccessToken,
                    selected.Uuid,
                    cancellationToken)
                .ConfigureAwait(true);
            page.UpdateProgress(0.84d);
            string skinAddress = MySkin.ResolveSkinAddress(
                skinAddress: null,
                uuid: session.Uuid,
                authServer: LittleSkinOAuthService.YggdrasilServer);
            LoginProfileInfo profile = new(
                session.Username,
                "LittleSkin OAuth",
                LaunchLoginProfileKind.LittleSkin,
                session.Uuid,
                SvgIcon: "lucide/boxes",
                SkinAddress: string.IsNullOrWhiteSpace(skinAddress) ? null : skinAddress,
                AuthServer: LittleSkinOAuthService.YggdrasilServer,
                AccessToken: session.AccessToken,
                RefreshToken: tokens.RefreshToken,
                ClientToken: session.ClientToken,
                ProviderAccessToken: tokens.AccessToken,
                ProviderTokenExpiresAtUnix: tokens.ExpiresAt.ToUnixTimeSeconds());
            AddOrUpdateLoginProfile(profile);
            _launchLoginSurface.ProfilePage?.SetProfiles(_loginProfiles, profile);
            _launchLoginSurface.ProfileSkinPage?.SetProfile(profile);
            launchPage.SetSelectedProfilePresent(true);
            launchPage.RefreshPage(anim: true);
            SaveProfilesInBackground("保存 LittleSkin OAuth 档案");
            page.UpdateProgress(1d);
            _launchRight?.AppendLog($"LittleSkin 登录成功，已选中角色 {profile.Username}。");
            ShowTextDialog(
                "登录成功",
                $"已添加并选中 LittleSkin 角色 {profile.Username}。\n\n" +
                "现在可以在外观页直接使用 LittleSkin 衣柜中的皮肤与披风。",
                "知道了");
        }
        catch (OperationCanceledException)
        {
            _launchRight?.AppendLog("LittleSkin OAuth 登录已取消。");
        }
        catch (Exception exception)
        {
            if (dialog?.Parent is not null)
                dialog.CloseLikeWpf();
            string message = exception.Message;
            bool invalidClient =
                message.Contains("invalid_client", StringComparison.OrdinalIgnoreCase) ||
                message.Contains(
                    LittleSkinOAuthService.InvalidClientUserMessage,
                    StringComparison.Ordinal);
            if (invalidClient)
                message = LittleSkinOAuthService.InvalidClientUserMessage;

            _launchRight?.AppendLog("LittleSkin OAuth 登录失败：" + message);
            ShowTextDialog(
                invalidClient ? "LittleSkin 暂不可用" : "LittleSkin 登录失败",
                message,
                "知道了");
        }
        finally
        {
            page.FinishLogin();
        }
    }

    private async Task<int?> SelectLittleSkinProfileAsync(
        IReadOnlyList<LittleSkinProfile> profiles,
        CancellationToken cancellationToken)
    {
        if (profiles.Count == 1)
            return 0;

        TaskCompletionSource<int?> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        MyMsgSelect dialog = new();
        dialog.Configure(
            "选择 LittleSkin 角色",
            profiles.Select(profile => CreateProfileTypeItem(
                    profile.Username,
                    profile.Uuid,
                    "lucide/user-round-check"))
                .ToArray());
        ShowSelectionDialog(dialog, selected => completion.TrySetResult(selected));
        using CancellationTokenRegistration registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<int?>)state!).TrySetCanceled(),
            completion);
        return await completion.Task.ConfigureAwait(true);
    }

    private async Task<LoginProfileInfo> RefreshLittleSkinLaunchProfileAsync(
        LoginProfileInfo profile,
        CancellationToken cancellationToken,
        Action<string>? status = null)
    {
        void Report(string message) => status?.Invoke(message);

        if (string.IsNullOrWhiteSpace(profile.AuthServer))
            throw new InvalidOperationException("LittleSkin 档案缺少 Yggdrasil 服务器地址，请重新登录。");

        if (!string.IsNullOrWhiteSpace(profile.AccessToken))
        {
            Report("正在校验 LittleSkin Minecraft 会话…");
            bool valid = await _thirdPartyAuthService
                .ValidateAsync(
                    profile.AuthServer,
                    profile.AccessToken,
                    string.IsNullOrWhiteSpace(profile.ClientToken) ? null : profile.ClientToken,
                    cancellationToken)
                .ConfigureAwait(false);
            if (valid)
            {
                Report("LittleSkin Minecraft 会话仍然有效。");
                return profile;
            }
        }

        Report("正在通过 LittleSkin OAuth 重新签发 Minecraft 会话…");
        (LoginProfileInfo refreshedProfile, LittleSkinMinecraftSession session) =
            await InvokeLittleSkinOAuthAsync(
                    profile,
                    (accessToken, token) => _littleSkinOAuthService.CreateMinecraftSessionAsync(
                        accessToken,
                        profile.Uuid,
                        token),
                    cancellationToken)
                .ConfigureAwait(false);
        Report("LittleSkin Minecraft 会话已刷新。");
        return refreshedProfile with
        {
            Username = session.Username,
            Uuid = session.Uuid,
            AccessToken = session.AccessToken,
            ClientToken = session.ClientToken,
            AuthServer = LittleSkinOAuthService.YggdrasilServer,
            Info = "LittleSkin OAuth"
        };
    }

    private async Task<(LoginProfileInfo Profile, T Result)> InvokeLittleSkinOAuthAsync<T>(
        LoginProfileInfo profile,
        Func<string, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(operation);
        LoginProfileInfo current = profile;
        bool providerTokenExpired =
            current.ProviderTokenExpiresAtUnix > 0 &&
            current.ProviderTokenExpiresAtUnix <=
            DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds();
        if (string.IsNullOrWhiteSpace(current.ProviderAccessToken) || providerTokenExpired)
        {
            current = await RefreshLittleSkinProviderTokenAsync(current, cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            T result = await operation(current.ProviderAccessToken, cancellationToken)
                .ConfigureAwait(false);
            return (current, result);
        }
        catch (HttpRequestException exception) when (
            exception.StatusCode is System.Net.HttpStatusCode.Unauthorized or
                System.Net.HttpStatusCode.Forbidden)
        {
            current = await RefreshLittleSkinProviderTokenAsync(current, cancellationToken)
                .ConfigureAwait(false);
            T result = await operation(current.ProviderAccessToken, cancellationToken)
                .ConfigureAwait(false);
            return (current, result);
        }
    }

    private async Task<LoginProfileInfo> RefreshLittleSkinProviderTokenAsync(
        LoginProfileInfo profile,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profile.RefreshToken))
        {
            throw new InvalidOperationException(
                "LittleSkin OAuth 刷新令牌缺失，请删除此档案并重新授权。");
        }

        LittleSkinOAuthConfiguration configuration = LittleSkinOAuthService.ResolveConfiguration();
        LittleSkinOAuthTokens tokens = await _littleSkinOAuthService
            .RefreshOAuthTokenAsync(configuration, profile.RefreshToken, cancellationToken)
            .ConfigureAwait(false);
        return profile with
        {
            ProviderAccessToken = tokens.AccessToken,
            ProviderTokenExpiresAtUnix = tokens.ExpiresAt.ToUnixTimeSeconds(),
            RefreshToken = tokens.RefreshToken
        };
    }

    private static void PublishMicrosoftProfile(
        LoginProfileInfo profile,
        HostAccountSessionReason reason,
        bool allowBackgroundOnlineWork = true)
    {
        HostAccountSessionEvents.PublishMicrosoftProfile(
            new HostMicrosoftProfileSnapshot(profile.Username, profile.Uuid, profile.SkinAddress),
            reason,
            allowBackgroundOnlineWork);
    }

    private async Task SaveProfilesAfterAsync(
        Task predecessor,
        LaunchProfileSet snapshot,
        string action)
    {
        try
        {
            await predecessor.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A failed notification dispatch must not poison the ordered save queue.
            PortableLog.Warn(ex, "AccountProfile", "前一个账户档案保存任务异常结束，继续保存最新快照。");
        }

        try
        {
            using LaunchProfileStore store = CreateLaunchProfileStore();
            await store.SaveAsync(snapshot).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PortableLog.Error(ex, "AccountProfile", action + "失败。");
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    _launchRight?.AppendLog(action + "失败：" + ex.Message));
            }
            catch (Exception dispatchException)
            {
                PortableLog.Debug(dispatchException, "AccountProfile", "窗口关闭后无法显示账户档案保存失败提示。");
            }
        }
    }

    private bool HasPendingProfileSaves()
    {
        lock (_profileSaveQueueLock)
            return !_profileSaveQueue.IsCompleted;
    }

    private async Task DrainProfileSaveQueueAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task pending;
            lock (_profileSaveQueueLock)
                pending = _profileSaveQueue;

            await pending.WaitAsync(cancellationToken).ConfigureAwait(true);

            lock (_profileSaveQueueLock)
            {
                if (ReferenceEquals(pending, _profileSaveQueue))
                    return;
            }
        }
    }

    private static LaunchProfileStore CreateLaunchProfileStore() =>
        new(CreateLaunchProfilePath());

    private static string CreateLaunchProfilePath()
    {
        string? overridePath = Environment.GetEnvironmentVariable("PCLN_LAUNCH_PROFILES_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
            return overridePath;

        return Path.Combine(PCL.Desktop.Paths.LauncherPathLayout.ResolveDataDirectory(), "launch-profiles.json");
    }

    private static LoginProfileInfo ToLoginProfileInfo(LaunchProfile profile)
    {
        LaunchLoginProfileKind kind = profile.Kind switch
        {
            LaunchProfileKind.Microsoft => LaunchLoginProfileKind.Microsoft,
            LaunchProfileKind.LittleSkin => LaunchLoginProfileKind.LittleSkin,
            LaunchProfileKind.NCloud => LaunchLoginProfileKind.NCloud,
            LaunchProfileKind.ThirdParty => LaunchLoginProfileKind.ThirdParty,
            _ => LaunchLoginProfileKind.Offline
        };
        // Migrate empty / legacy Supabase Edge N Cloud roots to the current API so
        // Authlib skinDomains and sessionserver match the live Cloudflare backend.
        string authServer = kind == LaunchLoginProfileKind.NCloud
            ? MinecraftLaunchPlanFactory.ResolveNCloudAuthServer(profile.AuthServer)
            : profile.AuthServer;
        return new LoginProfileInfo(
            profile.Username,
            profile.Info,
            kind,
            profile.Uuid,
            profile.Logo,
            profile.SvgIcon,
            profile.SkinAddress,
            authServer,
            profile.AccessToken,
            profile.RefreshToken,
            profile.ClientToken,
            profile.ProviderAccessToken,
            profile.ProviderTokenExpiresAtUnix);
    }

    private static LaunchProfile ToLaunchProfile(LoginProfileInfo profile) =>
        new()
        {
            Username = profile.Username,
            Info = profile.Info,
            Kind = profile.Kind switch
            {
                LaunchLoginProfileKind.Microsoft => LaunchProfileKind.Microsoft,
                LaunchLoginProfileKind.LittleSkin => LaunchProfileKind.LittleSkin,
                LaunchLoginProfileKind.NCloud => LaunchProfileKind.NCloud,
                LaunchLoginProfileKind.ThirdParty => LaunchProfileKind.ThirdParty,
                _ => LaunchProfileKind.Offline
            },
            Uuid = profile.Uuid,
            Logo = profile.Logo,
            SvgIcon = profile.SvgIcon,
            SkinAddress = profile.SkinAddress,
            AuthServer = profile.AuthServer,
            AccessToken = profile.AccessToken,
            RefreshToken = profile.RefreshToken,
            ProviderAccessToken = profile.ProviderAccessToken,
            ProviderTokenExpiresAtUnix = profile.ProviderTokenExpiresAtUnix,
            ClientToken = profile.ClientToken
        };

    private static string? NormalizeAuthServerUrl(string authServer)
    {
        if (string.IsNullOrWhiteSpace(authServer))
            return null;

        string trimmed = authServer.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal))
            trimmed = "https://" + trimmed;

        return Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.ToString()
            : null;
    }

    private static bool TryCreateHttpUri(string value, out Uri? uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return true;
        }

        uri = null;
        return false;
    }

}

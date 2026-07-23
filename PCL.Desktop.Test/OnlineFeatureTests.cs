// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json.Nodes;
using PCL.Application.Settings;
using PCL.Desktop.Features.Community;
using PCL.Desktop.Features.Launching.Views;
using PCL.Desktop.Features.Online;
using PCL.Desktop.Features.Settings.Views;

namespace PCL.Desktop.Test;

[TestClass]
[DoNotParallelize]
public sealed class OnlineFeatureTests
{
    [TestMethod]
    public void RuntimeHostBuildsSafeSectionedSnapshotFromAvaloniaState()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcln-online-host-test-" + Guid.NewGuid().ToString("N"));
        string settingsPath = Path.Combine(root, "launcher-settings.json");
        string? previousSettingsPath = Environment.GetEnvironmentVariable("PCLN_LAUNCHER_SETTINGS_PATH");
        Environment.SetEnvironmentVariable("PCLN_LAUNCHER_SETTINGS_PATH", settingsPath);
        try
        {
            LauncherSettings settings = new();
            settings.SetBooleanOption("UiBlur", true);
            settings.SetIntegerOption("SystemUpdateChannel", 2);
            settings.SetTextOption("LaunchArgumentTitle", "Cloud title");
            settings.SetTextOption("LaunchAdvanceRun", "must-not-sync --token secret");
            LauncherSettingsPageBinder.SaveSettings(settings, notify: false);

            CommunityFavoritesStore favorites = new(Path.Combine(root, "favorites.json"));
            CommunityResourceEntry entry = new(
                "AANobbMI",
                "sodium",
                "Sodium",
                "Fast renderer",
                "mod",
                null,
                42,
                null)
            {
                Source = CommunityResourceSource.Modrinth
            };
            favorites.Toggle(entry, CommunityResourceCategory.Mod);

            DesktopOnlineRuntimeHost host = new(favorites);
            host.HydrateMicrosoftProfile(
                new LoginProfileInfo(
                    "Player",
                    "Microsoft 正版",
                    LaunchLoginProfileKind.Microsoft,
                    "01234567-89ab-cdef-0123-456789abcdef",
                    AccessToken: "local-access-token",
                    RefreshToken: "local-refresh-token"),
                explicitLogin: true);

            Dictionary<string, JsonObject> snapshot = host.BuildSnapshot();

            CollectionAssert.IsSubsetOf(
                new[] { "account", "favorites", "uiPreferences", "launchPreferences", "updatePreferences" },
                snapshot.Keys.ToArray());
            Assert.AreEqual("0123456789abcdef0123456789abcdef", snapshot["account"]["msid"]?.GetValue<string>());
            Assert.IsNull(snapshot["account"]["access_token"]);
            Assert.IsNull(snapshot["account"]["refresh_token"]);
            Assert.AreEqual(true, snapshot["uiPreferences"]["booleans"]?["UiBlur"]?.GetValue<bool>());
            Assert.AreEqual("Cloud title", snapshot["launchPreferences"]["texts"]?["LaunchArgumentTitle"]?.GetValue<string>());
            Assert.IsNull(snapshot["launchPreferences"]["texts"]?["LaunchAdvanceRun"]);
            Assert.AreEqual(2, snapshot["updatePreferences"]["integers"]?["SystemUpdateChannel"]?.GetValue<int>());
            Assert.AreEqual(1, snapshot["favorites"]["items"]?.AsArray().Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PCLN_LAUNCHER_SETTINGS_PATH", previousSettingsPath);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}

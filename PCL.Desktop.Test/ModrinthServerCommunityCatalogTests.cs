// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using PCL.Desktop.Features.Community;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class ModrinthServerCommunityCatalogTests
{
    [TestMethod]
    public void ParseEntry_PreservesServerAndAssociatedModpackMetadata()
    {
        using JsonDocument hit = JsonDocument.Parse(
            """
            {
              "project_id": "search-id",
              "slug": "search-slug",
              "title": "Search title",
              "description": "Search description",
              "downloads": 123,
              "versions": ["1.21.1"]
            }
            """);
        using JsonDocument detail = JsonDocument.Parse(
            """
            {
              "id": "server-id",
              "slug": "aero-smp",
              "name": "Aero SMP",
              "summary": "Server summary",
              "icon_url": "https://cdn.example/icon.png",
              "game_versions": ["1.21.1", "1.21.1"],
              "minecraft_java_server": {
                "address": "play.example.invalid",
                "content": {
                  "kind": "modpack",
                  "project_id": "pack-project",
                  "version_id": "pack-version",
                  "project_name": "Aero SMP Pack",
                  "project_icon": "https://cdn.example/pack.png"
                },
                "ping": { "data": { "players_online": 42, "players_max": 100 } }
              }
            }
            """);

        CommunityResourceEntry? entry = ModrinthServerCommunityCatalog.ParseEntry(
            hit.RootElement,
            detail.RootElement);

        Assert.IsNotNull(entry);
        Assert.AreEqual("server-id", entry.ProjectId);
        Assert.AreEqual("https://modrinth.com/server/aero-smp", entry.WebsiteUrl);
        Assert.IsNotNull(entry.Server);
        Assert.AreEqual("play.example.invalid", entry.Server.Address);
        Assert.AreEqual(42, entry.Server.PlayersOnline);
        Assert.AreEqual(100, entry.Server.PlayersMax);
        Assert.AreEqual("pack-project", entry.Server.ContentProjectId);
        Assert.AreEqual("pack-version", entry.Server.ContentVersionId);
        CollectionAssert.AreEqual(new[] { "1.21.1" }, entry.Server.GameVersions.ToArray());
    }
}

// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Text;
using PCL.Desktop.Features.Community;
using PCL.Desktop.Features.Launching.Views;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class CommunityFeatureTests
{
    [TestMethod]
    public void McModIndex_ShouldParseEmbeddedDatabaseAndResolveKnownSlug()
    {
        McModIndex index = McModIndex.Current;
        Assert.IsTrue(index.Count > 1000, $"Unexpected MC百科 index size: {index.Count}");

        McModIndexEntry? entry = index.FindBySlug(CommunityResourceSource.CurseForge, "advanced-solar-panels");
        Assert.IsNotNull(entry);
        Assert.IsTrue(entry.WikiId > 0);
        Assert.IsFalse(string.IsNullOrWhiteSpace(entry.ChineseName));
        Assert.IsTrue(index.SearchChinese(entry.ChineseName[..Math.Min(2, entry.ChineseName.Length)]).Count > 0);
    }

    [TestMethod]
    public void McModDecoration_ShouldExposeChineseTitleAndExactWikiUrl()
    {
        McModIndexEntry mapping = new(42, "测试模组", "test-mod", "test-modrinth");
        McModIndex index = new([mapping]);
        CommunityResourceEntry original = new("id", "test-mod", "Test Mod", "Description", "mod", null, 0, null)
        {
            Source = CommunityResourceSource.CurseForge
        };

        CommunityResourceEntry decorated = index.Decorate(original);
        Assert.AreEqual("测试模组", decorated.ChineseName);
        Assert.AreEqual("Test Mod", decorated.OriginalTitle);
        Assert.AreEqual("https://www.mcmod.cn/class/42.html", decorated.McModUrl);
    }

    [TestMethod]
    public async Task CurseForgeCatalog_ShouldUsePopularityAndRankExactMatchesFirst()
    {
        HttpRequestMessage? captured = null;
        using HttpClient client = new(new DelegateHandler(request =>
        {
            captured = request;
            return JsonResponse(
                """
                { "data": [
                  { "id": 1, "name": "JEI Addons", "slug": "jei-addons", "downloadCount": 9000 },
                  { "id": 2, "name": "Just Enough Items", "slug": "jei", "downloadCount": 8000 },
                  { "id": 3, "name": "JEI", "slug": "other", "downloadCount": 10 }
                ] }
                """);
        }));
        using CurseForgeCommunityResourceCatalog catalog = new(client, "test-key");

        IReadOnlyList<CommunityResourceEntry> entries = await catalog.SearchAsync(
            CommunityResourceCategory.Mod,
            "jei",
            new CommunitySearchOptions(CommunityResourceSort.Relevance, Source: CommunityResourceSource.CurseForge));

        Assert.AreEqual("JEI", entries[0].Title);
        Assert.AreEqual("jei", entries[1].Slug);
        StringAssert.Contains(Uri.UnescapeDataString(captured!.RequestUri!.Query), "sortField=2");
    }

    [TestMethod]
    public async Task ModrinthDependencies_ShouldDisplayNamesAndResolveBeforeRootDownload()
    {
        using HttpClient client = new(new DelegateHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                "/v2/project/root/version" => JsonResponse(
                    """
                    [{
                      "id": "root-version",
                      "name": "Root 1.0",
                      "version_number": "1.0",
                      "date_published": "2026-07-01T00:00:00Z",
                      "game_versions": ["1.21.1"],
                      "loaders": ["fabric"],
                      "dependencies": [{
                        "project_id": "fabric-api",
                        "version_id": "fabric-version",
                        "dependency_type": "required"
                      }],
                      "files": [{
                        "filename": "root.jar",
                        "url": "https://cdn.modrinth.com/root.jar",
                        "size": 20
                      }]
                    }]
                    """),
                "/v2/project/fabric-api" => JsonResponse(
                    """
                    {
                      "id": "fabric-api",
                      "slug": "fabric-api",
                      "title": "Fabric API",
                      "description": "Required API",
                      "project_type": "mod",
                      "downloads": 100,
                      "updated": "2026-07-01T00:00:00Z"
                    }
                    """),
                "/v2/project/fabric-api/version" => JsonResponse(
                    """
                    [{
                      "id": "fabric-version",
                      "name": "Fabric API 1.0",
                      "version_number": "1.0",
                      "date_published": "2026-06-01T00:00:00Z",
                      "game_versions": ["1.21.1"],
                      "loaders": ["fabric"],
                      "dependencies": [],
                      "files": [{
                        "filename": "fabric-api.jar",
                        "url": "https://cdn.modrinth.com/fabric-api.jar",
                        "size": 10
                      }]
                    }]
                    """),
                _ => throw new AssertFailedException("Unexpected request: " + request.RequestUri)
            };
        }));
        using ModrinthCommunityResourceCatalog catalog = new(client);
        CommunityResourceEntry root = new("root", "root", "Root Mod", string.Empty, "mod", null, 0, null);
        CommunitySearchOptions options = new(GameVersion: "1.21.1", Loader: "fabric");

        IReadOnlyList<CommunityResourceVersion> versions = await catalog.GetVersionsAsync(root, options);
        IReadOnlyList<CommunityResourceVersion> enriched = await CommunityResourceDependencyResolver
            .EnrichNamesAsync(catalog, versions);
        IReadOnlyList<CommunityResourceDownloadPlanItem> plan = await CommunityResourceDependencyResolver
            .ResolveRequiredDownloadsAsync(catalog, root, versions.Single(), versions.Single().Files.Single(), options);

        CommunityResourceDependency dependency = enriched.Single().Dependencies.Single();
        Assert.AreEqual(CommunityResourceDependencyType.Required, dependency.Type);
        Assert.AreEqual("Fabric API", dependency.DisplayName);
        Assert.AreEqual(2, plan.Count);
        Assert.IsTrue(plan[0].IsDependency);
        Assert.AreEqual("Fabric API", plan[0].Entry.Title);
        Assert.IsFalse(plan[1].IsDependency);
        Assert.AreEqual("Root Mod", plan[1].Entry.Title);
    }

    [TestMethod]
    public async Task ModrinthDependencies_VersionOnlyReferenceResolvesProjectAndFile()
    {
        using HttpClient client = new(new DelegateHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                "/v2/project/root/version" => JsonResponse(
                    """
                    [{
                      "id": "root-version",
                      "name": "Root 1.0",
                      "version_number": "1.0",
                      "game_versions": ["1.21.1"],
                      "loaders": ["fabric"],
                      "dependencies": [{
                        "version_id": "dependency-version",
                        "file_name": "dependency.jar",
                        "dependency_type": "required"
                      }],
                      "files": [{
                        "filename": "root.jar",
                        "url": "https://cdn.modrinth.com/root.jar",
                        "size": 20
                      }]
                    }]
                    """),
                "/v2/version/dependency-version" => JsonResponse(
                    """
                    {
                      "id": "dependency-version",
                      "project_id": "dependency-project",
                      "name": "Dependency 1.0",
                      "version_number": "1.0",
                      "game_versions": ["1.21.1"],
                      "loaders": ["fabric"],
                      "dependencies": [],
                      "files": [{
                        "filename": "dependency.jar",
                        "url": "https://cdn.modrinth.com/dependency.jar",
                        "size": 10
                      }]
                    }
                    """),
                "/v2/project/dependency-project" => JsonResponse(
                    """
                    {
                      "id": "dependency-project",
                      "slug": "dependency-project",
                      "title": "Resolved Dependency",
                      "description": "Required dependency",
                      "project_type": "mod",
                      "downloads": 100
                    }
                    """),
                _ => throw new AssertFailedException("Unexpected request: " + request.RequestUri)
            };
        }));
        using ModrinthCommunityResourceCatalog catalog = new(client);
        CommunityResourceEntry root = new("root", "root", "Root Mod", string.Empty, "mod", null, 0, null);
        CommunitySearchOptions options = new(GameVersion: "1.21.1", Loader: "fabric");

        IReadOnlyList<CommunityResourceVersion> versions = await catalog.GetVersionsAsync(root, options);
        IReadOnlyList<CommunityResourceVersion> enriched = await CommunityResourceDependencyResolver
            .EnrichNamesAsync(catalog, versions);
        IReadOnlyList<CommunityResourceDownloadPlanItem> plan = await CommunityResourceDependencyResolver
            .ResolveRequiredDownloadsAsync(catalog, root, versions.Single(), versions.Single().Files.Single(), options);

        Assert.AreEqual("Resolved Dependency", enriched.Single().Dependencies.Single().DisplayName);
        Assert.AreEqual(2, plan.Count);
        Assert.AreEqual("dependency-project", plan[0].Entry.ProjectId);
        Assert.AreEqual("dependency-version", plan[0].Version.VersionId);
        Assert.AreEqual("dependency.jar", plan[0].File.FileName);
        Assert.IsFalse(plan[1].IsDependency);
    }

    [TestMethod]
    public async Task VersionInspector_ShouldPreferClientVersionForStandaloneLoaderProfile()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcln-version-test-" + Guid.NewGuid().ToString("N"));
        string jsonPath = Path.Combine(root, "26.2-Fabric_0.19.3.json");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(
                jsonPath,
                """
                {
                  "id": "26.2-Fabric_0.19.3",
                  "clientVersion": "26.2",
                  "libraries": [{ "name": "net.fabricmc:fabric-loader:0.19.3" }]
                }
                """);
            LaunchInstanceInfo instance = new("26.2-Fabric_0.19.3", jsonPath, root);

            CommunitySearchOptions options = CommunityInstanceCompatibility.Apply(
                new CommunitySearchOptions(),
                CommunityResourceCategory.Mod,
                instance);
            HttpRequestMessage? captured = null;
            using HttpClient client = new(new DelegateHandler(request =>
            {
                captured = request;
                return JsonResponse(
                    """
                    [{
                      "id": "fabric-api-26.2",
                      "name": "[26.2] Fabric API 0.154.2+26.2",
                      "files": [{
                        "filename": "fabric-api-0.154.2+26.2.jar",
                        "url": "https://cdn.modrinth.com/fabric-api-26.2.jar",
                        "size": 123,
                        "primary": true
                      }]
                    }]
                    """);
            }));
            using ModrinthCommunityResourceCatalog catalog = new(client);
            CommunityResourceEntry entry = new(
                "P7dR8mSH",
                "fabric-api",
                "Fabric API",
                string.Empty,
                "mod",
                null,
                0,
                null);

            CommunityResourceDownloadFile? file = await catalog.ResolveDownloadAsync(entry, options);

            Assert.AreEqual("26.2", options.GameVersion);
            Assert.AreEqual("fabric", options.Loader);
            Assert.AreEqual("fabric-api-0.154.2+26.2.jar", file?.FileName);
            string query = Uri.UnescapeDataString(captured!.RequestUri!.Query);
            StringAssert.Contains(query, "game_versions=[\"26.2\"]");
            StringAssert.Contains(query, "loaders=[\"fabric\"]");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CurseForgeCatalog_ShouldAuthenticateFilterAndParseProjects()
    {
        HttpRequestMessage? captured = null;
        using HttpClient client = new(new DelegateHandler(request =>
        {
            captured = request;
            return JsonResponse(
                """
                {
                  "data": [{
                    "id": 238222,
                    "name": "Just Enough Items",
                    "slug": "jei",
                    "summary": "Item and recipe viewing mod",
                    "downloadCount": 456789,
                    "dateModified": "2026-06-01T00:00:00Z",
                    "logo": { "thumbnailUrl": "https://media.forgecdn.net/jei.png" },
                    "links": { "websiteUrl": "https://www.curseforge.com/minecraft/mc-mods/jei" }
                  }]
                }
                """);
        }));
        using CurseForgeCommunityResourceCatalog catalog = new(client, "test-key");

        IReadOnlyList<CommunityResourceEntry> entries = await catalog.SearchAsync(
            CommunityResourceCategory.Mod,
            "jei",
            new CommunitySearchOptions(
                CommunityResourceSort.Downloads,
                GameVersion: "1.20.1",
                Loader: "fabric",
                Source: CommunityResourceSource.CurseForge));

        CommunityResourceEntry entry = entries.Single();
        Assert.AreEqual(CommunityResourceSource.CurseForge, entry.Source);
        Assert.AreEqual("238222", entry.ProjectId);
        Assert.AreEqual("https://www.curseforge.com/minecraft/mc-mods/jei", entry.WebsiteUrl);
        Assert.AreEqual("test-key", captured!.Headers.GetValues("x-api-key").Single());
        string query = Uri.UnescapeDataString(captured.RequestUri!.Query);
        StringAssert.Contains(query, "classId=6");
        StringAssert.Contains(query, "modLoaderType=4");
        StringAssert.Contains(query, "gameVersion=1.20.1");
        StringAssert.Contains(query, "sortField=6");
        StringAssert.Contains(query, "pageSize=50");
        Assert.IsFalse(query.Contains("categoryId=", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CurseForgeCatalog_ShouldPassCategoryIdFromDualTag()
    {
        HttpRequestMessage? captured = null;
        using HttpClient client = new(new DelegateHandler(request =>
        {
            captured = request;
            return JsonResponse("""{ "data": [] }""");
        }));
        using CurseForgeCommunityResourceCatalog catalog = new(client, "test-key");

        await catalog.SearchAsync(
            CommunityResourceCategory.Mod,
            "jei",
            new CommunitySearchOptions(Tag: "412/technology", Source: CommunityResourceSource.CurseForge));

        string query = Uri.UnescapeDataString(captured!.RequestUri!.Query);
        StringAssert.Contains(query, "categoryId=412");
    }

    [TestMethod]
    public async Task CurseForgeCatalog_ShouldOmitCategoryIdWhenTagHasNoCurseHalf()
    {
        HttpRequestMessage? captured = null;
        using HttpClient client = new(new DelegateHandler(request =>
        {
            captured = request;
            return JsonResponse("""{ "data": [] }""");
        }));
        using CurseForgeCommunityResourceCatalog catalog = new(client, "test-key");

        await catalog.SearchAsync(
            CommunityResourceCategory.Mod,
            "jei",
            new CommunitySearchOptions(Tag: "/technology", Source: CommunityResourceSource.CurseForge));

        string query = Uri.UnescapeDataString(captured!.RequestUri!.Query);
        Assert.IsFalse(query.Contains("categoryId=", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CurseForgeCatalog_ShouldUseWorldClassAndWorldWebsiteFallback()
    {
        HttpRequestMessage? captured = null;
        using HttpClient client = new(new DelegateHandler(request =>
        {
            captured = request;
            return JsonResponse(
                """
                {
                  "data": [{
                    "id": 123,
                    "name": "Sky World",
                    "slug": "sky-world",
                    "summary": "A world",
                    "downloadCount": 10,
                    "dateModified": "2026-06-01T00:00:00Z"
                  }]
                }
                """);
        }));
        using CurseForgeCommunityResourceCatalog catalog = new(client, "test-key");

        CommunityResourceEntry entry = (await catalog.SearchAsync(
            CommunityResourceCategory.World,
            string.Empty,
            new CommunitySearchOptions(Source: CommunityResourceSource.CurseForge))).Single();

        StringAssert.Contains(captured!.RequestUri!.Query, "classId=17");
        StringAssert.Contains(captured.RequestUri.Query, "pageSize=50");
        Assert.AreEqual("world", entry.ProjectType);
        Assert.AreEqual("https://www.curseforge.com/minecraft/worlds/sky-world", entry.WebsiteUrl);
    }

    [TestMethod]
    public async Task CurseForgeCatalog_ShouldBuildCdnUrlWhenDownloadUrlIsMissing()
    {
        HttpRequestMessage? captured = null;
        using HttpClient client = new(new DelegateHandler(request =>
        {
            captured = request;
            return JsonResponse(
            """
            {
              "data": [{
                "id": 5678123,
                "displayName": "Example 1.0",
                "fileName": "Example Mod.jar",
                "downloadUrl": null,
                "fileLength": 1234,
                "fileDate": "2026-06-02T00:00:00Z",
                "gameVersions": ["1.20.1", "Fabric"],
                "dependencies": [{ "modId": 306612, "relationType": 3 }]
              }]
            }
            """);
        }));
        using CurseForgeCommunityResourceCatalog catalog = new(client, "test-key");
        CommunityResourceEntry entry = new("42", "example", "Example", string.Empty, "mod", null, 0L, null)
        {
            Source = CommunityResourceSource.CurseForge
        };

        IReadOnlyList<CommunityResourceVersion> versions = await catalog.GetVersionsAsync(entry);

        CommunityResourceVersion version = versions.Single();
        // Both CurseForge search and file-list endpoints reject page sizes above 50.
        StringAssert.Contains(captured!.RequestUri!.Query, "pageSize=50");
        Assert.AreEqual("https://edge.forgecdn.net/files/5678/123/Example%20Mod.jar", version.Files.Single().Url);
        CollectionAssert.Contains(version.GameVersions.ToArray(), "1.20.1");
        CollectionAssert.Contains(version.Loaders.ToArray(), "fabric");
        Assert.AreEqual(CommunityResourceDependencyType.Required, version.Dependencies.Single().Type);
        Assert.AreEqual("306612", version.Dependencies.Single().ProjectId);
    }

    [TestMethod]
    public async Task CurseForgeCatalog_ShouldFollowFilePaginationWithoutExceedingPageLimit()
    {
        List<int> requestedIndexes = [];
        using HttpClient client = new(new DelegateHandler(request =>
        {
            string query = request.RequestUri!.Query;
            int index = query.Contains("index=1", StringComparison.Ordinal) ? 1 : 0;
            requestedIndexes.Add(index);
            int id = 5678000 + index;
            return JsonResponse($$"""
                {
                  "data": [{
                    "id": {{id}},
                    "displayName": "Version {{index}}",
                    "fileName": "version-{{index}}.jar",
                    "downloadUrl": "https://example.test/version-{{index}}.jar",
                    "fileDate": "2026-06-02T00:00:00Z",
                    "gameVersions": ["1.20.1", "Fabric"]
                  }],
                  "pagination": { "index": {{index}}, "pageSize": 50, "resultCount": 1, "totalCount": 2 }
                }
                """);
        }));
        using CurseForgeCommunityResourceCatalog catalog = new(client, "test-key");
        CommunityResourceEntry entry = new("42", "example", "Example", string.Empty, "mod", null, 0L, null)
        {
            Source = CommunityResourceSource.CurseForge
        };

        IReadOnlyList<CommunityResourceVersion> versions = await catalog.GetVersionsAsync(entry);

        Assert.AreEqual(2, versions.Count);
        CollectionAssert.AreEqual(new[] { 0, 1 }, requestedIndexes);
    }

    [TestMethod]
    public async Task ModrinthCatalog_ShouldBackfillVersionsOmittedFromListPagination()
    {
        // Reproduces Modrinth Sodium: /project/.../version stops at recent releases while
        // project.versions still lists legacy IDs (1.16.x) only retrievable via /versions?ids=.
        List<string> requested = [];
        using HttpClient client = new(new DelegateHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            string query = request.RequestUri!.Query;
            requested.Add(path + query);
            if (path.EndsWith("/version", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """
                    [{
                      "id": "new-only",
                      "name": "Sodium 0.6",
                      "version_number": "0.6.0",
                      "date_published": "2026-01-01T00:00:00Z",
                      "game_versions": ["1.21.1"],
                      "loaders": ["fabric"],
                      "files": [{
                        "filename": "sodium-0.6.jar",
                        "url": "https://cdn.modrinth.com/sodium-0.6.jar",
                        "size": 10
                      }]
                    }]
                    """);
            }

            if (path.EndsWith("/project/AANobbMI", StringComparison.Ordinal) ||
                path.EndsWith("/project/AANobbMI/", StringComparison.Ordinal) ||
                path == "/v2/project/AANobbMI")
            {
                return JsonResponse(
                    """
                    {
                      "id": "AANobbMI",
                      "slug": "sodium",
                      "title": "Sodium",
                      "description": "Fast",
                      "project_type": "mod",
                      "downloads": 1,
                      "versions": ["new-only", "legacy-116"]
                    }
                    """);
            }

            if (path == "/v2/versions")
            {
                StringAssert.Contains(Uri.UnescapeDataString(query), "legacy-116");
                return JsonResponse(
                    """
                    [{
                      "id": "legacy-116",
                      "name": "Sodium 0.1",
                      "version_number": "mc1.16.3-0.1.0",
                      "date_published": "2021-01-03T00:00:00Z",
                      "game_versions": ["1.16.3", "1.16.4", "1.16.5"],
                      "loaders": ["fabric"],
                      "files": [{
                        "filename": "sodium-0.1.jar",
                        "url": "https://cdn.modrinth.com/sodium-0.1.jar",
                        "size": 5
                      }]
                    }]
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        using ModrinthCommunityResourceCatalog catalog = new(client, ownsClient: false);
        CommunityResourceEntry entry = new("AANobbMI", "sodium", "Sodium", "Fast", "mod", null, 1L, null);

        IReadOnlyList<CommunityResourceVersion> versions = await catalog.GetVersionsAsync(entry);

        Assert.AreEqual(2, versions.Count);
        Assert.IsTrue(versions.Any(v => v.VersionId == "new-only"));
        Assert.IsTrue(versions.Any(v => v.VersionId == "legacy-116"));
        CollectionAssert.Contains(
            versions.Single(v => v.VersionId == "legacy-116").GameVersions.ToArray(),
            "1.16.3");
        Assert.IsTrue(requested.Any(r => r.Contains("/v2/versions", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void FavoritesStore_ShouldPersistAndToggleBySourceAndProjectId()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcln-favorites-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "favorites.json");
        try
        {
            CommunityResourceEntry entry = new("AANobbMI", "sodium", "Sodium", "Fast", "mod", null, 10L, null);
            CommunityFavoritesStore store = new(path);

            Assert.IsTrue(store.Toggle(entry, CommunityResourceCategory.Mod));
            Assert.IsTrue(store.Contains(entry));

            CommunityFavoritesStore reloaded = new(path);
            Assert.IsTrue(reloaded.Contains(entry));
            Assert.AreEqual(CommunityResourceCategory.Mod, reloaded.Items.Single().Category);
            Assert.IsFalse(reloaded.Toggle(entry, CommunityResourceCategory.Mod));
            Assert.IsFalse(new CommunityFavoritesStore(path).Contains(entry));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task FavoritesStore_ShouldSharePathStateAcrossConcurrentInstances()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcln-favorites-race-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "favorites.json");
        try
        {
            CommunityFavoritesStore first = new(path);
            CommunityFavoritesStore second = new(path);
            int changeCount = 0;
            second.Changed += (_, _) => Interlocked.Increment(ref changeCount);

            Task[] updates = Enumerable.Range(0, 16)
                .Select(index => Task.Run(() =>
                {
                    CommunityResourceEntry entry = new(
                        "project-" + index,
                        "slug-" + index,
                        "Title " + index,
                        "Summary",
                        "mod",
                        null,
                        index,
                        null);
                    CommunityFavoritesStore store = index % 2 == 0 ? first : second;
                    Assert.IsTrue(store.Toggle(entry, CommunityResourceCategory.Mod));
                }))
                .ToArray();

            await Task.WhenAll(updates);

            Assert.AreEqual(16, first.Items.Count);
            Assert.AreEqual(16, second.Items.Count);
            Assert.AreEqual(16, new CommunityFavoritesStore(path).Items.Count);
            Assert.AreEqual(16, changeCount);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
}

// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Text;
using System.Text.Json;
using PCL.Application.Settings;
using PCL.Desktop.Features.Community;
using PCL.Desktop.Features.Launching.Views;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class CommunityFeatureTests
{
    [TestMethod]
    public void MyMsgInput_ShouldPreserveLegacyConfigureBinarySignature()
    {
        Assert.IsTrue(typeof(PCL.Desktop.Controls.Legacy.MyMsgInput)
            .GetMethods()
            .Any(static method =>
                string.Equals(method.Name, "Configure", StringComparison.Ordinal) &&
                method.GetParameters().Length == 8));
    }

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
    public async Task ModrinthCatalog_ShouldFallbackWhenMirrorReturnsHtml()
    {
        List<Uri> requests = [];
        using HttpClient client = new(new DelegateHandler(request =>
        {
            requests.Add(request.RequestUri!);
            return requests.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html>mirror unavailable</html>", Encoding.UTF8, "text/html")
                }
                : JsonResponse(
                    """
                    { "hits": [{
                      "project_id": "modrinth-only",
                      "slug": "modrinth-only",
                      "title": "Modrinth Only",
                      "project_type": "mod"
                    }] }
                    """);
        }));
        using ModrinthCommunityResourceCatalog catalog = new(
            client,
            DownloadSourcePreference.MirrorOnly);

        IReadOnlyList<CommunityResourceEntry> entries = await catalog.SearchAsync(
            CommunityResourceCategory.Mod,
            "modrinth-only");

        Assert.AreEqual("modrinth-only", entries.Single().ProjectId);
        Assert.AreEqual("mod.mcimirror.top", requests[0].Host);
        Assert.AreEqual("api.modrinth.com", requests[1].Host);
    }

    [TestMethod]
    public async Task ModrinthFileLookup_ShouldNotRepeatProjectLookup()
    {
        const string sha1 = "0123456789abcdef0123456789abcdef01234567";
        List<Uri> requests = [];
        using HttpClient client = new(new DelegateHandler(request =>
        {
            requests.Add(request.RequestUri!);
            return JsonResponse(
                $$"""
                {
                  "project_id": "project-id",
                  "id": "version-id",
                  "version_number": "1.0.0",
                  "date_published": "2026-01-02T03:04:05Z",
                  "files": [{
                    "filename": "project.jar",
                    "url": "https://cdn.modrinth.com/project.jar",
                    "size": 42,
                    "hashes": { "sha1": "{{sha1}}" }
                  }]
                }
                """);
        }));
        using ModrinthCommunityResourceCatalog catalog = new(
            client,
            DownloadSourcePreference.OfficialOnly);

        CommunityResourceFileIdentity? identity = await catalog.LookupFileBySha1Async(sha1);

        Assert.IsNotNull(identity);
        Assert.AreEqual("project-id", identity.ProjectId);
        Assert.AreEqual(sha1, identity.CurrentFile?.Sha1);
        Assert.AreEqual(1, requests.Count);
        StringAssert.Contains(requests[0].AbsolutePath, "/version_file/");
    }

    [TestMethod]
    public async Task CurseForgeCatalog_ShouldUseMirrorWithoutApiKey()
    {
        List<(Uri Uri, string? ApiKey)> requests = [];
        using HttpClient client = new(new DelegateHandler(request =>
        {
            requests.Add((request.RequestUri!, ReadApiKey(request)));
            return JsonResponse("""{ "data": [] }""");
        }));
        using CurseForgeCommunityResourceCatalog catalog = new(
            client,
            apiKey: null,
            DownloadSourcePreference.MirrorOnly);

        await catalog.SearchAsync(CommunityResourceCategory.Mod, "mirror-only");

        Assert.AreEqual(1, requests.Count);
        Assert.AreEqual("mod.mcimirror.top", requests[0].Uri.Host);
        Assert.IsNull(requests[0].ApiKey);

        using CurseForgeCommunityResourceCatalog officialOnlyCatalog = new(
            client,
            apiKey: null,
            DownloadSourcePreference.OfficialOnly);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            officialOnlyCatalog.SearchAsync(CommunityResourceCategory.Mod, "official-only"));
        Assert.AreEqual(1, requests.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurseForgeCatalog_ShouldFallbackAndSignOnlyOfficial(bool mirrorReturnsHtml)
    {
        List<(Uri Uri, string? ApiKey)> requests = [];
        using HttpClient client = new(new DelegateHandler(request =>
        {
            requests.Add((request.RequestUri!, ReadApiKey(request)));
            if (request.RequestUri!.Host.Equals("mod.mcimirror.top", StringComparison.OrdinalIgnoreCase))
            {
                return mirrorReturnsHtml
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("<html>mirror unavailable</html>", Encoding.UTF8, "text/html")
                    }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return JsonResponse(
                """
                { "data": [{
                  "id": 42,
                  "name": "CurseForge Only",
                  "slug": "curseforge-only"
                }] }
                """);
        }));
        using CurseForgeCommunityResourceCatalog catalog = new(
            client,
            "test-key",
            DownloadSourcePreference.MirrorOnly);

        IReadOnlyList<CommunityResourceEntry> entries = await catalog.SearchAsync(
            CommunityResourceCategory.Mod,
            "curseforge-only");

        Assert.AreEqual("42", entries.Single().ProjectId);
        Assert.AreEqual(2, requests.Count);
        Assert.AreEqual("mod.mcimirror.top", requests[0].Uri.Host);
        Assert.IsNull(requests[0].ApiKey);
        Assert.AreEqual("api.curseforge.com", requests[1].Uri.Host);
        Assert.AreEqual("test-key", requests[1].ApiKey);
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
    [DataRow(CommunityResourceSort.Downloads, "sortField=6")]
    [DataRow(CommunityResourceSort.Updated, "sortField=3")]
    public async Task CurseForgeCatalog_NonRelevanceSort_ShouldPreserveApiOrdering(
        CommunityResourceSort sort,
        string expectedSortField)
    {
        HttpRequestMessage? captured = null;
        using HttpClient client = new(new DelegateHandler(request =>
        {
            captured = request;
            return JsonResponse(
                """
                { "data": [
                  {
                    "id": 1,
                    "name": "JEI Addons",
                    "slug": "jei-addons",
                    "downloadCount": 9000,
                    "dateModified": "2026-07-02T00:00:00Z"
                  },
                  {
                    "id": 2,
                    "name": "JEI",
                    "slug": "jei",
                    "downloadCount": 8000,
                    "dateModified": "2026-07-01T00:00:00Z"
                  }
                ] }
                """);
        }));
        using CurseForgeCommunityResourceCatalog catalog = new(client, "test-key");

        IReadOnlyList<CommunityResourceEntry> entries = await catalog.SearchAsync(
            CommunityResourceCategory.Mod,
            "jei",
            new CommunitySearchOptions(sort, Source: CommunityResourceSource.CurseForge));

        CollectionAssert.AreEqual(new[] { "1", "2" }, entries.Select(static entry => entry.ProjectId).ToArray());
        StringAssert.Contains(Uri.UnescapeDataString(captured!.RequestUri!.Query), expectedSortField);
    }

    [TestMethod]
    public void FavoritesDownloadOptions_ShouldUseAllOnlyForMergedEntries()
    {
        CommunityResourceEntry merged = new(
            "modrinth-id",
            "example",
            "Example",
            string.Empty,
            "mod",
            null,
            0,
            null)
        {
            Source = CommunityResourceSource.Modrinth,
            ModrinthProject = new CommunityResourceProjectReference(
                CommunityResourceSource.Modrinth,
                "modrinth-id",
                "example",
                "https://modrinth.com/mod/example"),
            CurseForgeProject = new CommunityResourceProjectReference(
                CommunityResourceSource.CurseForge,
                "curseforge-id",
                "example",
                "https://www.curseforge.com/minecraft/mc-mods/example")
        };
        CommunityResourceEntry legacyCurseForge = merged with
        {
            ProjectId = "curseforge-id",
            Source = CommunityResourceSource.CurseForge,
            ModrinthProject = null,
            CurseForgeProject = null
        };

        CommunitySearchOptions mergedOptions = PageCommunityFavoritesRight.CreateDownloadOptions(merged);
        CommunitySearchOptions legacyOptions = PageCommunityFavoritesRight.CreateDownloadOptions(legacyCurseForge);

        Assert.AreEqual(CommunityResourceSource.All, mergedOptions.Source);
        Assert.AreEqual(CommunityResourceSource.CurseForge, legacyOptions.Source);
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
    [DataRow(6, "mod")]
    [DataRow(4471, "modpack")]
    [DataRow(6945, "datapack")]
    [DataRow(12, "resourcepack")]
    [DataRow(6552, "shader")]
    [DataRow(17, "world")]
    [DataRow(9999, "mod")]
    public async Task CurseForgeCatalog_GetProjectAsyncShouldMapClassId(int classId, string projectType)
    {
        using HttpClient client = new(new DelegateHandler(_ => JsonResponse(
            $$"""
            {
              "data": {
                "id": 1479191,
                "classId": {{classId}},
                "name": "Imported Project",
                "slug": "imported-project",
                "summary": "Imported from CE",
                "downloadCount": 100
              }
            }
            """)));
        using CurseForgeCommunityResourceCatalog catalog = new(client, "test-key");

        CommunityResourceEntry? entry = await catalog.GetProjectAsync(
            CommunityResourceSource.CurseForge,
            "1479191");

        Assert.IsNotNull(entry);
        Assert.AreEqual(projectType, entry.ProjectType);
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
    public async Task CompositeCatalog_ShouldMergeProjectsBySummedProviderRanks()
    {
        StubCommunityResourceCatalog modrinth = new()
        {
            Projects =
            [
                CreateProject("modrinth-a", "alpha", "Alpha", 10, ["fabric"]),
                CreateProject("modrinth-b", "beta", "Beta", 20, ["technology"]),
                CreateProject("modrinth-c", "gamma", "Gamma", 30, ["utility"])
            ]
        };
        StubCommunityResourceCatalog curseForge = new()
        {
            Projects =
            [
                CreateProject("curse-b", "beta", "Beta", 200, ["magic"]),
                CreateProject("curse-c", "gamma", "Gamma", 300, ["library"]),
                CreateProject("curse-a", "alpha", "Alpha", 100, ["optimization"])
            ]
        };
        using CompositeCommunityResourceCatalog catalog = new(modrinth, curseForge);

        IReadOnlyList<CommunityResourceEntry> projects = await catalog.SearchAsync(
            CommunityResourceCategory.Mod,
            string.Empty,
            new CommunitySearchOptions(Source: CommunityResourceSource.All));

        CollectionAssert.AreEqual(new[] { "beta", "alpha", "gamma" }, projects.Select(p => p.Slug).ToArray());
        CommunityResourceEntry beta = projects[0];
        Assert.AreEqual(220L, beta.Downloads);
        Assert.AreEqual("modrinth-b", beta.ModrinthProject?.ProjectId);
        Assert.AreEqual("curse-b", beta.CurseForgeProject?.ProjectId);
        Assert.AreEqual("Modrinth + CurseForge", beta.SourceDisplayName);
        CollectionAssert.AreEquivalent(new[] { "technology", "magic" }, beta.Tags.ToArray());
    }

    [TestMethod]
    public async Task CompositeCatalog_ShouldNotMergeProjectsWithConflictingWikiIds()
    {
        StubCommunityResourceCatalog modrinth = new()
        {
            Projects = [CreateProject("modrinth-project", "same-slug", "Same Title", 10, []) with { WikiId = 101 }]
        };
        StubCommunityResourceCatalog curseForge = new()
        {
            Projects = [CreateProject("curse-project", "same-slug", "Same Title", 20, []) with { WikiId = 202 }]
        };
        using CompositeCommunityResourceCatalog catalog = new(modrinth, curseForge);

        IReadOnlyList<CommunityResourceEntry> projects = await catalog.SearchAsync(
            CommunityResourceCategory.Mod,
            string.Empty,
            new CommunitySearchOptions(Source: CommunityResourceSource.All));

        Assert.AreEqual(2, projects.Count);
        Assert.IsFalse(projects.Any(project =>
            project.ModrinthProject is not null && project.CurseForgeProject is not null));
    }

    [TestMethod]
    public async Task CompositeCatalog_ShouldLoadVersionsForEitherSingleSource()
    {
        CommunityResourceVersion modrinthVersion = CreateVersion(
            "modrinth-version",
            "2026-07-02T00:00:00Z",
            new string('a', 64),
            "https://modrinth.test/version.jar");
        StubCommunityResourceCatalog modrinth = new()
        {
            Versions = new Dictionary<string, CommunityResourceVersion[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["modrinth-only"] = [modrinthVersion]
            }
        };
        StubCommunityResourceCatalog unusedCurseForge = new();
        using (CompositeCommunityResourceCatalog catalog = new(modrinth, unusedCurseForge))
        {
            CommunityResourceEntry project = CreateProject(
                "modrinth-only",
                "modrinth-only",
                "Modrinth Only",
                0,
                []);

            IReadOnlyList<CommunityResourceVersion> versions = await catalog.GetVersionsAsync(
                project,
                new CommunitySearchOptions(Source: CommunityResourceSource.All));

            Assert.AreEqual("modrinth-version", versions.Single().VersionId);
            CollectionAssert.AreEqual(new[] { "modrinth-only" }, modrinth.VersionRequests.ToArray());
            Assert.AreEqual(0, unusedCurseForge.VersionRequests.Count);
        }

        CommunityResourceVersion curseForgeVersion = CreateVersion(
            "curseforge-version",
            "2026-07-03T00:00:00Z",
            new string('b', 64),
            "https://curseforge.test/version.jar") with
        {
            Source = CommunityResourceSource.CurseForge
        };
        StubCommunityResourceCatalog unusedModrinth = new();
        StubCommunityResourceCatalog curseForge = new()
        {
            Versions = new Dictionary<string, CommunityResourceVersion[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["curseforge-only"] = [curseForgeVersion]
            }
        };
        using (CompositeCommunityResourceCatalog catalog = new(unusedModrinth, curseForge))
        {
            CommunityResourceEntry project = CreateProject(
                "curseforge-only",
                "curseforge-only",
                "CurseForge Only",
                0,
                []) with
            {
                Source = CommunityResourceSource.CurseForge
            };

            IReadOnlyList<CommunityResourceVersion> versions = await catalog.GetVersionsAsync(
                project,
                new CommunitySearchOptions(Source: CommunityResourceSource.All));

            Assert.AreEqual("curseforge-version", versions.Single().VersionId);
            Assert.AreEqual(0, unusedModrinth.VersionRequests.Count);
            CollectionAssert.AreEqual(new[] { "curseforge-only" }, curseForge.VersionRequests.ToArray());
        }
    }

    [TestMethod]
    public async Task CompositeCatalog_ShouldKeepAvailableSourceWhenOtherVersionLookupFails()
    {
        CommunityResourceEntry project = CreateProject("modrinth-project", "example", "Example", 0, []) with
        {
            ModrinthProject = new CommunityResourceProjectReference(
                CommunityResourceSource.Modrinth,
                "modrinth-project",
                "example",
                "https://modrinth.com/mod/example"),
            CurseForgeProject = new CommunityResourceProjectReference(
                CommunityResourceSource.CurseForge,
                "curseforge-project",
                "example",
                "https://www.curseforge.com/minecraft/mc-mods/example")
        };
        StubCommunityResourceCatalog modrinth = new()
        {
            Versions = new Dictionary<string, CommunityResourceVersion[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["modrinth-project"] =
                [
                    CreateVersion(
                        "available-version",
                        "2026-07-03T00:00:00Z",
                        new string('a', 64),
                        "https://modrinth.test/available.jar")
                ]
            }
        };
        StubCommunityResourceCatalog curseForge = new()
        {
            VersionsException = new System.Text.Json.JsonException("'<' is an invalid start of a value.")
        };
        using CompositeCommunityResourceCatalog catalog = new(modrinth, curseForge);

        IReadOnlyList<CommunityResourceVersion> versions = await catalog.GetVersionsAsync(
            project,
            new CommunitySearchOptions(Source: CommunityResourceSource.All));

        Assert.AreEqual("available-version", versions.Single().VersionId);
        CollectionAssert.AreEqual(new[] { "modrinth-project" }, modrinth.VersionRequests.ToArray());
        CollectionAssert.AreEqual(new[] { "curseforge-project" }, curseForge.VersionRequests.ToArray());
    }

    [TestMethod]
    public async Task CompositeCatalog_ShouldMergeVersionsByPublishedTimeAndSha256()
    {
        const string duplicateSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string differentSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        CommunityResourceEntry mergedProject = CreateProject("modrinth-project", "example", "Example", 10, []) with
        {
            ModrinthProject = new CommunityResourceProjectReference(
                CommunityResourceSource.Modrinth,
                "modrinth-project",
                "example",
                "https://modrinth.com/mod/example"),
            CurseForgeProject = new CommunityResourceProjectReference(
                CommunityResourceSource.CurseForge,
                "curse-project",
                "example",
                "https://www.curseforge.com/minecraft/mc-mods/example")
        };
        StubCommunityResourceCatalog modrinth = new()
        {
            Versions = new Dictionary<string, CommunityResourceVersion[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["modrinth-project"] =
                [
                    CreateVersion("newest", "2026-07-05T00:00:00Z", differentSha, "https://modrinth.test/newest.jar"),
                    CreateVersion("duplicate-mr", "2026-07-03T00:00:00Z", duplicateSha, "https://modrinth.test/duplicate.jar") with
                    {
                        Dependencies =
                        [
                            new CommunityResourceDependency(
                                "shared-dependency",
                                "shared-version",
                                null,
                                CommunityResourceDependencyType.Required,
                                CommunityResourceSource.Modrinth)
                        ]
                    }
                ]
            }
        };
        StubCommunityResourceCatalog curseForge = new()
        {
            Versions = new Dictionary<string, CommunityResourceVersion[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["curse-project"] =
                [
                    CreateVersion("duplicate-cf", "2026-07-03T00:00:00Z", duplicateSha, "https://curseforge.test/duplicate.jar") with
                    {
                        Dependencies =
                        [
                            new CommunityResourceDependency(
                                "curse-only-dependency",
                                "curse-version",
                                "curse-only.jar",
                                CommunityResourceDependencyType.Optional,
                                CommunityResourceSource.CurseForge),
                            new CommunityResourceDependency(
                                "curse-only-dependency",
                                "curse-version",
                                "duplicate-file-name-is-ignored.jar",
                                CommunityResourceDependencyType.Optional,
                                CommunityResourceSource.CurseForge)
                        ]
                    },
                    CreateVersion("same-time-different-hash", "2026-07-03T00:00:00Z", differentSha, "https://curseforge.test/different.jar"),
                    CreateVersion("same-time-no-hash", "2026-07-03T00:00:00Z", null, "https://curseforge.test/no-hash.jar"),
                    CreateVersion("same-hash-different-time", "2026-07-02T00:00:00Z", duplicateSha, "https://curseforge.test/older.jar")
                ]
            }
        };
        using CompositeCommunityResourceCatalog catalog = new(modrinth, curseForge);

        IReadOnlyList<CommunityResourceVersion> versions = await catalog.GetVersionsAsync(
            mergedProject,
            new CommunitySearchOptions(Source: CommunityResourceSource.All));

        Assert.AreEqual(5, versions.Count);
        CollectionAssert.AreEqual(
            new[] { "2026-07-05", "2026-07-03", "2026-07-03", "2026-07-03", "2026-07-02" },
            versions.Select(version => version.PublishedAt!.Value.ToString("yyyy-MM-dd")).ToArray());
        CommunityResourceVersion duplicate = versions.Single(version => version.Source == CommunityResourceSource.All);
        Assert.AreEqual("duplicate-mr", duplicate.VersionId);
        CollectionAssert.AreEquivalent(
            new[] { "https://modrinth.test/duplicate.jar", "https://curseforge.test/duplicate.jar" },
            duplicate.Files.Single().CandidateUrls.ToArray());
        Assert.AreEqual(2, duplicate.Dependencies.Count);
        Assert.IsTrue(duplicate.Dependencies.Any(dependency =>
            dependency.Source == CommunityResourceSource.Modrinth &&
            dependency.ProjectId == "shared-dependency"));
        Assert.IsTrue(duplicate.Dependencies.Any(dependency =>
            dependency.Source == CommunityResourceSource.CurseForge &&
            dependency.ProjectId == "curse-only-dependency"));
        Assert.AreEqual(2, versions.Count(version => version.Files.Any(file => file.Sha256 == duplicateSha)));
        Assert.IsTrue(versions.Any(version => version.Files.Any(file => file.Sha256 is null)));
        CollectionAssert.AreEquivalent(
            new[] { "modrinth-project" },
            modrinth.VersionRequests.ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "curse-project" },
            curseForge.VersionRequests.ToArray());
    }

    [TestMethod]
    public async Task CompositeCatalog_ShouldFallbackToSha1WhenSha256IsUnavailable()
    {
        const string sha1 = "0123456789abcdef0123456789abcdef01234567";
        CommunityResourceEntry project = CreateProject("modrinth-project", "example", "Example", 0, []) with
        {
            ModrinthProject = new CommunityResourceProjectReference(
                CommunityResourceSource.Modrinth,
                "modrinth-project",
                "example",
                "https://modrinth.com/mod/example"),
            CurseForgeProject = new CommunityResourceProjectReference(
                CommunityResourceSource.CurseForge,
                "curse-project",
                "example",
                "https://www.curseforge.com/minecraft/mc-mods/example")
        };
        StubCommunityResourceCatalog modrinth = new()
        {
            Versions = new Dictionary<string, CommunityResourceVersion[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["modrinth-project"] =
                [
                    CreateVersion(
                        "modrinth-version",
                        "2026-07-01T00:00:00Z",
                        null,
                        "https://modrinth.test/example.jar",
                        sha1)
                ]
            }
        };
        StubCommunityResourceCatalog curseForge = new()
        {
            Versions = new Dictionary<string, CommunityResourceVersion[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["curse-project"] =
                [
                    CreateVersion(
                        "curse-version",
                        "2026-07-01T00:00:00Z",
                        null,
                        "https://curseforge.test/example.jar",
                        sha1)
                ]
            }
        };
        using CompositeCommunityResourceCatalog catalog = new(modrinth, curseForge);

        CommunityResourceVersion version = (await catalog.GetVersionsAsync(
            project,
            new CommunitySearchOptions(Source: CommunityResourceSource.All))).Single();

        Assert.AreEqual(CommunityResourceSource.All, version.Source);
        Assert.AreEqual(sha1, version.Files.Single().Sha1);
        CollectionAssert.AreEquivalent(
            new[] { "https://modrinth.test/example.jar", "https://curseforge.test/example.jar" },
            version.Files.Single().CandidateUrls.ToArray());
    }

    [TestMethod]
    public async Task CompositeCatalog_ShouldPreferSha256OverMatchingSha1()
    {
        const string sha1 = "0123456789abcdef0123456789abcdef01234567";
        CommunityResourceEntry project = CreateProject("modrinth-project", "example", "Example", 0, []) with
        {
            ModrinthProject = new CommunityResourceProjectReference(
                CommunityResourceSource.Modrinth,
                "modrinth-project",
                "example",
                "https://modrinth.com/mod/example"),
            CurseForgeProject = new CommunityResourceProjectReference(
                CommunityResourceSource.CurseForge,
                "curse-project",
                "example",
                "https://www.curseforge.com/minecraft/mc-mods/example")
        };
        StubCommunityResourceCatalog modrinth = new()
        {
            Versions = new Dictionary<string, CommunityResourceVersion[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["modrinth-project"] =
                [
                    CreateVersion(
                        "modrinth-version",
                        "2026-07-01T00:00:00Z",
                        new string('a', 64),
                        "https://modrinth.test/example.jar",
                        sha1)
                ]
            }
        };
        StubCommunityResourceCatalog curseForge = new()
        {
            Versions = new Dictionary<string, CommunityResourceVersion[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["curse-project"] =
                [
                    CreateVersion(
                        "curse-version",
                        "2026-07-01T00:00:00Z",
                        new string('b', 64),
                        "https://curseforge.test/example.jar",
                        sha1)
                ]
            }
        };
        using CompositeCommunityResourceCatalog catalog = new(modrinth, curseForge);

        IReadOnlyList<CommunityResourceVersion> versions = await catalog.GetVersionsAsync(
            project,
            new CommunitySearchOptions(Source: CommunityResourceSource.All));

        Assert.AreEqual(2, versions.Count);
        Assert.IsFalse(versions.Any(version => version.Source == CommunityResourceSource.All));
    }

    [TestMethod]
    public async Task CommunityCatalogs_ShouldParseSha256WhenProvidersReturnIt()
    {
        const string sha = "abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd";
        const string sha1 = "0123456789abcdef0123456789abcdef01234567";
        using HttpClient modrinthClient = new(new DelegateHandler(_ => JsonResponse($$"""
            [{
              "id": "version",
              "name": "Version",
              "version_number": "1.0",
              "date_published": "2026-07-01T00:00:00Z",
              "game_versions": ["1.21.1"],
              "loaders": ["fabric"],
              "files": [{
                "filename": "modrinth.jar",
                "url": "https://modrinth.test/modrinth.jar",
                "hashes": { "sha1": "{{sha1}}", "sha256": "{{sha}}" }
              }]
            }]
            """)));
        using ModrinthCommunityResourceCatalog modrinth = new(modrinthClient);
        CommunityResourceVersion modrinthVersion = (await modrinth.GetVersionsAsync(
            CreateProject("modrinth-project", "example", "Example", 0, []),
            new CommunitySearchOptions(GameVersion: "1.21.1"))).Single();

        using HttpClient curseForgeClient = new(new DelegateHandler(_ => JsonResponse($$"""
            {
              "data": [{
                "id": 1234567,
                "displayName": "Version",
                "fileName": "curseforge.jar",
                "downloadUrl": "https://curseforge.test/curseforge.jar",
                "fileDate": "2026-07-01T00:00:00Z",
                "hashes": [
                  { "algo": 1, "value": "{{sha1}}" },
                  { "algo": 3, "value": "{{sha}}" }
                ]
              }]
            }
            """)));
        using CurseForgeCommunityResourceCatalog curseForge = new(curseForgeClient, "test-key");
        CommunityResourceVersion curseForgeVersion = (await curseForge.GetVersionsAsync(
            CreateProject("curse-project", "example", "Example", 0, []) with
            {
                Source = CommunityResourceSource.CurseForge
            })).Single();

        Assert.AreEqual(sha, modrinthVersion.Files.Single().Sha256);
        Assert.AreEqual(sha, curseForgeVersion.Files.Single().Sha256);
        Assert.AreEqual(sha1, modrinthVersion.Files.Single().Sha1);
        Assert.AreEqual(sha1, curseForgeVersion.Files.Single().Sha1);
        Assert.AreEqual(CommunityResourceSource.Modrinth, modrinthVersion.Files.Single().Source);
        Assert.AreEqual(CommunityResourceSource.CurseForge, curseForgeVersion.Files.Single().Source);
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
    public void FavoritesStore_ShouldMigrateLegacyFlatJsonWithoutDroppingMetadata()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcln-favorites-migration-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "favorites.json");
        try
        {
            Directory.CreateDirectory(root);
            CommunityResourceEntry entry = new(
                "AANobbMI",
                "sodium",
                "Sodium",
                "Fast renderer",
                "mod",
                "https://cdn.example/sodium.png",
                42L,
                DateTimeOffset.Parse("2026-07-01T00:00:00Z"))
            {
                Source = CommunityResourceSource.Modrinth,
                ChineseName = "钠"
            };
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(
                    new[]
                    {
                        new CommunityFavoriteEntry(
                            entry,
                            CommunityResourceCategory.Mod,
                            DateTimeOffset.Parse("2026-07-02T00:00:00Z"))
                    }));

            CommunityFavoritesStore store = new(path);

            Assert.AreEqual(1, store.Folders.Count);
            Assert.AreEqual(CommunityFavoritesStore.DefaultFolderName, store.SelectedFolder.Name);
            Assert.AreEqual("Sodium", store.Items.Single().Entry.Title);
            Assert.AreEqual("钠", store.Items.Single().Entry.ChineseName);
            Assert.AreEqual("https://cdn.example/sodium.png", store.Items.Single().Entry.IconUrl);
            using JsonDocument migrated = JsonDocument.Parse(File.ReadAllText(path));
            Assert.AreEqual(JsonValueKind.Object, migrated.RootElement.ValueKind);
            Assert.AreEqual(1, migrated.RootElement.GetProperty("folders").GetArrayLength());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void FavoritesStore_ShouldRecognizeLegacyPluginPlaceholdersForMetadataResolution()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcln-favorites-placeholder-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "favorites.json");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(
                path,
                """
                [{
                  "entry": {
                    "projectId": "1479191",
                    "slug": "1479191",
                    "title": "1479191",
                    "summary": "从旧版收藏夹迁移",
                    "projectType": "mod",
                    "downloads": 0,
                    "source": 2
                  },
                  "category": 0,
                  "addedAt": "2026-07-01T00:00:00Z"
                }]
                """);

            CommunityFavoritesStore store = new(path);
            CommunityFavoriteEntry placeholder = store.Items.Single();

            Assert.IsTrue(CommunityFavoritesStore.IsImportedPlaceholder(placeholder.Entry));
            Assert.AreEqual(1, store.ApplyResolvedMetadata(
                store.SelectedFolderId,
                [
                    new CommunityResourceEntry(
                        "1479191",
                        "example-pack",
                        "Example Pack",
                        "Resolved metadata",
                        "modpack",
                        null,
                        10,
                        null)
                    {
                        Source = CommunityResourceSource.CurseForge
                    }
                ]));
            Assert.AreEqual(CommunityResourceCategory.Modpack, store.Items.Single().Category);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void FavoritesStore_ShouldManageFoldersAndKeepAtLeastOne()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcln-favorites-folders-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "favorites.json");
        try
        {
            CommunityFavoritesStore store = new(path);
            string defaultFolderId = store.SelectedFolderId;
            CommunityResourceEntry entry = new("AANobbMI", "sodium", "Sodium", "Fast", "mod", null, 10L, null);
            Assert.IsTrue(store.Toggle(entry, CommunityResourceCategory.Mod));

            CommunityFavoriteFolder performance = store.CreateFolder("性能优化");
            Assert.AreEqual(performance.Id, store.SelectedFolderId);
            Assert.AreEqual(0, store.Items.Count);
            Assert.IsTrue(store.Contains(entry));
            Assert.IsFalse(store.Contains(entry, performance.Id));
            Assert.IsTrue(store.Toggle(entry, CommunityResourceCategory.Mod, performance.Id));
            Assert.IsTrue(store.RenameFolder(performance.Id, "客户端优化"));
            Assert.AreEqual("客户端优化", store.SelectedFolder.Name);
            Assert.ThrowsExactly<InvalidOperationException>(() => store.CreateFolder("客户端优化"));

            Assert.IsTrue(store.DeleteFolder(defaultFolderId));
            Assert.AreEqual(1, store.Folders.Count);
            Assert.ThrowsExactly<InvalidOperationException>(() => store.DeleteFolder(performance.Id));
            Assert.AreEqual(1, store.Folders.Count);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void FavoritesStore_ShouldImportAndExportCeShareArraysByFolder()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcln-favorites-ce-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "favorites.json");
        string restoredPath = Path.Combine(root, "restored.json");
        try
        {
            CommunityFavoritesStore store = new(path);
            CommunityResourceEntry known = new(
                "AANobbMI",
                "sodium",
                "Sodium",
                "Fast renderer",
                "mod",
                "https://cdn.example/sodium.png",
                42L,
                null)
            {
                Source = CommunityResourceSource.Modrinth
            };
            store.Toggle(known, CommunityResourceCategory.Mod);

            CommunityFavoriteFolder imported = store.CreateFolderFromShare(
                "CE 导入",
                """["AANobbMI","1479191","AANobbMI"]""");

            Assert.AreEqual(2, imported.Items.Count);
            Assert.AreEqual("Sodium", imported.Items.Single(item => item.Entry.ProjectId == "AANobbMI").Entry.Title);
            CommunityFavoriteEntry curseForge = imported.Items.Single(item => item.Entry.ProjectId == "1479191");
            Assert.AreEqual(CommunityResourceSource.CurseForge, curseForge.Entry.Source);
            Assert.AreEqual(1, store.ApplyResolvedMetadata(
                imported.Id,
                [
                    new CommunityResourceEntry(
                        "1479191",
                        "example-pack",
                        "Example Pack",
                        "Resolved metadata",
                        "modpack",
                        "https://cdn.example/pack.png",
                        100L,
                        null)
                    {
                        Source = CommunityResourceSource.CurseForge
                    }
                ]));
            CommunityFavoriteEntry resolvedCurseForge = store.Items.Single(item => item.Entry.ProjectId == "1479191");
            Assert.AreEqual("Example Pack", resolvedCurseForge.Entry.Title);
            Assert.AreEqual("https://cdn.example/pack.png", resolvedCurseForge.Entry.IconUrl);
            Assert.AreEqual(CommunityResourceCategory.Modpack, resolvedCurseForge.Category);
            CollectionAssert.AreEquivalent(
                new[] { "AANobbMI", "1479191" },
                JsonSerializer.Deserialize<string[]>(store.ExportShareJson())!);
            Assert.AreEqual(1, store.ImportShareJson("""["another-project","1479191"]""", imported.Id));

            CommunityFavoritesExportSnapshot snapshot = store.ExportSnapshot();
            string native = snapshot.NativeJson;
            CommunityFavoritesStore restored = new(restoredPath);
            restored.ReplaceFromJson(native);
            Assert.AreEqual(2, restored.Folders.Count);
            Assert.AreEqual("CE 导入", restored.SelectedFolder.Name);
            Assert.AreEqual("Sodium", restored.Items.Single(item => item.Entry.ProjectId == "AANobbMI").Entry.Title);

            using JsonDocument ceFolders = JsonDocument.Parse(snapshot.CeFoldersJson);
            Assert.AreEqual(2, ceFolders.RootElement.GetArrayLength());
            Assert.AreEqual("CE 导入", ceFolders.RootElement[1].GetProperty("Name").GetString());
            using JsonDocument nativeDocument = JsonDocument.Parse(snapshot.NativeJson);
            Assert.AreEqual(
                nativeDocument.RootElement.GetProperty("folders")[1].GetProperty("id").GetString(),
                ceFolders.RootElement[1].GetProperty("Id").GetString());
            Assert.ThrowsExactly<InvalidDataException>(() => store.ImportShareJson("{}"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void FavoritesStore_ShouldRestoreCeCloudFoldersWithoutFlattening()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcln-favorites-ce-cloud-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            CommunityFavoritesStore store = new(Path.Combine(root, "favorites.json"));
            store.ReplaceFromCeFoldersJson(
                """
                [
                  {"Name":"Modrinth","Id":"folder-mr","Favs":["AANobbMI"],"Notes":{"AANobbMI":"性能优化"}},
                  {"Name":"CurseForge","Id":"folder-cf","Favs":["1479191"],"Notes":{}}
                ]
                """);

            Assert.AreEqual(2, store.Folders.Count);
            Assert.AreEqual("Modrinth", store.Folders[0].Name);
            Assert.AreEqual("CurseForge", store.Folders[1].Name);
            Assert.AreEqual(CommunityResourceSource.Modrinth, store.Folders[0].Items.Single().Entry.Source);
            Assert.AreEqual(CommunityResourceSource.CurseForge, store.Folders[1].Items.Single().Entry.Source);
            Assert.AreEqual("性能优化", store.Folders[0].Notes["AANobbMI"]);
            using JsonDocument exported = JsonDocument.Parse(store.ExportCeFoldersJson());
            Assert.AreEqual(
                "性能优化",
                exported.RootElement[0].GetProperty("Notes").GetProperty("AANobbMI").GetString());
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

    private static string? ReadApiKey(HttpRequestMessage request) =>
        request.Headers.TryGetValues("x-api-key", out IEnumerable<string>? values)
            ? values.SingleOrDefault()
            : null;

    private static CommunityResourceEntry CreateProject(
        string projectId,
        string slug,
        string title,
        long downloads,
        IReadOnlyList<string> tags) =>
        new(projectId, slug, title, title + " description", "mod", null, downloads, DateTimeOffset.Parse("2026-07-01T00:00:00Z"))
        {
            Tags = tags
        };

    private static CommunityResourceVersion CreateVersion(
        string versionId,
        string publishedAt,
        string? sha256,
        string url,
        string? sha1 = null)
    {
        CommunityResourceDownloadFile file = new(
            versionId + ".jar",
            url,
            10,
            versionId,
            versionId)
        {
            Sha1 = sha1,
            Sha256 = sha256
        };
        return new CommunityResourceVersion(
            versionId,
            versionId,
            versionId,
            null,
            DateTimeOffset.Parse(publishedAt),
            ["1.21.1"],
            ["fabric"],
            [file]);
    }

    private sealed class StubCommunityResourceCatalog : ICommunityResourceCatalog
    {
        public CommunityResourceEntry[] Projects { get; init; } = [];

        public Dictionary<string, CommunityResourceVersion[]> Versions { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);

        public List<string> VersionRequests { get; } = [];

        public Exception? VersionsException { get; init; }

        public Task<IReadOnlyList<CommunityResourceEntry>> SearchAsync(
            CommunityResourceCategory category,
            string query,
            CommunitySearchOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<CommunityResourceEntry>>(Projects);
        }

        public async Task<CommunityResourceDownloadFile?> ResolveDownloadAsync(
            CommunityResourceEntry entry,
            CommunitySearchOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<CommunityResourceVersion> versions = await GetVersionsAsync(
                entry,
                options,
                cancellationToken);
            return versions.SelectMany(static version => version.Files).FirstOrDefault();
        }

        public Task<IReadOnlyList<CommunityResourceVersion>> GetVersionsAsync(
            CommunityResourceEntry entry,
            CommunitySearchOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VersionRequests.Add(entry.ProjectId);
            if (VersionsException is not null)
                return Task.FromException<IReadOnlyList<CommunityResourceVersion>>(VersionsException);
            IReadOnlyList<CommunityResourceVersion> versions = Versions.GetValueOrDefault(entry.ProjectId) ?? [];
            return Task.FromResult(versions);
        }

        public Task<CommunityResourceEntry?> GetProjectAsync(
            CommunityResourceSource source,
            string projectId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Projects.FirstOrDefault(project =>
                string.Equals(project.ProjectId, projectId, StringComparison.OrdinalIgnoreCase)));
        }

        public Task<CommunityResourceFileIdentity?> LookupFileBySha1Async(
            string sha1Hex,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<CommunityResourceFileIdentity?>(null);
        }

        public async Task<CommunityResourceVersion?> GetLatestVersionAsync(
            string projectId,
            CommunitySearchOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CommunityResourceEntry entry = CreateProject(projectId, projectId, projectId, 0, []);
            IReadOnlyList<CommunityResourceVersion> versions = await GetVersionsAsync(
                entry,
                options,
                cancellationToken);
            return versions.FirstOrDefault();
        }
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
}

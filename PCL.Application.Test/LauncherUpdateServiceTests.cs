// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Application.Updates;
using PCL.Core.App;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace PCL.Application.Test;

[TestClass]
public sealed class LauncherUpdateServiceTests
{
    [TestMethod]
    public void CreateReplacementProcess_RunsVerifiedTargetAsUpdateHelper()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PCL-N-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string current = Path.Combine(directory, "PCL-N-Edition.exe");
            string staged = Path.Combine(directory, ".PCL-N-Edition.exe.update");
            File.WriteAllText(current, "old");
            File.WriteAllText(staged, "new");
            LauncherUpdatePackage package = new(
                "2.0.0", "v2.0.0", "https://example.test/update.zip", "update.zip",
                "PCL-N-Edition.exe", null, null, [], "win-x64", "SelfContained", "Release");
            PreparedLauncherUpdate prepared = new(package, current, staged, directory, false);

            ProcessStartInfo startInfo = LauncherUpdateInstaller.CreateReplacementProcess(prepared, 123, true);

            Assert.AreEqual(staged, startInfo.FileName);
            CollectionAssert.AreEqual(
                new[]
                {
                    "--pcln-apply-update",
                    "123",
                    current,
                    staged,
                    directory,
                    "1"
                },
                startInfo.ArgumentList.ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
    [TestMethod]
    public void ParseAtomFeed_ReadsTagsTitlesAndNotes()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <id>tag:github.com,2008:Repository/1/v1.1.6-release</id>
                <updated>2026-07-16T08:00:00Z</updated>
                <link rel="alternate" type="text/html" href="https://github.com/MuXue1230-owo/PCL-N/releases/tag/v1.1.6-release"/>
                <title>PCL N v1.1.6</title>
                <content type="html">&lt;p&gt;hello &lt;em&gt;world&lt;/em&gt;&lt;/p&gt;</content>
              </entry>
              <entry>
                <id>tag:github.com,2008:Repository/1/v1.1.6-beta</id>
                <link href="https://github.com/MuXue1230-owo/PCL-N/releases/tag/v1.1.6-beta"/>
                <title>beta</title>
              </entry>
              <entry>
                <id>tag:github.com,2008:Repository/1/ci-latest</id>
                <link href="https://github.com/MuXue1230-owo/PCL-N/releases/tag/ci-latest"/>
                <title>CI</title>
                <content type="html">commit: abcdef1234567890</content>
              </entry>
            </feed>
            """;

        IReadOnlyList<LauncherUpdateService.AtomReleaseEntry> entries = LauncherUpdateService.ParseAtomFeed(xml);
        Assert.AreEqual(3, entries.Count);
        Assert.AreEqual("v1.1.6-release", entries[0].Tag);
        Assert.AreEqual("PCL N v1.1.6", entries[0].Title);
        Assert.IsTrue(entries[0].Notes?.Contains("hello", StringComparison.Ordinal) == true);
        Assert.IsTrue(entries[0].Notes?.Contains("*world*", StringComparison.Ordinal) == true);
        Assert.AreEqual("v1.1.6-beta", entries[1].Tag);
        Assert.AreEqual("ci-latest", entries[2].Tag);
    }

    [TestMethod]
    public void ResolveRuntimeId_ReturnsNonEmpty()
    {
        Assert.IsFalse(string.IsNullOrWhiteSpace(LauncherUpdateService.ResolveRuntimeId()));
    }

    [TestMethod]
    public void CompareVersions_TreatsDisplayAndTagReleaseAsEqual()
    {
        // Local DisplayVersion is "1.1.8 release"; remote tag normalizes to "1.1.8-release".
        Assert.AreEqual(0, LauncherUpdateService.CompareVersions("1.1.8 release", "v1.1.8-release"));
        Assert.AreEqual(0, LauncherUpdateService.CompareVersions("1.1.8-release", "1.1.8"));
        Assert.AreEqual(0, LauncherUpdateService.CompareVersions("v1.1.8", "1.1.8 release"));
        Assert.IsTrue(LauncherUpdateService.CompareVersions("1.1.9-release", "1.1.8 release") > 0);
        Assert.IsTrue(LauncherUpdateService.CompareVersions("1.1.8-beta", "1.1.8 release") < 0);
        Assert.IsTrue(LauncherUpdateService.CompareVersions("1.1.8 release", "1.1.8-beta") > 0);
    }

    [TestMethod]
    public void NormalizeVersion_UnifiesSpaceAndDashSuffix()
    {
        Assert.AreEqual("1.1.8-release", LauncherUpdateService.NormalizeVersion("1.1.8 release"));
        Assert.AreEqual("1.1.8-release", LauncherUpdateService.NormalizeVersion("v1.1.8-release"));
        Assert.AreEqual("1.1.8", LauncherUpdateService.NormalizeVersion("v1.1.8"));
    }

    [TestMethod]
    public void BuildIdentity_NormalizesRuntimeAndPluginVariant()
    {
        Assert.AreEqual(
            "NoRuntime",
            LauncherBuildIdentity.NormalizeRuntimeVariant("NoRuntime"));
        Assert.AreEqual(
            "SelfContained",
            LauncherBuildIdentity.NormalizeRuntimeVariant("SelfContained"));
        Assert.AreEqual("CI", LauncherBuildIdentity.NormalizeConfiguration("dev"));
    }

    [TestMethod]
    public async Task CheckAsync_FollowsPermanentRedirectForReleaseFeed()
    {
        int feedRequests = 0;
        RoutingHandler handler = new(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.Equals("/owner/repo/releases.atom", StringComparison.Ordinal))
            {
                feedRequests++;
                return Redirect("https://github.test/owner/canonical-repo/releases.atom");
            }
            if (path.EndsWith("/canonical-repo/releases.atom", StringComparison.Ordinal))
                return XmlResponse(ReleaseFeed("v1.2.0-beta"));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using LauncherUpdateService service = new(new HttpClient(handler), "owner", "repo");

        LauncherUpdateCheckResult result = await service.CheckAsync(
            UpdateChannel.Beta,
            new LauncherBuildIdentity("1.1.0 beta", "win-x64", "NoRuntime", "Beta"));

        Assert.IsTrue(result.Success, result.ErrorMessage);
        Assert.IsTrue(result.IsUpdateAvailable);
        Assert.AreEqual("1.2.0-beta", result.LatestVersion);
        Assert.AreEqual(1, feedRequests);
    }

    [TestMethod]
    public async Task CheckAsync_RejectsInsecureReleaseFeedRedirect()
    {
        RoutingHandler handler = new(request => request.RequestUri!.AbsolutePath.EndsWith(
            "/releases.atom", StringComparison.Ordinal)
            ? Redirect("http://github.test/owner/repo/releases.atom")
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        using LauncherUpdateService service = new(new HttpClient(handler), "owner", "repo");

        InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            service.CheckAsync(
                UpdateChannel.Beta,
                new LauncherBuildIdentity("1.1.0 beta", "win-x64", "NoRuntime", "Beta")));

        StringAssert.Contains(error.Message, "不安全");
    }

    [TestMethod]
    public async Task CheckCiAsync_UsesArtifactMetadataForEverySuccessfulCommit()
    {
        const string remoteCommit = "1234567890abcdef1234567890abcdef12345678";
        RoutingHandler handler = new(request =>
        {
            Assert.AreNotEqual(
                "api.github.com",
                request.RequestUri!.Host,
                "Launcher updates must not consume GitHub REST API quota.");
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/releases.atom", StringComparison.Ordinal))
                return XmlResponse(ReleaseFeed("ci-latest"));
            if (path.EndsWith("PCL_N_CI_win-x64_SelfContained.ci.json", StringComparison.Ordinal))
            {
                return JsonResponse($$"""
                    {
                      "channel": "CI",
                      "commit": "{{remoteCommit}}",
                      "ref": "refs/heads/dev",
                      "runId": "42",
                      "artifact": "PCL_N_CI_win-x64_SelfContained",
                      "supportsPatches": false,
                      "builtAt": "2026-07-16T15:00:00Z"
                    }
                    """);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using LauncherUpdateService service = new(new HttpClient(handler), "owner", "repo");
        LauncherBuildIdentity identity = new("1.2.1 beta", "win-x64", "NoRuntime", "Beta");

        LauncherUpdateCheckResult oldBuild = await service.CheckAsync(
            UpdateChannel.CI,
            identity,
            currentCommitSha: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        LauncherUpdateCheckResult currentBuild = await service.CheckAsync(
            UpdateChannel.CI,
            identity,
            currentCommitSha: remoteCommit);

        Assert.IsTrue(oldBuild.Success);
        Assert.IsTrue(oldBuild.IsUpdateAvailable);
        Assert.AreEqual(remoteCommit, oldBuild.RemoteCommitSha);
        Assert.IsFalse(oldBuild.SupportsPatches);
        Assert.AreEqual("PCL_N_CI_win-x64_SelfContained.zip", oldBuild.Package?.TargetAssetName);
        Assert.AreEqual("SelfContained", oldBuild.Package?.RuntimeVariant);
        Assert.IsTrue(currentBuild.Success);
        Assert.IsFalse(currentBuild.IsUpdateAvailable);
    }

    [TestMethod]
    public async Task CheckAsync_SelectsExactVariantAndBuildsMultiHopPatchPlan()
    {
        RoutingHandler handler = new(request =>
        {
            Assert.AreNotEqual(
                "api.github.com",
                request.RequestUri!.Host,
                "Launcher updates must not consume GitHub REST API quota.");
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/releases.atom", StringComparison.Ordinal))
            {
                return XmlResponse(ReleaseFeed(
                    "v1.2.0-release",
                    "<h2>Complete changelog</h2><ul><li>First fix</li><li>Second fix</li></ul>"));
            }
            if (path.EndsWith("/releases/latest", StringComparison.Ordinal))
                return Redirect("https://github.test/owner/repo/releases/tag/v1.2.0-release");
            if (path.Contains("/v1.2.0-release/patch-index.json", StringComparison.Ordinal))
                return JsonResponse(PatchIndex("1.2.0-release", "v1.2.0-release", "v1.1.0-release", "v1.1.0-release", 40, "target-sha", "from-11", ["v1.1.0-release"]));
            if (path.Contains("/v1.1.0-release/patch-index.json", StringComparison.Ordinal))
                return JsonResponse(PatchIndex("1.1.0-release", "v1.1.0-release", "1.0.0-release", "v1.0.0-release", 30, "from-11", "from-10", []));
            if (path.EndsWith("PCL_N_Release_win-x64_NoRuntime.zip", StringComparison.Ordinal))
                return BytesResponse(new byte[1000]);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using HttpClient client = new(handler);
        using LauncherUpdateService service = new(client, "owner", "repo");

        LauncherUpdateCheckResult result = await service.CheckAsync(
            UpdateChannel.Release,
            new LauncherBuildIdentity("1.0.0 release", "win-x64", "NoRuntime", "Release"));

        Assert.IsTrue(result.Success);
        Assert.IsTrue(result.IsUpdateAvailable);
        Assert.IsNotNull(result.Package);
        Assert.AreEqual("NoRuntime", result.Package.RuntimeVariant);
        Assert.AreEqual("## Complete changelog\n\n- First fix\n- Second fix", result.ReleaseNotes);
        Assert.AreEqual("PCL_N_Release_win-x64_NoRuntime.zip", result.Package.TargetAssetName);
        Assert.AreEqual(2, result.Package.PatchSteps.Count);
        Assert.AreEqual("1.1.0-release", result.Package.PatchSteps[0].TargetVersion);
        Assert.AreEqual("1.2.0-release", result.Package.PatchSteps[1].TargetVersion);
        Assert.IsTrue(result.Package.PatchSteps[0].DownloadUrl.EndsWith("win-x64__NoRuntime__1.0.0-release-to-1.1.0-release.hdiff", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CheckAsync_UsesFullPackageWhenPatchChainIsNotSmaller()
    {
        RoutingHandler handler = new(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/releases.atom", StringComparison.Ordinal))
                return XmlResponse(ReleaseFeed("v1.2.0-release"));
            if (path.EndsWith("/releases/latest", StringComparison.Ordinal))
                return Redirect("https://github.test/owner/repo/releases/tag/v1.2.0-release");
            if (path.Contains("/v1.2.0-release/patch-index.json", StringComparison.Ordinal))
                return JsonResponse(PatchIndex("1.2.0-release", "v1.2.0-release", "1.0.0-release", "v1.0.0-release", 101, "target-sha", "from-10", []));
            if (path.EndsWith("PCL_N_Release_win-x64_NoRuntime.zip", StringComparison.Ordinal))
                return BytesResponse(new byte[100]);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using LauncherUpdateService service = new(new HttpClient(handler), "owner", "repo");

        LauncherUpdateCheckResult result = await service.CheckAsync(
            UpdateChannel.Release,
            new LauncherBuildIdentity("1.0.0 release", "win-x64", "NoRuntime", "Release"));

        Assert.IsNotNull(result.Package);
        Assert.AreEqual(0, result.Package.PatchSteps.Count);
        Assert.IsFalse(result.SupportsPatches);
    }

    [TestMethod]
    public async Task CheckAsync_LegacyNoPluginBuildMigratesToHostFullPackage()
    {
        RoutingHandler handler = new(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/releases.atom", StringComparison.Ordinal))
                return XmlResponse(ReleaseFeed("v1.2.0-release"));
            if (path.EndsWith("/releases/latest", StringComparison.Ordinal))
                return Redirect("https://github.test/owner/repo/releases/tag/v1.2.0-release");
            if (path.Contains("/v1.2.0-release/patch-index.json", StringComparison.Ordinal))
                return JsonResponse(PatchIndex(
                    "1.2.0-release",
                    "v1.2.0-release",
                    "1.0.0-release",
                    "v1.0.0-release",
                    10,
                    "target-sha",
                    "from-with-plugin",
                    []));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using LauncherUpdateService service = new(new HttpClient(handler), "owner", "repo");

        LauncherUpdateCheckResult result = await service.CheckAsync(
            UpdateChannel.Release,
            new LauncherBuildIdentity("1.0.0 release", "win-x64", "NoRuntime", "Release"));

        Assert.IsNotNull(result.Package);
        Assert.AreEqual("NoRuntime", result.Package.RuntimeVariant);
        Assert.AreEqual("PCL_N_Release_win-x64_NoRuntime.zip", result.Package.TargetAssetName);
        // Host-only variants may use the patch graph when indexes match.
    }

    [TestMethod]
    public async Task Installer_FallsBackToFullPackageAndStagesVerifiedBinary()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcl-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] expected = Encoding.UTF8.GetBytes("new launcher binary");
            byte[] archive = CreateZip("PCL-N-Edition.exe", expected);
            string targetSha = Convert.ToHexStringLower(SHA256.HashData(expected));
            using HttpClient client = new(new RoutingHandler(_ => BytesResponse(archive)));
            using LauncherUpdateInstaller installer = new(client, new AcceptAllGpgVerifier());
            string current = Path.Combine(root, "PCL-N-Edition.exe");
            await File.WriteAllTextAsync(current, "old launcher binary");
            LauncherUpdatePackage package = new(
                "1.2.0-release",
                "v1.2.0-release",
                "https://download.test/PCL.zip",
                "PCL.zip",
                "PCL-N-Edition.exe",
                targetSha,
                expected.Length,
                [new LauncherUpdatePatchStep("1.0.0", "1.2.0", "https://download.test/a.hdiff", "00", 1, "00", 1, targetSha, expected.Length)],
                "win-x64",
                "SelfContained",
                "Release",
                "https://download.test/PCL.zip.asc",
                "https://download.test/PCL.zip.binary.asc");

            PreparedLauncherUpdate prepared = await installer.PrepareAsync(package, current, hpatchzPath: null);

            try
            {
                CollectionAssert.AreEqual(expected, await File.ReadAllBytesAsync(prepared.StagedExecutablePath));
                Assert.IsFalse(prepared.UsedPatch);
            }
            finally
            {
                if (Directory.Exists(prepared.WorkDirectory))
                    Directory.Delete(prepared.WorkDirectory, recursive: true);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task GpgVerifier_AcceptsPinnedReleaseKeySignature()
    {
        const string signatureText = """
            -----BEGIN PGP SIGNATURE-----

            iJEEABYKADkWIQRXASGNabUx4aftNbtuMfWXSic67gUCaljqzRsUgAAAAAAEAA5t
            YW51MiwyLjUrMS4xMiwyLDEACgkQbjH1l0onOu75xQEAp/sh1N1prODi/PTektMy
            F83vBveFCkLxqw2pdM7NNugBAMOx4NTqDsy/EBjTTBUUzvcXCp+wdjb7fO6jOAsn
            JKsB
            =4FMC
            -----END PGP SIGNATURE-----
            """;
        await using Stream resource = typeof(LauncherGpgVerifier).Assembly.GetManifestResourceStream(
                "PCL.Application.Updates.PclNReleasePublicKey.asc")
            ?? throw new AssertFailedException("Pinned public key resource is missing.");
        using StreamReader reader = new(resource, Encoding.ASCII);
        string normalizedKey = (await reader.ReadToEndAsync()).Replace("\r\n", "\n", StringComparison.Ordinal);
        await using MemoryStream content = new(Encoding.ASCII.GetBytes(normalizedKey));
        await using MemoryStream signature = new(Encoding.ASCII.GetBytes(signatureText));

        await LauncherGpgVerifier.Instance.VerifyAsync(content, signature, CancellationToken.None);
    }

    [TestMethod]
    public async Task GpgVerifier_AcceptsForwardOnlyHttpStyleStreams()
    {
        const string signatureText = """
            -----BEGIN PGP SIGNATURE-----

            iJEEABYKADkWIQRXASGNabUx4aftNbtuMfWXSic67gUCaljqzRsUgAAAAAAEAA5t
            YW51MiwyLjUrMS4xMiwyLDEACgkQbjH1l0onOu75xQEAp/sh1N1prODi/PTektMy
            F83vBveFCkLxqw2pdM7NNugBAMOx4NTqDsy/EBjTTBUUzvcXCp+wdjb7fO6jOAsn
            JKsB
            =4FMC
            -----END PGP SIGNATURE-----
            """;
        await using Stream resource = typeof(LauncherGpgVerifier).Assembly.GetManifestResourceStream(
                "PCL.Application.Updates.PclNReleasePublicKey.asc")
            ?? throw new AssertFailedException("Pinned public key resource is missing.");
        using StreamReader reader = new(resource, Encoding.ASCII);
        byte[] contentBytes = Encoding.ASCII.GetBytes(
            (await reader.ReadToEndAsync()).Replace("\r\n", "\n", StringComparison.Ordinal));
        byte[] signatureBytes = Encoding.ASCII.GetBytes(signatureText);
        await using NonSeekableReadStream content = new(new MemoryStream(contentBytes));
        await using NonSeekableReadStream signature = new(new MemoryStream(signatureBytes));

        Assert.IsFalse(content.CanSeek);
        Assert.IsFalse(signature.CanSeek);
        await LauncherGpgVerifier.Instance.VerifyAsync(content, signature, CancellationToken.None);
    }

    private static string ReleaseFeed(string tag, string? contentHtml = null)
    {
        string content = string.IsNullOrEmpty(contentHtml)
            ? string.Empty
            : $"<content type=\"html\"><![CDATA[{contentHtml}]]></content>";
        return $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <id>tag:github.com,2008:Repository/1/{{tag}}</id>
                <link href="https://github.test/owner/repo/releases/tag/{{tag}}"/>
                <title>{{tag}}</title>
                {{content}}
              </entry>
            </feed>
            """;
    }

    private static string PatchIndex(
        string targetVersion,
        string targetTag,
        string fromVersion,
        string fromTag,
        long patchSize,
        string targetSha,
        string fromSha,
        string[] selectedFromTags)
    {
        string selected = string.Join(',', selectedFromTags.Select(tag => $"\"{tag}\""));
        string patchName = $"patches/win-x64/NoRuntime/win-x64__NoRuntime__{fromVersion}-to-{targetVersion}.hdiff";
        return $$"""
            {
              "formatVersion": 2,
              "targetVersion": "{{targetVersion}}",
              "targetTag": "{{targetTag}}",
              "strategy": { "selectedFromTags": [{{selected}}] },
              "variants": [{
                "runtimeId": "win-x64",
                "runtimeVariant": "NoRuntime",
                "configuration": "Release",
                "targetAssetName": "PCL_N_Release_win-x64_NoRuntime.zip",
                "targetBinaryName": "PCL-N-Edition.exe",
                "targetSha256": "{{targetSha}}",
                "targetSize": 5000,
                "patches": [{
                  "fromVersion": "{{fromVersion}}",
                  "fromTag": "{{fromTag}}",
                  "algorithm": "hdiffpatch",
                  "fileName": "{{patchName}}",
                  "sha256": "patch-sha",
                  "size": {{patchSize}},
                  "fromSha256": "{{fromSha}}",
                  "fromSize": 4000
                }]
              }]
            }
            """;
    }

    private static byte[] CreateZip(string name, byte[] content)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry(name);
            using Stream target = entry.Open();
            target.Write(content);
        }
        return stream.ToArray();
    }

    private static HttpResponseMessage XmlResponse(string value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/atom+xml") };

    private static HttpResponseMessage JsonResponse(string value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage BytesResponse(byte[] value) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(value) };

    private static HttpResponseMessage Redirect(string location)
    {
        HttpResponseMessage response = new(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(route(request));
    }

    private sealed class NonSeekableReadStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }

    private sealed class AcceptAllGpgVerifier : ILauncherGpgVerifier
    {
        public Task VerifyAsync(Stream content, Stream detachedSignature, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}

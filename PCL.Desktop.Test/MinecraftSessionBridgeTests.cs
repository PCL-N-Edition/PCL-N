// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jvm.NET;
using Jvm.NET.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Application.Launching;
using PCL.Desktop.Features.Launching;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class MinecraftSessionBridgeTests
{
    [TestMethod]
    public void JvmHostInitializationOptions_UseJniOnlySafeMode()
    {
        MinecraftJvmHostRequest request = new()
        {
            JavaExecutablePath = "java",
            WorkingDirectory = "game",
            MainClass = "example.Main",
            PlayerName = "Player",
            PlayerUuid = Guid.Empty.ToString("N"),
            JavaMajorVersion = 21,
            ClasspathEntries = ["client.jar"]
        };

        JvmInitializationOptions options = MinecraftJvmHostEntryPoint.CreateInitializationOptions(
            request,
            ["-Xmx2G"],
            "jdk-bin");

        Assert.IsFalse(options.EnableBytecodeModification);
        Assert.IsFalse(options.EnableEventListening);
        Assert.IsFalse(options.RequireJvmti);
        Assert.AreEqual("jdk-bin", options.JdkBinPath);
        CollectionAssert.AreEqual(new[] { "-Xmx2G" }, options.VmArguments.ToArray());
    }

    [TestMethod]
    public void JvmHostEntryPoint_UsesJniOnlyOptionsWithoutJvmtiSentinel()
    {
        MinecraftJvmHostRequest request = new()
        {
            JavaExecutablePath = "C:\\jdk-21\\bin\\java.exe",
            WorkingDirectory = "C:\\game",
            MainClass = "example.Main",
            PlayerName = "Player",
            PlayerUuid = Guid.Empty.ToString("N"),
            JavaMajorVersion = 21,
            ClasspathEntries = ["client.jar"]
        };
        JvmInitializationOptions options = MinecraftJvmHostEntryPoint.CreateInitializationOptions(
            request,
            request.VmArguments,
            "C:\\jdk-21\\bin");

        Assert.IsFalse(options.RequireJvmti);
        Assert.IsFalse(options.EnableBytecodeModification);
        Assert.IsFalse(options.EnableEventListening);
    }

    [TestMethod]
    public async Task UnexpectedHostExitReport_DoesNotReadIdFromDisposedProcess()
    {
        ProcessStartInfo startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c exit 9") { UseShellExecute = false }
            : new ProcessStartInfo("/bin/sh", "-c 'exit 9'") { UseShellExecute = false };
        Process process = Process.Start(startInfo)!;
        int processId = process.Id;
        await process.WaitForExitAsync();
        process.Dispose();

        MinecraftLaunchFaultReport? report = await MinecraftJvmHostProcessLauncher.CreateUnexpectedHostExitReportAsync(
            process,
            processId,
            "JvmMode");

        Assert.IsNull(report);
    }

    [TestMethod]
    public void NeoForgeDependencyLog_IsReportedWithoutProcessExit()
    {
        MinecraftLaunchFaultReport? report = MinecraftJvmHostProcessLauncher.AnalyzeNeoForgeLogLines(
        [
            "[main/ERROR] Missing or unsupported mandatory dependencies:",
            "Mod farmersdelight requires version [1.2,) of bookshelf"
        ]);

        Assert.IsNotNull(report);
        Assert.AreEqual(MinecraftLaunchFaultCode.MissingModDependency, report.Code);
        Assert.AreEqual("NeoForgeDependencyCheck", report.Stage);
        CollectionAssert.Contains(
            report.AllowedActions,
            MinecraftRepairActionKind.InstallMissingModDependencies);
    }

    [TestMethod]
    public void NeoForgeNormalLoadingLog_DoesNotCreateFault()
    {
        MinecraftLaunchFaultReport? report = MinecraftJvmHostProcessLauncher.AnalyzeNeoForgeLogLines(
        [
            "[main/INFO] NeoForge mod loading, version 21.1.235",
            "[Render thread/INFO] Reloading ResourceManager"
        ]);

        Assert.IsNull(report);
    }

    [TestMethod]
    public void JvmHostEntryPoint_RunsSimpleMainWhenJavaIsConfigured()
    {
        string? java = Environment.GetEnvironmentVariable("PCL_JVM_HOST_SMOKE_JAVA");
        if (string.IsNullOrWhiteSpace(java) || !File.Exists(java))
            Assert.Inconclusive("Set PCL_JVM_HOST_SMOKE_JAVA to run the native Jvm.NET smoke test.");

        string bin = MinecraftJvmHostEntryPoint.ResolveJdkBinPath(java);
        string javac = Path.Combine(bin, OperatingSystem.IsWindows() ? "javac.exe" : "javac");
        if (!File.Exists(javac))
            Assert.Inconclusive("The configured Java runtime does not include javac.");

        string root = Path.Combine(Path.GetTempPath(), "pcl-jvm-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string sourcePath = Path.Combine(root, "PclJvmHostSmoke.java");
            File.WriteAllText(
                sourcePath,
                "public final class PclJvmHostSmoke { public static void main(String[] args) { " +
                "if (args.length != 1 || !\"ok\".equals(args[0])) throw new IllegalArgumentException(); " +
                "String endpoint = System.getProperty(\"minecraft.api.session.host\", \"\"); " +
                "if (!endpoint.startsWith(\"http://127.0.0.1:\") || !endpoint.endsWith(\"/sessionserver\")) " +
                "throw new IllegalStateException(endpoint); } }");
            ProcessStartInfo compileInfo = new()
            {
                FileName = javac,
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            compileInfo.ArgumentList.Add("-d");
            compileInfo.ArgumentList.Add(root);
            compileInfo.ArgumentList.Add(sourcePath);
            using Process compiler = Process.Start(compileInfo)!;
            compiler.WaitForExit();
            Assert.AreEqual(0, compiler.ExitCode, "Java smoke class compilation failed.");

            string modulePath = Path.Combine(root, "module-path");
            Directory.CreateDirectory(modulePath);
            MinecraftJvmHostRequest request = new()
            {
                JavaExecutablePath = java,
                WorkingDirectory = root,
                MainClass = "PclJvmHostSmoke",
                PlayerName = "Smoke",
                PlayerUuid = Guid.Empty.ToString("N"),
                JavaMajorVersion = 21,
                VmArguments =
                [
                    "--module-path=" + modulePath,
                    "--add-modules=ALL-MODULE-PATH",
                    "--add-opens=java.base/java.lang=ALL-UNNAMED"
                ],
                ClasspathEntries = [root],
                GameArguments = ["ok"],
                IdentityMode = MinecraftJvmHostIdentityMode.Offline
            };
            string requestPath = Path.Combine(root, "request.json");
            File.WriteAllText(
                requestPath,
                JsonSerializer.Serialize(request, MinecraftJvmHostJsonContext.Default.MinecraftJvmHostRequest));

            Assert.AreEqual(0, MinecraftJvmHostEntryPoint.Run(requestPath));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task OfflineProfile_ExposesSessionScopedCustomSkin()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcl-jvm-bridge-" + Guid.NewGuid().ToString("N"));
        string skinPath = Path.Combine(root, "skin.png");
        Directory.CreateDirectory(root);
        byte[] png = CreatePngHeader(64, 64);
        await File.WriteAllBytesAsync(skinPath, png);

        try
        {
            MinecraftJvmHostRequest request = new()
            {
                JavaExecutablePath = Path.Combine(root, "java"),
                WorkingDirectory = root,
                MainClass = "net.minecraft.client.main.Main",
                PlayerName = "OfflineUser",
                PlayerUuid = "0123456789abcdef0123456789abcdef",
                JavaMajorVersion = 17,
                ClasspathEntries = [Path.Combine(root, "client.jar")],
                IdentityMode = MinecraftJvmHostIdentityMode.Offline,
                OfflineSkinSource = skinPath,
                OfflineSkinSlim = true
            };

            using JvmHostLifecycleWriter lifecycle = new(string.Empty);
            using MinecraftSessionBridge bridge = MinecraftSessionBridge.Start(request, lifecycle);
            using HttpClient client = new(new HttpClientHandler { UseProxy = false });

            string profileJson = await client.GetStringAsync(
                bridge.BaseUrl + "/sessionserver/session/minecraft/profile/" + request.PlayerUuid);
            JsonObject profile = JsonNode.Parse(profileJson)!.AsObject();
            Assert.AreEqual(request.PlayerName, profile["name"]!.GetValue<string>());
            JsonObject property = profile["properties"]!.AsArray()[0]!.AsObject();
            string value = property["value"]!.GetValue<string>();
            JsonObject texturesPayload = JsonNode.Parse(Convert.FromBase64String(value))!.AsObject();
            JsonObject skin = texturesPayload["textures"]!["SKIN"]!.AsObject();
            Assert.AreEqual("slim", skin["metadata"]!["model"]!.GetValue<string>());

            string textureUrl = skin["url"]!.GetValue<string>();
            byte[] downloaded = await client.GetByteArrayAsync(textureUrl);
            CollectionAssert.AreEqual(png, downloaded);

            string discoveryJson = await client.GetStringAsync(bridge.BaseUrl + "/minecraft/client");
            StringAssert.Contains(discoveryJson, "session");
            StringAssert.Contains(discoveryJson, "getProfileById");
            StringAssert.Contains(discoveryJson, "getPublicKeys");
            StringAssert.Contains(discoveryJson, bridge.BaseUrl);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ThirdPartyJoin_UsesStoredTokenAndSelectedProfile()
    {
        using TcpListener upstream = new(IPAddress.Loopback, 0);
        upstream.Start();
        int port = ((IPEndPoint)upstream.LocalEndpoint).Port;
        string? receivedBody = null;
        Task server = Task.Run(async () =>
        {
            using TcpClient connection = await upstream.AcceptTcpClientAsync();
            using NetworkStream stream = connection.GetStream();
            (string _, byte[] body) = await ReadHttpRequestAsync(stream);
            receivedBody = Encoding.UTF8.GetString(body);
            byte[] response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response);
        });

        MinecraftJvmHostRequest request = new()
        {
            JavaExecutablePath = "java",
            WorkingDirectory = "game",
            MainClass = "example.Main",
            PlayerName = "ThirdPartyUser",
            PlayerUuid = "0123456789abcdef0123456789abcdef",
            AccessToken = "stored-token",
            JavaMajorVersion = 21,
            ClasspathEntries = ["client.jar"],
            IdentityMode = MinecraftJvmHostIdentityMode.ThirdParty,
            AuthServer = $"http://127.0.0.1:{port}"
        };
        using JvmHostLifecycleWriter lifecycle = new(string.Empty);
        using MinecraftSessionBridge bridge = MinecraftSessionBridge.Start(request, lifecycle);
        using HttpClient client = new(new HttpClientHandler { UseProxy = false });
        using StringContent content = new(
            "{\"accessToken\":\"wrong\",\"selectedProfile\":\"wrong\",\"serverId\":\"hash\"}",
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await client.PostAsync(
            bridge.BaseUrl + "/sessionserver/session/minecraft/join",
            content);
        await server;

        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        JsonObject join = JsonNode.Parse(receivedBody!)!.AsObject();
        Assert.AreEqual("stored-token", join["accessToken"]!.GetValue<string>());
        Assert.AreEqual(request.PlayerUuid, join["selectedProfile"]!.GetValue<string>());
        Assert.AreEqual("hash", join["serverId"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ThirdPartyMinecraftServicesProfile_IsServedLocally()
    {
        MinecraftJvmHostRequest request = new()
        {
            JavaExecutablePath = "java",
            WorkingDirectory = "game",
            MainClass = "example.Main",
            PlayerName = "ThirdPartyUser",
            PlayerUuid = "0123456789abcdef0123456789abcdef",
            AccessToken = "stored-token",
            JavaMajorVersion = 21,
            ClasspathEntries = ["client.jar"],
            IdentityMode = MinecraftJvmHostIdentityMode.ThirdParty,
            // Closed local port: connection fails immediately (no DNS hang) when hydrating skins.
            AuthServer = "http://127.0.0.1:1"
        };
        using JvmHostLifecycleWriter lifecycle = new(string.Empty);
        using MinecraftSessionBridge bridge = MinecraftSessionBridge.Start(request, lifecycle);
        using HttpClient client = new(new HttpClientHandler { UseProxy = false });

        JsonObject profile = JsonNode.Parse(await client.GetStringAsync(
            bridge.BaseUrl + "/minecraftservices/minecraft/profile"))!.AsObject();

        Assert.AreEqual(request.PlayerUuid, profile["id"]!.GetValue<string>());
        Assert.AreEqual(request.PlayerName, profile["name"]!.GetValue<string>());
        Assert.IsNotNull(profile["skins"]?.AsArray());
        Assert.IsNotNull(profile["capes"]?.AsArray());
    }

    [TestMethod]
    public async Task ThirdPartyPlayerCertificates_ReturnUsableRsaKeyPair()
    {
        MinecraftJvmHostRequest request = new()
        {
            JavaExecutablePath = "java",
            WorkingDirectory = "game",
            MainClass = "example.Main",
            PlayerName = "ThirdPartyUser",
            PlayerUuid = "0123456789abcdef0123456789abcdef",
            AccessToken = "stored-token",
            JavaMajorVersion = 21,
            ClasspathEntries = ["client.jar"],
            IdentityMode = MinecraftJvmHostIdentityMode.ThirdParty,
            AuthServer = "http://127.0.0.1:1"
        };
        using JvmHostLifecycleWriter lifecycle = new(string.Empty);
        using MinecraftSessionBridge bridge = MinecraftSessionBridge.Start(request, lifecycle);
        using HttpClient client = new(new HttpClientHandler { UseProxy = false });

        using HttpResponseMessage response = await client.PostAsync(
            bridge.BaseUrl + "/minecraftservices/player/certificates",
            new ByteArrayContent([]) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") } });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        JsonObject body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        string privateKey = body["keyPair"]!["privateKey"]!.GetValue<string>();
        string publicKey = body["keyPair"]!["publicKey"]!.GetValue<string>();
        StringAssert.Contains(privateKey, "BEGIN RSA PRIVATE KEY");
        StringAssert.Contains(publicKey, "BEGIN RSA PUBLIC KEY");
        Assert.IsFalse(string.IsNullOrWhiteSpace(body["publicKeySignature"]?.GetValue<string>()));
        Assert.IsFalse(string.IsNullOrWhiteSpace(body["publicKeySignatureV2"]?.GetValue<string>()));
        Assert.IsFalse(string.IsNullOrWhiteSpace(body["expiresAt"]?.GetValue<string>()));
    }

    [TestMethod]
    public async Task ThirdPartyMinecraftServicesProfile_HydratesSkinsFromSessionServer()
    {
        string uuid = "0123456789abcdef0123456789abcdef";
        string skinUrl = "https://textures.example.test/skin.png";
        JsonObject texturesPayload = new()
        {
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["profileId"] = uuid,
            ["profileName"] = "ThirdPartyUser",
            ["textures"] = new JsonObject
            {
                ["SKIN"] = new JsonObject
                {
                    ["url"] = skinUrl,
                    ["metadata"] = new JsonObject { ["model"] = "slim" }
                }
            }
        };
        string texturesValue = Convert.ToBase64String(Encoding.UTF8.GetBytes(texturesPayload.ToJsonString()));
        string profileBody = new JsonObject
        {
            ["id"] = uuid,
            ["name"] = "ThirdPartyUser",
            ["properties"] = new JsonArray(new JsonObject
            {
                ["name"] = "textures",
                ["value"] = texturesValue,
                ["signature"] = "AA=="
            })
        }.ToJsonString();

        using TcpListener upstream = new(IPAddress.Loopback, 0);
        upstream.Start();
        int port = ((IPEndPoint)upstream.LocalEndpoint).Port;
        using CancellationTokenSource serverCts = new(TimeSpan.FromSeconds(10));
        Task server = Task.Run(async () =>
        {
            using TcpClient connection = await upstream.AcceptTcpClientAsync(serverCts.Token);
            using NetworkStream stream = connection.GetStream();
            await ReadHttpRequestAsync(stream);
            byte[] payload = Encoding.UTF8.GetBytes(profileBody);
            byte[] header = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" +
                $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header);
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
        }, serverCts.Token);

        MinecraftJvmHostRequest request = new()
        {
            JavaExecutablePath = "java",
            WorkingDirectory = "game",
            MainClass = "example.Main",
            PlayerName = "ThirdPartyUser",
            PlayerUuid = uuid,
            AccessToken = "stored-token",
            JavaMajorVersion = 21,
            ClasspathEntries = ["client.jar"],
            IdentityMode = MinecraftJvmHostIdentityMode.ThirdParty,
            AuthServer = $"http://127.0.0.1:{port}"
        };
        using JvmHostLifecycleWriter lifecycle = new(string.Empty);
        using MinecraftSessionBridge bridge = MinecraftSessionBridge.Start(request, lifecycle);
        using HttpClient client = new(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        using CancellationTokenSource requestCts = new(TimeSpan.FromSeconds(10));
        JsonObject profile = JsonNode.Parse(await client.GetStringAsync(
            bridge.BaseUrl + "/minecraftservices/minecraft/profile",
            requestCts.Token))!.AsObject();
        await server.WaitAsync(TimeSpan.FromSeconds(10));

        JsonArray skins = profile["skins"]!.AsArray();
        Assert.AreEqual(1, skins.Count);
        Assert.AreEqual("ACTIVE", skins[0]!["state"]!.GetValue<string>());
        Assert.AreEqual("SLIM", skins[0]!["variant"]!.GetValue<string>());
        StringAssert.StartsWith(skins[0]!["url"]!.GetValue<string>(), bridge.BaseUrl + "/pcl/texture/");
    }

    [TestMethod]
    public async Task OfflinePlayerCertificates_ReturnUsableRsaKeyPair()
    {
        MinecraftJvmHostRequest request = new()
        {
            JavaExecutablePath = "java",
            WorkingDirectory = "game",
            MainClass = "example.Main",
            PlayerName = "OfflineUser",
            PlayerUuid = "0123456789abcdef0123456789abcdef",
            JavaMajorVersion = 21,
            ClasspathEntries = ["client.jar"],
            IdentityMode = MinecraftJvmHostIdentityMode.Offline
        };
        using JvmHostLifecycleWriter lifecycle = new(string.Empty);
        using MinecraftSessionBridge bridge = MinecraftSessionBridge.Start(request, lifecycle);
        using HttpClient client = new(new HttpClientHandler { UseProxy = false });

        using HttpResponseMessage response = await client.PostAsync(
            bridge.BaseUrl + "/minecraftservices/player/certificates",
            new ByteArrayContent([]) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") } });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        JsonObject body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        StringAssert.Contains(body["keyPair"]!["privateKey"]!.GetValue<string>(), "BEGIN RSA PRIVATE KEY");
        StringAssert.Contains(body["keyPair"]!["publicKey"]!.GetValue<string>(), "BEGIN RSA PUBLIC KEY");
    }

    private static async Task<(string Headers, byte[] Body)> ReadHttpRequestAsync(NetworkStream stream)
    {
        using MemoryStream bytes = new();
        byte[] buffer = new byte[1024];
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int read = await stream.ReadAsync(buffer);
            Assert.IsGreaterThan(0, read);
            bytes.Write(buffer, 0, read);
            headerEnd = Encoding.ASCII.GetString(bytes.ToArray()).IndexOf("\r\n\r\n", StringComparison.Ordinal);
        }
        byte[] all = bytes.ToArray();
        string headers = Encoding.ASCII.GetString(all, 0, headerEnd);
        int contentLength = headers.Split("\r\n")
            .Where(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            .Select(line => int.Parse(line[(line.IndexOf(':') + 1)..].Trim()))
            .DefaultIfEmpty(0)
            .Single();
        int bodyOffset = headerEnd + 4;
        using MemoryStream body = new();
        if (all.Length > bodyOffset)
            body.Write(all, bodyOffset, all.Length - bodyOffset);
        while (body.Length < contentLength)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, contentLength - (int)body.Length)));
            Assert.IsGreaterThan(0, read);
            body.Write(buffer, 0, read);
        }
        return (headers, body.ToArray());
    }

    private static byte[] CreatePngHeader(int width, int height)
    {
        byte[] bytes = new byte[24];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8, 4), 13);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(bytes, 12);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        return bytes;
    }
}

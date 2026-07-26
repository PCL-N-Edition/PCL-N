// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Application.Minecraft.Launch.Libraries;

namespace PCL.Application.Test;

[TestClass]
public sealed class MinecraftLibraryResolverTests
{
    [TestMethod]
    public void Resolve_ShouldUseArtifactDownloadMetadata()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcl-libs");
        JsonObject versionJson = Parse(
            """
            {
              "libraries": [
                {
                  "name": "org.lwjgl:lwjgl:3.3.3",
                  "downloads": {
                    "artifact": {
                      "path": "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar",
                      "url": "https://libraries.minecraft.net/org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar",
                      "sha1": "abcdef",
                      "size": 123
                    }
                  }
                }
              ]
            }
            """);

        MinecraftLibraryToken token = MinecraftLibraryResolver.Resolve(CreateRequest(versionJson, root))[0];

        Assert.AreEqual("org.lwjgl:lwjgl:3.3.3", token.OriginalName);
        Assert.AreEqual("org.lwjgl:lwjgl", token.NameWithoutVersion);
        Assert.AreEqual("https://libraries.minecraft.net/org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar", token.Url);
        Assert.AreEqual(Path.Combine(root, "libraries", "org", "lwjgl", "lwjgl", "3.3.3", "lwjgl-3.3.3.jar"), token.LocalPath);
        Assert.AreEqual("abcdef", token.Sha1);
        Assert.AreEqual(123L, token.Size);
        Assert.IsFalse(token.IsNatives);
    }

    [TestMethod]
    public void Resolve_ShouldUseLocalInstanceLibraryPath_WhenHintIsLocal()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcl-libs");
        string instance = Path.Combine(root, "versions", "Custom");
        JsonObject versionJson = Parse(
            """
            {
              "libraries": [
                {
                  "name": "custom.loader:bootstrap:1.0",
                  "hint": "local"
                }
              ]
            }
            """);

        MinecraftLibraryToken token = MinecraftLibraryResolver.Resolve(CreateRequest(versionJson, root) with
        {
            TargetInstanceDirectory = instance
        })[0];

        Assert.AreEqual(Path.Combine(instance, "libraries", "bootstrap-1.0.jar"), token.LocalPath);
        Assert.IsTrue(token.IsLocal);
    }

    [TestMethod]
    public void Resolve_ShouldSelectLinuxNativeClassifier()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcl-libs");
        JsonObject versionJson = Parse(
            """
            {
              "libraries": [
                {
                  "name": "org.lwjgl:lwjgl:3.3.3",
                  "natives": {
                    "windows": "natives-windows",
                    "linux": "natives-linux",
                    "osx": "natives-macos"
                  },
                  "downloads": {
                    "classifiers": {
                      "natives-linux": {
                        "path": "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-linux.jar",
                        "url": "https://example.test/linux.jar",
                        "sha1": "linux",
                        "size": 456
                      }
                    }
                  }
                }
              ]
            }
            """);

        MinecraftLibraryToken token = MinecraftLibraryResolver.Resolve(CreateRequest(
            versionJson,
            root,
            MinecraftLibraryOperatingSystem.Linux))[0];

        Assert.IsTrue(token.IsNatives);
        Assert.AreEqual("https://example.test/linux.jar", token.Url);
        Assert.AreEqual(Path.Combine(root, "libraries", "org", "lwjgl", "lwjgl", "3.3.3", "lwjgl-3.3.3-natives-linux.jar"), token.LocalPath);
        Assert.AreEqual("linux", token.Sha1);
        Assert.AreEqual(456L, token.Size);
    }

    [TestMethod]
    public void Resolve_ShouldReplaceNativeArchitecturePlaceholder()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcl-libs");
        JsonObject versionJson = Parse(
            """
            {
              "libraries": [
                {
                  "name": "org.lwjgl:lwjgl:2.9.4",
                  "natives": { "windows": "natives-windows-${arch}" },
                  "downloads": {
                    "classifiers": {
                      "natives-windows-64": {
                        "path": "org/lwjgl/lwjgl/2.9.4/lwjgl-2.9.4-natives-windows-64.jar"
                      }
                    }
                  }
                }
              ]
            }
            """);

        MinecraftLibraryToken token = MinecraftLibraryResolver.Resolve(CreateRequest(
            versionJson,
            root,
            MinecraftLibraryOperatingSystem.Win32) with
        {
            Is64BitArchitecture = true
        })[0];

        Assert.AreEqual(Path.Combine(root, "libraries", "org", "lwjgl", "lwjgl", "2.9.4", "lwjgl-2.9.4-natives-windows-64.jar"), token.LocalPath);
    }

    [TestMethod]
    public void Resolve_ShouldUseArm64LwjglNativeArtifactOnLinuxArm64()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcl-libs");
        JsonObject versionJson = CreateModernLwjglNativeVersionJson("natives-linux");

        MinecraftLibraryToken token = MinecraftLibraryResolver.Resolve(CreateRequest(
            versionJson,
            root,
            MinecraftLibraryOperatingSystem.Linux) with
        {
            IsArm64Architecture = true
        })[0];

        Assert.AreEqual("org.lwjgl:lwjgl:3.4.1:natives-linux-arm64", token.OriginalName);
        Assert.AreEqual(
            Path.Combine(root, "libraries", "org", "lwjgl", "lwjgl", "3.4.1", "lwjgl-3.4.1-natives-linux-arm64.jar"),
            token.LocalPath);
        Assert.AreEqual(
            "https://repo1.maven.org/maven2/org/lwjgl/lwjgl/3.4.1/lwjgl-3.4.1-natives-linux-arm64.jar",
            token.Url);
        Assert.AreEqual("46883f3b622d8b4d7f27b627ca3360cda3db0e0e", token.Sha1);
        Assert.AreEqual(120_615L, token.Size);
        Assert.IsFalse(token.IsNatives);
    }

    [TestMethod]
    public void Resolve_ShouldKeepX64LwjglNativeArtifactOnLinuxX64()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcl-libs");
        JsonObject versionJson = CreateModernLwjglNativeVersionJson("natives-linux");

        MinecraftLibraryToken token = MinecraftLibraryResolver.Resolve(CreateRequest(
            versionJson,
            root,
            MinecraftLibraryOperatingSystem.Linux))[0];

        Assert.AreEqual("org.lwjgl:lwjgl:3.4.1:natives-linux", token.OriginalName);
        Assert.AreEqual("https://libraries.minecraft.net/lwjgl-3.4.1-natives-linux.jar", token.Url);
        Assert.AreEqual("original", token.Sha1);
        Assert.AreEqual(123L, token.Size);
    }

    [TestMethod]
    public void Resolve_ShouldKeepExistingLinuxArm64Classifier()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcl-libs");
        JsonObject versionJson = CreateModernLwjglNativeVersionJson("natives-linux-arm64");

        MinecraftLibraryToken token = MinecraftLibraryResolver.Resolve(CreateRequest(
            versionJson,
            root,
            MinecraftLibraryOperatingSystem.Linux) with
        {
            IsArm64Architecture = true
        })[0];

        Assert.AreEqual("org.lwjgl:lwjgl:3.4.1:natives-linux-arm64", token.OriginalName);
        Assert.AreEqual("https://libraries.minecraft.net/lwjgl-3.4.1-natives-linux-arm64.jar", token.Url);
        Assert.AreEqual("original", token.Sha1);
        Assert.AreEqual(123L, token.Size);
    }

    [TestMethod]
    public void Resolve_ShouldKeepExistingLinuxArm64NativeMappingMetadata()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcl-libs");
        JsonObject versionJson = Parse(
            """
            {
              "libraries": [
                {
                  "name": "org.lwjgl:lwjgl:3.4.2",
                  "natives": { "linux": "natives-linux-arm64" },
                  "downloads": {
                    "classifiers": {
                      "natives-linux-arm64": {
                        "path": "org/lwjgl/lwjgl/3.4.2/lwjgl-3.4.2-natives-linux-arm64.jar",
                        "url": "https://example.test/lwjgl-arm64.jar",
                        "sha1": "manifest-arm64",
                        "size": 654
                      }
                    }
                  }
                }
              ]
            }
            """);

        MinecraftLibraryToken token = MinecraftLibraryResolver.Resolve(CreateRequest(
            versionJson,
            root,
            MinecraftLibraryOperatingSystem.Linux) with
        {
            IsArm64Architecture = true
        })[0];

        Assert.AreEqual("org.lwjgl:lwjgl:3.4.2", token.OriginalName);
        Assert.AreEqual("https://example.test/lwjgl-arm64.jar", token.Url);
        Assert.AreEqual("manifest-arm64", token.Sha1);
        Assert.AreEqual(654L, token.Size);
        Assert.IsTrue(token.IsNatives);
    }

    [TestMethod]
    public void Resolve_ShouldNotRewriteNonLinuxNativeArtifactOnArm64()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcl-libs");
        JsonObject versionJson = CreateModernLwjglNativeVersionJson("natives-macos");

        MinecraftLibraryToken token = MinecraftLibraryResolver.Resolve(CreateRequest(
            versionJson,
            root,
            MinecraftLibraryOperatingSystem.MacOs) with
        {
            IsArm64Architecture = true
        })[0];

        Assert.AreEqual("org.lwjgl:lwjgl:3.4.1:natives-macos", token.OriginalName);
        Assert.AreEqual("https://libraries.minecraft.net/lwjgl-3.4.1-natives-macos.jar", token.Url);
        Assert.AreEqual("original", token.Sha1);
        Assert.AreEqual(123L, token.Size);
    }

    [TestMethod]
    [DataRow("3.1.6")]
    [DataRow("3.2.1")]
    [DataRow("3.2.2")]
    [DataRow("3.3.1")]
    public void Resolve_ShouldUpgradeLegacyLwjgl3LibrariesOnLinuxArm64(string originalVersion)
    {
        string root = Path.Combine(Path.GetTempPath(), "pcl-libs");
        JsonObject versionJson = CreateLegacyLwjgl3VersionJson(originalVersion);

        IReadOnlyList<MinecraftLibraryToken> tokens = MinecraftLibraryResolver.Resolve(CreateRequest(
            versionJson,
            root,
            MinecraftLibraryOperatingSystem.Linux) with
        {
            IsArm64Architecture = true
        });

        Assert.AreEqual(2, tokens.Count);
        MinecraftLibraryToken artifact = tokens.Single(static token =>
            token.OriginalName == "org.lwjgl:lwjgl:3.3.2");
        MinecraftLibraryToken native = tokens.Single(static token =>
            token.OriginalName == "org.lwjgl:lwjgl:3.3.2:natives-linux-arm64");
        Assert.AreEqual("org.lwjgl:lwjgl:3.3.2", artifact.OriginalName);
        Assert.AreEqual(
            "https://repo1.maven.org/maven2/org/lwjgl/lwjgl/3.3.2/lwjgl-3.3.2.jar",
            artifact.Url);
        Assert.AreEqual(
            Path.Combine(root, "libraries", "org", "lwjgl", "lwjgl", "3.3.2", "lwjgl-3.3.2.jar"),
            artifact.LocalPath);
        Assert.AreEqual("org.lwjgl:lwjgl:3.3.2:natives-linux-arm64", native.OriginalName);
        Assert.AreEqual(
            "https://repo1.maven.org/maven2/org/lwjgl/lwjgl/3.3.2/lwjgl-3.3.2-natives-linux-arm64.jar",
            native.Url);
        Assert.AreEqual(
            Path.Combine(root, "libraries", "org", "lwjgl", "lwjgl", "3.3.2", "lwjgl-3.3.2-natives-linux-arm64.jar"),
            native.LocalPath);
        Assert.AreEqual("4421d94af68e35dcaa31737a6fc59136a1e61b94", artifact.Sha1);
        Assert.AreEqual(786_196L, artifact.Size);
        Assert.AreEqual("8bd89332c90a90e6bc4aa997a25c05b7db02c90a", native.Sha1);
        Assert.AreEqual(90_795L, native.Size);
        Assert.IsFalse(native.IsNatives);

        MinecraftClasspathPlan classpath = MinecraftClasspathPlanner.CreatePlan(
            new MinecraftClasspathPlanRequest { Libraries = tokens });
        CollectionAssert.AreEquivalent(
            new[] { artifact.LocalPath, native.LocalPath },
            classpath.Entries.ToArray());
    }

    [TestMethod]
    public void Resolve_ShouldUseVerifiedMetadataForEveryLegacyLwjgl3Module()
    {
        (string Module, string ArtifactSha1, long ArtifactSize, string NativeSha1, long NativeSize)[] expected =
        [
            ("lwjgl", "4421d94af68e35dcaa31737a6fc59136a1e61b94", 786_196,
                "8bd89332c90a90e6bc4aa997a25c05b7db02c90a", 90_795),
            ("lwjgl-jemalloc", "877e17e39ebcd58a9c956dc3b5b777813de0873a", 43_233,
                "5249f18a9ae20ea86c5816bc3107a888ce7a17d2", 206_402),
            ("lwjgl-openal", "ae5357ed6d934546d3533993ea84c0cfb75eed95", 108_230,
                "22408980cc579709feaf9acb807992d3ebcf693f", 590_865),
            ("lwjgl-opengl", "ee8e95be0b438602038bc1f02dc5e3d011b1b216", 928_871,
                "bb9eb56da6d1d549d6a767218e675e36bc568eb9", 58_627),
            ("lwjgl-glfw", "757920418805fb90bfebb3d46b1d9e7669fca2eb", 135_828,
                "bc49e64bae0f7ff103a312ee8074a34c4eb034c7", 120_168),
            ("lwjgl-stb", "a2550795014d622b686e9caac50b14baa87d2c70", 118_874,
                "11a380c37b0f03cb46db235e064528f84d736ff7", 207_419),
            ("lwjgl-tinyfd", "9f65c248dd77934105274fcf8351abb75b34327c", 13_404,
                "93f8c5bc1984963cd79109891fb5a9d1e580373e", 43_381)
        ];

        string root = Path.Combine(Path.GetTempPath(), "pcl-libs");
        foreach ((string module, string artifactSha1, long artifactSize, string nativeSha1, long nativeSize)
                 in expected)
        {
            IReadOnlyList<MinecraftLibraryToken> tokens = MinecraftLibraryResolver.Resolve(CreateRequest(
                CreateLegacyLwjgl3VersionJson("3.2.2", module),
                root,
                MinecraftLibraryOperatingSystem.Linux) with
            {
                IsArm64Architecture = true
            });

            MinecraftLibraryToken artifact = tokens.Single(token =>
                token.OriginalName == $"org.lwjgl:{module}:3.3.2");
            MinecraftLibraryToken native = tokens.Single(token =>
                token.OriginalName == $"org.lwjgl:{module}:3.3.2:natives-linux-arm64");
            Assert.AreEqual(artifactSha1, artifact.Sha1, module);
            Assert.AreEqual(artifactSize, artifact.Size, module);
            Assert.AreEqual(nativeSha1, native.Sha1, module);
            Assert.AreEqual(nativeSize, native.Size, module);
            Assert.IsFalse(native.IsNatives, module);
        }
    }

    [TestMethod]
    public void Resolve_ShouldNotRewriteUnknownLwjgl3Module()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcl-libs");
        IReadOnlyList<MinecraftLibraryToken> tokens = MinecraftLibraryResolver.Resolve(CreateRequest(
            CreateLegacyLwjgl3VersionJson("3.2.2", "lwjgl-custom"),
            root,
            MinecraftLibraryOperatingSystem.Linux) with
        {
            IsArm64Architecture = true
        });

        Assert.AreEqual(2, tokens.Count);
        MinecraftLibraryToken artifact = tokens.Single(static token => !token.IsNatives);
        MinecraftLibraryToken native = tokens.Single(static token => token.IsNatives);
        Assert.AreEqual("org.lwjgl:lwjgl-custom:3.2.2", artifact.OriginalName);
        Assert.AreEqual("original-artifact", artifact.Sha1);
        Assert.AreEqual("org.lwjgl:lwjgl-custom:3.2.2", native.OriginalName);
        Assert.AreEqual("original-native", native.Sha1);
    }

    [TestMethod]
    public void Resolve_ShouldUsePortableLwjgl2NativesAndDropUnsupportedAuxiliaryNativesOnLinuxArm64()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcl-libs");
        JsonObject versionJson = Parse(
            """
            {
              "libraries": [
                {
                  "name": "org.lwjgl.lwjgl:lwjgl:2.9.4-nightly-20150209",
                  "downloads": {
                    "artifact": {
                      "path": "org/lwjgl/lwjgl/lwjgl/2.9.4-nightly-20150209/lwjgl-2.9.4-nightly-20150209.jar",
                      "url": "https://libraries.minecraft.net/lwjgl2.jar",
                      "sha1": "lwjgl2",
                      "size": 123
                    }
                  }
                },
                {
                  "name": "org.lwjgl.lwjgl:lwjgl-platform:2.9.4-nightly-20150209",
                  "natives": { "linux": "natives-linux" },
                  "downloads": {
                    "classifiers": {
                      "natives-linux": {
                        "path": "org/lwjgl/lwjgl/lwjgl-platform/2.9.4-nightly-20150209/lwjgl-platform-2.9.4-nightly-20150209-natives-linux.jar"
                      }
                    }
                  }
                },
                {
                  "name": "net.java.jinput:jinput-platform:2.0.5",
                  "natives": { "linux": "natives-linux" }
                },
                {
                  "name": "com.mojang:text2speech:1.10.3",
                  "natives": { "linux": "natives-linux" }
                },
                {
                  "name": "com.mojang:text2speech:1.11.3",
                  "natives": { "linux": "natives-linux" }
                },
                {
                  "name": "com.mojang:text2speech:1.12.4",
                  "natives": { "linux": "natives-linux" }
                },
                {
                  "name": "com.mojang:text2speech:1.13.9:natives-linux",
                  "downloads": {
                    "artifact": {
                      "path": "com/mojang/text2speech/1.13.9/text2speech-1.13.9-natives-linux.jar",
                      "sha1": "unsupported-direct",
                      "size": 789
                    }
                  }
                }
              ]
            }
            """);

        IReadOnlyList<MinecraftLibraryToken> tokens = MinecraftLibraryResolver.Resolve(CreateRequest(
            versionJson,
            root,
            MinecraftLibraryOperatingSystem.Linux) with
        {
            IsArm64Architecture = true
        });

        Assert.AreEqual(2, tokens.Count);
        Assert.IsTrue(tokens.Any(static token =>
            token.OriginalName == "org.lwjgl.lwjgl:lwjgl:2.9.4-nightly-20150209"));
        MinecraftLibraryToken native = tokens.Single(static token => token.IsNatives);
        Assert.AreEqual("org.glavo.hmcl:lwjgl2-natives:2.9.3-linux-arm64", native.OriginalName);
        Assert.AreEqual(
            Path.Combine(
                root,
                "libraries",
                "org",
                "glavo",
                "hmcl",
                "lwjgl2-natives",
                "2.9.3-linux-arm64",
                "lwjgl2-natives-2.9.3-linux-arm64.jar"),
            native.LocalPath);
        Assert.AreEqual(
            "https://repo1.maven.org/maven2/org/glavo/hmcl/lwjgl2-natives/2.9.3-linux-arm64/lwjgl2-natives-2.9.3-linux-arm64.jar",
            native.Url);
        Assert.AreEqual("c47df34b6a0414b2d9972f602d0c85191129d69c", native.Sha1);
        Assert.AreEqual(7_346_768L, native.Size);
    }

    [TestMethod]
    public void Resolve_ShouldKeepFutureAndExistingArm64AuxiliaryNatives()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcl-libs");
        JsonObject versionJson = Parse(
            """
            {
              "libraries": [
                {
                  "name": "net.java.jinput:jinput-platform:2.0.6",
                  "natives": { "linux": "natives-linux" },
                  "downloads": {
                    "classifiers": {
                      "natives-linux": {
                        "path": "net/java/jinput/jinput-platform/2.0.6/jinput-platform-2.0.6-natives-linux.jar",
                        "sha1": "future-jinput",
                        "size": 101
                      }
                    }
                  }
                },
                {
                  "name": "com.mojang:text2speech:1.14.0",
                  "natives": { "linux": "natives-linux" },
                  "downloads": {
                    "classifiers": {
                      "natives-linux": {
                        "path": "com/mojang/text2speech/1.14.0/text2speech-1.14.0-natives-linux.jar",
                        "sha1": "future-text2speech",
                        "size": 102
                      }
                    }
                  }
                },
                {
                  "name": "com.mojang:text2speech:1.10.3",
                  "natives": { "linux": "natives-linux-arm64" },
                  "downloads": {
                    "classifiers": {
                      "natives-linux-arm64": {
                        "path": "com/mojang/text2speech/1.10.3/text2speech-1.10.3-natives-linux-arm64.jar",
                        "sha1": "existing-text2speech-arm64",
                        "size": 103
                      }
                    }
                  }
                },
                {
                  "name": "com.mojang:text2speech:1.13.9:natives-linux-arm64",
                  "downloads": {
                    "artifact": {
                      "path": "com/mojang/text2speech/1.13.9/text2speech-1.13.9-natives-linux-arm64.jar",
                      "sha1": "direct-text2speech-arm64",
                      "size": 104
                    }
                  }
                },
                {
                  "name": "org.lwjgl.lwjgl:lwjgl-platform:2.9.5",
                  "natives": { "linux": "natives-linux" },
                  "downloads": {
                    "classifiers": {
                      "natives-linux": {
                        "path": "org/lwjgl/lwjgl/lwjgl-platform/2.9.5/lwjgl-platform-2.9.5-natives-linux.jar",
                        "sha1": "future-lwjgl2",
                        "size": 105
                      }
                    }
                  }
                }
              ]
            }
            """);

        IReadOnlyList<MinecraftLibraryToken> tokens = MinecraftLibraryResolver.Resolve(CreateRequest(
            versionJson,
            root,
            MinecraftLibraryOperatingSystem.Linux) with
        {
            IsArm64Architecture = true
        });

        Assert.AreEqual(5, tokens.Count);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "future-jinput",
                "future-text2speech",
                "existing-text2speech-arm64",
                "direct-text2speech-arm64",
                "future-lwjgl2"
            },
            tokens.Select(static token => token.Sha1).ToArray());
    }

    [TestMethod]
    public void Resolve_ShouldRespectOperatingSystemRules()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcl-libs");
        JsonObject versionJson = Parse(
            """
            {
              "libraries": [
                {
                  "name": "win.only:lib:1.0",
                  "rules": [{ "action": "allow", "os": { "name": "windows" } }]
                },
                {
                  "name": "linux.only:lib:1.0",
                  "rules": [{ "action": "allow", "os": { "name": "linux" } }]
                }
              ]
            }
            """);

        IReadOnlyList<MinecraftLibraryToken> tokens = MinecraftLibraryResolver.Resolve(CreateRequest(
            versionJson,
            root,
            MinecraftLibraryOperatingSystem.Linux));

        Assert.AreEqual(1, tokens.Count);
        Assert.AreEqual("linux.only:lib:1.0", tokens[0].OriginalName);
    }

    [TestMethod]
    public void Resolve_ShouldSkipArtifactPathThatEscapesLibrariesDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcl-libs");
        JsonObject versionJson = Parse(
            """
            {
              "libraries": [
                {
                  "name": "malicious:artifact:1.0",
                  "downloads": {
                    "artifact": { "path": "../../launcher-overwrite.exe" }
                  }
                },
                {
                  "name": "safe:artifact:1.0",
                  "downloads": {
                    "artifact": { "path": "safe/artifact/1.0/artifact-1.0.jar" }
                  }
                }
              ]
            }
            """);

        IReadOnlyList<MinecraftLibraryToken> tokens = MinecraftLibraryResolver.Resolve(CreateRequest(versionJson, root));

        Assert.AreEqual(1, tokens.Count);
        Assert.AreEqual("safe:artifact:1.0", tokens[0].OriginalName);
        StringAssert.StartsWith(tokens[0].LocalPath, Path.Combine(root, "libraries") + Path.DirectorySeparatorChar);
    }

    [TestMethod]
    public void Resolve_ShouldSkipNativePathThatEscapesLibrariesDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcl-libs");
        JsonObject versionJson = Parse(
            """
            {
              "libraries": [
                {
                  "name": "malicious:native:1.0",
                  "natives": { "windows": "natives-windows" },
                  "downloads": {
                    "classifiers": {
                      "natives-windows": { "path": "../outside-native.jar" }
                    }
                  }
                }
              ]
            }
            """);

        IReadOnlyList<MinecraftLibraryToken> tokens = MinecraftLibraryResolver.Resolve(CreateRequest(versionJson, root));

        Assert.AreEqual(0, tokens.Count);
    }

    [TestMethod]
    public void Resolve_ShouldSkipCoordinateThatContainsPathTraversal()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcl-libs");
        JsonObject versionJson = Parse(
            """
            {
              "libraries": [
                { "name": "safe:../outside:1.0", "hint": "local" },
                { "name": "safe:artifact:1.0" }
              ]
            }
            """);

        IReadOnlyList<MinecraftLibraryToken> tokens = MinecraftLibraryResolver.Resolve(CreateRequest(versionJson, root));

        Assert.AreEqual(1, tokens.Count);
        Assert.AreEqual("safe:artifact:1.0", tokens[0].OriginalName);
    }

    private static MinecraftLibraryResolutionRequest CreateRequest(
        JsonObject versionJson,
        string root,
        MinecraftLibraryOperatingSystem operatingSystem = MinecraftLibraryOperatingSystem.Win32) =>
        new()
        {
            VersionJson = versionJson,
            MinecraftRootDirectory = root,
            OperatingSystem = operatingSystem,
            OperatingSystemVersion = "10.0.19045",
            Is64BitArchitecture = true
        };

    private static JsonObject CreateModernLwjglNativeVersionJson(string classifier) => Parse(
        $$"""
        {
          "libraries": [
            {
              "name": "org.lwjgl:lwjgl:3.4.1:{{classifier}}",
              "downloads": {
                "artifact": {
                  "path": "org/lwjgl/lwjgl/3.4.1/lwjgl-3.4.1-{{classifier}}.jar",
                  "url": "https://libraries.minecraft.net/lwjgl-3.4.1-{{classifier}}.jar",
                  "sha1": "original",
                  "size": 123
                }
              }
            }
          ]
        }
        """);

    private static JsonObject CreateLegacyLwjgl3VersionJson(
        string version,
        string module = "lwjgl") => Parse(
        $$"""
        {
          "libraries": [
            {
              "name": "org.lwjgl:{{module}}:{{version}}",
              "downloads": {
                "artifact": {
                  "path": "org/lwjgl/{{module}}/{{version}}/{{module}}-{{version}}.jar",
                  "url": "https://libraries.minecraft.net/{{module}}-{{version}}.jar",
                  "sha1": "original-artifact",
                  "size": 123
                }
              }
            },
            {
              "name": "org.lwjgl:{{module}}:{{version}}",
              "natives": { "linux": "natives-linux" },
              "downloads": {
                "classifiers": {
                  "natives-linux": {
                    "path": "org/lwjgl/{{module}}/{{version}}/{{module}}-{{version}}-natives-linux.jar",
                    "url": "https://libraries.minecraft.net/{{module}}-{{version}}-natives-linux.jar",
                    "sha1": "original-native",
                    "size": 456
                  }
                }
              }
            }
          ]
        }
        """);

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();
}

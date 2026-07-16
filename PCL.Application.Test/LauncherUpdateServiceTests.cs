// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Application.Updates;

namespace PCL.Application.Test;

[TestClass]
public sealed class LauncherUpdateServiceTests
{
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
}

// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Desktop.Controls.Legacy;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class MyImageCacheTests
{
    [TestMethod]
    public async Task DownloadImageAsync_FailedRefreshPreservesExistingCache()
    {
        string url = $"http://127.0.0.1:1/pcl-cache-{Guid.NewGuid():N}.png";
        string cachePath = MyImage.GetTempPath(url);
        byte[] cachedBytes = [1, 2, 3, 4, 5];
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        await File.WriteAllBytesAsync(cachePath, cachedBytes);

        try
        {
            string result = await MyImage.DownloadImageAsync(url);

            Assert.AreEqual(cachePath, result);
            Assert.IsTrue(File.Exists(cachePath));
            CollectionAssert.AreEqual(cachedBytes, await File.ReadAllBytesAsync(cachePath));
        }
        finally
        {
            File.Delete(cachePath);
        }
    }
}

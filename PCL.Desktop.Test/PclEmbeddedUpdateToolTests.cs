// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Security.Cryptography;
using System.Text;
using PCL.Desktop.Hosting;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class PclEmbeddedUpdateToolTests
{
    [TestMethod]
    public async Task ExtractToolAsync_ClosesHashStreamBeforeMovingAndReusesContentCopy()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcln-update-tool-test-" + Guid.NewGuid().ToString("N"));
        byte[] payload = Encoding.UTF8.GetBytes("embedded update tool fixture");
        try
        {
            string first;
            await using (MemoryStream stream = new(payload, writable: false))
                first = await PclEmbeddedUpdateTool.ExtractToolAsync(stream, root);

            string second;
            await using (MemoryStream stream = new(payload, writable: false))
                second = await PclEmbeddedUpdateTool.ExtractToolAsync(stream, root);

            Assert.AreEqual(first, second);
            string hash = Convert.ToHexStringLower(SHA256.HashData(payload));
            string extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
            Assert.AreEqual($"hpatchz-{hash[..16]}{extension}", Path.GetFileName(first));
            CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(first));
            Assert.AreEqual(0, Directory.GetFiles(root, "*.tmp").Length);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExtractToolAsync_ConcurrentCallsReuseContentCopy()
    {
        const int operationCount = 8;
        string root = Path.Combine(Path.GetTempPath(), "pcln-update-tool-test-" + Guid.NewGuid().ToString("N"));
        byte[] payload = new byte[256 * 1024];
        Array.Fill(payload, (byte)0x5A);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource allReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int readyCount = 0;
        try
        {
            Task<string>[] extractions = Enumerable.Range(0, operationCount)
                .Select(async _ =>
                {
                    if (Interlocked.Increment(ref readyCount) == operationCount)
                        allReady.SetResult();
                    await release.Task;
                    await using MemoryStream stream = new(payload, writable: false);
                    return await PclEmbeddedUpdateTool.ExtractToolAsync(stream, root);
                })
                .ToArray();

            await allReady.Task;
            release.SetResult();
            string[] paths = await Task.WhenAll(extractions);

            Assert.IsTrue(paths.All(path => string.Equals(path, paths[0], StringComparison.Ordinal)));
            CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(paths[0]));
            Assert.AreEqual(0, Directory.GetFiles(root, "*.tmp").Length);
        }
        finally
        {
            release.TrySetResult();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExtractToolAsync_CopyFailureRemovesTemporaryFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcln-update-tool-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using FailingReadStream stream = new();
            await Assert.ThrowsExactlyAsync<IOException>(
                () => PclEmbeddedUpdateTool.ExtractToolAsync(stream, root));

            Assert.AreEqual(0, Directory.GetFiles(root).Length);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FailingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("Synthetic read failure.");

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("Synthetic read failure."));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

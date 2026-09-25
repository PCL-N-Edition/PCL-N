using Nexa.Sidecar.Protocol;
using Nexa.Xsr;

namespace Nexa.Xsr.Runtime.Tests;

internal static partial class Program
{
    private static async ValueTask SidecarDisposalCannotBeUndoneByBufferedSnapshot()
    {
        PausableSidecarStream? stream = null;
        var (session, plugin) = await HandshakeAndRegister(wrap: inner => stream = new(inner));
        using (session)
        using (plugin)
        {
            stream!.BlockSnapshotEnd = true;
            Task snapshot = SnapshotAndReady(session, plugin).AsTask();
            await stream.SnapshotBuffered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try { session.Dispose(); }
            finally { stream.ReleaseSnapshot.TrySetResult(); }
            try { await snapshot.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) when (error is SidecarProtocolException or ObjectDisposedException or InvalidOperationException) { }
            var mirror = session.Mirror!;
            var progress = mirror.TryResolve(XsrSemanticId_Parse("plugin.download.progress"))!.Value;
            AssertEqual(XsrStateAvailability.Unavailable, mirror.Store.Read<string>(progress).Availability);
            AssertEqual(SidecarSessionState.Closed, session.State);
        }
    }

    private static async ValueTask SidecarUnsentCancellationPreservesOtherRequests()
    {
        var (session, plugin, _, _) = await ActivatedSession();
        using (session)
        using (plugin)
        {
            var first = session.SendCommandAsync(XsrSemanticId_Parse("plugin.download.start")).AsTask();
            var request = await DataPlaneReceiveAsync(plugin);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var result = await session.SendCommandAsync(XsrSemanticId_Parse("plugin.download.start"), cancellationToken: cancelled.Token);
            AssertEqual("xsr.cancelled", result.Error!.Code.Value);
            AssertEqual(SidecarSessionState.Active, session.State);
            AssertEqual(1, session.PendingCount);
            await plugin.SendAsync(Result(request, true, "ok", null));
            AssertTrue((await first.WaitAsync(TimeSpan.FromSeconds(3))).IsSuccess);
        }
    }

    private static async ValueTask SidecarDeadlinesIncludeBlockedWrites()
    {
        foreach (bool blockCancellation in new[] { false, true })
        {
            PausableSidecarStream? stream = null;
            var (session, plugin, _, loop) = await ActivatedSession(wrap: inner => stream = new(inner));
            using (session)
            using (plugin)
            {
                stream!.Blocked = !blockCancellation;
                var result = session.SendCommandAsync(XsrSemanticId_Parse("plugin.download.start"), timeout: TimeSpan.FromMilliseconds(150)).AsTask();
                if (blockCancellation)
                {
                    await DataPlaneReceiveAsync(plugin);
                    stream.Blocked = true;
                }
                var outcome = await result.WaitAsync(TimeSpan.FromSeconds(3));
                AssertFalse(outcome.IsSuccess);
                AssertEqual("xsr.timed_out", outcome.Error!.Code.Value);
                AssertEqual(0, session.PendingCount);
                AssertEqual(SidecarSessionState.Failed, session.State);
                await loop.WaitAsync(TimeSpan.FromSeconds(3));
            }
        }
    }

    private static async ValueTask SidecarTerminalPathsCompleteAllPending()
    {
        foreach (string ending in new[] { "dispose", "host-shutdown", "disconnect", "crash", "shutdown", "malformed" })
        {
            var (session, plugin, mirror, loop) = await ActivatedSession();
            using (session)
            using (plugin)
            {
                var progress = mirror.TryResolve(XsrSemanticId_Parse("plugin.download.progress"))!.Value;
                await plugin.SendAsync(Delta("plugin.download.progress", "80"));
                await WaitUntil(() => mirror.Store.Read<string>(progress).Value == "80");
                var first = session.SendCommandAsync(XsrSemanticId_Parse("plugin.download.start")).AsTask();
                var second = session.SendCommandAsync(XsrSemanticId_Parse("plugin.download.start")).AsTask();
                var request = await DataPlaneReceiveAsync(plugin);
                await DataPlaneReceiveAsync(plugin);
                if (ending == "dispose") session.Dispose();
                else if (ending == "host-shutdown") await session.ShutdownAsync();
                else if (ending == "disconnect") plugin.Dispose();
                else await plugin.SendAsync(new(SidecarProtocol.Version,
                    ending == "crash" ? SidecarMessageType.Crash : ending == "shutdown" ? SidecarMessageType.Shutdown : SidecarMessageType.CommandResult,
                    SidecarFrameTraits.Final, request.CorrelationId, ending == "malformed" ? new byte[] { 255 } : Array.Empty<byte>()));
                var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
                AssertTrue(results.All(result => !result.IsSuccess));
                AssertEqual(0, session.PendingCount);
                await loop.WaitAsync(TimeSpan.FromSeconds(3));
                AssertEqual(XsrStateAvailability.Unavailable, mirror.Store.Read<string>(progress).Availability);
                AssertEqual("80", mirror.Store.Read<string>(progress).Value);
                if (ending is "dispose" or "host-shutdown" or "shutdown") AssertEqual(SidecarSessionState.Closed, session.State);
            }
        }
    }

    private static async ValueTask SidecarConcurrentAdmissionRespectsCapacity()
    {
        var (session, plugin, _, loop) = await ActivatedSession(maxPending: 2);
        using (session)
        using (plugin)
        {
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<XsrResult>[] requests = Enumerable.Range(0, 32).Select(async _ =>
            {
                await start.Task;
                return await session.SendCommandAsync(XsrSemanticId_Parse("plugin.download.start"));
            }).ToArray();
            start.SetResult();
            await WaitUntil(() => requests.Count(task => task.IsCompleted) == 30);
            AssertEqual(2, session.PendingCount);
            session.Dispose();
            var results = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(3));
            AssertEqual(30, results.Count(result => result.Error?.Code.Value == "xsr.backpressure"));
            AssertEqual(0, session.PendingCount);
            await loop.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private sealed class PausableSidecarStream(Stream inner) : Stream
    {
        internal bool Blocked { get; set; }
        internal bool BlockSnapshotEnd { get; set; }
        internal TaskCompletionSource SnapshotBuffered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseSnapshot { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int count = await inner.ReadAsync(buffer, cancellationToken);
            if (BlockSnapshotEnd && count == SidecarProtocol.HeaderSize
                && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buffer.Span) == SidecarProtocol.Magic
                && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(buffer.Span[8..]) == (ushort)SidecarMessageType.StateSnapshotEnd)
            {
                SnapshotBuffered.TrySetResult();
                await ReleaseSnapshot.Task.WaitAsync(cancellationToken);
            }
            return count;
        }
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Blocked)
            {
                await inner.WriteAsync(buffer[..Math.Min(8, buffer.Length)], cancellationToken);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            else await inner.WriteAsync(buffer, cancellationToken);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}

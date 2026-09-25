using Nexa.Sidecar.Protocol;
using Nexa.Xsr;

namespace Nexa.Xsr.Runtime.Tests;

internal static partial class Program
{
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
        foreach (string ending in new[] { "dispose", "disconnect", "crash", "shutdown", "malformed" })
        {
            var (session, plugin, _, loop) = await ActivatedSession();
            using (session)
            using (plugin)
            {
                var first = session.SendCommandAsync(XsrSemanticId_Parse("plugin.download.start")).AsTask();
                var second = session.SendCommandAsync(XsrSemanticId_Parse("plugin.download.start")).AsTask();
                var request = await DataPlaneReceiveAsync(plugin);
                await DataPlaneReceiveAsync(plugin);
                if (ending == "dispose") session.Dispose();
                else if (ending == "disconnect") plugin.Dispose();
                else await plugin.SendAsync(new(SidecarProtocol.Version,
                    ending == "crash" ? SidecarMessageType.Crash : ending == "shutdown" ? SidecarMessageType.Shutdown : SidecarMessageType.CommandResult,
                    SidecarFrameTraits.Final, request.CorrelationId, ending == "malformed" ? new byte[] { 255 } : Array.Empty<byte>()));
                var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
                AssertTrue(results.All(result => !result.IsSuccess));
                AssertEqual(0, session.PendingCount);
                await loop.WaitAsync(TimeSpan.FromSeconds(3));
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
        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
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

using PCL.Sidecar.Protocol;

namespace PCL.Sidecar.Tests;

internal static partial class Program
{
    private static readonly (string Name, Func<ValueTask> Body)[] TestCases =
    [
        // XSR-401: Sidecar protocol surface.
        ("frames round trip header and payload", Sync(FrameRoundTripsHeaderAndPayload)),
        ("payload fields round trip every type", Sync(PayloadFieldsRoundTripEveryType)),
        ("unknown fields are skipped by length", Sync(UnknownFieldsAreSkippedByLength)),
        ("frame magic and versions are enforced", Sync(FrameMagicAndVersionsAreEnforced)),
        ("truncated and oversized frames are rejected", Sync(TruncatedAndOversizedFramesAreRejected)),
        ("unknown message types are rejected", Sync(UnknownMessageTypesAreRejected)),
        ("message numbers are frozen", Sync(MessageNumbersAreFrozen)),
        ("ascending field ids are enforced", Sync(AscendingFieldIdsAreEnforced)),
        ("tag mismatches are rejected on read", Sync(TagMismatchesAreRejectedOnRead)),
        ("malformed payloads fail deterministically", Sync(MalformedPayloadsFailDeterministically)),
        ("frame decode allocates only the payload", Sync(FrameDecodeAllocatesOnlyThePayload)),
        ("correlation ids are stable identities", Sync(CorrelationIdsAreStableIdentities)),
        // XSR-402: transport and connection lifecycle.
        ("connection round trips frames over loopback", ConnectionRoundTripsFramesOverLoopback),
        ("concurrent sends never interleave", ConcurrentSendsNeverInterleave),
        ("protocol failures move the connection to failed", ProtocolFailuresMoveTheConnectionToFailed),
        ("peer close ends receive with stream end", PeerCloseEndsReceiveWithStreamEnd),
        ("close is idempotent and rejects further use", CloseIsIdempotentAndRejectsFurtherUse),
        ("send cancellation is observed", SendCancellationIsObserved),
        ("ipc stream round trips frames", IpcStreamRoundTripsFrames),
    ];

    private static async Task<int> Main()
    {
        foreach ((string name, Func<ValueTask> body) in TestCases)
        {
            await body().ConfigureAwait(false);
            Console.WriteLine($"PASS: {name}");
        }

        Console.WriteLine($"Sidecar protocol tests passed: {TestCases.Length}.");
        return 0;
    }

    private static Func<ValueTask> Sync(Action action) => () =>
    {
        action();
        return ValueTask.CompletedTask;
    };

    private static void AssertTrue(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException("Expected true but received false.");
        }
    }

    private static void AssertFalse(bool value) => AssertTrue(!value);

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}' but received '{actual}'.");
        }
    }

    private static void AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected exception {typeof(TException).Name}.");
    }

    private static T ReadAs<T>(byte[] payload, Func<SidecarPayloadField, T> read)
        where T : allows ref struct
    {
        SidecarPayloadReader reader = new(payload);
        return read(reader.ReadNext());
    }

    private static byte[] EncodeFrame(SidecarFrame frame)
    {
        byte[] buffer = new byte[SidecarFrameCodec.GetFrameSize(frame.Payload.Length)];
        SidecarFrameCodec.Encode(frame, buffer);
        return buffer;
    }
}

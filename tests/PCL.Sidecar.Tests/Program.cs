using PCL.Sidecar.Protocol;

namespace PCL.Sidecar.Tests;

internal static partial class Program
{
    private static readonly (string Name, Action Body)[] TestCases =
    [
        // XSR-401: Sidecar protocol surface.
        ("frames round trip header and payload", FrameRoundTripsHeaderAndPayload),
        ("payload fields round trip every type", PayloadFieldsRoundTripEveryType),
        ("unknown fields are skipped by length", UnknownFieldsAreSkippedByLength),
        ("frame magic and versions are enforced", FrameMagicAndVersionsAreEnforced),
        ("truncated and oversized frames are rejected", TruncatedAndOversizedFramesAreRejected),
        ("unknown message types are rejected", UnknownMessageTypesAreRejected),
        ("message numbers are frozen", MessageNumbersAreFrozen),
        ("ascending field ids are enforced", AscendingFieldIdsAreEnforced),
        ("tag mismatches are rejected on read", TagMismatchesAreRejectedOnRead),
        ("malformed payloads fail deterministically", MalformedPayloadsFailDeterministically),
        ("frame decode allocates only the payload", FrameDecodeAllocatesOnlyThePayload),
        ("correlation ids are stable identities", CorrelationIdsAreStableIdentities),
    ];

    private static int Main()
    {
        foreach ((string name, Action body) in TestCases)
        {
            body();
            Console.WriteLine($"PASS: {name}");
        }

        Console.WriteLine($"Sidecar protocol tests passed: {TestCases.Length}.");
        return 0;
    }

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

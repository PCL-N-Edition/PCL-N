using PCL.Xsr;
using PCL.Sidecar.Protocol;

namespace PCL.Xsr.Runtime;

/// <summary>
/// Encodes and decodes the data-plane payloads for one session. Field IDs are frozen for
/// protocol v1; values are string-encoded until the generated typed codecs arrive.
/// </summary>
public static class SidecarDataPlane
{
    /// <summary>
    /// Encodes a command or query request: the semantic ID and its argument.
    /// </summary>
    public static byte[] EncodeRequest(XsrSemanticId semantic, string? argument)
    {
        SidecarPayloadWriter writer = new();
        writer.WriteString(1, semantic.Value);
        writer.WriteString(2, argument ?? string.Empty);
        return writer.ToArray();
    }

    public static (XsrSemanticId Semantic, string Argument) DecodeRequest(ReadOnlySpan<byte> payload)
    {
        XsrSemanticId semantic = default;
        string argument = string.Empty;
        SidecarPayloadReader reader = new(payload);
        while (reader.HasMore)
        {
            SidecarPayloadField field = reader.ReadNext();
            switch (field.Id)
            {
                case 1:
                    semantic = XsrSemanticId.Parse(field.ReadString());
                    break;
                case 2:
                    argument = field.ReadString();
                    break;
            }
        }

        if (!semantic.IsAssigned)
        {
            throw new SidecarProtocolException("The request payload carries no semantic ID.");
        }

        return (semantic, argument);
    }

    /// <summary>
    /// Encodes a result: success flag, the value, and the stable error code on failure.
    /// </summary>
    public static byte[] EncodeResult(bool success, string value, string? errorCode)
    {
        SidecarPayloadWriter writer = new();
        writer.WriteBoolean(1, success);
        writer.WriteString(2, value);
        writer.WriteString(3, errorCode ?? string.Empty);
        return writer.ToArray();
    }

    public static (bool Success, string Value, string ErrorCode) DecodeResult(ReadOnlySpan<byte> payload)
    {
        bool success = false;
        string value = string.Empty;
        string errorCode = string.Empty;
        SidecarPayloadReader reader = new(payload);
        while (reader.HasMore)
        {
            SidecarPayloadField field = reader.ReadNext();
            switch (field.Id)
            {
                case 1:
                    success = field.ReadBoolean();
                    break;
                case 2:
                    value = field.ReadString();
                    break;
                case 3:
                    errorCode = field.ReadString();
                    break;
            }
        }

        return (success, value, errorCode);
    }

    /// <summary>
    /// Encodes a state delta: the semantic ID and the string-encoded value.
    /// </summary>
    public static byte[] EncodeStateDelta(XsrSemanticId semantic, string value)
    {
        SidecarPayloadWriter writer = new();
        writer.WriteString(1, semantic.Value);
        writer.WriteString(2, value);
        return writer.ToArray();
    }

    public static (XsrSemanticId Semantic, string Value) DecodeStateDelta(ReadOnlySpan<byte> payload)
    {
        XsrSemanticId semantic = default;
        string value = string.Empty;
        SidecarPayloadReader reader = new(payload);
        while (reader.HasMore)
        {
            SidecarPayloadField field = reader.ReadNext();
            switch (field.Id)
            {
                case 1:
                    semantic = XsrSemanticId.Parse(field.ReadString());
                    break;
                case 2:
                    value = field.ReadString();
                    break;
            }
        }

        if (!semantic.IsAssigned)
        {
            throw new SidecarProtocolException("The state delta payload carries no semantic ID.");
        }

        return (semantic, value);
    }

    /// <summary>
    /// Encodes an event: the semantic ID and its payload. Events are never coalesced.
    /// </summary>
    public static byte[] EncodeEvent(XsrSemanticId semantic, string payload) =>
        EncodeStateDelta(semantic, payload);

    public static (XsrSemanticId Semantic, string Payload) DecodeEvent(ReadOnlySpan<byte> payload) =>
        DecodeStateDelta(payload);
}

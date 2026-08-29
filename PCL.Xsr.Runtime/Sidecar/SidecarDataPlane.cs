using PCL.Sidecar.Protocol;

namespace PCL.Xsr.Runtime;

/// <summary>
/// Encodes and decodes the data-plane payloads for one session. Every request, delta, and event
/// carries the session-local uint32 contract ID assigned at registration — semantic strings
/// never cross the data plane. Field IDs are frozen for the protocol draft; values are
/// string-encoded under codec 0 until the generated typed codecs arrive.
/// </summary>
public static class SidecarDataPlane
{
    /// <summary>
    /// Encodes a command or query request: the semantic ID and its argument.
    /// </summary>
    public static byte[] EncodeRequest(uint contractId, string? argument)
    {
        SidecarPayloadWriter writer = new();
        writer.WriteUInt32(1, contractId);
        writer.WriteString(2, argument ?? string.Empty);
        return writer.ToArray();
    }

    public static (uint ContractId, string Argument) DecodeRequest(ReadOnlySpan<byte> payload)
    {
        uint contractId = 0;
        string argument = string.Empty;
        SidecarPayloadReader reader = new(payload);
        while (reader.HasMore)
        {
            SidecarPayloadField field = reader.ReadNext();
            switch (field.Id)
            {
                case 1:
                    contractId = field.ReadUInt32();
                    break;
                case 2:
                    argument = field.ReadString();
                    break;
            }
        }

        if (contractId == 0)
        {
            throw new SidecarProtocolException("The request payload carries no contract ID.");
        }

        return (contractId, argument);
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
    /// Encodes a state delta: the session-local contract ID and the raw codec-encoded value.
    /// </summary>
    public static byte[] EncodeStateDelta(uint contractId, ReadOnlySpan<byte> encodedValue)
    {
        SidecarPayloadWriter writer = new();
        writer.WriteUInt32(1, contractId);
        writer.WriteBytes(2, encodedValue);
        return writer.ToArray();
    }

    public static (uint ContractId, byte[] EncodedValue) DecodeStateDelta(ReadOnlySpan<byte> payload)
    {
        uint contractId = 0;
        byte[]? encodedValue = null;
        SidecarPayloadReader reader = new(payload);
        while (reader.HasMore)
        {
            SidecarPayloadField field = reader.ReadNext();
            switch (field.Id)
            {
                case 1:
                    contractId = field.ReadUInt32();
                    break;
                case 2:
                    encodedValue = field.ReadBytes();
                    break;
            }
        }

        if (contractId == 0)
        {
            throw new SidecarProtocolException("The state delta payload carries no contract ID.");
        }

        if (encodedValue is null)
        {
            throw new SidecarProtocolException("The state delta payload carries no value.");
        }

        return (contractId, encodedValue);
    }

    /// <summary>
    /// Encodes an event: the contract ID and its UTF-8 text payload. Events are never coalesced.
    /// </summary>
    public static byte[] EncodeEvent(uint contractId, string payload)
    {
        SidecarPayloadWriter writer = new();
        writer.WriteUInt32(1, contractId);
        writer.WriteString(2, payload);
        return writer.ToArray();
    }

    public static (uint ContractId, string Payload) DecodeEvent(ReadOnlySpan<byte> payload)
    {
        uint contractId = 0;
        string text = string.Empty;
        SidecarPayloadReader reader = new(payload);
        while (reader.HasMore)
        {
            SidecarPayloadField field = reader.ReadNext();
            switch (field.Id)
            {
                case 1:
                    contractId = field.ReadUInt32();
                    break;
                case 2:
                    text = field.ReadString();
                    break;
            }
        }

        if (contractId == 0)
        {
            throw new SidecarProtocolException("The event payload carries no contract ID.");
        }

        return (contractId, text);
    }
}

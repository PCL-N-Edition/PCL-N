namespace PCL.Sidecar.Protocol;

/// <summary>
/// Encodes and decodes the HELLO and WELCOME handshake payloads. Both sides validate the
/// protocol version; the session id is assigned by the accepting side.
/// </summary>
public static class SidecarHandshake
{
    /// <summary>
    /// Encodes a HELLO payload: the sender's protocol version and its peer name.
    /// </summary>
    public static byte[] EncodeHello(uint protocolVersion, string peerName)
    {
        SidecarPayloadWriter writer = new();
        writer.WriteUInt32(1, protocolVersion);
        writer.WriteString(2, peerName);
        return writer.ToArray();
    }

    public static (uint ProtocolVersion, string PeerName) DecodeHello(ReadOnlySpan<byte> payload)
    {
        uint version = 0;
        string name = string.Empty;
        SidecarPayloadReader reader = new(payload);
        while (reader.HasMore)
        {
            SidecarPayloadField field = reader.ReadNext();
            switch (field.Id)
            {
                case 1:
                    version = field.ReadUInt32();
                    break;
                case 2:
                    name = field.ReadString();
                    break;
            }
        }

        if (version == 0)
        {
            throw new SidecarProtocolException("The HELLO payload carries no protocol version.");
        }

        return (version, name);
    }

    /// <summary>
    /// Encodes a WELCOME payload: the negotiated protocol version and the session identity.
    /// </summary>
    public static byte[] EncodeWelcome(uint negotiatedVersion, Guid sessionId) =>
        EncodeWelcome(negotiatedVersion, sessionId, null);

    public static byte[] EncodeWelcome(uint negotiatedVersion, Guid sessionId, string? notice)
    {
        SidecarPayloadWriter writer = new();
        writer.WriteUInt32(1, negotiatedVersion);
        writer.WriteGuid(2, sessionId);
        if (notice is not null)
        {
            writer.WriteString(3, notice);
        }

        return writer.ToArray();
    }

    public static (uint NegotiatedVersion, Guid SessionId) DecodeWelcome(ReadOnlySpan<byte> payload)
    {
        uint version = 0;
        Guid sessionId = Guid.Empty;
        SidecarPayloadReader reader = new(payload);
        while (reader.HasMore)
        {
            SidecarPayloadField field = reader.ReadNext();
            switch (field.Id)
            {
                case 1:
                    version = field.ReadUInt32();
                    break;
                case 2:
                    sessionId = field.ReadGuid();
                    break;
            }
        }

        if (version == 0 || sessionId == Guid.Empty)
        {
            throw new SidecarProtocolException("The WELCOME payload is missing the version or session id.");
        }

        return (version, sessionId);
    }
}

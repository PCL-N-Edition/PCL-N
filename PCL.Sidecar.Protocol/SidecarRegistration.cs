using System.Buffers.Binary;
using System.Text;

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

/// <summary>
/// Classifies one registration declaration.
/// </summary>
public enum SidecarRegistrationKind : uint
{
    Command = 1,
    Query = 2,
    State = 3,
    Event = 4,
}

/// <summary>
/// One registration declaration: a kind plus the stable semantic identifier. Capability flags
/// ride along for per-kind interpretation by the session.
/// </summary>
public readonly record struct SidecarRegistrationItem(
    SidecarRegistrationKind Kind,
    string SemanticId,
    uint Flags);

/// <summary>
/// Encodes and decodes the REGISTER_* payloads: RegisterBegin carries the declaration count,
/// each RegisterItem frame carries one declaration, RegisterEnd closes the sequence.
/// </summary>
public static class SidecarRegistration
{
    public static byte[] EncodeBegin(uint itemCount)
    {
        SidecarPayloadWriter writer = new();
        writer.WriteUInt32(1, itemCount);
        return writer.ToArray();
    }

    public static uint DecodeBegin(ReadOnlySpan<byte> payload)
    {
        SidecarPayloadReader reader = new(payload);
        while (reader.HasMore)
        {
            SidecarPayloadField field = reader.ReadNext();
            if (field.Id == 1)
            {
                return field.ReadUInt32();
            }
        }

        throw new SidecarProtocolException("The REGISTER_BEGIN payload carries no item count.");
    }

    public static byte[] EncodeItem(in SidecarRegistrationItem item)
    {
        SidecarPayloadWriter writer = new();
        writer.WriteUInt32(1, (uint)item.Kind);
        writer.WriteString(2, item.SemanticId);
        writer.WriteUInt32(3, item.Flags);
        return writer.ToArray();
    }

    public static SidecarRegistrationItem DecodeItem(ReadOnlySpan<byte> payload)
    {
        uint kind = 0;
        string semanticId = string.Empty;
        uint flags = 0;
        SidecarPayloadReader reader = new(payload);
        while (reader.HasMore)
        {
            SidecarPayloadField field = reader.ReadNext();
            switch (field.Id)
            {
                case 1:
                    kind = field.ReadUInt32();
                    break;
                case 2:
                    semanticId = field.ReadString();
                    break;
                case 3:
                    flags = field.ReadUInt32();
                    break;
            }
        }

        if (!Enum.IsDefined((SidecarRegistrationKind)kind))
        {
            throw new SidecarProtocolException($"The registration item carries unknown kind {kind}.");
        }

        if (semanticId.Length == 0)
        {
            throw new SidecarProtocolException("The registration item carries no semantic ID.");
        }

        return new SidecarRegistrationItem((SidecarRegistrationKind)kind, semanticId, flags);
    }

    public static byte[] EncodeEnd() => [];
}

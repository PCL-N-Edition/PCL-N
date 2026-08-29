
namespace PCL.Sidecar.Protocol;

/// <summary>
/// Classifies one registration declaration. UiModule and Resource declarations make
/// render-local plugin pages possible: the host caches the declared module and opening it
/// performs zero IPC.
/// </summary>
public enum SidecarRegistrationKind : uint
{
    Command = 1,
    Query = 2,
    State = 3,
    Event = 4,
    UiModule = 5,
    Resource = 6,
}

/// <summary>
/// One registration declaration: a kind, the stable semantic identifier, capability flags, and
/// the payload codec contract for state declarations. Codec 0 is the UTF-8 string codec; other
/// codes are reserved for the generated typed codecs.
/// </summary>
public readonly record struct SidecarRegistrationItem(
    SidecarRegistrationKind Kind,
    string SemanticId,
    uint Flags,
    uint CodecId);

/// <summary>
/// Encodes and decodes the REGISTER_* payloads: RegisterBegin carries the declaration count,
/// each RegisterItem frame carries one declaration, RegisterEnd closes the sequence. Contract
/// IDs are not on the wire here — both sides derive the identical session-local ID table from
/// the declaration order (per-kind ordinals starting at 1).
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
        writer.WriteUInt32(4, item.CodecId);
        return writer.ToArray();
    }

    public static SidecarRegistrationItem DecodeItem(ReadOnlySpan<byte> payload)
    {
        uint kind = 0;
        string semanticId = string.Empty;
        uint flags = 0;
        uint codecId = 0;
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
                case 4:
                    codecId = field.ReadUInt32();
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

        if (codecId != 0)
        {
            throw new SidecarProtocolException(
                $"The registration item declares codec {codecId}; only codec 0 (UTF-8 string) exists in this protocol draft.");
        }

        return new SidecarRegistrationItem((SidecarRegistrationKind)kind, semanticId, flags, codecId);
    }

    public static byte[] EncodeEnd() => [];
}

/// <summary>
/// Encodes and decodes the STATE_SNAPSHOT_* payloads the sidecar sends after REGISTER_END. The
/// host commits the snapshot into the fresh mirror, sends READY, and only then does activation
/// expose it — the reconnect atomicity contract.
/// </summary>
public static class SidecarStateSnapshot
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

        throw new SidecarProtocolException("The STATE_SNAPSHOT_BEGIN payload carries no item count.");
    }

    /// <summary>
    /// Encodes one snapshot item: the session-local state contract ID and its string value.
    /// </summary>
    public static byte[] EncodeItem(uint contractId, string value)
    {
        SidecarPayloadWriter writer = new();
        writer.WriteUInt32(1, contractId);
        writer.WriteString(2, value);
        return writer.ToArray();
    }

    public static (uint ContractId, string Value) DecodeItem(ReadOnlySpan<byte> payload)
    {
        uint contractId = 0;
        string value = string.Empty;
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
                    value = field.ReadString();
                    break;
            }
        }

        if (contractId == 0)
        {
            throw new SidecarProtocolException("The snapshot item carries no contract ID.");
        }

        return (contractId, value);
    }

    public static byte[] EncodeEnd() => [];

    /// <summary>
    /// Encodes the CANCEL payload: the correlation ID of the exchange to abort and a reason.
    /// </summary>
    public static byte[] EncodeCancel(Guid correlationId, string reason)
    {
        SidecarPayloadWriter writer = new();
        writer.WriteGuid(1, correlationId);
        writer.WriteString(2, reason);
        return writer.ToArray();
    }

    public static (Guid CorrelationId, string Reason) DecodeCancel(ReadOnlySpan<byte> payload)
    {
        Guid correlationId = Guid.Empty;
        string reason = string.Empty;
        SidecarPayloadReader reader = new(payload);
        while (reader.HasMore)
        {
            SidecarPayloadField field = reader.ReadNext();
            switch (field.Id)
            {
                case 1:
                    correlationId = field.ReadGuid();
                    break;
                case 2:
                    reason = field.ReadString();
                    break;
            }
        }

        if (correlationId == Guid.Empty)
        {
            throw new SidecarProtocolException("The CANCEL payload carries no correlation ID.");
        }

        return (correlationId, reason);
    }
}

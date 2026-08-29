namespace PCL.Sidecar.Protocol;

/// <summary>
/// Classifies one registration declaration. UiModule and Resource declarations carry their
/// content inline: the host caches the module and resources at registration, so opening a
/// registered plugin page performs zero IPC.
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
/// the payload codec contract for state declarations (codec 0 = UTF-8 string; 1 = Bool; 2 =
/// Int32; 3 = Int64; 4 = Float64; 5 = Bytes; 6 = schema-contracted DTO blob). UiModule and
/// Resource declarations additionally carry the content payload, its SHA-256 hash, and — for
/// UiModule — the semicolon-separated resource semantic references it requires.
/// </summary>
public readonly record struct SidecarRegistrationItem(
    SidecarRegistrationKind Kind,
    string SemanticId,
    uint Flags,
    uint CodecId,
    byte[]? Payload = null,
    byte[]? ContentHash = null,
    string? RequiredResources = null);

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
        if (item.Payload is { } payload)
        {
            writer.WriteBytes(5, payload);
        }

        if (item.ContentHash is { } hash)
        {
            writer.WriteBytes(6, hash);
        }

        if (item.RequiredResources is { } resources)
        {
            writer.WriteString(7, resources);
        }

        return writer.ToArray();
    }

    public static SidecarRegistrationItem DecodeItem(ReadOnlySpan<byte> payload)
    {
        uint kind = 0;
        string semanticId = string.Empty;
        uint flags = 0;
        uint codecId = 0;
        byte[]? content = null;
        byte[]? hash = null;
        string? requiredResources = null;
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
                case 5:
                    content = field.ReadBytes();
                    break;
                case 6:
                    hash = field.ReadBytes();
                    break;
                case 7:
                    requiredResources = field.ReadString();
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

        var declaredKind = (SidecarRegistrationKind)kind;
        if (codecId != 0 && declaredKind != SidecarRegistrationKind.State)
        {
            throw new SidecarProtocolException(
                "Only state declarations carry a payload codec contract.");
        }

        if (declaredKind is SidecarRegistrationKind.UiModule or SidecarRegistrationKind.Resource)
        {
            if (content is null || hash is null || hash.Length != 32)
            {
                throw new SidecarProtocolException(
                    $"The {kind} declaration '{semanticId}' must carry its content and SHA-256 hash.");
            }
        }

        return new SidecarRegistrationItem(
            (SidecarRegistrationKind)kind,
            semanticId,
            flags,
            codecId,
            content,
            hash,
            requiredResources);
    }

    public static byte[] EncodeEnd() => [];
}

/// <summary>
/// Encodes and decodes the STATE_SNAPSHOT_* payloads the sidecar sends after REGISTER_END. Item
/// values are raw codec-encoded bytes; the host validates the complete snapshot against the
/// registration and commits it atomically before sending READY.
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
    /// Encodes one snapshot item: the session-local state contract ID and the raw codec-encoded
    /// value.
    /// </summary>
    public static byte[] EncodeItem(uint contractId, ReadOnlySpan<byte> encodedValue)
    {
        SidecarPayloadWriter writer = new();
        writer.WriteUInt32(1, contractId);
        writer.WriteBytes(2, encodedValue);
        return writer.ToArray();
    }

    public static (uint ContractId, byte[] EncodedValue) DecodeItem(ReadOnlySpan<byte> payload)
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
            throw new SidecarProtocolException("The snapshot item carries no contract ID.");
        }

        if (encodedValue is null)
        {
            throw new SidecarProtocolException("The snapshot item carries no value.");
        }

        return (contractId, encodedValue);
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

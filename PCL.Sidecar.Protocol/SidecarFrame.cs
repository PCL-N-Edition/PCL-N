namespace PCL.Sidecar.Protocol;

/// <summary>
/// One Sidecar frame: the fixed header plus an opaque payload. Frames are the only unit on the
/// wire; the payload codec interprets the bytes per message type.
/// </summary>
public readonly record struct SidecarFrame(
    ushort ProtocolVersion,
    SidecarMessageType MessageType,
    SidecarFrameTraits Flags,
    SidecarCorrelationId CorrelationId,
    ReadOnlyMemory<byte> Payload)
{
    public bool IsControlPlane => (ushort)MessageType < 64;

    public bool IsDataPlane => (ushort)MessageType >= 64;
}

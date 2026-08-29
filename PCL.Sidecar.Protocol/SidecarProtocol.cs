namespace PCL.Sidecar.Protocol;

/// <summary>
/// The Sidecar protocol constants. The protocol is 1.0-draft (pre-freeze): framing and message
/// numbers are append-only, existing values never change meaning, and the freeze happens before
/// the Plugin SDK RC. Nothing here is a plugin-consumable stable ABI yet.
/// </summary>
public static class SidecarProtocol
{
    /// <summary>
    /// The frame magic: "PXCS" in little-endian byte order.
    /// </summary>
    public const uint Magic = 0x53584350;

    /// <summary>
    /// The current protocol version negotiated during the handshake.
    /// </summary>
    public const ushort Version = 1;

    /// <summary>
    /// The fixed frame header size in bytes: magic(4), header version(2), protocol version(2),
    /// message type(2), flags(2), correlation ID(16), payload length(4).
    /// </summary>
    public const int HeaderSize = 32;

    /// <summary>
    /// The maximum payload length in bytes. Guards against corrupt or hostile length fields.
    /// </summary>
    public const int MaxPayloadLength = 16 * 1024 * 1024;
}

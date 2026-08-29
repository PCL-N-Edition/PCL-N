namespace PCL.Sidecar.Protocol;

/// <summary>
/// The Sidecar message types. The control plane carries session lifecycle; the data plane
/// carries commands, queries, state, events, and streams. Numbers are append-only while the
/// protocol is in 1.0-draft (pre-freeze): existing numbers never change meaning and gaps are
/// filled only with new messages. Freezing happens before the Plugin SDK RC.
/// </summary>
public enum SidecarMessageType : ushort
{
    // Control plane: session lifecycle.
    Hello = 1,
    Welcome = 2,
    RegisterBegin = 8,
    RegisterItem = 9,
    RegisterEnd = 10,
    Ready = 11,
    Activate = 12,
    Deactivate = 13,
    HealthPing = 16,
    HealthPong = 17,
    StateSnapshotBegin = 14,
    StateSnapshotItem = 15,
    StateSnapshotEnd = 18,
    Cancel = 19,
    Crash = 24,
    Shutdown = 30,

    // Data plane.
    CommandRequest = 64,
    CommandResult = 65,
    QueryRequest = 66,
    QueryResult = 67,
    StateDelta = 72,
    Event = 73,
    StreamChunk = 80,
}

/// <summary>
/// Frame trait bits. Traits are advisory metadata; correctness never depends on a trait.
/// </summary>
[Flags]
public enum SidecarFrameTraits : ushort
{
    None = 0,

    /// <summary>
    /// The payload is compressed at the transport's discretion.
    /// </summary>
    Compressed = 1,

    /// <summary>
    /// The message is the last of a stream or registration sequence.
    /// </summary>
    Final = 2,
}

/// <summary>
/// Correlates one Sidecar exchange across the process boundary. Guid-based; the protocol never
/// exchanges CLR objects, so this is the protocol's own identity type.
/// </summary>
public readonly record struct SidecarCorrelationId(Guid Value)
{
    public static SidecarCorrelationId Create() => new(Guid.NewGuid());

    public bool IsAssigned => Value != Guid.Empty;

    public override string ToString() => Value.ToString("D");
}

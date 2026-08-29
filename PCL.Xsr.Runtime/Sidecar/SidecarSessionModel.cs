using PCL.Xsr.State;

namespace PCL.Xsr.Runtime;

/// <summary>
/// The host-side session lifecycle. A session starts handshaking; registration moves it to
/// ready; activation starts runtime behavior; shutdown closes it. Failures are terminal.
/// </summary>
public enum SidecarSessionState
{
    Handshaking = 1,
    Registering = 2,
    Ready = 3,
    Active = 4,
    Closed = 5,
    Failed = 6,
}

/// <summary>
/// One registration declaration the host accepted, keyed by the sidecar's stable semantic ID.
/// The kind is the protocol's own registration kind.
/// </summary>
public sealed record SidecarRegistrationEntry(Sidecar.Protocol.SidecarRegistrationKind Kind, XsrSemanticId SemanticId);

/// <summary>
/// The accepted registration of one session.
/// </summary>
public sealed class SidecarRegistrationSet
{
    public SidecarRegistrationSet(
        IReadOnlyList<SidecarRegistrationEntry> entries)
    {
        Entries = entries;
    }

    public IReadOnlyList<SidecarRegistrationEntry> Entries { get; }

    public IEnumerable<XsrSemanticId> Commands => Entries
        .Where(entry => entry.Kind == Sidecar.Protocol.SidecarRegistrationKind.Command)
        .Select(entry => entry.SemanticId);

    public IEnumerable<XsrSemanticId> Queries => Entries
        .Where(entry => entry.Kind == Sidecar.Protocol.SidecarRegistrationKind.Query)
        .Select(entry => entry.SemanticId);

    public IEnumerable<XsrSemanticId> States => Entries
        .Where(entry => entry.Kind == Sidecar.Protocol.SidecarRegistrationKind.State)
        .Select(entry => entry.SemanticId);

    public IEnumerable<XsrSemanticId> Events => Entries
        .Where(entry => entry.Kind == Sidecar.Protocol.SidecarRegistrationKind.Event)
        .Select(entry => entry.SemanticId);
}

/// <summary>
/// The per-session state mirror: one revisioned store whose cells correspond to the states the
/// sidecar registered. Cells start unavailable; the data plane publishes into them after the
/// session is active. The store is the renderer's only view of sidecar state.
/// </summary>
public sealed class SidecarStateMirror
{
    public SidecarStateMirror(string pluginName, XsrStateStore store)
    {
        PluginName = pluginName;
        Store = store;
    }

    public string PluginName { get; }

    public XsrStateStore Store { get; }

    public XsrStateId? TryResolve(XsrSemanticId semantic) =>
        Store.TryResolve(semantic, out XsrStateId stateId) ? stateId : null;
}

/// <summary>
/// Reports session lifecycle transitions and failures. Observer failures never change the
/// session.
/// </summary>
public interface ISidecarSessionObserver
{
    void OnStateChanged(SidecarSessionState state);
}

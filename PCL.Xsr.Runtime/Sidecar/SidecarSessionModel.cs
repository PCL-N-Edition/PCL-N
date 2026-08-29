using PCL.Sidecar.Protocol;
using PCL.Xsr.State;

namespace PCL.Xsr.Runtime;

/// <summary>
/// The host-side session lifecycle. Handshaking, registering (declarations accepted), ready
/// (state snapshot committed), active (runtime behavior), closed, failed. Deactivation returns
/// from active to ready without re-registration.
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
/// One accepted registration declaration, carrying its session-local contract ID. Contract IDs
/// are per-kind ordinals starting at 1 in declaration order; both sides derive the identical
/// table from the registration stream, so the data plane never carries semantic strings.
/// </summary>
public sealed record SidecarRegistrationEntry(
    SidecarRegistrationKind Kind,
    XsrSemanticId SemanticId,
    uint ContractId,
    uint Flags,
    uint CodecId);

/// <summary>
/// The accepted registration of one session with the session-local contract table.
/// </summary>
public sealed class SidecarRegistrationSet
{
    private readonly Dictionary<(SidecarRegistrationKind Kind, XsrSemanticId Semantic), SidecarRegistrationEntry> _byContract;

    public SidecarRegistrationSet(IReadOnlyList<SidecarRegistrationEntry> entries)
    {
        Entries = entries;
        _byContract = [];
        foreach (SidecarRegistrationEntry entry in entries)
        {
            _byContract[(entry.Kind, entry.SemanticId)] = entry;
        }
    }

    public IReadOnlyList<SidecarRegistrationEntry> Entries { get; }

    public IEnumerable<XsrSemanticId> Commands => OfKind(SidecarRegistrationKind.Command);

    public IEnumerable<XsrSemanticId> Queries => OfKind(SidecarRegistrationKind.Query);

    public IEnumerable<XsrSemanticId> States => OfKind(SidecarRegistrationKind.State);

    public IEnumerable<XsrSemanticId> Events => OfKind(SidecarRegistrationKind.Event);

    public IEnumerable<XsrSemanticId> UiModules => OfKind(SidecarRegistrationKind.UiModule);

    public IEnumerable<XsrSemanticId> Resources => OfKind(SidecarRegistrationKind.Resource);

    /// <summary>
    /// Resolves one declared contract to its session-local entry, or null when the semantic was
    /// not registered under that kind — the capability boundary for the data plane.
    /// </summary>
    public SidecarRegistrationEntry? TryResolve(SidecarRegistrationKind kind, XsrSemanticId semantic) =>
        _byContract.TryGetValue((kind, semantic), out SidecarRegistrationEntry? entry) ? entry : null;

    private IEnumerable<XsrSemanticId> OfKind(SidecarRegistrationKind kind) =>
        Entries.Where(entry => entry.Kind == kind).Select(entry => entry.SemanticId);
}

/// <summary>
/// The per-session state mirror: one revisioned store whose cells correspond to the states the
/// sidecar registered, typed by the declared codec (codec 0 = UTF-8 string). Cells start
/// unavailable; the pre-activation state snapshot fills them, and the mirror is coherent only
/// after that snapshot commits — before READY, before activation. The store is the renderer's
/// only view of sidecar state.
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

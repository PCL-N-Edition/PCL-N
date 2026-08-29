# XSR-403 Sidecar session, registration, and state mirror

## Outcome

Wave 4 adds the host-side session over a Sidecar connection: the locked handshake, registration acceptance, the per-session state mirror, activation, and shutdown. It also delivers the platform local-IPC stream factories (named pipes on Windows, Unix-domain sockets elsewhere) that the transport deferred.

## Locked contract

- Lifecycle: `SidecarHostSession` drives HELLO → WELCOME (protocol version enforced on both directions), REGISTER_BEGIN/ITEM*/END, READY, ACTIVATE, DEACTIVATE, SHUTDOWN over a `SidecarConnection`. Every await validates the expected message type; any deviation, duplicate semantic ID, or version mismatch fails the session terminally (state Failed, connection closed, reason observable). Failed sessions never resurrect.
- Registration: items are `(kind, semantic ID, flags)` with the kind and semantic ID validated by the protocol codec. Semantic IDs are parsed to `XsrSemanticId` at acceptance. Duplicate declarations fail registration.
- State mirror: one per-session revisioned store built from the accepted state declarations, owned by the plugin name. Cells start unavailable — the mirror becomes meaningful only when the data plane publishes. The mirror store is the renderer's only view of sidecar state; hand-built host state and mirrors never share cells.
- Activation semantics: ACTIVATE requires Ready; DEACTIVATE returns to the registered-but-inactive state; SHUTDOWN works from any live state and closes the connection.
- Session observers receive every lifecycle transition; observer failures never change the session.
- IPC factories: `SidecarIpcListener.Bind`/`AcceptAsync` and `SidecarIpcConnector.ConnectAsync` produce duplex streams over named pipes (Windows) or Unix-domain sockets (Linux/macOS), guarded by platform checks with a deterministic `PlatformNotSupportedException` elsewhere. The socket file is best-effort cleaned up on dispose. The factories produce plain streams; framing stays in the transport.
- Data-plane values in the mirror are string-encoded for now; typed state contracts arrive with the generated codecs (XSR-404 data plane).

## Non-goals

Command/query forwarding, event delivery, crash detection and reconnect, capability/permission validation, and multi-session fan-out are XSR-404 and later units.

## Verification

`PCL.Xsr.Runtime.Tests` covers the complete locked lifecycle over a loopback connection with transition observation, per-kind registration sets, mirror store presence with unavailable cells, handshake version-mismatch failure, duplicate-declaration rejection with transactional mirror state (no mirror on failure), terminal failed sessions, and unexpected-message failure. `PCL.Sidecar.Tests` covers a full frame round trip over real named-pipe IPC with concurrent receive (pipe writes block when the buffer fills, mirroring the production read loop). All suites pass locally, including NativeAOT for the protocol and transport.

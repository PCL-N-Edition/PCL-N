# XSR Sidecar Fabric v2

## Purpose

The Sidecar is a dynamic plugin code-execution engine, not a remote object graph or Host UI process.

```text
register once -> execute by ID -> observe through state -> render locally
```

Host owns plugin metadata, capabilities, permissions, registries, local UI modules, resource cache, and state mirrors. Sidecar owns CoreCLR, assembly load contexts, runtime patching where permitted, plugin code, native plugin dependencies, dispatch tables, and private runtime state.

## Session lifecycle

```text
PROCESS_START
  -> HELLO
  -> WELCOME
  -> DISCOVER
  -> VERIFY
  -> LOAD
  -> REGISTER_BEGIN
  -> REGISTER_*
  -> REGISTER_END
  -> READY
  -> ACTIVE
  -> DEACTIVATE
  -> UNREGISTER
  -> SHUTDOWN
```

Plugin construction is lightweight and cannot call Host capabilities. Registration declares services, commands, queries, states, events, UI modules, resources, permissions, and health. Activation starts runtime behavior.

## Planes

Control plane messages include handshake, registration, unregistration, version negotiation, health, reload, crash, error, and shutdown.

Data plane messages include command/result, query/result, state delta, event, and stream. Message numbers and frozen semantics are append-only after protocol v1 release.

## Frame and codec

Every frame carries protocol version, message type, flags, correlation ID, and payload length. The payload codec supports optional fields, unknown-field skipping, schema generation, forward/backward compatibility, and low-allocation decoding.

JSON is allowed for manifests, diagnostics, and debug dumps. It is forbidden on the production data-plane hot path. A fixed, non-extensible CLR struct layout is also forbidden as the wire ABI.

## Dispatch

Source generation produces registration, codecs, numeric IDs, local stubs, and static dispatch tables. Runtime hot paths do not use `Type`, `MethodInfo`, `Activator`, reflection invocation, or repeated string dictionaries.

Semantic identifiers are stable across builds. Compact runtime IDs are negotiated per Host/Sidecar session and do not need to survive reconnect.

## Transport

The first transport is reliable local IPC: named pipes on Windows and Unix-domain sockets on Unix. Transport remains replaceable behind the protocol. Shared memory or a ring buffer is introduced only after benchmarks demonstrate a need, primarily for high-frequency streams.

## Correctness requirements

- commands and queries carry correlation, timeout, cancellation, and a stable error model;
- queues are bounded and expose backpressure;
- replaceable state updates may coalesce to the newest value;
- events are not silently coalesced;
- renderer state reads and registered UI opens perform zero IPC;
- shutdown is ordered and cannot block the UI thread;
- a Sidecar crash cannot terminate the Host.

## Reconnect

Reconnect starts a new session: handshake, complete registration, state snapshot, then activation. The Host marks old mirrors stale/unavailable, keeps locally registered UI renderable, rejects commands with a clear unavailable result, and replaces the mirror only after the new snapshot is coherent.

## Compatibility surfaces

Sidecar protocol, Plugin SDK, Plugin API, manifest schema, package format, Plugin UI IR, PXML language, and XSR product version are independent version axes. None may be inferred from another.

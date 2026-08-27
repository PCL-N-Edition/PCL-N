# XSR service model

## Capability boundaries

Services are grouped by business capability and change ownership, not by legacy assembly layout. Initial families include Settings, Logging, Telemetry, File, Network, Download, Account, Update, Minecraft, and Cloud. A capability stays in the modular monolith unless an independent deployment boundary has a demonstrated need.

## Service rules

A service:

- contains no Avalonia, ViewModel, renderer, window, dialog, or toast code;
- accepts commands and queries through declared contracts;
- publishes durable facts as state and transient facts as events;
- honors cancellation and never synchronously waits on asynchronous work;
- returns a documented error model rather than leaking transport or UI exceptions;
- owns its persistence and external integration ports;
- is constructed through the composition root, not a global service locator.

Services do not manipulate UI enablement or visibility. For example, a launch service publishes `launch.status` and `launch.progress`; derived state may expose `launch.canStart`; UI.Next decides how those facts appear.

## Command semantics

Commands request effects. Acceptance and business completion are separate facts:

```text
StartDownload command
  -> accepted or rejected
  -> download.status = Running
  -> progress state deltas
  -> download.completed or download.failed event
```

A command handler defines idempotency, concurrency, cancellation, authorization, and retry semantics. Fire-and-forget means the caller does not await business completion; it does not mean errors are discarded.

## Query semantics

Queries are reserved for one-time results such as discovery, search, file selection, or an expensive calculation. Queries are asynchronous, cancellable, correlated, timed out at an explicit boundary, and forbidden in renderer frame or binding evaluation paths.

Repeatedly queried data should become state. A query must not be introduced merely to preserve an old RPC shape.

## Events

Events describe facts that already occurred. They are ordered within their documented scope and cannot maintain progress, selection, account, or other current state. Consumers must tolerate replay or duplication when the transport contract allows it.

## Migration unit

Each migrated capability is a vertical slice containing its service behavior, contracts, commands/queries, state/events, composition, and parity tests. The old implementation remains unchanged until the slice passes behavior, data, architecture, AOT/trim, and integration gates.

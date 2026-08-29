# XSR-517 network and telemetry capabilities

## Outcome

Two capability families land together as thin, fixture-testable services. Network gains
reachability probing with wall-clock latency per endpoint — the input mirror ranking and
offline explanations need. Telemetry gains strictly opt-in event buffering with a bounded
queue, an explicit flush through an upload port, and a pending-count state cell. Both run
over a caller-owned `HttpClient` / transport port so tests are stub-handler fixtures.

## Locked contract

- Network probes: every configured endpoint is measured with a headers-early GET and
  wall-clock latency. Status codes below 500 count as reachable (an HTTP error is an
  answer; the network is up); connection failures, timeouts, and 5xx are unreachable. A
  probe never throws for an unreachable network — failures become `Unreachable` results with
  the error text, so surfaces can rank mirrors and explain offline states uniformly.
- Telemetry consent is a hard gate: without consent `Record` is a no-op and `FlushAsync`
  uploads nothing — the legacy `TelemetryExperienceProgram = false` default is a rule, not a
  starting value. Events carry a semantic name, a UTC timestamp, and free-form properties.
- Buffering is bounded: at capacity the oldest event drops. The pending depth publishes as
  one integer state cell (`telemetry.pending`, owner `PCL.Services.Telemetry`) — the queue
  depth, not the lifetime total — so surfaces read it like any other state fact.
- Flush: the whole buffer goes through `ITelemetryTransport.SendAsync` as one batch; success
  clears exactly the events that were sent (records racing the flush stay), rejection or an
  empty buffer changes nothing and returns zero. Batches serialize to a stable JSON array
  (name, unix-millisecond timestamp, ordinal-ordered properties) via source-generated-free
  `Utf8JsonWriter` — AOT-safe.

## Deliberate scope

No scheduler (flush cadence is a composition decision), no OS network-state integration
(connectivity change events can wrap `NetworkProbeService` later), and no schema registry
for event properties — names are semantic identifiers by convention and validated at the
transport boundary when the upload endpoint lands.

## Verification

`tests/PCL.Services.Tests` (120 executable tests, 5 new) covers: probe outcomes for a
reachable endpoint (status, non-negative latency, no error), an HTTP-level failure that is
still "reachable", and a connection failure that is unreachable with error text; telemetry
recording nothing without consent; bounded buffering with oldest-eviction and a state cell
tracking the current depth; flush uploading the batch (property contents intact), clearing
the buffer, refusing rejected batches with retention, and no-op empty flushes; and stable
batch serialization with ordinal property ordering. Runs under CoreCLR and NativeAOT in CI.

# XSR-402 Sidecar transport and connection lifecycle

## Outcome

Wave 4 adds the frame transport and the connection lifecycle in `PCL.Sidecar.Transport`: framed reading and writing over any duplex stream, an explicit connection state machine, and an in-memory loopback pair for tests and in-process hosts.

## Locked contract

- `SidecarFrameTransport` reads and writes frames over a duplex `Stream`: writes are serialized under a gate so concurrent senders cannot interleave frame bytes; reads pull the 32-byte header, validate magic and the 16 MiB payload bound, read the payload, and decode over the complete frame as one contiguous buffer. Short reads raise `EndOfStreamException`; garbage bytes raise `SidecarProtocolException`. Both poison the stream.
- `SidecarConnection` owns the lifecycle: `Connected → Closed` on graceful close, `Connected → Failed` on protocol or stream failure with the reason observable. Closed and failed connections reject further use; failures are terminal — reconnection is the session's job (XSR-404), not the connection's.
- `SidecarLoopbackStream` is an unbounded in-memory duplex pair: writes appear on the peer's read buffer, closing one end delivers EOF to the peer after buffered bytes, and reads wait asynchronously when empty. It exists for tests and in-process hosts; the real named-pipe and Unix-domain-socket stream factories land with the session unit (XSR-403) that consumes them.
- Cancellation on send is observed before bytes are written, so a cancelled send leaves the connection usable.

## Non-goals

Named pipes, Unix-domain sockets, request/response correlation, backpressure accounting, and half-close semantics are later Wave 4 units. The transport never interprets payloads — framing only.

## Verification

`PCL.Sidecar.Tests` covers loopback frame round trips with correlation and traits, eight concurrent senders producing zero interleaved frames and a complete event set, garbage-header failure transitioning the connection to Failed with a reason, peer close delivering EOF after buffered frames, idempotent close rejecting further use, and pre-write send cancellation leaving the connection usable. The transport project is AOT-compatible and covered by the architecture gate.

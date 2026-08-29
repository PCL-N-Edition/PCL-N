# XSR-401 Sidecar protocol surface

## Outcome

Wave 4 starts the Sidecar Fabric: the versioned, extensible binary protocol surface in `PCL.Sidecar.Protocol` — frame framing, message-number table, and the tag-length-value payload codec with unknown-field skipping. The project has zero project references; neither Host nor plugin code exchanges CLR objects through it.

## Locked contract

- Frames: a fixed 32-byte little-endian header — magic, header version, protocol version, message type, trait bits, correlation ID, payload length — followed by an opaque payload. Correlation IDs are protocol-owned Guid identities. Traits are advisory; correctness never depends on a trait.
- Validation: magic, header version, and negotiated protocol version are enforced on decode; unknown message types, undefined trait bits, and payload lengths that disagree with the buffer or exceed the 16 MiB maximum are rejected before any allocation sized by the wire.
- Message numbers are frozen by test for protocol v1: control-plane lifecycle (Hello 1, Welcome 2, RegisterBegin/Item/End 8-10, Ready 11, Activate 12, Deactivate 13, HealthPing/Pong 16-17, Crash 24, Shutdown 30) and data-plane exchange (CommandRequest/Result 64-65, QueryRequest/Result 66-67, StateDelta 72, Event 73, StreamChunk 80). The numbering is append-only; existing numbers never change meaning.
- Payload codec: fields are tag-length-value records with strictly ascending field IDs — deterministic layout, O(1) duplicate detection, and cheap skipping. Readers skip unknown field IDs by length (the forward-compatibility contract) and reject known IDs carrying unexpected type tags. Fixed-width types are bounded; strings and byte blobs carry a 64 KiB length. Field iteration is ref-struct based and allocation-free: decoding an empty-payload frame allocates zero bytes, and a payload-bearing frame allocates exactly its payload copy.
- Strings are UTF-8; all numbers are little-endian. JSON is never used on this surface.

## Non-goals

The handshake and version negotiation messages, the named-pipe/Unix-socket transport, session state machines, state mirroring, and security validation are later Wave 4 units; this unit defines only the wire surface they build on.

## Verification

`PCL.Sidecar.Tests` (new executable test project) covers frame round trips, per-type payload round trips including multi-byte UTF-8, unknown-field skipping, magic and version enforcement, truncated/hostile-length rejection, unknown message types, frozen numbering, ascending-ID enforcement, tag-mismatch rejection, deterministic malformed-payload failures, zero-allocation decode, and correlation identity. The protocol project is AOT-compatible and covered by the architecture gate.

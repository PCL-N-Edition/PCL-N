# XSR-504 download capability

## Outcome

The download capability migrates the legacy failover download engine into `PCL.Services`:
ordered source failover with resume, one shared transfer per destination, bounded-buffer
streaming, and the full progress-stage contract — now published as an ordered state
collection so surfaces read active transfers locally instead of subscribing to engine
callbacks.

## Locked contract

- Ports: `IDownloadConnection` (start from offset, stop, buffered reads where zero means end
  of stream) and `IDownloadWriter` (resume-aware existing length, offset open, stop, commit)
  are the capability's external boundaries, exactly the legacy connection/writer split.
  `DownloadRequest` carries ordered sources, one destination, the two factories, and the
  parallel-segment preference; the default writer factory is the file writer.
- File writer parity: `FileDownloadWriter` keeps the legacy temporary-file scheme
  (`<destination>.PCLDownloading`), resume via existing temp length, truncate-restart at
  offset zero, bounded-retry atomic rename on finish, and bounded-retry temp cleanup.
- Failover engine: sources are tried in order; per-source failures are recorded as
  `DownloadAttemptError` and the next source is tried; a resumed destination rejected with
  HTTP 416 discards the partial file before continuing. Progress stages flow in the legacy
  order — Connecting, Reading, Downloading, Committing, Completed — with Retrying between
  failed sources and Failed after the last source. Speed is bytes over wall time, zero
  before the first 100 ms.
- Destination coalescing: concurrent `DownloadAsync` calls for the same destination share
  one transfer (path comparer is case-insensitive on Windows, ordinal elsewhere). Every
  caller keeps an independent progress callback and cancellation token; when the last waiter
  leaves without completion, the shared transfer is cancelled. Progress handlers cannot
  terminate a transfer — handler exceptions are swallowed.
- Published state: `download.transfers` is an ordered collection of
  `DownloadTransferView` keyed by destination path (owner `PCL.Services.Downloads`). Every
  progress report upserts the view; terminal reports are visible briefly, then the entry is
  removed — the collection is exactly the set of active transfers. Renderer reads are local
  state reads, never engine callbacks.
- Cleanup isolation: writer and connection shutdown swallow cleanup exceptions so the
  original transfer outcome is preserved, matching the legacy rule that diagnostic and
  cleanup paths never mask results.
- Logging: the service optionally receives the Wave 5 `LogService` through its constructor
  and writes the legacy log lines (queued, per-source attempts, resume checks, completion,
  exhaustion) with the `Download` module name — no static logging.

## Deliberate scope

Segmented parallel transfer (`MaxParallelSegments > 1`) is rejected eagerly with
`NotSupportedException` rather than silently degrading to one stream; it lands as its own
unit with the segmented connection contract, part-file assembly, and progress aggregation
the legacy engine implemented. The legacy immediate rewrite of a 416-discarded source list
is unchanged otherwise.

## Verification

`tests/PCL.Services.Tests` (44 executable tests, 10 new) covers failover with error
attribution, total failure, resume across service instances through the temp file, one
shared transfer for concurrent callers (the second caller's connection factory must never
run), stage ordering, hostile progress handlers, cancellation, eager segmented rejection,
the active-transfer state lifecycle (appear, mirror stages, drain to empty), and the file
writer's short-resume rejection plus truncate-append commit. Fake in-memory ports keep
every scenario deterministic without network. The project runs under CoreCLR and NativeAOT
in CI.

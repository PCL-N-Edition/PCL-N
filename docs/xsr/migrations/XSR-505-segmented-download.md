# XSR-505 segmented download

## Outcome

Completes the download capability: `MaxParallelSegments > 1` now performs the legacy
segmented parallel transfer instead of being rejected. One source is split into byte ranges,
fetched in parallel through per-segment connections, and assembled into the destination
through the regular writer commit path. Sources that cannot serve segments fall back to the
single-stream engine from XSR-504.

## Locked contract

- Segmented connection port: `ISegmentedDownloadConnection` extends `IDownloadConnection`
  with `StartSegmentAsync(begin, end)` returning the negotiated range. The engine probes with
  the legacy `(0, 0)` request; a probe answer that denies segmentation, reports a nonzero
  range, or declares a non-positive length means "this source cannot segment" and the engine
  falls back to one stream.
- Segment planning, migrated: segment count is `min(MaxParallelSegments,
  ceil(totalLength / minimumSegmentBytes))` with an 8 MiB legacy default floor (constructor
  parameter for testing). Counts at or below one fall back to the single-stream path. Ranges
  partition the file exactly: `[i * L / n, (i+1) * L / n - 1]`.
- Per-segment execution: each segment gets its own connection from the request's connection
  factory, verifies the server echoed the exact requested range (a mismatch is an
  `IOException`), streams into its own part file
  (`<destination>.PCLSegment.<guid><index>`), and requires the exact expected byte count — a
  short stream is an `EndOfStreamException`. Part files are deleted on every exit path,
  never masking the transfer outcome.
- Aggregated progress: downloaded bytes are summed across segments under a lock and reported
  as ordinary `Downloading` progress at most once per megabyte of movement, with a final
  report at the full length; speed is computed over the segmented phase as in the legacy
  engine.
- Assembly and commit: after all segments finish (`Task.WhenAll` waits for every task even
  on fault), the destination writer opens at offset zero, part files are concatenated in
  order, and the normal Committing → Finished → Completed stages flow. Any segment failure
  fails the whole segmented attempt; the exception propagates to the source failover loop,
  which records the attempt error and moves to the next source — the same behavior as the
  legacy engine.
- Fallback safety: a segmented-capable source used for a below-floor file behaves exactly
  like a plain single-stream source; `MaxParallelSegments = 1` never touches the segmented
  path.

## Verification

`tests/PCL.Services.Tests` (49 executable tests, 6 new replacing the eager-rejection probe)
covers: parallel assembly byte-for-byte with part-file cleanup (small floor makes 100 bytes
split into four-plus segments); fallback when the connection is not segmented; fallback when
the file is below the configured floor; a server that reports a wrong range on real segment
requests failing over to an honest source; a server that truncates a segment (early EOF)
failing over with the attempt error recorded; and aggregated progress reaching Completed at
exactly the full length. All scenarios are deterministic in-memory fakes. The suite runs
under CoreCLR and NativeAOT in CI.

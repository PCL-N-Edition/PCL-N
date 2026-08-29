# XSR-507 update block data contracts

## Outcome

First unit of the Update family: the content-addressed block layer's data contracts —
content-defined chunking, full-block compression codecs, and the local block index — migrated
as pure algorithms with format parity. These are the pieces every later update unit
(discovery, orchestration, installers) builds on, and their identifiers and file shapes are
frozen by the legacy updater's released blockmaps.

## Locked contract

- Chunking profiles: `pcln-fastcdc-v1` (256 KiB / 1 MiB / 2 MiB, masks 21/19) and
  `pcln-fastcdc-v2` (128 KiB / 512 KiB / 1 MiB, masks 20/18) with byte-exact bounds and dual
  masks; `TryGet` falls back to v1. The gear table is SplitMix64 over the constant
  `0x9E3779B97F4A7C15`, the rolling rule is `(rolling << 1) + gear[byte]`, the early/late
  mask switch happens at the average size, and the last partial chunk always lands. Chunk
  identity is lowercase hex SHA-256 over the raw slice.
- Blockmap layout identifiers stay exactly: `pcln-blockmap-v1`, `pcln-blockmap-file-v1`,
  `pcln-blockmap-v2`, `pcln-blockmap-file-v2`.
- Block map model: `UpdateBlockMap` / `UpdateBlockFile` / `UpdateBlock` / `UpdateBlockFull` /
  `UpdateBlockDelta` / `UpdateChunkingParameters` / `UpdateFileEntry` with the legacy JSON
  property names, read case-insensitively through a source-generated context (AOT-safe).
  `ResolveFullPath` / `ResolveCompressedSize` / `ResolveCompression` keep the legacy
  full-over-flat and per-block-over-default precedence.
- Codecs: `gzip` (default) and `zstd`, with the legacy alias set (`gz`, `zst`, `zstandard`;
  unknown → `InvalidDataException`). Detection is by magic (1f 8b / 28 b5 2f fd); the
  declared compression is advisory — decoding follows the detected format while verification
  stays bound to the declared identity: exact raw size (over-limit fails fast) and SHA-256
  over the decompressed bytes, streamed through a rented slab buffer with a prefix-replay
  stream so nothing is double-read. The legacy mismatch warning line moves to the
  orchestration unit, which owns logging.
- Local block index: `UpdateState/installed.blockmap.json` inside the installation root,
  saved atomically (temporary file + replace, failures swallowed — the index is an
  optimization and the fallback is live chunking). Reuse requires, per file: containment in
  the installation root (path escapes skipped), exact size match, and full-file SHA-256 match
  when declared; only needed hashes are retained. Window reconstruction re-verifies every
  chunk hash and the window hash before returning bytes; any mismatch returns null so callers
  fall back to downloads.

## Deliberate scope

Package models (patch steps, channels, GitHub DTOs), the vcdiff delta codec, GPG signature
verification, and the update service orchestration stay in their own upcoming units — this
one carries only the block layer's pure contracts. `ZstdSharp.Port` 0.8.6 is pinned at the
legacy version so zstd frames remain bit-compatible.

## Verification

`tests/PCL.Services.Tests` (62 executable tests, 8 new) covers: profile bounds/masks and
layout identifier parity; deterministic chunking over a 6 MiB payload with full coverage,
min/max bounds, per-slice hash correctness, small-file single-chunk behavior, and v1/v2
boundary divergence; codec normalization/aliasing/magic detection; gzip and zstd round trips
through multi-read streams with SHA-256 and size-limit enforcement and a declared-vs-actual
codec mismatch; installed-map JSON round trip including nested full/delta representations
and chmod-mode metadata; index reuse gated by root containment, size, and full-file hash
with needed-hash filtering; and window reconstruction with per-chunk and window hash
verification plus corruption rejection. All deterministic, no network. Runs under CoreCLR
and NativeAOT in CI.

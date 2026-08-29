# XSR-516 payload extraction and patch orchestration

## Outcome

The Update family's application side comes online: zip and tar payload extraction into a
verified staged root, HDiffPatch integration behind a process port, full-file patch-chain
application over the running binary, and scatter bundle operations that produce exactly the
manifest's promised tree. Combined with XSR-504/505 (downloads), 509 (planning), 510
(discovery), 511 (signatures + vcdiff), and 512 (staged install), the updater can now apply
an update end to end.

## Locked contract

- Payload extraction: `UpdatePayloadExtractor.ExtractZipAsync/ExtractTarAsync` unpack into
  the staged root. Archive entry paths are normalized (backslash separators, leading `./`,
  trailing slashes) and resolved inside the staged root — traversal entries are hard
  failures. Every extracted file is SHA-256-hashed on the way in and returned as an
  `UpdateFileEntry` inventory with sizes and Unix modes (zip external attributes high word,
  tar entry mode) so `UpdateStaging.VerifyStagedTree` consumes it unchanged. Directory
  markers are skipped, bare directory entries still resolve (and refuse escapes).
- HDiffPatch: `IProcessRunner` is the external-tool port (production `ProcessRunner` over
  `System.Diagnostics.Process`, argument list vector, no shell); `HDiffPatchTool` runs the
  legacy `hpatchz source patch output` command line and turns any nonzero exit into an
  `InvalidDataException` — a dubious patched file is never kept.
- Binary chain: `ApplyBinaryChainAsync` verifies the running binary against the first step's
  source digest, downloads each patch through the injected delegate, verifies every patch by
  SHA-256 and size, applies `hpatchz` hop by hop, verifies the final output against the last
  step's target digest, then moves it to the staged path. Work files live in a per-run
  temporary directory that never survives, and the current-file digest check fails fast
  before any download.
- Scatter operations: `UpdateScatterPatchManifest`/`Operation` keep the `files.json`
  contract. `ApplyScatterOpsAsync` walks the ops — `hdiff` verifies the current file's source
  digest, extracts and verifies the patch member, applies, and checks the target digest and
  size; `add`/`replace` extract and verify bundle blobs; `delete` stages nothing — and then
  requires the staged tree to satisfy the manifest's own target files (and restores their
  Unix modes). A corrupted member refuses before anything is staged. Deletions become
  install-plan managed leftovers via XSR-512's `BuildPlan` as before.

## Deliberate scope

The helper process hand-off and restart scheduling (`ScheduleInstallAndRestart`,
replacement-process creation) remain orchestration: they touch process lifecycle and the
desktop composition root and land with the product UI integration. Progress event mapping
onto download progress trackers is likewise composition-level.

## Verification

`tests/PCL.Services.Tests` (115 executable tests, 5 new) covers: zip extraction with nested
directories, Unix mode restoration, wrapper-root flattening, traversal refusal (outside
file untouched), and per-file inventory hashes; tar extraction with modes and content;
HDiffPatch argument order through the port with nonzero-exit refusal; a two-hop binary
chain with download order, digest verification, final output placement, work-directory
cleanup, and fast refusal on a current-file digest mismatch; and scatter `hdiff`/`add`/
`delete` operations producing a verified staged tree with tool invocation checks and a
corrupted-blob refusal. All offline via fake runner and in-memory downloads. Runs under
CoreCLR and NativeAOT in CI.

# XSR-512 staged install core

## Outcome

The update family gains its staged-install core: verify a downloaded tree against the target
manifest, flatten single-package wrapper roots, build the install plan (target files plus the
managed leftovers to delete), and apply that plan into the installation — safe paths,
re-verification before every placement, atomic replaces, Unix mode restore, and deletes.
This is the trust-critical tail of the update flow that XSR-507 (block codecs), XSR-509
(planning), XSR-510 (discovery), and XSR-511 (signatures) feed.

## Locked contract

- Install plan: `UpdateInstallPlan` carries the legacy install-plan contract verbatim —
  `formatVersion` (1), `installRoot`, `entryRelativePath`, `stagedRoot`, the target `files`
  (with sha256, size, unixMode), and `deletePaths` — serialized through the shared
  source-generated JSON context so the plan file round-trips with the legacy helper.
- Verify before anything: `VerifyStagedTree` resolves every manifest entry under the staged
  root (safe paths only), requires presence, exact size, and SHA-256 match, and throws
  `InvalidDataException` naming the path on the first mismatch.
- Single-root flattening: while the staged root holds exactly one directory and no files, its
  contents move up one level (zip/tar wrappers); a name collision keeps the wrapper rather
  than merging.
- Plan building: managed leftovers = every file under the install root, recursively, that the
  target manifest no longer lists, excluding the updater-owned `UpdateState/` directory.
  Manifest paths and the entry path are safe-resolved against the install root at plan time,
  so traversal is refused before the plan exists, not only when it is applied.
- Applying: every staged file is re-verified (size + SHA-256) at apply time — the plan is a
  hand-off across a process boundary and re-checks its own inputs — then moved into the
  install root with atomic overwrite, directories created as needed, and Unix modes restored
  on Unix only. Delete paths are safe-resolved and tolerate absence. A replayed plan refuses
  (the staged files are consumed by the first apply) instead of double-installing.
- Failure model: verification and safety problems throw `InvalidDataException`; deletes and
  missing optional state never mask an otherwise successful apply.

## Deliberate scope

Zip/tar payload extraction, HDiffPatch patch application, the external helper process
(`CreateReplacementProcess`, restart scheduling), the Cloudflare client certificate, and the
block download/materialization loop around the local index belong to the orchestration unit
that drives `VerifyStagedTree → FlattenSingleRoot → BuildPlan → ApplyPlan`. The staged-core
contract those units must produce is locked here, including the plan file shape.

## Verification

`tests/PCL.Services.Tests` (93 executable tests, 5 new) covers: staged-tree verification
rejecting missing files, hash mismatches, and size mismatches with path-bearing errors;
wrapper-root flattening including mixed-content stop and untouched direct roots; plan
building that inventories managed leftovers (excluding `UpdateState/`), round-trips the plan
file contract, and refuses traversal at plan time; plan application placing re-verified
files, creating directories, running deletes (including a missing path), consuming staged
files, and refusing replay; and traversal refusal in both manifest and delete entries with
the outside file untouched. Runs under CoreCLR and NativeAOT in CI.

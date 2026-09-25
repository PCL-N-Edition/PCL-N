# XSR-515 file capability

## Outcome

The File family opens with the shared persistence substrate every other service has been
re-implementing inline: the canonical application data folder tree and a safe file port with
atomic writes, traversal refusal, and a size cap. Settings, profiles, logs, the updater's
`UpdateState`, and the download cache all resolve through one tree and one port.

## Locked contract

- Folder names are the on-disk contract and never change across releases: `logs`,
  `UpdateState`, `profiles`, `settings`, `cache`. `UpdateState` matches the block index
  directory XSR-507 already excludes from managed deletes; the installed block map therefore
  lives at `<root>/UpdateState/installed.blockmap.json`.
- `AppFolders` confines everything to one resolved root. `ResolveSafePath` normalizes
  separators, requires single-segment canonical folder names, and refuses any resolution
  that escapes the selected folder — with case-insensitive comparison on Windows and ordinal
  comparison elsewhere. This security correction supersedes root-wide sibling traversal:
  callers must explicitly select another folder rather than reach it using `..`.
  `EnsureFolder` creates on first use. This API is a trusted composition utility, not a
  delegated plugin capability; folder selection itself must remain host-controlled.
- Default root resolution is a composition decision with one rule: the `Nexa_NEXA_DATA_DIR`
  environment variable wins, otherwise the per-user local application data directory under
  `Nexa` (the branch product name; the folder name follows the official rename when it
  happens).
- `SafeFilePort`: UTF-8 text and binary reads with missing-files-as-null; writes are
  atomic — content lands in a unique temporary file and then replaces the destination with
  bounded retries, so readers never observe a torn file and failed writes leave no debris;
  the destination directory tree is created on demand; a per-file size cap (default 64 MiB)
  rejects oversized writes before anything touches the disk and bounds actual bytes read
  before text decoding (including BOM detection); deletes report whether they
  removed anything. All operations refuse traversal identically.

## Deliberate scope

The generic file port does not encrypt arbitrary files or maintain an independent audit log.
Live account credentials instead use [protected account storage](account-protected-storage.md)
with OS-backed protection. The Network and Telemetry families are their own units.

## Verification

`tests/Nexa.Services.Tests` (110 executable tests, 4 new) covers: canonical folder
resolution and creation; traversal refusal on write and read including sibling-folder escapes;
UTF-8 text (including non-ASCII and BOM detection) and binary
round trips, overwrite, absence-as-null, atomic-write cleanliness (no temporary debris), and
deletes; and the size cap rejecting oversized writes without side effects plus default-root
resolution honoring the environment override. Runs under CoreCLR and NativeAOT in CI.

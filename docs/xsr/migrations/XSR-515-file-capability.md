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
  that escapes the root — with case-insensitive comparison on Windows and ordinal
  comparison elsewhere. The boundary is the root: reaching a sibling canonical folder is
  allowed, leaving the tree never is. `EnsureFolder` creates on first use.
- Default root resolution is a composition decision with one rule: the `PCL_NEXA_DATA_DIR`
  environment variable wins, otherwise the per-user local application data directory under
  `PCL Nexa` (the branch product name; the folder name follows the official rename when it
  happens).
- `SafeFilePort`: UTF-8 text and binary reads with missing-files-as-null; writes are
  atomic — content lands in a unique temporary file and then replaces the destination with
  bounded retries, so readers never observe a torn file and failed writes leave no debris;
  the destination directory tree is created on demand; a per-file size cap (default 64 MiB)
  rejects oversized writes before anything touches the disk; deletes report whether they
  removed anything. All operations refuse traversal identically.

## Deliberate scope

No encryption-at-rest and no independent audit log yet — credentials in the profile roster
rely on the platform's file permissions exactly as the legacy launcher did; an OS keychain
port is a future decision. The Network and Telemetry families are their own units.

## Verification

`tests/PCL.Services.Tests` (110 executable tests, 4 new) covers: canonical folder
resolution and creation; traversal refusal on write and read for genuinely escaping paths
while in-tree cross-folder reach stays legal; UTF-8 text (including non-ASCII) and binary
round trips, overwrite, absence-as-null, atomic-write cleanliness (no temporary debris), and
deletes; and the size cap rejecting oversized writes without side effects plus default-root
resolution honoring the environment override. Runs under CoreCLR and NativeAOT in CI.

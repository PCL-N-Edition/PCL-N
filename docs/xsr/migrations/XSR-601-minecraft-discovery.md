# XSR-601 — Minecraft version and instance discovery

## Scope

This unit establishes the portable Minecraft-core boundary for local discovery. It migrates
version catalog classification, safe version JSON/JAR resolution, deterministic installation
enumeration, and the schema-1 per-instance metadata document. It deliberately does not launch
Java, download artifacts, or depend on a ViewModel.

## Locked behavior

- `versions/<reference>/<reference>.json` is preferred when it exists; a JSON `id` match is
  accepted as the compatibility fallback.
- Version references are single safe file names. Rooted paths, `.`/`..`, separators, invalid
  file-name characters, and references longer than 180 characters are rejected.
- Discovered directories and candidate files are ordered with ordinal-ignore-case comparison so
  the same installation produces the same catalog on every platform.
- `PCL/InstanceMetadata.json` is schema 1 and preserves the legacy field names/defaults. Missing,
  unreadable, malformed, or newer documents return a default metadata value.
- Metadata writes use a per-path lock and temporary-file replacement. A reader therefore sees
  either the previous complete document or the next complete document, never a partially written
  JSON file. Read-modify-write updates serialize through the same lock.

## Verification

`tests/PCL.Services.Tests` covers catalog aliases, April-Fools normalization, path traversal
rejection, deterministic discovery, metadata round trips, atomic file existence, and concurrent
read-modify-write updates. The service project remains the single business boundary and has no
reference to the legacy worktree, UI, or Runtime router.

# Folder import: source inheritance

The folder import Service resolves a bounded inheritance chain before publishing the leaf.
For a source under `versions/<id>`, missing parents may be obtained from that source root.
Only each missing parent's manifest and optional own JAR are copied, never its settings,
worlds, mods or arbitrary siblings. An existing target parent is preserved; when both roots
provide a manifest, differing bytes are treated as a conflict rather than guessed compatible.

All source references use safe version IDs and reject links. The staged chain is validated
again before commit. Dependencies are committed without overwrite before the leaf; a later
failure may retain completed reusable parent dependencies, but never publishes an incomplete
leaf. This is not an atomic multi-directory filesystem transaction. Detached versions can
still reuse target parents, and missing parents produce an explicit error before launch.

# Local installation recovery authorization

An imported game directory is data, never proof that this host approved running an installer or publishing files. Before discovery, resume, rollback or prepared publication consumes a journal, its exact bounded bytes must match local provenance outside the game root.

The Services-owned InstallRecoveryAuthority stores SHA-256 receipts in the current user profile. Identity includes normalized absolute root, task category, task GUID and record path. Fresh authorized commands and verified metadata producers record receipts before atomic journal publication. Readers verify bytes before parsing/using them. This covers task intent/status, loader installer receipts, persistent metadata, modpack download/publication plans and markers, installation publication plans/progress, and standalone/nested rename plans/progress. Blob and installer bytes remain checked against these authenticated hashes.

Unapproved legacy or imported records are retained, rejected with an actionable message, and never silently adopted. Deleted terminal records cannot revert a locally completed/canceled task to pending. Legitimate paused tasks produced by this version retain their pinned intent and metadata across process restart. A crash between the authority write and journal replacement fails closed with the files preserved; it cannot execute mismatched state. Cross-record atomic recovery remains a separate availability improvement.

This boundary addresses game-directory-controlled records. It does not claim isolation from malware already able to write the same user's private launcher data, or from active same-user filesystem substitution. The stricter updater privilege boundary remains separate. Game roots containing the authority store are rejected. No Sidecar or UI service lookup is added.

Recovered scratch is rebuilt from locally verified completed downloads. Unapproved residual files are removed before rerunning the authorized build, so enumeration cannot promote injected files into fresh publication authority. Missing pinned metadata, installer receipts and terminal markers stop recovery instead of silently accepting new identities. After a receipt is durably written, cooperative cancellation is deferred until its matching record replacement completes.


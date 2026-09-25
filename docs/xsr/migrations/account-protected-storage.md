# Protected account persistence (SEC-01)

The Desktop live account store uses `ProtectedLaunchProfilePort`. Schema 1 JSON remains
the in-memory serialization and read-only legacy import format, not the on-disk live format.
The whole serialized roster is protected before creating any temporary file. Public state
continues to exclude credentials. Windows uses current-user DPAPI; macOS uses Keychain and
Linux Secret Service to hold random AES-256 keys, with authenticated AES-GCM envelopes.
No secret appears in process arguments. Missing/locked secure storage fails closed, with
an actionable account error; it never falls back to plaintext or silently resets the store.

Migration reads bounded legacy JSON, validates it, encrypts in memory and atomically replaces
the original. The exact old `.invalid` and GUID-named temporary files owned by this port
are protected in place as well. External legacy import sources remain byte-for-byte unchanged.
Corrupt/unreadable data is retained without producing another plaintext quarantine copy.
Any failed load locks this port against writes for the session. Direct Save on an existing
store first validates Load, so constructing a second port cannot overwrite unreadable data.
The legacy JSON port remains a compatibility/test adapter; production composition must use
the protected port. Native platform tests are distinct from injectable cipher contract tests.

Linux requires `/usr/bin/secret-tool` and an unlocked Secret Service. Native backend failure
is reported as unavailable rather than replaced by a filesystem key. This does not defend
against malware already controlling the user's session or erase historical external backups.

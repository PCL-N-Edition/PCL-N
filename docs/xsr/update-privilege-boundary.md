# Privileged update boundary

The required attacker model includes a malicious unprivileged process running as
the same operating-system user as Nexa. It may keep replacing entries in user-owned
directories. An attacker already holding the updater's root/administrator authority
is outside this isolation boundary.

If isolation cannot be established, automatic replacement must stop and explain
that administrator authorization through a trusted installer is required. Do not
elevate an executable downloaded or staged in a user-writable directory.

## Admission requirements

A privileged update helper must be installed by the system installer in a protected
namespace. Verify the opened helper, installation and staging ancestors, including
owner, ACLs and parent deletion rights. An elevated token alone is not admission.
Fresh staging is created by the helper beneath its protected namespace. Changing
permissions on an existing user-owned tree cannot revoke pre-existing open handles.

Downloads, cached manifests, IPC requests and installation plans are untrusted data.
Inside the protected process, independently verify publisher signature, version and
channel, platform, exact lengths and hashes, destinations and the signed previous
inventory authorizing deletions. Never inherit plugin/tool paths or execute code
from caller-supplied staging. A helper must use handle-relative file operations and
keep verification and mutation bound to the admitted objects.

- Windows: protected ownership and DACLs, rejecting reparse points and user rights
  to write, rename/delete, change ownership or change DACLs, including parent
  FILE_DELETE_CHILD. Retain appropriate sharing restrictions through mutation.
- Linux/macOS: root-owned namespaces without user/group/ACL mutation grants,
  no-follow descriptor-relative access. Name-based rename/unlink after closing a
  verified file is not verification of the object that will be changed.

User-owned settings, accounts, logs and caches stay unprivileged. SafeFilePort is
not a guarantee that a same-user process cannot directly change those files.

## Current implementation status

The production settings UI currently only discovers updates and opens installer,
portable download and release-note URLs. No privileged automatic update helper or
authenticated handoff is wired. Machine-wide packages and release signatures are
prerequisites; they do not complete this boundary. Existing path-based staging and
restart utilities must not be connected to an elevated host before this contract is
implemented and deterministic replacement-race tests pass. SEC-07 remains open.

### Audit enforcement
UpdateStaging.ApplyPlan is now an explicit fail-closed compatibility entry point: it throws NotSupportedException before inspecting or mutating any install/staging path. There is no trusted object-bound privileged helper yet. Verified planning, download and manual installer flows remain usable; calling the legacy path-based apply API is not an authorization to replace files. A future helper must meet the same-account attacker contract before this capability is enabled.


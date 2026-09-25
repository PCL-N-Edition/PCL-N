# XSR-718 Tagged multiplatform distribution

Tags use the dotted product forms in versioning.md (an optional `v` prefix is accepted).
The release workflow derives channel, numeric prefix, and prerelease sequence from the tag;
branch pushes produce CI builds. Changelogs come from commit messages since the preceding
reachable release tag, including the pushed commits. No shell evaluates commit message text.

Each release requires six RIDs: Windows, Linux, macOS, each x64 and arm64. Packaging preserves
the legacy distribution formats: Windows EXE/MSI and ZIP, Linux DEB/RPM/AppImage and tar.gz,
macOS DMG and a portable app archive. Native executable/bundle/desktop icons are included;
Windows installers provide a desktop shortcut choice. Release publication waits for every
matrix target and verifies the expected package set with checksums. No release tag is created
implicitly during implementation.

`eng/release/metadata.py` reads the actual tagged commit and the preceding reachable version
tag. `package.py` packages the complete self-contained publish directory on native runners;
`verify.py` requires all 18 packages before the distribution job can publish. The Windows EXE
has an optional desktop-shortcut checkbox. MSI installs the `DesktopShortcut` feature by
default; `ADDLOCAL=Launcher` excludes it. MSI's numeric version uses the product prefix,
while artifact names and executable informational versions retain the exact channel version.
macOS bundles are ad-hoc signed; Developer ID signing and notarization require release-owner
credentials and are not claimed by this pipeline. Portable archives require no installation.

All installers now use machine scope as defined in [system installation](../system-installation.md).
DMG retains its distribution format but contains a system-domain PKG rather than a
drag-to-Applications copy. Disposable native CI runners install EXE/MSI, DEB or the
DMG's PKG and smoke-run the installed executable; RPM file ownership is inspected.
Publisher signing of the full distribution is defined in [release signatures](../release-signatures.md).

Validation: Python contracts cover tag parsing, rejected malformed/mismatched tags, commit
message preservation without execution, and refusal to release missing or empty packages.
Every runner executes the published launcher with `--validate-shell` before packaging.

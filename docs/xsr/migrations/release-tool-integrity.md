# Release build input integrity

External GitHub Actions are pinned to full commit IDs. Direct executable downloads are
listed in `eng/release/tool-lock.json` with fixed release URLs and SHA-256 digests obtained
from the corresponding upstream GitHub release asset metadata. Changing a tool requires a
reviewed lock change, not accepting a digest from the download response at build time.
Fresh and cached downloads are verified before execution; failed downloads never publish
an executable destination. Inno Setup installs into an isolated runner directory and that
compiler is explicitly selected. AppImageTool receives a separately pinned type2 runtime,
so it cannot implicitly fetch the moving runtime release.

APT repositories, fixed WiX/FPM package versions and their package manager dependencies
remain build trust inputs. This contract does not imply reproducible whole-system builds
or replace release signing and platform notarization.

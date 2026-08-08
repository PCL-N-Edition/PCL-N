# PCL N launcher binary patches (vendored tools)

Scripts used by GitHub Actions on **every launcher release** to build
materially useful, layout-compatible HDiffPatch bundles from recent versions.

Canonical standalone repo (optional mirror): `PCL-N-Patches`.

## GA flow

1. `release-stable_publish.yml` / `release-beta_publish.yml` builds and uploads full assets  
2. Job **`generate-patches`** (workflow call → `generate-launcher-patches.yml`) runs after `publish-assets`  
   - Only releases inside the **14-day rollback window** are considered.
   - At most the **last 3 versions** get a direct patch; clients may chain 3-version hops while retained, otherwise they use a full package.
3. Downloads historical + target assets from this repo’s Releases  
4. For each RID × variant, generates `from → to` scatter `.patch.zip` bundles
   - incompatible single-file/scatter transitions use the full package;
   - a bundle must be below 35% of the full package;
   - all patches for one RID/variant together may consume at most 50% of that full package, with newest predecessors taking priority.
5. Replaces `index.json` + patches on **the same release tag**, then deletes obsolete patch assets from GitHub and R2
6. Optionally mirrors to `PCL-N-Patches` when `PATCHES_REPO_TOKEN` is set  

**CI channel (`ci-latest` from `build-test.yml`) never runs this pipeline.** Rolling CI only ships canonical scatter archives.
## Local

```powershell
cd tools/pcl-n-patches
python scripts/bootstrap_hdiffpatch.py
$env:GH_TOKEN = "..."
python scripts/generate_patches.py `
  --source-repo MuXue1230-owo/PCL-N `
  --target-tag v1.0.0 `
  --out-dir ../../artifacts/patches/v1.0.0
```

See `docs/CLIENT.md` for launcher client discovery.

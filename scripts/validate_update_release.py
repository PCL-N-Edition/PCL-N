#!/usr/bin/env python3
"""Validate blockmaps and referenced CAS objects before channel promote (protocol v2 §16).

Checks:
  * every *.blockmap.json / *.blockmap.v2.json is well-formed
  * every full block path matches block/<hh>/<sha256>
  * every delta path is under delta/v2/
  * referenced objects exist either under --local-root and/or on R2 when --require-remote

Exit non-zero on any failure so the promote job cannot publish a broken release.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path


def _iter_maps(manifest_dir: Path) -> list[Path]:
    found = {
        *manifest_dir.glob("*.blockmap.json"),
        *manifest_dir.glob("*.blockmap.v2.json"),
    }
    return sorted(found)


def _collect_refs(map_path: Path) -> tuple[set[str], set[str], list[str]]:
    errors: list[str] = []
    blocks: set[str] = set()
    deltas: set[str] = set()
    try:
        value = json.loads(map_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        return set(), set(), [f"{map_path.name}: cannot parse ({exc})"]

    layout = value.get("layout")
    algorithm = value.get("algorithm")
    compression = value.get("compression")
    if value.get("formatVersion") not in {1, 2}:
        errors.append(f"{map_path.name}: invalid formatVersion")
    if not isinstance(layout, str) or not layout.startswith("pcln-blockmap"):
        errors.append(f"{map_path.name}: invalid layout")
    if algorithm not in {"pcln-fastcdc-v1", "pcln-fastcdc-v2"}:
        errors.append(f"{map_path.name}: invalid algorithm")
    if compression not in {"gzip", "zstd", None}:
        errors.append(f"{map_path.name}: invalid compression {compression!r}")
    if value.get("blockBasePath") != "/v1/updates/block":
        errors.append(f"{map_path.name}: unexpected blockBasePath")

    files = value.get("targetFiles") or []
    if not isinstance(files, list) or not files:
        errors.append(f"{map_path.name}: empty targetFiles")
        return blocks, deltas, errors

    for file_entry in files:
        if not isinstance(file_entry, dict):
            errors.append(f"{map_path.name}: non-object file entry")
            continue
        for chunk in file_entry.get("chunks") or []:
            if not isinstance(chunk, dict):
                errors.append(f"{map_path.name}: non-object chunk")
                continue
            full = chunk.get("full") if isinstance(chunk.get("full"), dict) else None
            sha = str((full or {}).get("sha256") or chunk.get("sha256") or "").lower()
            path = str((full or {}).get("path") or chunk.get("path") or "")
            expected = f"block/{sha[:2]}/{sha}" if len(sha) == 64 else ""
            if len(sha) != 64 or any(ch not in "0123456789abcdef" for ch in sha):
                errors.append(f"{map_path.name}: bad block sha {sha!r}")
                continue
            if path != expected:
                errors.append(f"{map_path.name}: path {path!r} != {expected}")
                continue
            blocks.add(path)
            for delta in chunk.get("deltas") or []:
                if not isinstance(delta, dict):
                    continue
                dpath = str(delta.get("path") or "").replace("\\", "/")
                if not dpath.startswith("delta/v2/") or ".." in dpath:
                    errors.append(f"{map_path.name}: bad delta path {dpath!r}")
                    continue
                deltas.add(dpath)
    return blocks, deltas, errors


def validate(
    manifest_dir: Path,
    *,
    local_root: Path | None,
    require_remote: bool,
) -> int:
    maps = _iter_maps(manifest_dir)
    if not maps:
        print(f"error: no blockmaps in {manifest_dir}", file=sys.stderr)
        return 1

    all_blocks: set[str] = set()
    all_deltas: set[str] = set()
    errors: list[str] = []
    for path in maps:
        blocks, deltas, map_errors = _collect_refs(path)
        errors.extend(map_errors)
        all_blocks |= blocks
        all_deltas |= deltas

    print(
        f"validate: maps={len(maps)} block_refs={len(all_blocks)} "
        f"delta_refs={len(all_deltas)}"
    )

    if local_root is not None:
        for key in sorted(all_blocks | all_deltas):
            candidate = local_root / key
            if not candidate.is_file():
                # Local tree is optional for matrix residual; warn only unless no remote.
                if not require_remote:
                    errors.append(f"missing local CAS object: {key}")

    if require_remote:
        # Import lazily so offline structural checks need no credentials.
        scripts = Path(__file__).resolve().parent
        if str(scripts) not in sys.path:
            sys.path.insert(0, str(scripts))
        from upload_r2_cas import resolve_client  # noqa: WPS433

        client = resolve_client()
        block_metadata = client.list_object_metadata("block/")
        remote: set[str] = set(block_metadata)
        remote |= client.list_keys("delta/")
        print(f"validate: remote inventory keys={len(remote)}")
        missing_blocks = sorted(key for key in all_blocks if key not in remote)
        missing_deltas = sorted(key for key in all_deltas if key not in remote)
        for key in missing_blocks:
            # Allow local fallback residual upload path: object may only be local
            # if central catch-up has not run; still fail — promote requires remote.
            errors.append(f"missing remote block: {key}")
        for key in missing_deltas:
            errors.append(f"missing remote delta: {key}")

        # Existence alone is insufficient for immutable raw-SHA CAS keys: an
        # older gzip representation may already own a key while a newer map was
        # generated from zstd bytes. Verify the stored magic and compressed size
        # before making the release discoverable.
        from reconcile_update_blockmaps import reconcile  # noqa: WPS433

        try:
            reconcile(
                manifest_dir,
                client=client,
                apply=False,
                require_remote=True,
                concurrency=8,
                remote_metadata=block_metadata,
            )
        except (ValueError, RuntimeError) as exc:
            errors.extend(f"remote CAS metadata: {line}" for line in str(exc).splitlines())

    if errors:
        for line in errors[:80]:
            print(f"error: {line}", file=sys.stderr)
        if len(errors) > 80:
            print(f"error: … and {len(errors) - 80} more", file=sys.stderr)
        print(f"validate FAILED: {len(errors)} issue(s)", file=sys.stderr)
        return 1

    print("validate OK")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest-dir", type=Path, required=True)
    parser.add_argument(
        "--local-root",
        type=Path,
        help="Optional block-dist root for local CAS existence checks",
    )
    parser.add_argument(
        "--require-remote",
        action="store_true",
        help="List R2 and require every referenced block/delta key to exist",
    )
    args = parser.parse_args(argv)
    return validate(
        args.manifest_dir,
        local_root=args.local_root,
        require_remote=args.require_remote,
    )


if __name__ == "__main__":
    raise SystemExit(main())

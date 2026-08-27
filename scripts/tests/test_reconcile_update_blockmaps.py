import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).parents[1] / "reconcile_update_blockmaps.py"
SPEC = importlib.util.spec_from_file_location("reconcile_update_blockmaps", SCRIPT)
assert SPEC and SPEC.loader
reconcile_module = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = reconcile_module
SPEC.loader.exec_module(reconcile_module)


class FakeClient:
    def __init__(self, objects: dict[str, bytes]) -> None:
        self.objects = objects

    def inspect_object(self, key: str, prefix_length: int = 4):
        payload = self.objects.get(key)
        return None if payload is None else (payload[:prefix_length], len(payload))

    def list_object_metadata(self, prefix: str):
        class Metadata:
            def __init__(self, size: int) -> None:
                self.size = size
                self.content_type = "application/octet-stream"

        return {
            key: Metadata(len(payload))
            for key, payload in self.objects.items()
            if key.startswith(prefix)
        }


def write_manifest(path: Path, key: str) -> None:
    full = {
        "sha256": key.rsplit("/", 1)[-1],
        "size": 123,
        "compressedSize": 9,
        "path": key,
        "compression": "zstd",
    }
    path.write_text(
        json.dumps(
            {
                "formatVersion": 2,
                "layout": "pcln-blockmap-v2",
                "algorithm": "pcln-fastcdc-v2",
                "compression": "zstd",
                "blockBasePath": "/v1/updates/block",
                "targetFiles": [
                    {
                        "path": "host/a",
                        "chunks": [
                            {"full": dict(full), "deltas": []},
                            {"full": dict(full), "deltas": []},
                        ],
                    }
                ],
                "stats": {"referencedCompressedBytes": 18},
            }
        )
        + "\n",
        encoding="utf-8",
    )


class ReconcileUpdateBlockmapsTests(unittest.TestCase):
    def test_apply_rewrites_codec_size_and_reference_stats(self):
        sha = "a" * 64
        key = f"block/{sha[:2]}/{sha}"
        remote = b"\x1f\x8b" + b"canonical-gzip"
        with tempfile.TemporaryDirectory() as temporary:
            manifest_dir = Path(temporary)
            manifest = manifest_dir / "target.blockmap.v2.json"
            write_manifest(manifest, key)

            _, mismatches, missing = reconcile_module.reconcile(
                manifest_dir,
                client=FakeClient({key: remote}),
                apply=True,
                require_remote=True,
                concurrency=2,
            )

            self.assertEqual(2, mismatches)
            self.assertEqual(0, missing)
            updated = json.loads(manifest.read_text(encoding="utf-8"))
            full_blocks = [chunk["full"] for chunk in updated["targetFiles"][0]["chunks"]]
            self.assertTrue(all(block["compression"] == "gzip" for block in full_blocks))
            self.assertTrue(all(block["compressedSize"] == len(remote) for block in full_blocks))
            self.assertEqual(len(remote) * 2, updated["stats"]["referencedCompressedBytes"])

    def test_check_rejects_manifest_that_disagrees_with_remote(self):
        sha = "b" * 64
        key = f"block/{sha[:2]}/{sha}"
        with tempfile.TemporaryDirectory() as temporary:
            manifest_dir = Path(temporary)
            write_manifest(manifest_dir / "target.blockmap.v2.json", key)

            with self.assertRaisesRegex(ValueError, "CAS metadata mismatch"):
                reconcile_module.reconcile(
                    manifest_dir,
                    client=FakeClient({key: b"\x1f\x8bremote"}),
                    apply=False,
                    require_remote=True,
                    concurrency=1,
                )


if __name__ == "__main__":
    unittest.main()

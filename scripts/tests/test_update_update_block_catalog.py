import importlib.util
import sys
import unittest
from datetime import datetime, timezone
from pathlib import Path


SCRIPT = Path(__file__).parents[1] / "update_update_block_catalog.py"
SPEC = importlib.util.spec_from_file_location("update_update_block_catalog", SCRIPT)
assert SPEC and SPEC.loader
catalog_module = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = catalog_module
SPEC.loader.exec_module(catalog_module)


class UpdateBlockCatalogTests(unittest.TestCase):
    def test_expired_release_deletes_only_unshared_blocks_and_its_objects(self):
        shared = "a" * 64
        expired_only = "b" * 64
        current_only = "c" * 64
        previous = {
            "formatVersion": 1,
            "releases": [
                {
                    "tag": "v1.4.3-beta",
                    "channel": "beta",
                    "publishedAt": "2026-07-01T00:00:00Z",
                    "blocks": [shared, expired_only],
                    "objects": ["releases/v1.4.3-beta/old.blockmap.json"],
                }
            ],
        }

        result, deletions = catalog_module.update_catalog(
            previous,
            tag="v1.4.4-beta",
            channel="beta",
            published_at="2026-08-09T00:00:00Z",
            blocks={shared, current_only},
            objects=["releases/v1.4.4-beta/new.blockmap.json"],
            now=datetime(2026, 8, 9, tzinfo=timezone.utc),
        )

        self.assertEqual(["v1.4.4-beta"], [entry["tag"] for entry in result["releases"]])
        self.assertIn(f"block/{expired_only[:2]}/{expired_only}", deletions)
        self.assertNotIn(f"block/{shared[:2]}/{shared}", deletions)
        self.assertIn("releases/v1.4.3-beta/old.blockmap.json", deletions)

    def test_ci_latest_replacement_deletes_only_previous_unique_blocks(self):
        shared = "d" * 64
        previous_only = "e" * 64
        current_only = "f" * 64
        previous = {
            "formatVersion": 1,
            "releases": [
                {
                    "tag": "ci-latest",
                    "channel": "ci",
                    "publishedAt": "2026-08-09T00:00:00Z",
                    "blocks": [shared, previous_only],
                    "objects": [
                        "releases/ci-latest/ci-channel.json",
                        "releases/ci-latest/old.blockmap.json",
                    ],
                }
            ],
        }

        result, deletions = catalog_module.update_catalog(
            previous,
            tag="ci-latest",
            channel="ci",
            published_at="2026-08-09T01:00:00Z",
            blocks={shared, current_only},
            objects=[
                "releases/ci-latest/ci-channel.json",
                "releases/ci-latest/new.blockmap.json",
            ],
            now=datetime(2026, 8, 9, 1, tzinfo=timezone.utc),
        )

        self.assertEqual(["ci-latest"], [entry["tag"] for entry in result["releases"]])
        self.assertIn(f"block/{previous_only[:2]}/{previous_only}", deletions)
        self.assertNotIn(f"block/{shared[:2]}/{shared}", deletions)
        self.assertIn("releases/ci-latest/old.blockmap.json", deletions)
        self.assertNotIn("releases/ci-latest/ci-channel.json", deletions)

    def test_expired_release_deletes_unshared_deltas(self):
        shared = "a" * 64
        expired_only = "b" * 64
        shared_delta = "delta/v2/aa/" + ("1" * 64) + "/" + ("2" * 64) + ".vcdiff"
        expired_delta = "delta/v2/bb/" + ("3" * 64) + "/" + ("4" * 64) + ".vcdiff"
        previous = {
            "formatVersion": 1,
            "releases": [
                {
                    "tag": "v1.4.7-beta",
                    "channel": "beta",
                    "publishedAt": "2026-07-01T00:00:00Z",
                    "blocks": [shared, expired_only],
                    "deltas": [shared_delta, expired_delta],
                    "objects": ["releases/v1.4.7-beta/old.blockmap.v2.json"],
                }
            ],
        }

        result, deletions = catalog_module.update_catalog(
            previous,
            tag="v1.4.8-beta",
            channel="beta",
            published_at="2026-08-09T00:00:00Z",
            blocks={shared},
            deltas={shared_delta},
            objects=["releases/v1.4.8-beta/new.blockmap.v2.json"],
            now=datetime(2026, 8, 9, tzinfo=timezone.utc),
        )

        self.assertEqual(["v1.4.8-beta"], [entry["tag"] for entry in result["releases"]])
        self.assertIn(f"block/{expired_only[:2]}/{expired_only}", deletions)
        self.assertIn(expired_delta, deletions)
        self.assertNotIn(shared_delta, deletions)

    def test_inventory_gc_sweeps_unreferenced_remote_keys(self):
        live = "a" * 64
        dead = "b" * 64
        catalog = {
            "formatVersion": 1,
            "releases": [
                {
                    "tag": "v1.4.8-beta",
                    "channel": "beta",
                    "publishedAt": "2026-08-09T00:00:00Z",
                    "blocks": [live],
                    "deltas": [f"delta/v2/aa/{live}/{live}.vcdiff"],
                    "objects": [],
                }
            ],
        }
        remote = {
            f"block/{live[:2]}/{live}",
            f"block/{dead[:2]}/{dead}",
            f"delta/v2/aa/{live}/{live}.vcdiff",
            f"delta/v2/bb/{dead}/{dead}.vcdiff",
            "block/catalog.json",
        }
        deletions = catalog_module.inventory_gc_deletions(catalog, remote)
        self.assertIn(f"block/{dead[:2]}/{dead}", deletions)
        self.assertIn(f"delta/v2/bb/{dead}/{dead}.vcdiff", deletions)
        self.assertNotIn(f"block/{live[:2]}/{live}", deletions)
        self.assertNotIn("block/catalog.json", deletions)


if __name__ == "__main__":
    unittest.main()

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


if __name__ == "__main__":
    unittest.main()

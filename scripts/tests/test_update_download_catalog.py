import importlib.util
import sys
import unittest
from datetime import datetime, timezone
from pathlib import Path


SCRIPT = Path(__file__).parents[1] / "update_download_catalog.py"
SPEC = importlib.util.spec_from_file_location("update_download_catalog", SCRIPT)
assert SPEC and SPEC.loader
catalog_module = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = catalog_module
SPEC.loader.exec_module(catalog_module)


class UpdateDownloadCatalogTests(unittest.TestCase):
    def test_beta_publish_keeps_current_stable_outside_rollback_window(self):
        previous = {
            "schemaVersion": 1,
            "versions": [
                {
                    "id": "v1.3.20-release",
                    "label": "1.3.20",
                    "tag": "v1.3.20-release",
                    "channel": "release",
                    "packaging": "v2",
                    "supportsPluginChoice": False,
                    "publishedAt": "2026-07-01T00:00:00Z",
                },
                {
                    "id": "v1.4.2-beta",
                    "label": "1.4.2 Beta",
                    "tag": "v1.4.2-beta",
                    "channel": "beta",
                    "packaging": "v2",
                    "supportsPluginChoice": False,
                    "publishedAt": "2026-08-04T00:00:00Z",
                },
            ],
        }

        result = catalog_module.update_catalog(
            previous,
            tag="v1.4.3-beta",
            channel="beta",
            published_at=datetime(2026, 8, 9, tzinfo=timezone.utc),
        )

        self.assertEqual(
            {"v1.3.20-release", "v1.4.2-beta", "v1.4.3-beta"},
            {item["tag"] for item in result["versions"]},
        )

    def test_expired_non_current_version_is_removed(self):
        previous = {
            "schemaVersion": 1,
            "versions": [
                {
                    "id": "v1.3.19-release",
                    "label": "1.3.19",
                    "tag": "v1.3.19-release",
                    "channel": "release",
                    "packaging": "legacy",
                    "supportsPluginChoice": False,
                    "publishedAt": "2026-06-01T00:00:00Z",
                },
                {
                    "id": "v1.3.20-release",
                    "label": "1.3.20",
                    "tag": "v1.3.20-release",
                    "channel": "release",
                    "packaging": "v2",
                    "supportsPluginChoice": False,
                    "publishedAt": "2026-07-01T00:00:00Z",
                },
            ],
        }

        result = catalog_module.update_catalog(
            previous,
            tag="v1.4.3-beta",
            channel="beta",
            published_at=datetime(2026, 8, 9, tzinfo=timezone.utc),
        )

        self.assertEqual(
            {"v1.3.20-release", "v1.4.3-beta"},
            {item["tag"] for item in result["versions"]},
        )


if __name__ == "__main__":
    unittest.main()

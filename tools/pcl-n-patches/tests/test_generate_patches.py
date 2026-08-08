import importlib.util
import io
import json
import stat
import sys
import tarfile
import tempfile
import unittest
import zipfile
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest import mock


SCRIPT = Path(__file__).parents[1] / "scripts" / "generate_patches.py"
SPEC = importlib.util.spec_from_file_location("generate_patches", SCRIPT)
assert SPEC and SPEC.loader
generate_patches = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = generate_patches
SPEC.loader.exec_module(generate_patches)


class GenerateScatterPatchTests(unittest.TestCase):
    def test_layout_contract_distinguishes_legacy_and_scatter_packages(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            legacy = root / "legacy"
            scatter = root / "scatter"
            legacy.mkdir()
            scatter.mkdir()
            (legacy / "PCL-N-Edition.exe").write_bytes(b"single-file")
            (scatter / "PCL-N-Edition.exe").write_bytes(b"bootstrap")
            (scatter / "pcln-layout").write_text(
                "pcln-scatter-v2-expanded\n", encoding="utf-8"
            )

            self.assertEqual(
                "legacy-single-file",
                generate_patches.package_layout(legacy, "win-x64"),
            )
            self.assertEqual(
                "pcln-scatter-v2-expanded",
                generate_patches.package_layout(scatter, "win-x64"),
            )

    def test_patch_requires_material_savings(self):
        self.assertTrue(generate_patches.patch_is_worth_shipping(79, 100, 0.80))
        self.assertFalse(generate_patches.patch_is_worth_shipping(80, 100, 0.80))
        self.assertFalse(generate_patches.patch_is_worth_shipping(101, 100, 1.0))

    def test_patch_storage_budget_prioritizes_newest_versions(self):
        candidates = [
            {"fromTag": "v1.0.0", "size": 20},
            {"fromTag": "v1.1.0", "size": 30},
            {"fromTag": "v1.2.0", "size": 40},
        ]

        kept, dropped, used = generate_patches.select_patch_metadata_with_budget(
            candidates,
            full_size=100,
            max_total_ratio=0.50,
        )

        self.assertEqual(["v1.2.0"], [item["fromTag"] for item in kept])
        self.assertEqual({"v1.0.0", "v1.1.0"}, {item["fromTag"] for item in dropped})
        self.assertEqual(40, used)

    def test_patch_history_is_limited_to_two_week_rollback_window(self):
        anchor = datetime(2026, 8, 9, tzinfo=timezone.utc)
        recent = generate_patches.ReleaseInfo(
            "v1.2.0", "1.2.0", True, anchor - timedelta(days=13), {}
        )
        expired = generate_patches.ReleaseInfo(
            "v1.1.0", "1.1.0", True, anchor - timedelta(days=15), {}
        )

        selected = generate_patches.filter_release_history_by_age(
            [expired, recent],
            anchor=anchor,
            max_age_days=14,
        )

        self.assertEqual([recent], selected)

    def test_patch_history_never_crosses_1_4_3_baseline(self):
        anchor = datetime(2026, 8, 9, tzinfo=timezone.utc)
        old = generate_patches.ReleaseInfo("v1.4.2", "1.4.2", True, anchor, {})
        baseline = generate_patches.ReleaseInfo("v1.4.3-beta", "1.4.3-beta", True, anchor, {})
        newer = generate_patches.ReleaseInfo("v1.4.4", "1.4.4", True, anchor, {})

        selected = generate_patches.filter_release_history_by_minimum(
            [old, baseline, newer],
            "1.4.3",
        )

        self.assertEqual([baseline, newer], selected)

    def test_default_patch_window_keeps_three_recent_versions(self):
        anchor = datetime(2026, 8, 9, tzinfo=timezone.utc)
        history = [
            generate_patches.ReleaseInfo(
                f"v1.0.{index}", f"1.0.{index}", True, anchor, {}
            )
            for index in range(6)
        ]

        selected, strategy = generate_patches.select_from_versions(history)

        self.assertEqual(["v1.0.3", "v1.0.4", "v1.0.5"], [item.tag for item in selected])
        self.assertEqual(3, strategy["maxDirectFromVersions"])
        self.assertEqual(3, strategy["hopInterval"])

    def test_extract_tree_normalizes_macos_app_root(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            archive = root / "mac.tar.gz"
            content = b"launcher"
            marker = b"pcln-scatter-v2-expanded\n"
            with tarfile.open(archive, "w:gz") as tar:
                for name, value, mode in [
                    ("PCL N.app/Contents/MacOS/PCL-N-Edition", content, 0o755),
                    ("PCL N.app/Contents/MacOS/pcln-layout", marker, 0o644),
                ]:
                    info = tarfile.TarInfo(name)
                    info.size = len(value)
                    info.mode = mode
                    tar.addfile(info, io.BytesIO(value))

            target = root / "target"
            generate_patches.extract_tree(archive, target)

            self.assertEqual(
                content,
                (target / "Contents" / "MacOS" / "PCL-N-Edition").read_bytes(),
            )
            self.assertFalse((target / "PCL N.app").exists())
            self.assertEqual(
                Path("Contents/MacOS/PCL-N-Edition"),
                generate_patches.binary_relative_path("osx-arm64"),
            )

    def test_bundle_uses_full_blob_when_delta_is_not_smaller(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            old_root = root / "old"
            new_root = root / "new"
            old_root.mkdir()
            new_root.mkdir()
            (old_root / "same.bin").write_bytes(b"same")
            (new_root / "same.bin").write_bytes(b"same")
            (old_root / "replace.bin").write_bytes(b"old")
            replacement = b"new-content"
            (new_root / "replace.bin").write_bytes(replacement)
            executable = new_root / "added-tool"
            executable.write_bytes(b"tool")
            executable.chmod(0o755)
            old_inventory = generate_patches.inventory_tree(old_root)
            new_inventory = generate_patches.inventory_tree(new_root)
            bundle = root / "bundle.patch.zip"

            def oversized_delta(_tool, _old, _new, patch):
                patch.write_bytes(b"x" * 1024)

            with mock.patch.object(generate_patches, "run_hdiff", oversized_delta):
                manifest, _ = generate_patches.build_scatter_patch_zip(
                    Path("unused"),
                    old_root,
                    new_root,
                    old_inventory,
                    new_inventory,
                    bundle,
                    "1.0.0",
                    "1.0.1",
                )

            operations = {item["path"]: item for item in manifest["ops"]}
            self.assertEqual("replace", operations["replace.bin"]["op"])
            self.assertEqual("add", operations["added-tool"]["op"])
            target_files = {item["path"]: item for item in manifest["targetFiles"]}
            self.assertEqual(
                stat.S_IMODE(executable.stat().st_mode),
                target_files["added-tool"]["unixMode"],
            )
            with zipfile.ZipFile(bundle) as archive:
                files_json = json.loads(archive.read("files.json"))
                member = operations["replace.bin"]["blob"]
                self.assertEqual(replacement, archive.read(member))
            self.assertEqual(manifest, files_json)


if __name__ == "__main__":
    unittest.main()

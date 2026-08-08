import importlib.util
import io
import json
import stat
import sys
import tarfile
import tempfile
import unittest
import zipfile
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

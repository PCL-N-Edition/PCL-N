import gzip
import importlib.util
import json
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path


SCRIPT = Path(__file__).parents[1] / "generate_update_blockmap.py"
SPEC = importlib.util.spec_from_file_location("generate_update_blockmap", SCRIPT)
assert SPEC and SPEC.loader
blockmap = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = blockmap
SPEC.loader.exec_module(blockmap)


class GenerateUpdateBlockmapTests(unittest.TestCase):
    def test_block_paths_are_content_addressed_and_shared(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "source"
            output = root / "output"
            source.mkdir()
            payload = (bytes(range(256)) * 8192) + b"tail"
            first = source / "first.bin"
            second = source / "second.bin"
            first.write_bytes(payload)
            second.write_bytes(payload)

            first_sha, _, first_chunks, created_first, _ = blockmap.chunk_file(first, output)
            second_sha, _, second_chunks, created_second, _ = blockmap.chunk_file(second, output)

            self.assertEqual(first_sha, second_sha)
            self.assertEqual(first_chunks, second_chunks)
            self.assertGreater(created_first, 0)
            self.assertEqual(0, created_second)
            reconstructed = b"".join(
                gzip.decompress((output / chunk["path"]).read_bytes())
                for chunk in first_chunks
            )
            self.assertEqual(payload, reconstructed)
            for chunk in first_chunks:
                self.assertEqual(
                    f"block/{chunk['sha256'][:2]}/{chunk['sha256']}",
                    chunk["path"],
                )

    def test_archive_manifest_reconstructs_scatter_tree(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            archive = root / "PCL_N_Beta_win-x64_NoRuntime.zip"
            files = {
                "PCL-N-Edition.exe": b"launcher",
                "pcln-layout": b"pcln-scatter-v2-expanded\n",
                "host/PCL-N-Host.exe": bytes(range(251)) * 5000,
            }
            with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as package:
                for name, content in files.items():
                    package.writestr(name, content)

            output = root / "block-output"
            manifest_paths = blockmap.build_blockmap(
                archive,
                output,
                target_tag="v1.4.3-beta",
                target_version="1.4.3-beta",
                runtime_id="win-x64",
                runtime_variant="NoRuntime",
                configuration="Beta",
            )
            self.assertEqual(2, len(manifest_paths))
            by_suffix = {path.name: path for path in manifest_paths}
            self.assertIn("PCL_N_Beta_win-x64_NoRuntime.blockmap.json", by_suffix)
            self.assertIn("PCL_N_Beta_win-x64_NoRuntime.blockmap.v2.json", by_suffix)

            for manifest_path in manifest_paths:
                manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
                if manifest_path.name.endswith(".blockmap.v2.json"):
                    self.assertEqual("pcln-blockmap-v2", manifest["layout"])
                    self.assertEqual("pcln-fastcdc-v2", manifest["algorithm"])
                    self.assertEqual(2, manifest["formatVersion"])
                    self.assertEqual(
                        {"min": 131072, "avg": 524288, "max": 1048576},
                        manifest["chunking"],
                    )
                else:
                    self.assertEqual("pcln-blockmap-v1", manifest["layout"])
                    self.assertEqual("pcln-fastcdc-v1", manifest["algorithm"])
                self.assertEqual("/v1/updates/block", manifest["blockBasePath"])
                self.assertEqual(set(files), {entry["path"] for entry in manifest["targetFiles"]})
                for entry in manifest["targetFiles"]:
                    reconstructed = b"".join(
                        gzip.decompress((output / chunk["path"]).read_bytes())
                        for chunk in entry["chunks"]
                    )
                    self.assertEqual(files[entry["path"]], reconstructed)

    def test_file_manifest_reconstructs_single_portable_executable(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "PCL_N_Beta_win-x64_NoRuntime_Portable.exe"
            payload = (bytes(range(251)) * 12000) + b"portable-tail"
            source.write_bytes(payload)
            output = root / "block-output"

            manifest_paths = blockmap.build_file_blockmap(
                source,
                output,
                target_asset_name=source.name,
                entry_name="PCL-N-Edition.exe",
                target_tag="v1.4.4-beta",
                target_version="1.4.4-beta",
                runtime_id="win-x64",
                runtime_variant="NoRuntime",
                configuration="Beta",
            )
            self.assertEqual(2, len(manifest_paths))

            for manifest_path in manifest_paths:
                manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
                if manifest_path.name.endswith(".blockmap.v2.json"):
                    self.assertEqual("pcln-blockmap-file-v2", manifest["layout"])
                    self.assertEqual("pcln-fastcdc-v2", manifest["algorithm"])
                else:
                    self.assertEqual("pcln-blockmap-file-v1", manifest["layout"])
                self.assertEqual(source.name, manifest["targetAssetName"])
                self.assertEqual(["PCL-N-Edition.exe"], [entry["path"] for entry in manifest["targetFiles"]])
                reconstructed = b"".join(
                    gzip.decompress((output / chunk["path"]).read_bytes())
                    for chunk in manifest["targetFiles"][0]["chunks"]
                )
                self.assertEqual(payload, reconstructed)


    def test_profile_auto_stops_v1_from_1_4_8(self):
        self.assertTrue(blockmap.should_emit_v1_blockmap("1.4.7"))
        self.assertTrue(blockmap.should_emit_v1_blockmap("1.4.7-beta"))
        self.assertTrue(blockmap.should_emit_v1_blockmap("v1.4.7-beta"))
        self.assertFalse(blockmap.should_emit_v1_blockmap("1.4.8"))
        self.assertFalse(blockmap.should_emit_v1_blockmap("1.4.8-beta"))
        self.assertFalse(blockmap.should_emit_v1_blockmap("v1.4.8-beta"))
        self.assertFalse(blockmap.should_emit_v1_blockmap("ci-latest", "CI"))
        self.assertEqual("both", blockmap.default_profile_arg("1.4.7-beta", "Beta"))
        self.assertEqual("v2", blockmap.default_profile_arg("1.4.8-beta", "Beta"))
        self.assertEqual("v2", blockmap.default_profile_arg("ci-latest", "CI"))

    def test_build_file_blockmap_v1_4_8_emits_v2_only(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "PCL_N_Beta_win-x64_SelfContained_Portable.exe"
            source.write_bytes((bytes(range(200)) * 4000) + b"tail")
            output = root / "block-output"
            paths = blockmap.build_file_blockmap(
                source,
                output,
                target_asset_name=source.name,
                entry_name="PCL-N-Edition.exe",
                target_tag="v1.4.8-beta",
                target_version="1.4.8-beta",
                runtime_id="win-x64",
                runtime_variant="SelfContained",
                configuration="Beta",
                profiles=blockmap._resolve_profiles(
                    blockmap.default_profile_arg("1.4.8-beta", "Beta")
                ),
            )
            names = {path.name for path in paths}
            self.assertIn("PCL_N_Beta_win-x64_SelfContained_Portable.blockmap.v2.json", names)
            self.assertNotIn("PCL_N_Beta_win-x64_SelfContained_Portable.blockmap.json", names)


if __name__ == "__main__":
    unittest.main()

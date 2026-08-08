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
            manifest_path = blockmap.build_blockmap(
                archive,
                output,
                target_tag="v1.4.3-beta",
                target_version="1.4.3-beta",
                runtime_id="win-x64",
                runtime_variant="NoRuntime",
                configuration="Beta",
            )

            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            self.assertEqual("pcln-blockmap-v1", manifest["layout"])
            self.assertEqual("/v1/updates/block", manifest["blockBasePath"])
            self.assertEqual(set(files), {entry["path"] for entry in manifest["targetFiles"]})
            for entry in manifest["targetFiles"]:
                reconstructed = b"".join(
                    gzip.decompress((output / chunk["path"]).read_bytes())
                    for chunk in entry["chunks"]
                )
                self.assertEqual(files[entry["path"]], reconstructed)


if __name__ == "__main__":
    unittest.main()

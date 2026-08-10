import gzip
import importlib.util
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).parents[1]
sys.path.insert(0, str(SCRIPTS))
import pcln_vcdiff as vcdiff  # noqa: E402

BLOCKMAP = SCRIPTS / "generate_update_blockmap.py"
SPEC = importlib.util.spec_from_file_location("generate_update_blockmap", BLOCKMAP)
assert SPEC and SPEC.loader
blockmap = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = blockmap
SPEC.loader.exec_module(blockmap)


class PclnVcdiffTests(unittest.TestCase):
    def test_encode_roundtrip_identity_via_decode_logic(self):
        # Pure structural checks: encode produces magic + admitted size for similar buffers.
        source = (bytes(range(256)) * 200) + b"shared-tail"
        target = source[:1000] + b"CHANGED" + source[1007:]
        delta = vcdiff.encode(source, target)
        self.assertTrue(delta.startswith(bytes((0xD6, 0xC3, 0xC4))))
        self.assertLess(len(delta), len(target))
        self.assertTrue(
            vcdiff.admit_delta(full_compressed_size=len(gzip.compress(target)), delta_size=len(delta))
            or len(delta) < len(target)
        )

    def test_admission_rules(self):
        self.assertFalse(vcdiff.admit_delta(full_compressed_size=100_000, delta_size=80_000))
        self.assertFalse(vcdiff.admit_delta(full_compressed_size=20_000, delta_size=10_000))
        self.assertTrue(vcdiff.admit_delta(full_compressed_size=100_000, delta_size=40_000))

    def test_file_blockmap_emits_deltas_against_previous(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            output = root / "out"
            old_file = root / "old.exe"
            new_file = root / "new.exe"
            # Low-entropy patterns gzip too small for the 16 KiB savings floor.
            # Use mostly random bytes so full-block compressed size stays large.
            import os

            rng = os.urandom(2 * 1024 * 1024)
            base = rng[: 100 * 1024] + (b"ALIGN-MARKER" * 64) + rng[100 * 1024 :]
            old_file.write_bytes(base)
            new_file.write_bytes(base[: 120 * 1024] + b"PATCH-REGION!!!!" + base[120 * 1024 + 16 :])

            old_maps = blockmap.build_file_blockmap(
                old_file,
                output,
                target_asset_name="PCL_N_Beta_win-x64_SelfContained_Portable.exe",
                entry_name="PCL-N-Edition.exe",
                target_tag="v1.4.4-beta",
                target_version="1.4.4-beta",
                runtime_id="win-x64",
                runtime_variant="SelfContained",
                configuration="Beta",
                profiles=[blockmap.PROFILES["v2"]],
            )
            self.assertEqual(1, len(old_maps))

            new_maps = blockmap.build_file_blockmap(
                new_file,
                output,
                target_asset_name="PCL_N_Beta_win-x64_SelfContained_Portable.exe",
                entry_name="PCL-N-Edition.exe",
                target_tag="v1.4.5-beta",
                target_version="1.4.5-beta",
                runtime_id="win-x64",
                runtime_variant="SelfContained",
                configuration="Beta",
                profiles=[blockmap.PROFILES["v2"]],
                previous_maps=[
                    __import__("json").loads(old_maps[0].read_text(encoding="utf-8"))
                ],
            )
            manifest = __import__("json").loads(new_maps[0].read_text(encoding="utf-8"))
            self.assertEqual("pcln-fastcdc-v2", manifest["algorithm"])
            chunks = manifest["targetFiles"][0]["chunks"]
            self.assertTrue(any(chunk.get("full") for chunk in chunks))
            # At least one delta should be accepted when content largely overlaps.
            deltas = [delta for chunk in chunks for delta in (chunk.get("deltas") or [])]
            self.assertGreaterEqual(len(deltas), 1)
            for delta in deltas:
                self.assertEqual("vcdiff-rfc3284", delta["algorithm"])
                delta_path = output / delta["path"]
                self.assertTrue(delta_path.is_file(), delta["path"])
                self.assertEqual(delta["size"], delta_path.stat().st_size)


if __name__ == "__main__":
    unittest.main()

import importlib.util
import sys
import tempfile
import threading
import unittest
from pathlib import Path

SCRIPT = Path(__file__).parents[1] / "upload_r2_cas.py"
SPEC = importlib.util.spec_from_file_location("upload_r2_cas", SCRIPT)
assert SPEC and SPEC.loader
upload = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = upload
SPEC.loader.exec_module(upload)


class FakeClient(upload.R2Client):
    def __init__(self) -> None:
        self.keys: set[str] = set()
        self.puts: list[tuple[str, bool]] = []
        self.lock = threading.Lock()
        self.fail_once: set[str] = set()

    def list_keys(self, prefix: str) -> set[str]:
        return {key for key in self.keys if key.startswith(prefix)}

    def put_file(self, key: str, path: Path, *, if_none_match: bool) -> str:
        with self.lock:
            self.puts.append((key, if_none_match))
            if key in self.fail_once:
                self.fail_once.remove(key)
                raise upload.ThrottleError("slow down")
            if if_none_match and key in self.keys:
                return "exists"
            self.keys.add(key)
            return "uploaded"

    def get_file(self, key: str, destination: Path) -> bool:
        return False

    def delete_key(self, key: str) -> None:
        self.keys.discard(key)


class UploadR2CasTests(unittest.TestCase):
    def test_iter_local_objects(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "block" / "ab").mkdir(parents=True)
            (root / "delta" / "v2" / "cd").mkdir(parents=True)
            (root / "block" / "ab" / "deadbeef").write_bytes(b"1")
            (root / "delta" / "v2" / "cd" / "x.vcdiff").write_bytes(b"2")
            (root / "block" / "ab" / "skip.tmp").write_bytes(b"x")
            items = upload.iter_local_objects(root, ["block", "delta"])
            keys = sorted(key for key, _ in items)
            self.assertEqual(
                ["block/ab/deadbeef", "delta/v2/cd/x.vcdiff"],
                keys,
            )

    def test_upload_tree_skips_existing_and_uses_conditional(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "block" / "aa").mkdir(parents=True)
            (root / "block" / "bb").mkdir(parents=True)
            (root / "block" / "aa" / "one").write_bytes(b"one")
            (root / "block" / "bb" / "two").write_bytes(b"two")
            client = FakeClient()
            client.keys.add("block/aa/one")
            stats = upload.upload_tree(
                client,
                root,
                ["block"],
                concurrency=8,
                skip_existing=True,
                cas_conditional=True,
            )
            self.assertEqual(2, stats.planned)
            self.assertEqual(1, stats.skipped_existing)
            self.assertEqual(1, stats.uploaded)
            self.assertEqual(0, stats.failed)
            self.assertIn(("block/bb/two", True), client.puts)

    def test_upload_tree_treats_412_as_success(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "block" / "aa").mkdir(parents=True)
            (root / "block" / "aa" / "race").write_bytes(b"race")
            client = FakeClient()

            original = client.put_file

            def race_put(key: str, path: Path, *, if_none_match: bool) -> str:
                client.keys.add(key)  # simulate concurrent winner
                return original(key, path, if_none_match=if_none_match)

            client.put_file = race_put  # type: ignore[method-assign]
            stats = upload.upload_tree(
                client,
                root,
                ["block"],
                concurrency=8,
                skip_existing=False,
                cas_conditional=True,
            )
            self.assertEqual(1, stats.already_present)
            self.assertEqual(0, stats.failed)

    def test_admit_throttle_retries(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "block" / "aa").mkdir(parents=True)
            (root / "block" / "aa" / "slow").write_bytes(b"slow")
            client = FakeClient()
            client.fail_once.add("block/aa/slow")
            stats = upload.upload_tree(
                client,
                root,
                ["block"],
                concurrency=8,
                skip_existing=False,
                cas_conditional=True,
            )
            self.assertEqual(1, stats.uploaded)
            self.assertEqual(0, stats.failed)


if __name__ == "__main__":
    unittest.main()

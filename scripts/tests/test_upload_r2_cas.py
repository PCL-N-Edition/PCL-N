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

    def put_file(
        self,
        key: str,
        path: Path,
        *,
        if_none_match: bool,
        content_type: str | None = None,
    ) -> str:
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

            def race_put(
                key: str,
                path: Path,
                *,
                if_none_match: bool,
                content_type: str | None = None,
            ) -> str:
                client.keys.add(key)  # simulate concurrent winner
                return original(
                    key, path, if_none_match=if_none_match, content_type=content_type
                )

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

    def test_put_files_batches_flat_directory(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            (directory / "a.blockmap.json").write_text("{}", encoding="utf-8")
            (directory / "a.blockmap.v2.json").write_text("{}", encoding="utf-8")
            (directory / "skip.me").write_text("x", encoding="utf-8")
            client = FakeClient()
            stats = upload.put_files(
                client,
                directory,
                "releases/v1.4.7-beta",
                concurrency=8,
                name_filter=lambda name: name.endswith(".json"),
            )
            self.assertEqual(2, stats.planned)
            self.assertEqual(2, stats.uploaded)
            keys = sorted(key for key, _ in client.puts)
            self.assertEqual(
                [
                    "releases/v1.4.7-beta/a.blockmap.json",
                    "releases/v1.4.7-beta/a.blockmap.v2.json",
                ],
                keys,
            )

    def test_guess_content_type(self):
        self.assertEqual(
            "application/json; charset=utf-8",
            upload.guess_content_type(Path("x.blockmap.json")),
        )
        self.assertEqual(
            "application/octet-stream",
            upload.guess_content_type(Path("x.vcdiff")),
        )

    def test_cloudflare_api_object_url_encodes_segments(self):
        client = upload.CloudflareApiR2Client("acct", "token", "pcln-releases")
        url = client._object_url("block/ab/deadbeef")
        self.assertIn("/accounts/acct/r2/buckets/pcln-releases/objects/", url)
        self.assertIn("block/ab/deadbeef", url)
        self.assertTrue(url.startswith(upload.CF_API))

    def test_cloudflare_api_put_treats_412_as_exists(self):
        client = upload.CloudflareApiR2Client("acct", "token", "pcln-releases")

        def fake_request(method, url, *, data=None, headers=None, timeout=120.0):
            self.assertEqual("PUT", method)
            self.assertEqual("*", (headers or {}).get("If-None-Match"))
            return 412, b"", {}

        client._request = fake_request  # type: ignore[method-assign]
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "blob"
            path.write_bytes(b"data")
            self.assertEqual(
                "exists",
                client.put_file("block/aa/one", path, if_none_match=True),
            )

    def test_resolve_client_prefers_cloudflare_token(self):
        import os

        previous = {
            key: os.environ.get(key)
            for key in (
                "CLOUDFLARE_API_TOKEN",
                "CLOUDFLARE_ACCOUNT_ID",
                "R2_ACCESS_KEY_ID",
                "R2_SECRET_ACCESS_KEY",
            )
        }
        try:
            os.environ["CLOUDFLARE_API_TOKEN"] = "cf-token"
            os.environ["CLOUDFLARE_ACCOUNT_ID"] = "account-id"
            os.environ.pop("R2_ACCESS_KEY_ID", None)
            os.environ.pop("R2_SECRET_ACCESS_KEY", None)
            client = upload.resolve_client()
            self.assertIsInstance(client, upload.CloudflareApiR2Client)
        finally:
            for key, value in previous.items():
                if value is None:
                    os.environ.pop(key, None)
                else:
                    os.environ[key] = value


if __name__ == "__main__":
    unittest.main()

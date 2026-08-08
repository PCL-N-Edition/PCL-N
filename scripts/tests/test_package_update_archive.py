import importlib.util
import tarfile
import tempfile
import unittest
import zipfile
from pathlib import Path


SCRIPT = Path(__file__).parents[1] / "package_update_archive.py"
SPEC = importlib.util.spec_from_file_location("package_update_archive", SCRIPT)
assert SPEC and SPEC.loader
package_update_archive = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(package_update_archive)


class PackageUpdateArchiveTests(unittest.TestCase):
    def _scatter(self, root: Path, platform: str) -> Path:
        artifact = root / "scatter"
        payload = artifact
        if platform == "macos":
            payload = artifact / "PCL N.app" / "Contents" / "MacOS"
        (payload / "host").mkdir(parents=True)
        (payload / "native").mkdir()
        suffix = ".exe" if platform == "windows" else ""
        (payload / f"PCL-N-Edition{suffix}").write_bytes(b"launcher")
        (payload / "host" / f"PCL-N-Host{suffix}").write_bytes(b"host")
        (payload / "native" / f"native{suffix}").write_bytes(b"native")
        (payload / "pcln-layout").write_text("pcln-scatter-v2-expanded\n", encoding="utf-8")
        return artifact

    def test_windows_archive_contains_only_scatter_tree(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            artifact = self._scatter(root, "windows")
            output = root / "update.zip"
            package_update_archive.create_archive(artifact, output, "windows")
            with zipfile.ZipFile(output) as archive:
                self.assertIn("PCL-N-Edition.exe", archive.namelist())
                self.assertIn("host/PCL-N-Host.exe", archive.namelist())

    def test_rejects_portable_binary_inside_scatter(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            artifact = self._scatter(root, "windows")
            (artifact / "PCL-N-Portable.exe").write_bytes(b"embedded payload")
            with self.assertRaisesRegex(ValueError, "portable single-file"):
                package_update_archive.create_archive(artifact, root / "update.zip", "windows")

    def test_macos_archive_keeps_app_bundle_root(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            artifact = self._scatter(root, "macos")
            output = root / "update.tar.gz"
            package_update_archive.create_archive(artifact, output, "macos")
            with tarfile.open(output, "r:gz") as archive:
                self.assertIn("PCL N.app/Contents/MacOS/PCL-N-Edition", archive.getnames())


if __name__ == "__main__":
    unittest.main()

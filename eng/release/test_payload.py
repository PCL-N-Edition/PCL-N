import sys
import tarfile
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import patch

import package


class RuntimePayloadTests(unittest.TestCase):
    def seed(self, source, platform):
        suffix = ".exe" if platform == "win" else ""
        keep = ["Nexa.Desktop" + suffix, "Nexa.Jvm.Host" + suffix,
                "runtime/SkiaSharp.dll", "runtime/libSkiaSharp.so", "runtime/libHarfBuzzSharp.dylib",
                "Nexa.Desktop.runtimeconfig.json", "Nexa.Desktop.deps.json",
                "resources/zh-CN/Nexa.resources.dll", "assets/test.png", "licenses/library.xml"]
        excluded = ["Nexa.Desktop.pdb", "Nexa.Jvm.Host.PDB", "runtime/native.dbg",
                    "runtime/native.DEBUG", "Nexa.Desktop.dSYM/Contents/Resources/DWARF/Nexa.Desktop",
                    "Nexa.Desktop.ilk", "Nexa.Desktop.ipdb", "Nexa.Desktop.iobj",
                    "Nexa.Desktop.Tests.exe", "Nexa.Desktop.Tests.dll", "Nexa.Services.Tests",
                    "Nexa.Desktop.Tests.runtimeconfig.json", "testhost.exe", "testhost.dll",
                    "Microsoft.TestPlatform.CoreUtilities.dll", "Microsoft.VisualStudio.TestPlatform.ObjectModel.dll",
                    "TestResults/run/results.trx", "coverage/index.html", "coverage.cobertura.xml"]
        for relative in keep + excluded:
            path = source / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"fixture")
        (source / ("Nexa.Desktop" + suffix)).chmod(0o755)
        return set(keep), excluded

    def test_every_target_builder_gets_only_staged_runtime_files(self):
        for platform in ("win", "linux", "osx"):
            for arch in ("x64", "arm64"):
                with self.subTest(platform=platform, arch=arch), tempfile.TemporaryDirectory() as temporary:
                    root = Path(temporary)
                    source = root / "publish"
                    keep, excluded = self.seed(source, platform)
                    rid = f"{platform}-{arch}"
                    output = root / "artifacts"
                    builder = {"win": "windows", "linux": "linux", "osx": "macos"}[platform]
                    with patch.object(sys, "argv", ["package.py", "--payload", str(source),
                                                   "--output", str(output), "--rid", rid,
                                                   "--version", "2.0.0.alpha.5"]), patch.object(package, builder) as build:
                        package.main()
                    staged = build.call_args.args[0]
                    self.assertNotEqual(source, staged)
                    self.assertEqual(keep, {path.relative_to(staged).as_posix() for path in staged.rglob("*") if path.is_file()})
                    self.assertTrue(all((source / name).is_file() for name in excluded))
                    self.assertEqual((source / next(iter(keep))).read_bytes(), b"fixture")
                    self.assertEqual((source / ("Nexa.Desktop.exe" if platform == "win" else "Nexa.Desktop")).stat().st_mode,
                                     (staged / ("Nexa.Desktop.exe" if platform == "win" else "Nexa.Desktop")).stat().st_mode)

    def test_portable_archives_exclude_development_files_recursively(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            keep, _ = self.seed(root / "publish", "linux")
            staged = root / "runtime"
            package.copy_runtime_payload(root / "publish", staged)
            package.archive(staged, root / "portable.zip", "Nexa")
            with zipfile.ZipFile(root / "portable.zip") as archive:
                self.assertEqual(keep, {entry.filename for entry in archive.infolist() if not entry.is_dir()})
            package.archive(staged, root / "portable.tar.gz", "Nexa")
            with tarfile.open(root / "portable.tar.gz") as archive:
                self.assertEqual({"Nexa/" + path for path in keep}, {entry.name for entry in archive if entry.isfile()})

    def test_gate_rejects_files_reintroduced_after_staging(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.seed(root / "publish", "win")
            staged = root / "runtime"
            package.copy_runtime_payload(root / "publish", staged)
            (staged / "leaked.PDB").write_bytes(b"debug")
            with self.assertRaisesRegex(ValueError, "leaked.PDB"):
                package.validate_runtime_contents(staged)
            with self.assertRaises(ValueError):
                package.archive(staged, root / "portable.zip", "Nexa")
            self.assertFalse((root / "portable.zip").exists())


if __name__ == "__main__":
    unittest.main()

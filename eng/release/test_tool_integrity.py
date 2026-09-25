import hashlib
import io
import json
import os
from pathlib import Path
import re
import tempfile
import unittest
from unittest.mock import patch

import fetch_tool
import package


class ToolIntegrityTests(unittest.TestCase):
    def test_runtime_bundle_excludes_native_debug_sidecars(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "publish"
            source.mkdir()
            (source / "Nexa.Desktop").write_bytes(b"runtime")
            (source / "libSkia.dylib").write_bytes(b"library")
            symbols = source / "Nexa.Desktop.dSYM"
            symbols.mkdir()
            (symbols / "symbols").write_bytes(b"debug")
            (source / "Nexa.Desktop.pdb").write_bytes(b"debug")
            package.copy_runtime_payload(source, root / "bundle")
            self.assertEqual({"Nexa.Desktop", "libSkia.dylib"}, {path.name for path in (root / "bundle").iterdir()})
            self.assertTrue(symbols.exists())

    def test_corrupt_download_and_cache_are_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            lock = root / "lock.json"
            lock.write_text(json.dumps({"tool": {"url": "https://example.invalid/tool", "sha256": hashlib.sha256(b"good").hexdigest()}}))
            destination = root / "tool"
            with patch.object(fetch_tool, "LOCK", lock):
                with self.assertRaises(ValueError):
                    fetch_tool.fetch("tool", destination, lambda *args, **kwargs: io.BytesIO(b"bad"))
                self.assertFalse(destination.exists())
                self.assertEqual([], list(root.glob(".tool-*")))
                fetch_tool.fetch("tool", destination, lambda *args, **kwargs: io.BytesIO(b"good"))
                self.assertEqual(b"good", destination.read_bytes())
                destination.write_bytes(b"bad")
                with self.assertRaises(ValueError):
                    fetch_tool.fetch("tool", destination)

    def test_workflows_pin_actions_and_disable_ambient_tool_selection(self):
        root = Path(__file__).resolve().parents[2]
        count = 0
        for workflow in (root / ".github/workflows").glob("*.yml"):
            for reference in re.findall(r"uses:\s*(\S+)", workflow.read_text()):
                if not reference.startswith("./"):
                    self.assertRegex(reference, r"^[\w-]+/[\w-]+@[0-9a-f]{40}$")
                    count += 1
        self.assertGreater(count, 0)
        with patch.dict(os.environ, {}, clear=True):
            with self.assertRaises(KeyError):
                package.required_tool("NEXA_ISCC")
        text = (root / "eng/release/package.py").read_text(encoding="utf-8")
        self.assertIn('"--runtime-file", required_tool("APPIMAGE_RUNTIME")', text)
        self.assertNotIn('shutil.which("ISCC")', text)
        lock = json.loads(fetch_tool.LOCK.read_text())
        for arch in ("x86_64", "aarch64"):
            self.assertIn("runtime-" + arch, lock)
            self.assertIn("appimagetool-" + arch, lock)
        self.assertEqual(5, len(lock))
        for entry in lock.values():
            self.assertRegex(entry["sha256"], r"^[0-9a-f]{64}$")
            self.assertNotIn("/continuous/", entry["url"])

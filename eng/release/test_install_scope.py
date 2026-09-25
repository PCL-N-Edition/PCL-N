import plistlib
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
import xml.etree.ElementTree as ET

import package


class InstallScopeTests(unittest.TestCase):
    def test_payload_requires_nonempty_host_on_every_platform(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for platform, suffix in (("win", ".exe"), ("linux", ""), ("osx", "")):
                (root / ("Nexa.Desktop" + suffix)).write_bytes(b"desktop")
                host = root / ("Nexa.Jvm.Host" + suffix)
                with self.assertRaises(ValueError):
                    package.validate_payload(root, platform)
                host.touch()
                with self.assertRaises(ValueError):
                    package.validate_payload(root, platform)
                host.write_bytes(b"host")
                package.validate_payload(root, platform)
                host.unlink()

    def test_windows_installers_require_machine_scope(self):
        root = Path(__file__).parent
        setup = (root / "windows.iss").read_text()
        self.assertIn("PrivilegesRequired=admin\n", setup)
        self.assertIn("PrivilegesRequiredOverridesAllowed=\n", setup)
        self.assertIn("DefaultDirName={autopf}\\NexaCL", setup)
        self.assertIn("{commondesktop}\\NexaCL", setup)
        self.assertIn("{commonprograms}\\NexaCL", setup)
        self.assertNotIn("{localappdata}", setup)
        ns = {"w": "http://wixtoolset.org/schemas/v4/wxs"}
        installer = ET.parse(root / "windows.wxs").find("w:Package", ns)
        self.assertEqual("perMachine", installer.attrib["Scope"])
        self.assertIsNotNone(installer.find("w:StandardDirectory[@Id='ProgramFiles6432Folder']", ns))
        self.assertTrue(all(value.attrib["Root"] == "HKLM" for value in installer.findall(".//w:RegistryValue", ns)))

    def test_macos_dmg_contains_nonrelocatable_system_installer(self):
        distribution = ET.parse(Path(__file__).with_name("macos-distribution.xml"))
        self.assertEqual({"enable_anywhere": "false", "enable_currentUserHome": "false",
                          "enable_localSystem": "true"}, distribution.find("domains").attrib)
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            payload, work, output = [root / name for name in ("payload", "work", "output")]
            for folder in (payload, work, output):
                folder.mkdir()
            (payload / "Nexa.Desktop").write_bytes(b"runtime")
            with patch.object(package, "run") as run, patch.object(package, "archive"):
                package.macos(payload, output, work, "Nexa-test", "2.0.0.alpha.1", "2.0.0", "arm64")
            commands = [[str(arg) for arg in call.args] for call in run.call_args_list]
            build = next(command for command in commands if command[0] == "pkgbuild")
            self.assertEqual("/Applications", build[build.index("--install-location") + 1])
            self.assertEqual("recommended", build[build.index("--ownership") + 1])
            with (work / "components.plist").open("rb") as stream:
                component = plistlib.load(stream)[0]
            self.assertFalse(component["BundleIsRelocatable"])
            self.assertTrue(component["BundleHasStrictIdentifier"])
            self.assertTrue((work / "macos-root/Nexa.app/Contents/MacOS/Nexa.Desktop").exists())
            self.assertTrue(any(command[0] == "productbuild" and command[-1].endswith("NexaCL.pkg") for command in commands))

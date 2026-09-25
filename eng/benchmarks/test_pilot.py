import json
import os
from pathlib import Path
import tempfile
import unittest

import pilot


class ArtifactBoundaryTests(unittest.TestCase):
    def fixture(self):
        pack = json.loads(pilot.CATALOG.read_text())["packs"][0]
        return dict(schema=1, source="controlled-benchmark", trainingEligible=False,
                    packSha256=pack["sha256"], minecraft=pack["minecraft"], loader=pack["loader"],
                    javaMajor=21, heapMiB=2048, classpathCount=100, logicalProcessors=2,
                    sampleWindows=1, exitCode=0, contiguous=True, terminalSample=True, forcedStop=False,
                    privatePath="must not be exported")

    def test_exports_only_normalized_measurements(self):
        source = pilot.HEADER + "\n0,30000,30000,0,60,1024,2048,3072,10,40,1,0\n"
        result, data = pilot.normalize(self.fixture(), source)
        self.assertNotIn("privatePath", result)
        self.assertFalse(result["trainingEligible"])
        self.assertEqual("software", result["renderer"])
        self.assertIn("1024,2048,3072", data)

    def test_rejects_eligibility_pack_mismatch_and_nonfinite_values(self):
        source = pilot.HEADER + "\n0,30000,30000,0,60,1024,2048,3072,10,40,1,0\n"
        for change in [{"trainingEligible": True}, {"packSha256": "0" * 64}, {"javaMajor": "21"}]:
            with self.assertRaises(ValueError):
                pilot.normalize(self.fixture() | change, source)
        for bad in ["NaN", "inf", "=HYPERLINK(1)", "1e99"]:
            with self.assertRaises(ValueError):
                pilot.normalize(self.fixture(), source.replace("1024", bad))

    def test_context_keeps_mod_identity_and_marks_unknowns(self):
        mod = dict(id="sodium", version="0.6.0", format="fabric.mod.json", enabled=True,
                   dependenciesComplete=False, dependencies={"minecraft": "~1.21.1"}, filename="private.jar")
        context = dict(schema=1, available=True, loaderVersion="0.19.3", complete=False, unknownFiles=2,
                       components={"Fabric": "0.19.3"}, mods=[mod], directory="private")
        result = pilot.normalize_context(context)
        self.assertFalse(result["complete"])
        self.assertEqual(2, result["unknownFiles"])
        self.assertEqual({"minecraft": "~1.21.1"}, result["mods"][0]["dependencies"])
        self.assertNotIn("filename", result["mods"][0])
        self.assertNotIn("directory", result)
        self.assertEqual({"schema": 1, "available": False}, pilot.normalize_context({"schema": 1, "available": False}))
        for change in [{"id": "../private"}, {"version": "contains secret"}, {"dependencies": {"a": "https://private"}}]:
            with self.assertRaises(ValueError):
                pilot.normalize_context(context | {"mods": [mod | change]})
        with self.assertRaises(ValueError):
            pilot.normalize_context(context | {"mods": [mod] * 4097})
        dependencies = {f"mod_{index}": ">=1" for index in range(64)}
        result = pilot.normalize_context(context | {"mods": [mod | {"dependencies": dependencies}]})
        self.assertEqual(dependencies, result["mods"][0]["dependencies"])
        with self.assertRaises(ValueError):
            pilot.normalize_context(context | {"mods": [mod | {"dependencies": dependencies | {"extra": "1"}}]})

    def test_rejects_inconsistent_windows_and_summary(self):
        row = ["0", "30000", "30000", "0", "60", "1024", "2048", "3072", "10", "40", "1", "0"]
        for index, value in [(0, "0.5"), (1, "-1"), (2, "30001"), (3, "-1"), (4, "0"),
                             (5, "-1"), (6, "512"), (8, "-2"), (9, "40.5"), (10, "2"), (11, "1")]:
            invalid = row.copy()
            invalid[index] = value
            with self.subTest(index=index), self.assertRaises(ValueError):
                pilot.normalize(self.fixture(), pilot.HEADER + "\n" + ",".join(invalid))
        source = pilot.HEADER + "\n" + ",".join(row)
        for change in [{"sampleWindows": 2}, {"terminalSample": False}, {"contiguous": False}]:
            with self.assertRaises(ValueError):
                pilot.normalize(self.fixture() | change, source)
        with self.assertRaises(ValueError):
            pilot.normalize(self.fixture() | {"sampleWindows": 2}, source + "\n" + ",".join(row))

    def test_preserves_missing_observations_without_fabricating_zero(self):
        source = pilot.HEADER + "\n0,1,1,0,0,-1,-1,-1,-1,-1,1,0\n"
        _, normalized = pilot.normalize(self.fixture(), source)
        self.assertIn("-1,-1,-1,-1,-1", normalized)

    @unittest.skipUnless(hasattr(os, "O_NOFOLLOW"), "Production collector runs on Linux")
    def test_collector_rejects_symlinks_and_oversized_files(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary)
            (path / "outside").write_text("private")
            (path / "run.json").symlink_to(path / "outside")
            fd = os.open(path, os.O_RDONLY | os.O_DIRECTORY)
            try:
                with self.assertRaises(OSError):
                    pilot.read_regular(fd, "run.json")
                (path / "run.json").unlink()
                (path / "run.json").write_bytes(b" " * 65537)
                with self.assertRaises(ValueError):
                    pilot.read_regular(fd, "run.json")
            finally:
                os.close(fd)


if __name__ == "__main__":
    unittest.main()

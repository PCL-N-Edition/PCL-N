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

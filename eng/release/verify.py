"""Reject incomplete release sets before anything is attached to a GitHub release."""
import argparse
import hashlib
from pathlib import Path

FORMATS = {"win": ("setup.exe", "msi", "portable.zip"),
           "linux": ("deb", "rpm", "AppImage", "portable.tar.gz"),
           "osx": ("dmg", "portable.tar.gz")}


def expected_names(version):
    return {f"Nexa-{version}-{platform}-{arch}.{extension}"
            for platform, extensions in FORMATS.items() for arch in ("x64", "arm64") for extension in extensions}


def verify(directory, version):
    expected = expected_names(version)
    actual = {path.name for path in directory.iterdir() if path.is_file() and path.name != "SHA256SUMS"}
    if actual != expected:
        raise ValueError(f"Package set mismatch: missing={sorted(expected - actual)}, unexpected={sorted(actual - expected)}")
    checksums = []
    for name in sorted(expected):
        path = directory / name
        if path.stat().st_size == 0:
            raise ValueError(f"Empty package: {name}")
        with path.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        checksums.append(f"{digest}  {name}\n")
    (directory / "SHA256SUMS").write_text("".join(checksums), encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    parser.add_argument("version")
    args = parser.parse_args()
    verify(args.directory, args.version)

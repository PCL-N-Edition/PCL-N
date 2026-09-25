"""Download locked build tools; only a verified file becomes executable input."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import tempfile
import urllib.request

LOCK = Path(__file__).with_name("tool-lock.json")
MAX_BYTES = 128 * 1024 * 1024


def verify(path, digest):
    with open(path, "rb") as stream:
        actual = hashlib.file_digest(stream, "sha256").hexdigest()
    if actual != digest:
        raise ValueError(f"Build tool checksum mismatch: {path}")


def fetch(name, destination, opener=urllib.request.urlopen):
    entry = json.loads(LOCK.read_text(encoding="utf-8"))[name]
    destination = Path(destination)
    destination.parent.mkdir(parents=True, exist_ok=True)
    if destination.exists():
        verify(destination, entry["sha256"])
        return
    descriptor, temporary = tempfile.mkstemp(prefix=".tool-", dir=destination.parent)
    try:
        with os.fdopen(descriptor, "wb") as output, opener(entry["url"], timeout=120) as response:
            received = 0
            while chunk := response.read(min(1024 * 1024, MAX_BYTES - received + 1)):
                received += len(chunk)
                if received > MAX_BYTES:
                    raise ValueError("Build tool download exceeds budget")
                output.write(chunk)
        verify(temporary, entry["sha256"])
        os.replace(temporary, destination)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("name")
    parser.add_argument("destination", type=Path)
    args = parser.parse_args()
    fetch(args.name, args.destination)

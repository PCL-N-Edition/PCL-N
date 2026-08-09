#!/usr/bin/env python3
"""Verify architecture and GUI subsystem of Windows native launcher helpers."""

from __future__ import annotations

import argparse
import struct
from pathlib import Path


MACHINES = {"x64": 0x8664, "arm64": 0xAA64}
WINDOWS_GUI_SUBSYSTEM = 2


def inspect(path: Path) -> tuple[int, int]:
    data = path.read_bytes()
    if len(data) < 0x40 or data[:2] != b"MZ":
        raise ValueError(f"not a PE executable: {path}")
    pe_offset = struct.unpack_from("<I", data, 0x3C)[0]
    if pe_offset + 24 + 70 > len(data) or data[pe_offset : pe_offset + 4] != b"PE\0\0":
        raise ValueError(f"invalid PE header: {path}")
    machine = struct.unpack_from("<H", data, pe_offset + 4)[0]
    optional_header = pe_offset + 24
    subsystem = struct.unpack_from("<H", data, optional_header + 68)[0]
    return machine, subsystem


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--architecture", required=True, choices=tuple(MACHINES))
    parser.add_argument("paths", nargs="+", type=Path)
    args = parser.parse_args()
    expected_machine = MACHINES[args.architecture]
    for path in args.paths:
        machine, subsystem = inspect(path)
        if machine != expected_machine:
            raise ValueError(
                f"{path}: machine 0x{machine:04X}, expected {args.architecture} "
                f"(0x{expected_machine:04X})"
            )
        if subsystem != WINDOWS_GUI_SUBSYSTEM:
            raise ValueError(
                f"{path}: subsystem {subsystem}, expected Windows GUI ({WINDOWS_GUI_SUBSYSTEM})"
            )
        print(f"{path}: machine=0x{machine:04X}, subsystem=Windows GUI")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

"""Pinned pilot input and bounded, no-follow collection. Never publishes training data."""
import csv
import hashlib
import io
import json
import math
import os
from pathlib import Path
import stat
import sys
import urllib.parse
import urllib.request

CATALOG = Path(__file__).with_name("packs.json")
HEADER = "sequence,elapsed_ms,window_ms,epoch,samples,working_mean_mib,working_peak_mib,private_mean_mib,cpu_mean_percent,threads_peak,ended,exit_code"


class CdnOnly(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        parsed = urllib.parse.urlparse(newurl)
        if parsed.scheme != "https" or parsed.hostname != "cdn.modrinth.com":
            raise ValueError("Pack redirect left approved CDN")
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def download(target):
    pack = json.loads(CATALOG.read_text())["packs"][0]
    parsed = urllib.parse.urlparse(pack["url"])
    if parsed.scheme != "https" or parsed.hostname != "cdn.modrinth.com":
        raise ValueError("Unapproved pack URL")
    request = urllib.request.Request(pack["url"], headers={"User-Agent": "NexaCL-Benchmark/0.1"})
    with urllib.request.build_opener(CdnOnly()).open(request, timeout=30) as response:
        data = response.read(pack["bytes"] + 1)
    if len(data) != pack["bytes"] or any(hashlib.new(kind, data).hexdigest() != pack[kind] for kind in ("sha256", "sha512")):
        raise ValueError("Pack size or digest mismatch")
    with open(target, "xb") as output:
        output.write(data)


def read_regular(directory, name):
    fd = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=directory)
    with os.fdopen(fd, "rb") as stream:
        info = os.fstat(stream.fileno())
        if not stat.S_ISREG(info.st_mode) or info.st_size > 65536:
            raise ValueError("Not a bounded regular artifact")
        data = stream.read(65537)
        if len(data) > 65536:
            raise ValueError("Artifact grew beyond limit")
        return data.decode("utf-8")


def normalize(document, csv_text):
    pack = json.loads(CATALOG.read_text())["packs"][0]
    if document.get("schema") != 1 or document.get("source") != "controlled-benchmark" or document.get("trainingEligible") is not False:
        raise ValueError("Unexpected artifact schema or eligibility")
    if document.get("packSha256", "").lower() != pack["sha256"] or document.get("minecraft") != pack["minecraft"] or document.get("loader") != pack["loader"]:
        raise ValueError("Artifact does not match the pinned pack")
    integers = ["javaMajor", "heapMiB", "classpathCount", "logicalProcessors", "sampleWindows", "exitCode"]
    flags = ["contiguous", "terminalSample", "forcedStop"]
    if any(type(document.get(key)) is not int or not -2147483648 <= document[key] <= 2147483647 for key in integers):
        raise ValueError("Invalid numeric metadata")
    if any(type(document.get(key)) is not bool for key in flags):
        raise ValueError("Invalid completion metadata")
    rows = list(csv.reader(io.StringIO(csv_text)))
    if not rows or rows[0] != HEADER.split(",") or len(rows) > 128:
        raise ValueError("Invalid sample columns or row count")
    normalized = [rows[0]]
    for row in rows[1:]:
        if len(row) != 12:
            raise ValueError("Invalid sample row")
        values = []
        for index, cell in enumerate(row):
            if index == 11 and cell == "":
                values.append("")
                continue
            number = float(cell)
            if not math.isfinite(number) or not -2147483648 <= number <= 1e12:
                raise ValueError("Invalid sample value")
            values.append(format(number, ".17g"))
        normalized.append(values)
    result = {key: document[key] for key in integers + flags}
    result.update(schema=1, source="controlled-benchmark", packId=pack["id"], packSha256=pack["sha256"],
                  minecraft=pack["minecraft"], loader=pack["loader"], scenario="unverified-client",
                  runner="github-hosted-ubuntu-24.04-container", renderer="software",
                  memoryLimitMiB=6144, cpuLimit=2, trainingEligible=False)
    output = io.StringIO(newline="")
    csv.writer(output).writerows(normalized)
    return result, output.getvalue()


def collect(source, destination):
    # The container has already stopped. Neither artifact paths nor arbitrary strings supplied by
    # mod code are forwarded to the artifact uploader. No recursive directory upload is permitted.
    directory = os.open(source, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        result, samples = normalize(json.loads(read_regular(directory, "run.json")), read_regular(directory, "samples.csv"))
    finally:
        os.close(directory)
    output = Path(destination)
    output.mkdir(exist_ok=False)
    (output / "run.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    (output / "samples.csv").write_text(samples, encoding="utf-8")


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "download":
        download(sys.argv[2])
    elif len(sys.argv) == 4 and sys.argv[1] == "collect":
        collect(sys.argv[2], sys.argv[3])
    else:
        raise SystemExit("Use download <new-file> or collect <run-directory> <new-output-directory>")

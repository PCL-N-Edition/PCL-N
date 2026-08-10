#!/usr/bin/env python3
"""High-throughput Cloudflare R2 CAS uploader (protocol v2 §15–19).

Prefers the S3-compatible API (boto3) with:

  * one ListObjectsV2 inventory for skip decisions (no per-object HEAD)
  * PutObject with If-None-Match: * for immutable CAS creates
  * adaptive concurrency (default 24, range 8–48)
  * 412 PreconditionFailed treated as success (another job won the race)

Falls back to ``npx wrangler r2 object …`` when S3 credentials are absent so
existing CI secrets keep working until R2 API tokens are provisioned.

Environment (S3 mode):

  CLOUDFLARE_ACCOUNT_ID or R2_ACCOUNT_ID
  R2_ACCESS_KEY_ID      (or AWS_ACCESS_KEY_ID)
  R2_SECRET_ACCESS_KEY  (or AWS_SECRET_ACCESS_KEY)
  R2_BUCKET             (default: pcln-releases)
  R2_ENDPOINT           (optional override)
"""

from __future__ import annotations

import argparse
import os
import subprocess
import sys
import threading
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable, Iterable


DEFAULT_BUCKET = "pcln-releases"
MIN_CONCURRENCY = 8
DEFAULT_CONCURRENCY = 24
MAX_CONCURRENCY = 48


@dataclass
class UploadStats:
    planned: int = 0
    skipped_existing: int = 0
    uploaded: int = 0
    already_present: int = 0  # 412
    failed: int = 0
    lock: threading.Lock = field(default_factory=threading.Lock)

    def inc(self, name: str, amount: int = 1) -> None:
        with self.lock:
            setattr(self, name, getattr(self, name) + amount)


class AdaptiveLimiter:
    def __init__(self, initial: int = DEFAULT_CONCURRENCY) -> None:
        self._value = max(MIN_CONCURRENCY, min(MAX_CONCURRENCY, initial))
        self._lock = threading.Lock()
        self._sem = threading.Semaphore(self._value)

    @property
    def concurrency(self) -> int:
        with self._lock:
            return self._value

    def acquire(self) -> None:
        self._sem.acquire()

    def release_success(self, latency_s: float) -> None:
        self._sem.release()
        if latency_s < 0.75:
            self._nudge(+1)

    def release_throttle(self) -> None:
        self._sem.release()
        self._nudge(-2)

    def release_error(self) -> None:
        self._sem.release()

    def _nudge(self, delta: int) -> None:
        with self._lock:
            new_value = max(MIN_CONCURRENCY, min(MAX_CONCURRENCY, self._value + delta))
            if new_value == self._value:
                return
            # Grow/shrink semaphore permits.
            while new_value > self._value:
                self._sem.release()
                self._value += 1
            while new_value < self._value:
                # Best-effort shrink: acquire without blocking if possible.
                if self._sem.acquire(blocking=False):
                    self._value -= 1
                else:
                    break


class R2Client:
    """Thin wrapper over boto3 or wrangler."""

    def list_keys(self, prefix: str) -> set[str]:
        raise NotImplementedError

    def put_file(self, key: str, path: Path, *, if_none_match: bool) -> str:
        """Return 'uploaded' | 'exists'."""
        raise NotImplementedError

    def get_file(self, key: str, destination: Path) -> bool:
        raise NotImplementedError

    def delete_key(self, key: str) -> None:
        raise NotImplementedError


class BotoR2Client(R2Client):
    def __init__(self, bucket: str, endpoint: str, access_key: str, secret_key: str) -> None:
        import boto3
        from botocore.config import Config

        self.bucket = bucket
        self._client = boto3.client(
            "s3",
            endpoint_url=endpoint,
            aws_access_key_id=access_key,
            aws_secret_access_key=secret_key,
            region_name="auto",
            config=Config(
                signature_version="s3v4",
                retries={"max_attempts": 8, "mode": "adaptive"},
                max_pool_connections=MAX_CONCURRENCY + 8,
            ),
        )

    def list_keys(self, prefix: str) -> set[str]:
        keys: set[str] = set()
        token: str | None = None
        while True:
            kwargs: dict = {"Bucket": self.bucket, "Prefix": prefix, "MaxKeys": 1000}
            if token:
                kwargs["ContinuationToken"] = token
            response = self._client.list_objects_v2(**kwargs)
            for item in response.get("Contents") or []:
                key = item.get("Key")
                if isinstance(key, str) and key:
                    keys.add(key)
            if not response.get("IsTruncated"):
                break
            token = response.get("NextContinuationToken")
            if not token:
                break
        return keys

    def put_file(self, key: str, path: Path, *, if_none_match: bool) -> str:
        extra: dict = {}
        if if_none_match:
            extra["IfNoneMatch"] = "*"
        try:
            with path.open("rb") as handle:
                self._client.put_object(
                    Bucket=self.bucket,
                    Key=key,
                    Body=handle,
                    **extra,
                )
            return "uploaded"
        except Exception as exc:  # noqa: BLE001 — map botocore errors by name
            name = type(exc).__name__
            code = getattr(exc, "response", {}).get("Error", {}).get("Code", "")
            status = getattr(exc, "response", {}).get("ResponseMetadata", {}).get("HTTPStatusCode")
            if status == 412 or code in {"PreconditionFailed", "412"} or "PreconditionFailed" in name:
                return "exists"
            if status == 429 or code in {"SlowDown", "TooManyRequests"}:
                raise ThrottleError(str(exc)) from exc
            raise

    def get_file(self, key: str, destination: Path) -> bool:
        try:
            destination.parent.mkdir(parents=True, exist_ok=True)
            self._client.download_file(self.bucket, key, str(destination))
            return True
        except Exception as exc:  # noqa: BLE001
            code = getattr(exc, "response", {}).get("Error", {}).get("Code", "")
            if code in {"404", "NoSuchKey", "NotFound"}:
                return False
            raise

    def delete_key(self, key: str) -> None:
        self._client.delete_object(Bucket=self.bucket, Key=key)


class WranglerR2Client(R2Client):
    """Compatibility fallback — no inventory, unconditional put."""

    def __init__(self, bucket: str) -> None:
        self.bucket = bucket
        self._np = ["npx", "--yes", "wrangler@4.120.0"]

    def _run(self, args: list[str]) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [*self._np, *args],
            check=False,
            text=True,
            capture_output=True,
        )

    def list_keys(self, prefix: str) -> set[str]:
        print(
            f"warning: wrangler fallback cannot list '{prefix}' efficiently; uploading without skip inventory",
            file=sys.stderr,
        )
        return set()

    def put_file(self, key: str, path: Path, *, if_none_match: bool) -> str:
        # wrangler has no If-None-Match; overwrite is idempotent for identical CAS bytes.
        result = self._run(
            ["r2", "object", "put", f"{self.bucket}/{key}", "--file", str(path), "--remote"]
        )
        if result.returncode != 0:
            raise RuntimeError(result.stderr or result.stdout or "wrangler put failed")
        return "uploaded"

    def get_file(self, key: str, destination: Path) -> bool:
        destination.parent.mkdir(parents=True, exist_ok=True)
        result = self._run(
            ["r2", "object", "get", f"{self.bucket}/{key}", "--file", str(destination), "--remote"]
        )
        if result.returncode != 0:
            if destination.exists():
                try:
                    destination.unlink()
                except OSError:
                    pass
            return False
        return destination.is_file()

    def delete_key(self, key: str) -> None:
        self._run(["r2", "object", "delete", f"{self.bucket}/{key}", "--remote", "--force"])


class ThrottleError(RuntimeError):
    pass


def resolve_client() -> R2Client:
    bucket = os.environ.get("R2_BUCKET", DEFAULT_BUCKET).strip() or DEFAULT_BUCKET
    account = (
        os.environ.get("R2_ACCOUNT_ID")
        or os.environ.get("CLOUDFLARE_ACCOUNT_ID")
        or ""
    ).strip()
    access = (
        os.environ.get("R2_ACCESS_KEY_ID")
        or os.environ.get("AWS_ACCESS_KEY_ID")
        or ""
    ).strip()
    secret = (
        os.environ.get("R2_SECRET_ACCESS_KEY")
        or os.environ.get("AWS_SECRET_ACCESS_KEY")
        or ""
    ).strip()
    endpoint = (
        os.environ.get("R2_ENDPOINT")
        or os.environ.get("AWS_ENDPOINT_URL")
        or (f"https://{account}.r2.cloudflarestorage.com" if account else "")
    ).strip()

    if access and secret and endpoint:
        try:
            return BotoR2Client(bucket, endpoint, access, secret)
        except ImportError:
            print("warning: boto3 not installed; falling back to wrangler", file=sys.stderr)

    if not os.environ.get("CLOUDFLARE_API_TOKEN") and not os.environ.get("CLOUDFLARE_ACCOUNT_ID"):
        raise SystemExit(
            "R2 S3 credentials (R2_ACCESS_KEY_ID/R2_SECRET_ACCESS_KEY) or "
            "CLOUDFLARE_API_TOKEN + CLOUDFLARE_ACCOUNT_ID required"
        )
    return WranglerR2Client(bucket)


def iter_local_objects(root: Path, relative_prefixes: Iterable[str]) -> list[tuple[str, Path]]:
    root = root.resolve()
    items: list[tuple[str, Path]] = []
    for prefix in relative_prefixes:
        base = root / prefix
        if not base.exists():
            continue
        for path in base.rglob("*"):
            if not path.is_file():
                continue
            if path.name.endswith(".tmp") or ".tmp-" in path.name:
                continue
            key = path.relative_to(root).as_posix()
            items.append((key, path))
    return items


def upload_tree(
    client: R2Client,
    root: Path,
    prefixes: list[str],
    *,
    concurrency: int,
    skip_existing: bool,
    cas_conditional: bool,
) -> UploadStats:
    objects = iter_local_objects(root, prefixes)
    stats = UploadStats(planned=len(objects))
    if not objects:
        print("nothing to upload")
        return stats

    remote: set[str] = set()
    if skip_existing:
        for prefix in prefixes:
            remote |= client.list_keys(prefix.rstrip("/") + "/")
        print(f"remote inventory: {len(remote)} keys under {', '.join(prefixes)}")

    limiter = AdaptiveLimiter(concurrency)
    pending = [(key, path) for key, path in objects if key not in remote]
    stats.skipped_existing = stats.planned - len(pending)
    print(
        f"upload plan: total={stats.planned} skip_existing={stats.skipped_existing} "
        f"to_put={len(pending)} concurrency~{limiter.concurrency}"
    )

    def worker(key: str, path: Path) -> None:
        attempts = 0
        while True:
            attempts += 1
            limiter.acquire()
            started = time.monotonic()
            try:
                result = client.put_file(key, path, if_none_match=cas_conditional)
                latency = time.monotonic() - started
                if result == "exists":
                    stats.inc("already_present")
                else:
                    stats.inc("uploaded")
                limiter.release_success(latency)
                return
            except ThrottleError:
                limiter.release_throttle()
                time.sleep(min(8.0, 0.25 * (2 ** min(attempts, 5))))
                if attempts >= 8:
                    stats.inc("failed")
                    print(f"error: throttled giving up on {key}", file=sys.stderr)
                    return
            except Exception as exc:  # noqa: BLE001
                limiter.release_error()
                if attempts >= 4:
                    stats.inc("failed")
                    print(f"error: {key}: {exc}", file=sys.stderr)
                    return
                time.sleep(0.5 * attempts)

    # Use a large pool; AdaptiveLimiter provides the real backpressure.
    workers = min(MAX_CONCURRENCY, max(MIN_CONCURRENCY, concurrency))
    with ThreadPoolExecutor(max_workers=workers) as pool:
        futures = [pool.submit(worker, key, path) for key, path in pending]
        for future in as_completed(futures):
            future.result()

    print(
        "upload done: "
        f"uploaded={stats.uploaded} already_present={stats.already_present} "
        f"skipped={stats.skipped_existing} failed={stats.failed} "
        f"final_concurrency~{limiter.concurrency}"
    )
    if stats.failed:
        raise SystemExit(1)
    return stats


def put_files(
    client: R2Client,
    directory: Path,
    key_prefix: str,
    *,
    concurrency: int,
    name_filter: Callable[[str], bool] | None = None,
) -> UploadStats:
    directory = directory.resolve()
    key_prefix = key_prefix.strip("/")
    items: list[tuple[str, Path]] = []
    for path in sorted(directory.iterdir()):
        if not path.is_file():
            continue
        if name_filter and not name_filter(path.name):
            continue
        key = f"{key_prefix}/{path.name}" if key_prefix else path.name
        items.append((key, path))

    stats = UploadStats(planned=len(items))
    limiter = AdaptiveLimiter(concurrency)

    def worker(key: str, path: Path) -> None:
        attempts = 0
        while True:
            attempts += 1
            limiter.acquire()
            started = time.monotonic()
            try:
                # Release maps/channel markers must overwrite.
                client.put_file(key, path, if_none_match=False)
                stats.inc("uploaded")
                limiter.release_success(time.monotonic() - started)
                return
            except ThrottleError:
                limiter.release_throttle()
                time.sleep(min(8.0, 0.25 * (2 ** min(attempts, 5))))
                if attempts >= 8:
                    stats.inc("failed")
                    return
            except Exception as exc:  # noqa: BLE001
                limiter.release_error()
                if attempts >= 4:
                    stats.inc("failed")
                    print(f"error: {key}: {exc}", file=sys.stderr)
                    return
                time.sleep(0.5 * attempts)

    with ThreadPoolExecutor(max_workers=min(MAX_CONCURRENCY, concurrency)) as pool:
        futures = [pool.submit(worker, key, path) for key, path in items]
        for future in as_completed(futures):
            future.result()

    print(f"put-files done: uploaded={stats.uploaded} failed={stats.failed}")
    if stats.failed:
        raise SystemExit(1)
    return stats


def delete_list(client: R2Client, list_path: Path, *, concurrency: int) -> int:
    if not list_path.is_file():
        print("delete-list: no file")
        return 0
    keys = [
        line.strip().replace("\\", "/")
        for line in list_path.read_text(encoding="utf-8").splitlines()
        if line.strip() and not line.strip().startswith("#")
    ]
    if not keys:
        print("delete-list: empty")
        return 0

    failed = 0
    lock = threading.Lock()

    def worker(key: str) -> None:
        nonlocal failed
        try:
            client.delete_key(key)
        except Exception as exc:  # noqa: BLE001
            with lock:
                failed += 1
            print(f"error deleting {key}: {exc}", file=sys.stderr)

    with ThreadPoolExecutor(max_workers=min(MAX_CONCURRENCY, concurrency)) as pool:
        list(pool.map(worker, keys))
    print(f"delete-list done: keys={len(keys)} failed={failed}")
    if failed:
        raise SystemExit(1)
    return len(keys)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)

    upload = sub.add_parser("upload-tree", help="Upload CAS tree (block/, delta/)")
    upload.add_argument("root", type=Path)
    upload.add_argument(
        "--prefix",
        action="append",
        dest="prefixes",
        default=[],
        help="Relative prefix under root (repeatable). Default: block delta",
    )
    upload.add_argument("--concurrency", type=int, default=DEFAULT_CONCURRENCY)
    upload.add_argument("--no-skip-existing", action="store_true")
    upload.add_argument("--no-conditional", action="store_true", help="Disable If-None-Match:*")

    put = sub.add_parser("put-files", help="Upload flat directory to a key prefix")
    put.add_argument("directory", type=Path)
    put.add_argument("--key-prefix", required=True)
    put.add_argument("--concurrency", type=int, default=DEFAULT_CONCURRENCY)
    put.add_argument(
        "--exclude",
        action="append",
        default=[],
        help="Basename to skip (repeatable), e.g. ci-channel.json",
    )

    getp = sub.add_parser("get", help="Download a single object")
    getp.add_argument("key")
    getp.add_argument("--file", required=True, type=Path)

    put1 = sub.add_parser("put", help="Upload a single file")
    put1.add_argument("key")
    put1.add_argument("--file", required=True, type=Path)
    put1.add_argument("--conditional", action="store_true")

    dell = sub.add_parser("delete-list", help="Delete keys listed in a text file")
    dell.add_argument("list_path", type=Path)
    dell.add_argument("--concurrency", type=int, default=DEFAULT_CONCURRENCY)

    listp = sub.add_parser("list", help="List keys under a prefix (debug)")
    listp.add_argument("prefix")

    args = parser.parse_args(argv)
    client = resolve_client()

    if args.command == "upload-tree":
        prefixes = args.prefixes or ["block", "delta"]
        upload_tree(
            client,
            args.root,
            prefixes,
            concurrency=args.concurrency,
            skip_existing=not args.no_skip_existing,
            cas_conditional=not args.no_conditional,
        )
        return 0

    if args.command == "put-files":
        excluded = {name for name in (args.exclude or []) if name}
        put_files(
            client,
            args.directory,
            args.key_prefix,
            concurrency=args.concurrency,
            name_filter=(None if not excluded else (lambda name: name not in excluded)),
        )
        return 0

    if args.command == "get":
        ok = client.get_file(args.key, args.file)
        if not ok:
            print(f"missing: {args.key}", file=sys.stderr)
            return 1
        print(f"downloaded {args.key} -> {args.file}")
        return 0

    if args.command == "put":
        result = client.put_file(args.key, args.file, if_none_match=args.conditional)
        print(result)
        return 0

    if args.command == "delete-list":
        delete_list(client, args.list_path, concurrency=args.concurrency)
        return 0

    if args.command == "list":
        keys = sorted(client.list_keys(args.prefix))
        for key in keys:
            print(key)
        print(f"count={len(keys)}")
        return 0

    raise SystemExit(f"unknown command {args.command}")


if __name__ == "__main__":
    raise SystemExit(main())

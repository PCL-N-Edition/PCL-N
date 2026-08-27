#!/usr/bin/env python3
"""High-throughput Cloudflare R2 CAS uploader (protocol v2 §15–19).

Primary auth reuses the existing Cloudflare CI secrets:

  CLOUDFLARE_API_TOKEN
  CLOUDFLARE_ACCOUNT_ID

via the Cloudflare R2 HTTP API (list + concurrent put/get/delete). No separate
R2 S3 access keys are required.

Optional overrides (local/dev only):

  R2_ACCESS_KEY_ID + R2_SECRET_ACCESS_KEY  →  boto3 S3-compatible API
  (otherwise falls back to wrangler CLI)

Features:

  * one List inventory for skip decisions (no per-object HEAD)
  * Put with If-None-Match: * for immutable CAS creates when supported
  * adaptive concurrency (default 24, range 8–48)
  * 412 PreconditionFailed treated as success (another job won the race)
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable, Iterable


DEFAULT_BUCKET = "pcln-releases"
MIN_CONCURRENCY = 2
DEFAULT_CONCURRENCY = 8
MAX_CONCURRENCY = 24
# Matrix jobs fan out; be patient under Cloudflare HTTP API rate limits.
THROTTLE_MAX_ATTEMPTS = 32
THROTTLE_MAX_SLEEP_S = 45.0
CF_API = "https://api.cloudflare.com/client/v4"


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


@dataclass(frozen=True)
class R2ObjectMetadata:
    size: int | None = None
    content_type: str | None = None


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
            while new_value > self._value:
                self._sem.release()
                self._value += 1
            while new_value < self._value:
                if self._sem.acquire(blocking=False):
                    self._value -= 1
                else:
                    break


class ThrottleError(RuntimeError):
    pass


class R2Client:
    """Thin wrapper over Cloudflare API / boto3 / wrangler."""

    def list_keys(self, prefix: str) -> set[str]:
        raise NotImplementedError

    def list_object_metadata(self, prefix: str) -> dict[str, R2ObjectMetadata]:
        return {key: R2ObjectMetadata() for key in self.list_keys(prefix)}

    def put_file(
        self,
        key: str,
        path: Path,
        *,
        if_none_match: bool,
        content_type: str | None = None,
    ) -> str:
        """Return 'uploaded' | 'exists'."""
        raise NotImplementedError

    def get_file(self, key: str, destination: Path) -> bool:
        raise NotImplementedError

    def inspect_object(self, key: str, prefix_length: int = 4) -> tuple[bytes, int] | None:
        """Return the first bytes and total stored size without changing the object."""
        with tempfile.TemporaryDirectory(prefix="pcln-r2-probe-") as temporary:
            destination = Path(temporary) / "object"
            if not self.get_file(key, destination):
                return None
            with destination.open("rb") as handle:
                prefix = handle.read(prefix_length)
            return prefix, destination.stat().st_size

    def delete_key(self, key: str) -> None:
        raise NotImplementedError


class CloudflareApiR2Client(R2Client):
    """R2 via Cloudflare HTTP API using CLOUDFLARE_API_TOKEN (no S3 keys)."""

    def __init__(self, account_id: str, token: str, bucket: str) -> None:
        self.account_id = account_id
        self.bucket = bucket
        self._token = token
        self._opener = urllib.request.build_opener()

    def _headers(self, *, content_type: str | None = None, extra: dict[str, str] | None = None) -> dict[str, str]:
        headers = {
            "Authorization": f"Bearer {self._token}",
        }
        if content_type:
            headers["Content-Type"] = content_type
        if extra:
            headers.update(extra)
        return headers

    def _object_url(self, key: str) -> str:
        # Encode each path segment so '/' remains a path separator for the API.
        encoded = "/".join(urllib.parse.quote(part, safe="") for part in key.split("/"))
        return (
            f"{CF_API}/accounts/{self.account_id}/r2/buckets/"
            f"{urllib.parse.quote(self.bucket, safe='')}/objects/{encoded}"
        )

    def _list_url(self) -> str:
        return (
            f"{CF_API}/accounts/{self.account_id}/r2/buckets/"
            f"{urllib.parse.quote(self.bucket, safe='')}/objects"
        )

    def _request(
        self,
        method: str,
        url: str,
        *,
        data: bytes | None = None,
        headers: dict[str, str] | None = None,
        timeout: float = 120.0,
    ) -> tuple[int, bytes, dict[str, str]]:
        request = urllib.request.Request(url, data=data, method=method, headers=headers or {})
        try:
            with self._opener.open(request, timeout=timeout) as response:
                body = response.read()
                status = getattr(response, "status", 200) or 200
                resp_headers = {k.lower(): v for k, v in response.headers.items()}
                return int(status), body, resp_headers
        except urllib.error.HTTPError as exc:
            body = exc.read() if exc.fp else b""
            resp_headers = {k.lower(): v for k, v in (exc.headers.items() if exc.headers else [])}
            return int(exc.code), body, resp_headers

    def list_keys(self, prefix: str) -> set[str]:
        return set(self.list_object_metadata(prefix))

    def list_object_metadata(self, prefix: str) -> dict[str, R2ObjectMetadata]:
        # Shard CAS trees so each page is smaller (proxy/chunked IncompleteRead).
        normalized = prefix.rstrip("/") + "/" if prefix else ""
        if normalized in {"block/", "delta/"} or normalized == "delta/v2/":
            shards: list[str]
            if normalized == "block/":
                shards = [f"block/{hh:02x}/" for hh in range(256)]
            elif normalized.startswith("delta"):
                # Prefer leaf under delta/v2/; fall back to whole delta/ tree.
                base = "delta/v2/" if normalized in {"delta/", "delta/v2/"} else normalized
                shards = [f"{base}{hh:02x}/" for hh in range(256)]
            else:
                shards = [normalized]
            objects: dict[str, R2ObjectMetadata] = {}
            total = len(shards)
            for index, shard in enumerate(shards, start=1):
                objects.update(self._list_object_metadata_prefix(shard, per_page=100))
                if index == 1 or index % 32 == 0 or index == total:
                    print(f"list {normalized}: shard {index}/{total} keys={len(objects)}", flush=True)
            return objects
        return self._list_object_metadata_prefix(normalized or prefix, per_page=100)

    def _list_keys_prefix(self, prefix: str, *, per_page: int = 200) -> set[str]:
        return set(self._list_object_metadata_prefix(prefix, per_page=per_page))

    def _list_object_metadata_prefix(
        self,
        prefix: str,
        *,
        per_page: int = 200,
    ) -> dict[str, R2ObjectMetadata]:
        import http.client

        objects: dict[str, R2ObjectMetadata] = {}
        cursor: str | None = None
        while True:
            query = {
                "prefix": prefix,
                "per_page": str(per_page),
            }
            if cursor:
                query["cursor"] = cursor
            url = f"{self._list_url()}?{urllib.parse.urlencode(query)}"
            body: bytes | None = None
            status = 0
            last_error: Exception | None = None
            for attempt in range(1, 8):
                try:
                    status, body, _ = self._request("GET", url, headers=self._headers())
                    last_error = None
                    break
                except (
                    TimeoutError,
                    ConnectionError,
                    OSError,
                    http.client.IncompleteRead,
                    http.client.RemoteDisconnected,
                ) as exc:
                    last_error = exc
                    time.sleep(min(10.0, 0.25 * (2 ** (attempt - 1))))
                except Exception as exc:  # noqa: BLE001
                    name = type(exc).__name__
                    if name in {"IncompleteRead", "RemoteDisconnected", "ProtocolError"} or "IncompleteRead" in str(exc):
                        last_error = exc
                        time.sleep(min(10.0, 0.25 * (2 ** (attempt - 1))))
                        continue
                    raise
            if last_error is not None:
                raise RuntimeError(f"list objects failed for prefix={prefix!r}: {last_error}") from last_error
            assert body is not None
            if status == 429:
                time.sleep(1.5)
                continue
            if status >= 400:
                raise RuntimeError(f"list objects failed HTTP {status}: {body[:400]!r}")
            payload = json.loads(body.decode("utf-8"))
            if not payload.get("success", True):
                raise RuntimeError(f"list objects error: {payload.get('errors')}")
            for item in payload.get("result") or []:
                key = item.get("key") if isinstance(item, dict) else None
                if isinstance(key, str) and key:
                    raw_size = item.get("size")
                    size = int(raw_size) if isinstance(raw_size, (int, float)) else None
                    http_metadata = item.get("http_metadata") or {}
                    content_type = (
                        str(http_metadata.get("contentType"))
                        if isinstance(http_metadata, dict) and http_metadata.get("contentType")
                        else None
                    )
                    objects[key] = R2ObjectMetadata(size=size, content_type=content_type)
            info = payload.get("result_info") or {}
            if not info.get("is_truncated"):
                break
            cursor = info.get("cursor")
            if not cursor:
                break
        return objects

    def put_file(
        self,
        key: str,
        path: Path,
        *,
        if_none_match: bool,
        content_type: str | None = None,
    ) -> str:
        data = path.read_bytes()
        extra: dict[str, str] = {}
        if if_none_match:
            extra["If-None-Match"] = "*"
        headers = self._headers(
            content_type=content_type or "application/octet-stream",
            extra=extra,
        )
        status, body, _ = self._request(
            "PUT",
            self._object_url(key),
            data=data,
            headers=headers,
            timeout=300.0,
        )
        if status in {200, 201}:
            return "uploaded"
        if status == 412:
            return "exists"
        if status in {429, 502, 503, 504}:
            raise ThrottleError(f"throttled putting {key} (HTTP {status})")
        # Some API versions may ignore If-None-Match and still overwrite; treat
        # unexpected 2xx/409 as soft success for CAS idempotency.
        if status == 409:
            return "exists"
        # CF sometimes wraps rate limits as JSON errors with HTTP 400.
        try:
            payload = json.loads(body.decode("utf-8"))
            errors = payload.get("errors") or []
            codes = {int(err.get("code", 0)) for err in errors if isinstance(err, dict)}
            if 10000 in codes or any("ratelimit" in str(err).lower() for err in errors):
                raise ThrottleError(f"throttled putting {key} (api codes={sorted(codes)})")
        except (UnicodeDecodeError, json.JSONDecodeError, TypeError, ValueError):
            pass
        raise RuntimeError(f"put {key} failed HTTP {status}: {body[:400]!r}")

    def get_file(self, key: str, destination: Path) -> bool:
        status, body, headers = self._request(
            "GET",
            self._object_url(key),
            headers=self._headers(),
            timeout=300.0,
        )
        if status == 404:
            return False
        if status == 429:
            raise ThrottleError(f"throttled getting {key}")
        if status >= 400:
            # CF may wrap errors as JSON even for missing objects.
            try:
                payload = json.loads(body.decode("utf-8"))
                errors = payload.get("errors") or []
                if any(int(err.get("code", 0)) in {10007, 7003} for err in errors if isinstance(err, dict)):
                    return False
            except (UnicodeDecodeError, json.JSONDecodeError, TypeError, ValueError):
                pass
            if status == 404:
                return False
            raise RuntimeError(f"get {key} failed HTTP {status}: {body[:400]!r}")
        # Successful binary body (not a JSON error envelope).
        content_type = headers.get("content-type", "")
        if "application/json" in content_type and body[:1] == b"{":
            try:
                payload = json.loads(body.decode("utf-8"))
                if payload.get("success") is False:
                    return False
            except (UnicodeDecodeError, json.JSONDecodeError):
                pass
        destination.parent.mkdir(parents=True, exist_ok=True)
        temporary = destination.with_suffix(destination.suffix + ".tmp")
        temporary.write_bytes(body)
        temporary.replace(destination)
        return True

    def inspect_object(self, key: str, prefix_length: int = 4) -> tuple[bytes, int] | None:
        # The Cloudflare object endpoint may honor Range (206) or return the full
        # representation (200). Read only the prefix in either case so validation
        # never downloads every complete CAS block.
        request = urllib.request.Request(
            self._object_url(key),
            method="GET",
            headers=self._headers(extra={"Range": f"bytes=0-{max(0, prefix_length - 1)}"}),
        )
        try:
            with self._opener.open(request, timeout=120.0) as response:
                status = int(getattr(response, "status", 200) or 200)
                headers = {k.lower(): v for k, v in response.headers.items()}
                prefix = response.read(prefix_length)
        except urllib.error.HTTPError as exc:
            if exc.code == 404:
                return None
            body = exc.read(400) if exc.fp else b""
            if exc.code in {429, 502, 503, 504}:
                raise ThrottleError(f"throttled inspecting {key} (HTTP {exc.code})") from exc
            raise RuntimeError(f"inspect {key} failed HTTP {exc.code}: {body!r}") from exc

        if status == 404:
            return None
        if status in {429, 502, 503, 504}:
            raise ThrottleError(f"throttled inspecting {key} (HTTP {status})")
        if status >= 400:
            raise RuntimeError(f"inspect {key} failed HTTP {status}")

        total_size: int | None = None
        content_range = headers.get("content-range", "")
        if "/" in content_range:
            tail = content_range.rsplit("/", 1)[-1]
            if tail.isdigit():
                total_size = int(tail)
        if total_size is None:
            content_length = headers.get("content-length", "")
            if content_length.isdigit():
                total_size = int(content_length)
        if total_size is None or (status == 206 and total_size < len(prefix)):
            raise RuntimeError(f"inspect {key} response did not include the stored object size")
        return prefix, total_size

    def delete_key(self, key: str) -> None:
        status, body, _ = self._request(
            "DELETE",
            self._object_url(key),
            headers=self._headers(),
        )
        if status in {200, 204, 404}:
            return
        if status in {429, 502, 503, 504}:
            raise ThrottleError(f"throttled deleting {key} (HTTP {status})")
        raise RuntimeError(f"delete {key} failed HTTP {status}: {body[:400]!r}")


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
        return set(self.list_object_metadata(prefix))

    def list_object_metadata(self, prefix: str) -> dict[str, R2ObjectMetadata]:
        objects: dict[str, R2ObjectMetadata] = {}
        token: str | None = None
        while True:
            kwargs: dict = {"Bucket": self.bucket, "Prefix": prefix, "MaxKeys": 1000}
            if token:
                kwargs["ContinuationToken"] = token
            response = self._client.list_objects_v2(**kwargs)
            for item in response.get("Contents") or []:
                key = item.get("Key")
                if isinstance(key, str) and key:
                    raw_size = item.get("Size")
                    objects[key] = R2ObjectMetadata(
                        size=int(raw_size) if isinstance(raw_size, (int, float)) else None
                    )
            if not response.get("IsTruncated"):
                break
            token = response.get("NextContinuationToken")
            if not token:
                break
        return objects

    def put_file(
        self,
        key: str,
        path: Path,
        *,
        if_none_match: bool,
        content_type: str | None = None,
    ) -> str:
        extra: dict = {}
        if if_none_match:
            extra["IfNoneMatch"] = "*"
        if content_type:
            extra["ContentType"] = content_type
        try:
            with path.open("rb") as handle:
                self._client.put_object(
                    Bucket=self.bucket,
                    Key=key,
                    Body=handle,
                    **extra,
                )
            return "uploaded"
        except Exception as exc:  # noqa: BLE001
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

    def inspect_object(self, key: str, prefix_length: int = 4) -> tuple[bytes, int] | None:
        try:
            response = self._client.get_object(
                Bucket=self.bucket,
                Key=key,
                Range=f"bytes=0-{max(0, prefix_length - 1)}",
            )
            prefix = response["Body"].read(prefix_length)
            content_range = str(response.get("ContentRange") or "")
            total_size = int(content_range.rsplit("/", 1)[-1]) if "/" in content_range else None
            if total_size is None:
                total_size = int(self._client.head_object(Bucket=self.bucket, Key=key)["ContentLength"])
            return prefix, total_size
        except Exception as exc:  # noqa: BLE001
            code = str(getattr(exc, "response", {}).get("Error", {}).get("Code", ""))
            status = getattr(exc, "response", {}).get("ResponseMetadata", {}).get("HTTPStatusCode")
            if status == 404 or code in {"404", "NoSuchKey", "NotFound"}:
                return None
            if status in {429, 502, 503, 504} or code in {"SlowDown", "TooManyRequests"}:
                raise ThrottleError(str(exc)) from exc
            raise

    def delete_key(self, key: str) -> None:
        self._client.delete_object(Bucket=self.bucket, Key=key)


class WranglerR2Client(R2Client):
    """Last-resort CLI fallback — concurrent put, no inventory."""

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

    def put_file(
        self,
        key: str,
        path: Path,
        *,
        if_none_match: bool,
        content_type: str | None = None,
    ) -> str:
        args = ["r2", "object", "put", f"{self.bucket}/{key}", "--file", str(path), "--remote"]
        if content_type:
            args.extend(["--content-type", content_type])
        result = self._run(args)
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


def resolve_client() -> R2Client:
    bucket = os.environ.get("R2_BUCKET", DEFAULT_BUCKET).strip() or DEFAULT_BUCKET
    account = (
        os.environ.get("CLOUDFLARE_ACCOUNT_ID")
        or os.environ.get("R2_ACCOUNT_ID")
        or ""
    ).strip()
    token = (os.environ.get("CLOUDFLARE_API_TOKEN") or "").strip()

    # Primary: reuse existing Cloudflare CI token (no separate R2 S3 keys).
    if token and account:
        print(f"R2 client: Cloudflare API (account={account[:6]}… bucket={bucket})")
        return CloudflareApiR2Client(account, token, bucket)

    # Optional local/dev override via S3-compatible R2 keys.
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
            print(f"R2 client: S3-compatible endpoint ({endpoint})")
            return BotoR2Client(bucket, endpoint, access, secret)
        except ImportError:
            print("warning: boto3 not installed; trying wrangler", file=sys.stderr)

    if token or account:
        print("R2 client: wrangler CLI fallback")
        return WranglerR2Client(bucket)

    raise SystemExit(
        "CLOUDFLARE_API_TOKEN + CLOUDFLARE_ACCOUNT_ID required "
        "(optional override: R2_ACCESS_KEY_ID/R2_SECRET_ACCESS_KEY)"
    )


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
                ctype = guess_content_type(path, key=key)
                result = client.put_file(
                    key, path, if_none_match=cas_conditional, content_type=ctype
                )
                latency = time.monotonic() - started
                if result == "exists":
                    stats.inc("already_present")
                else:
                    stats.inc("uploaded")
                limiter.release_success(latency)
                return
            except ThrottleError:
                limiter.release_throttle()
                delay = min(THROTTLE_MAX_SLEEP_S, 0.5 * (2 ** min(attempts, 6)))
                time.sleep(delay)
                if attempts >= THROTTLE_MAX_ATTEMPTS:
                    stats.inc("failed")
                    print(f"error: throttled giving up on {key}", file=sys.stderr)
                    return
            except Exception as exc:  # noqa: BLE001
                limiter.release_error()
                if attempts >= 6:
                    stats.inc("failed")
                    print(f"error: {key}: {exc}", file=sys.stderr)
                    return
                time.sleep(min(12.0, 0.75 * attempts))

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


def guess_content_type(path: Path, *, key: str | None = None) -> str | None:
    if key and key.startswith("block/"):
        with path.open("rb") as handle:
            prefix = handle.read(4)
        if prefix.startswith(b"\x1f\x8b"):
            return "application/gzip"
        if prefix.startswith(b"\x28\xb5\x2f\xfd"):
            return "application/zstd"
    name = path.name.lower()
    if name.endswith(".json"):
        return "application/json; charset=utf-8"
    if name.endswith(".asc") or name.endswith(".sig"):
        return "text/plain; charset=utf-8"
    if name.endswith(".vcdiff"):
        return "application/octet-stream"
    return None


def put_files(
    client: R2Client,
    directory: Path,
    key_prefix: str,
    *,
    concurrency: int,
    name_filter: Callable[[str], bool] | None = None,
    content_type: str | None = None,
) -> UploadStats:
    """Batch-upload every file in a flat directory (v1 maps, v2 maps, sigs, metadata)."""
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
    if not items:
        print("put-files: nothing to upload")
        return stats

    limiter = AdaptiveLimiter(concurrency)
    print(f"put-files plan: total={stats.planned} concurrency~{limiter.concurrency} prefix={key_prefix or '/'}")

    def worker(key: str, path: Path) -> None:
        attempts = 0
        ctype = content_type or guess_content_type(path)
        while True:
            attempts += 1
            limiter.acquire()
            started = time.monotonic()
            try:
                client.put_file(key, path, if_none_match=False, content_type=ctype)
                stats.inc("uploaded")
                limiter.release_success(time.monotonic() - started)
                return
            except ThrottleError:
                limiter.release_throttle()
                time.sleep(min(THROTTLE_MAX_SLEEP_S, 0.5 * (2 ** min(attempts, 6))))
                if attempts >= THROTTLE_MAX_ATTEMPTS:
                    stats.inc("failed")
                    return
            except Exception as exc:  # noqa: BLE001
                limiter.release_error()
                if attempts >= 6:
                    stats.inc("failed")
                    print(f"error: {key}: {exc}", file=sys.stderr)
                    return
                time.sleep(min(12.0, 0.75 * attempts))

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
    return delete_keys(client, keys, concurrency=concurrency)


def delete_keys(client: R2Client, keys: list[str], *, concurrency: int) -> int:
    if not keys:
        print("delete-list: empty")
        return 0

    failed = 0
    lock = threading.Lock()
    # GC is best-effort; keep concurrency low so promote is not killed by 429 storms.
    workers = min(8, max(1, concurrency))

    def worker(key: str) -> None:
        nonlocal failed
        attempts = 0
        while True:
            attempts += 1
            try:
                client.delete_key(key)
                return
            except ThrottleError as exc:
                delay = min(THROTTLE_MAX_SLEEP_S, 0.5 * (2 ** min(attempts, 6)))
                time.sleep(delay)
                if attempts >= THROTTLE_MAX_ATTEMPTS:
                    with lock:
                        failed += 1
                    print(f"error deleting {key}: {exc}", file=sys.stderr)
                    return
            except Exception as exc:  # noqa: BLE001
                if attempts >= 8:
                    with lock:
                        failed += 1
                    print(f"error deleting {key}: {exc}", file=sys.stderr)
                    return
                time.sleep(min(12.0, 0.5 * attempts))

    with ThreadPoolExecutor(max_workers=workers) as pool:
        list(pool.map(worker, keys))
    print(f"delete-list done: keys={len(keys)} failed={failed}")
    if failed:
        # Do not abort the release over residual GC failures.
        print(
            f"warning: delete-list left {failed} key(s) undeleted (throttled/errors); "
            "continuing — run scripts/gc later if needed",
            file=sys.stderr,
        )
    return len(keys) - failed


def _load_catalog_module():
    """Import sibling update_update_block_catalog without requiring package install."""
    import importlib.util

    script = Path(__file__).resolve().parent / "update_update_block_catalog.py"
    spec = importlib.util.spec_from_file_location("update_update_block_catalog", script)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"cannot load {script}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def _read_channel_pin_tags(client: R2Client) -> set[str]:
    """Tags currently advertised by channels/*.json — never GC their CAS roots."""
    import tempfile

    pins: set[str] = set()
    for name in ("release", "beta", "ci"):
        key = f"channels/{name}.json"
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / f"{name}.json"
            if not client.get_file(key, path):
                continue
            try:
                payload = json.loads(path.read_text(encoding="utf-8"))
            except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
                print(f"warning: ignore channel pointer {key}: {exc}", file=sys.stderr)
                continue
            tag = payload.get("tag")
            if isinstance(tag, str) and tag.strip():
                pins.add(tag.strip())
    return pins


def gc_unused_cas(
    client: R2Client,
    *,
    apply: bool,
    concurrency: int,
    delete_list_path: Path | None = None,
    catalog_out: Path | None = None,
) -> int:
    """
    Protocol v2 §19 standalone GC:

      1. Load block/catalog.json
      2. Pin live channel tags; prune catalog entries outside 14-day window
      3. List remote block/ + delta/ inventory
      4. Mark-and-sweep unreferenced full blocks and delta/v2/*
      5. With --apply: delete + write pruned catalog; otherwise dry-run only
    """
    catalog_module = _load_catalog_module()
    import tempfile
    from datetime import datetime, timezone

    with tempfile.TemporaryDirectory() as temporary:
        work = Path(temporary)
        catalog_path = work / "catalog.json"
        if not client.get_file("block/catalog.json", catalog_path):
            raise SystemExit("block/catalog.json missing on R2 — refusing GC")
        previous = catalog_module._read_catalog(catalog_path)
        if not (previous.get("releases") or []):
            raise SystemExit("block catalog has no releases — refusing inventory sweep")

        pin_tags = _read_channel_pin_tags(client)
        print(f"gc: pin channel tags={sorted(pin_tags) or ['(none)']}")
        now = datetime.now(timezone.utc)
        catalog, retention_deletions = catalog_module.prune_catalog(
            previous, now=now, pin_tags=pin_tags
        )
        print(
            f"gc: catalog retained={len(catalog['releases'])} "
            f"(was {len(previous.get('releases') or [])}) "
            f"retention_deletions={len(retention_deletions)}"
        )

        remote: set[str] = set()
        for prefix in ("block/", "delta/"):
            keys = client.list_keys(prefix)
            remote |= keys
            print(f"gc: listed {prefix} keys={len(keys)}")

        inventory = catalog_module.inventory_gc_deletions(catalog, remote)
        # Retention may also name release/* objects; only delete CAS (block/delta)
        # via inventory union, plus catalog-tracked block/delta retention deletes.
        cas_retention = [
            key
            for key in retention_deletions
            if key.startswith("block/") or key.startswith("delta/")
        ]
        release_retention = [
            key for key in retention_deletions if key.startswith("releases/")
        ]
        deletions = sorted(set(inventory) | set(cas_retention) | set(release_retention))

        block_deletes = sum(1 for k in deletions if k.startswith("block/"))
        delta_deletes = sum(1 for k in deletions if k.startswith("delta/"))
        release_deletes = sum(1 for k in deletions if k.startswith("releases/"))
        print(
            f"gc: candidates total={len(deletions)} "
            f"block={block_deletes} delta={delta_deletes} releases={release_deletes} "
            f"(inventory={len(inventory)} retention_cas={len(cas_retention)})"
        )

        if delete_list_path is not None:
            delete_list_path.write_text(
                "".join(f"{key}\n" for key in deletions), encoding="utf-8"
            )
            print(f"gc: wrote delete list -> {delete_list_path}")

        if catalog_out is not None:
            catalog_out.write_text(
                json.dumps(catalog, ensure_ascii=False, indent=2) + "\n",
                encoding="utf-8",
            )
            print(f"gc: wrote pruned catalog -> {catalog_out}")

        sample = deletions[:20]
        for key in sample:
            print(f"  would delete: {key}")
        if len(deletions) > len(sample):
            print(f"  ... and {len(deletions) - len(sample)} more")

        if not apply:
            print("gc: dry-run only (pass --apply to delete)")
            return 0

        if deletions:
            delete_keys(client, deletions, concurrency=concurrency)

        catalog_tmp = work / "catalog.next.json"
        catalog_tmp.write_text(
            json.dumps(catalog, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
        client.put_file(
            "block/catalog.json",
            catalog_tmp,
            if_none_match=False,
            content_type="application/json; charset=utf-8",
        )
        print(
            f"gc: applied deletions={len(deletions)} "
            f"catalog_releases={len(catalog['releases'])}"
        )
        return 0


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

    put = sub.add_parser("put-files", help="Batch-upload flat directory to a key prefix")
    put.add_argument("directory", type=Path)
    put.add_argument("--key-prefix", required=True)
    put.add_argument("--concurrency", type=int, default=DEFAULT_CONCURRENCY)
    put.add_argument(
        "--exclude",
        action="append",
        default=[],
        help="Basename to skip (repeatable), e.g. ci-channel.json",
    )
    put.add_argument(
        "--content-type",
        default=None,
        help="Force Content-Type for every object (default: guess from extension)",
    )

    getp = sub.add_parser("get", help="Download a single object")
    getp.add_argument("key")
    getp.add_argument("--file", required=True, type=Path)

    put1 = sub.add_parser("put", help="Upload a single file")
    put1.add_argument("key")
    put1.add_argument("--file", required=True, type=Path)
    put1.add_argument("--conditional", action="store_true")
    put1.add_argument("--content-type", default=None)

    dell = sub.add_parser("delete-list", help="Delete keys listed in a text file")
    dell.add_argument("list_path", type=Path)
    dell.add_argument("--concurrency", type=int, default=DEFAULT_CONCURRENCY)

    listp = sub.add_parser("list", help="List keys under a prefix (debug)")
    listp.add_argument("prefix")

    gcp = sub.add_parser(
        "gc",
        help="Mark-and-sweep unreferenced block/ + delta/ using block/catalog.json (§19)",
    )
    gcp.add_argument(
        "--apply",
        action="store_true",
        help="Actually delete unreachable keys and write pruned catalog (default: dry-run)",
    )
    gcp.add_argument("--concurrency", type=int, default=DEFAULT_CONCURRENCY)
    gcp.add_argument(
        "--delete-list",
        type=Path,
        default=None,
        help="Optional path to write the candidate key list",
    )
    gcp.add_argument(
        "--catalog-out",
        type=Path,
        default=None,
        help="Optional path to write the pruned catalog JSON",
    )

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
            content_type=args.content_type,
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
        result = client.put_file(
            args.key,
            args.file,
            if_none_match=args.conditional,
            content_type=args.content_type or guess_content_type(args.file),
        )
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

    if args.command == "gc":
        return gc_unused_cas(
            client,
            apply=bool(args.apply),
            concurrency=args.concurrency,
            delete_list_path=args.delete_list,
            catalog_out=args.catalog_out,
        )

    raise SystemExit(f"unknown command {args.command}")


if __name__ == "__main__":
    raise SystemExit(main())

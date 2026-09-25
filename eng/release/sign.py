"""Sign the complete distribution with the pinned publisher key, then verify it."""
import argparse
from contextlib import contextmanager
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

from verify import expected_names, verify

FINGERPRINT = "5701218D69B531E1A7ED35BB6E31F5974A273AEE"


def gpg(home, *arguments, data=None):
    # Do not inherit signing secrets into GnuPG, or print potentially sensitive stderr.
    environment = {k: v for k, v in os.environ.items()
                   if k not in ("GPG_PRIVATE_KEY", "GPG_PASSPHRASE")}
    result = subprocess.run(["gpg", "--no-options", "--homedir", str(home),
                             "--batch", "--no-tty", *arguments],
                            input=data, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            env=environment, timeout=120)
    if result.returncode:
        raise ValueError("Publisher signature operation failed")
    return result.stdout


@contextmanager
def keyring():
    with tempfile.TemporaryDirectory(prefix="nexa-sign-") as temp:
        home = Path(temp)
        home.chmod(0o700)
        try:
            yield home
        finally:
            # Shut down only the agent belonging to this temporary home.
            executable = shutil.which("gpgconf")
            if executable:
                subprocess.run([executable, "--homedir", str(home), "--kill", "gpg-agent"],
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                               timeout=30, check=False)


def import_public(home, public_key, fingerprint):
    gpg(home, "--import", data=public_key)
    listing = gpg(home, "--with-colons", "--fingerprint", "--list-keys")
    fingerprints = {line.split(b":")[9].decode("ascii")
                    for line in listing.splitlines() if line.startswith(b"fpr:")}
    if fingerprint not in fingerprints:
        raise ValueError("Repository public key does not match the pinned signing key")


def verify_signatures(directory, version, public_key, fingerprint=FINGERPRINT):
    names = expected_names(version) | {"SHA256SUMS"}
    expected = names | {name + ".asc" for name in names}
    if {path.name for path in directory.iterdir()} != expected:
        raise ValueError("Signed distribution is incomplete or contains unexpected files")
    with keyring() as home:
        import_public(home, public_key, fingerprint)
        for name in sorted(names):
            status = gpg(home, "--status-fd", "1", "--verify",
                         str(directory / (name + ".asc")), str(directory / name))
            signers = [line.split()[2].decode("ascii") for line in status.splitlines()
                       if line.startswith(b"[GNUPG:] VALIDSIG ")]
            if signers != [fingerprint]:
                raise ValueError("Distribution was not signed by the pinned signing key")


def sign_distribution(directory, version, public_key, private_key, passphrase,
                      fingerprint=FINGERPRINT):
    if not private_key.strip():
        raise ValueError("Publisher signing key is required")
    if "\n" in passphrase or "\r" in passphrase:
        raise ValueError("Signing passphrase cannot contain line breaks")
    directory = directory.resolve()
    names = expected_names(version) | {"SHA256SUMS"}
    # Only remove known signature outputs when re-running; unknown assets still fail.
    for name in names:
        (directory / (name + ".asc")).unlink(missing_ok=True)
    verify(directory, version)
    try:
        with keyring() as home:
            import_public(home, public_key, fingerprint)
            gpg(home, "--import", data=private_key.encode("utf-8"))
            for name in sorted(names):
                gpg(home, "--pinentry-mode", "loopback", "--passphrase-fd", "0",
                    "--local-user", fingerprint + "!", "--digest-algo", "SHA256",
                    "--armor", "--detach-sign", "--output", str(directory / (name + ".asc")),
                    str(directory / name), data=(passphrase + "\n").encode("utf-8"))
        verify_signatures(directory, version, public_key, fingerprint)
    except BaseException:
        for name in names:
            (directory / (name + ".asc")).unlink(missing_ok=True)
        raise


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    parser.add_argument("version")
    args = parser.parse_args()
    sign_distribution(args.directory, args.version,
                      Path(__file__).resolve().parents[2].joinpath("GPG-PUBLIC-KEY.asc").read_bytes(),
                      os.environ.get("GPG_PRIVATE_KEY", ""), os.environ.get("GPG_PASSPHRASE", ""))

"""Install packages on disposable native CI runners; never run on a developer host."""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile


def run(*args):
    subprocess.run([str(arg) for arg in args], check=True)


def require(path):
    if not path.is_file():
        raise RuntimeError(f"System installation is missing {path}")


def check_jvm_host(path):
    require(path)
    # No request must fail before loading a JVM or initializing application services.
    result = subprocess.run([str(path)], timeout=15, check=False)
    if result.returncode != 2:
        raise RuntimeError(f"Installed JVM host cannot execute: {result.returncode}")


def windows(root, base):
    executable = Path(os.environ.get("ProgramW6432", os.environ["ProgramFiles"])) / "NexaCL/Nexa.Desktop.exe"
    desktop = Path(os.environ["PUBLIC"]) / "Desktop/NexaCL.lnk"
    menu = Path(os.environ["ProgramData"]) / "Microsoft/Windows/Start Menu/Programs/NexaCL.lnk"
    def check():
        for path in (executable, desktop, menu):
            require(path)
        check_jvm_host(executable.parent / "Nexa.Jvm.Host.exe")
        run(executable, "--validate-shell")
    run(root / (base + ".setup.exe"), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/TASKS=desktopicon")
    check()
    run(executable.parent / "unins000.exe", "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART")
    if executable.exists():
        raise RuntimeError("EXE uninstall left the installed executable")
    if (executable.parent / "Nexa.Jvm.Host.exe").exists():
        raise RuntimeError("EXE uninstall left the JVM host")
    msi = root / (base + ".msi")
    run("msiexec.exe", "/i", msi, "/qn", "/norestart")
    try:
        check()
    finally:
        run("msiexec.exe", "/x", msi, "/qn", "/norestart")


def macos(root, base):
    with tempfile.TemporaryDirectory(prefix="nexa-dmg-") as temporary:
        mount = Path(temporary) / "volume"
        run("hdiutil", "attach", root / (base + ".dmg"), "-readonly", "-nobrowse", "-mountpoint", mount)
        try:
            run("sudo", "installer", "-pkg", mount / "NexaCL.pkg", "-target", "/")
            app = Path("/Applications/Nexa.app")
            executable = app / "Contents/MacOS/Nexa.Desktop"
            require(executable)
            if app.stat().st_uid != 0 or executable.stat().st_uid != 0:
                raise RuntimeError("macOS system payload must be root owned")
            run(executable, "--validate-shell")
        finally:
            run("hdiutil", "detach", mount)


def linux(root, base):
    run("sudo", "dpkg", "-i", root / (base + ".deb"))
    try:
        executable = Path("/usr/lib/nexacl/Nexa.Desktop")
        require(executable)
        require(Path("/usr/share/applications/nexacl.desktop"))
        if executable.stat().st_uid != 0 or executable.stat().st_mode & 0o022:
            raise RuntimeError("Linux system payload must be root owned and not user writable")
        host = executable.parent / "Nexa.Jvm.Host"
        check_jvm_host(host)
        if host.stat().st_uid != 0 or host.stat().st_mode & 0o022 or not os.access(host, os.X_OK):
            raise RuntimeError("Installed JVM host has invalid ownership or permissions")
        run(executable, "--validate-shell")
        # Validate RPM ownership too; installing an RPM over a DEB on Ubuntu is invalid.
        owners = subprocess.check_output(["rpm", "-qp", "--queryformat",
                                          "[%{FILEUSERNAME}:%{FILEGROUPNAME}\\n]", str(root / (base + ".rpm"))], text=True)
        if not owners.splitlines() or any(owner != "root:root" for owner in owners.splitlines()):
            raise RuntimeError("RPM contains non-system file ownership")
    finally:
        run("sudo", "dpkg", "-r", "nexacl")


if __name__ == "__main__":
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("RUNNER_ENVIRONMENT") != "github-hosted":
        raise SystemExit("Installation smoke is restricted to disposable GitHub-hosted runners")
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    parser.add_argument("version")
    parser.add_argument("rid")
    args = parser.parse_args()
    platform = args.rid.split("-")[0]
    {"win": windows, "osx": macos, "linux": linux}[platform](args.directory.resolve(), f"Nexa-{args.version}-{args.rid}")

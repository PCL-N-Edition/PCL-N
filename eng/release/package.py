"""Native packaging on the matching CI runner. No source or shell strings are evaluated."""
import argparse
import os
import plistlib
import shutil
import subprocess
import tarfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def required_tool(variable):
    path = Path(os.environ[variable])
    if not path.is_absolute() or not path.is_file():
        raise ValueError(f"Missing isolated build tool: {variable}")
    return path


def copy_runtime_payload(source, destination):
    # NativeAOT emits large debugging sidecars; they are not runtime dependencies.
    # Keep the publish directory intact for separate diagnostic artifact retention.
    shutil.copytree(source, destination, ignore=shutil.ignore_patterns("*.pdb", "*.dbg", "*.dSYM"))


def run(*args, **kwargs):
    subprocess.run([str(arg) for arg in args], check=True, **kwargs)


def archive(source, output, name):
    if output.suffix == ".zip":
        shutil.make_archive(str(output.with_suffix("")), "zip", source)
    else:
        with tarfile.open(output, "w:gz") as tar:
            tar.add(source, arcname=name)


def windows(payload, output, work, base, version, prefix, arch):
    iscc = required_tool("NEXA_ISCC")
    icon = ROOT / "Nexa.Desktop/Assets/icon.ico"
    run(iscc, f"/DPayload={payload}", f"/DProductVersion={version}", f"/DNumericVersion={prefix}",
        f"/DInstallArch={'arm64' if arch == 'arm64' else 'x64compatible'}", f"/DOutputDir={output}",
        f"/DOutputName={base}.setup", f"/DIconPath={icon}", ROOT / "eng/release/windows.iss")
    run("wix", "build", ROOT / "eng/release/windows.wxs", "-arch", arch,
        "-d", f"Payload={payload}", "-d", f"NumericVersion={prefix}", "-d", f"IconPath={icon}",
                "-o", work / "launcher.msi")
    shutil.copy2(work / "launcher.msi", output / f"{base}.msi")
    archive(payload, output / f"{base}.portable.zip", "NexaCL")


def macos(payload, output, work, base, version, prefix, arch):
    app = work / "Nexa.app"
    contents = app / "Contents"
    copy_runtime_payload(payload, contents / "MacOS")
    resources = contents / "Resources"
    resources.mkdir()
    iconset = work / "Launcher.iconset"
    iconset.mkdir()
    for size in (16, 32, 128, 256, 512):
        for scale in (1, 2):
            suffix = "@2x" if scale == 2 else ""
            run("sips", "-z", size * scale, size * scale, ROOT / "Nexa.Desktop/Assets/icon.png",
                "--out", iconset / f"icon_{size}x{size}{suffix}.png", stdout=subprocess.DEVNULL)
    run("iconutil", "-c", "icns", iconset, "-o", resources / "Launcher.icns")
    with (contents / "Info.plist").open("wb") as stream:
        plistlib.dump(dict(CFBundleName="NexaCL", CFBundleDisplayName="NexaCL", CFBundleIdentifier="org.nexacl.launcher",
                          CFBundleExecutable="Nexa.Desktop", CFBundlePackageType="APPL", CFBundleIconFile="Launcher.icns",
                          CFBundleShortVersionString=prefix, CFBundleVersion=prefix, NexaProductVersion=version,
                          NSHighResolutionCapable=True, LSMinimumSystemVersion="12.0"), stream)
    # Ad-hoc signing seals the complete bundle, including all NativeAOT/Skia libraries.
    run("codesign", "--force", "--deep", "--sign", "-", app)
    run("codesign", "--verify", "--deep", "--strict", app)
    run(contents / "MacOS/Nexa.Desktop", "--validate-shell")
    dmg_source = work / "dmg"
    dmg_source.mkdir()
    package_root = work / "macos-root"
    package_root.mkdir()
    shutil.copytree(app, package_root / app.name)
    component_plist = work / "components.plist"
    with component_plist.open("wb") as stream:
        plistlib.dump([dict(RootRelativeBundlePath=app.name, BundleIsRelocatable=False,
                           BundleIsVersionChecked=False, BundleHasStrictIdentifier=True,
                           BundleOverwriteAction="upgrade")], stream)
    run("pkgbuild", "--root", package_root, "--component-plist", component_plist,
        "--identifier", "org.nexacl.launcher", "--version", prefix,
        "--install-location", "/Applications", "--ownership", "recommended", work / "Nexa-component.pkg")
    run("productbuild", "--distribution", ROOT / "eng/release/macos-distribution.xml",
        "--package-path", work, dmg_source / "NexaCL.pkg")
    run("hdiutil", "create", "-volname", "NexaCL", "-srcfolder", dmg_source, "-format", "UDZO", output / f"{base}.dmg")
    run("hdiutil", "verify", output / f"{base}.dmg")
    archive(app, output / f"{base}.portable.tar.gz", app.name)


def linux(payload, output, work, base, version, prefix, arch):
    appdir = work / "Nexa.AppDir"
    shutil.copytree(payload, appdir / "usr/lib/nexacl")
    binary = appdir / "usr/lib/nexacl/Nexa.Desktop"
    binary.chmod(0o755)
    desktop = "[Desktop Entry]\nType=Application\nName=Nexa\nExec=nexacl %U\nIcon=nexacl\nTerminal=false\nCategories=Game;\nStartupWMClass=Nexa.Desktop\n"
    launcher = appdir / "usr/bin/nexacl"
    launcher.parent.mkdir(parents=True)
    launcher.write_text('#!/bin/sh\nexec "$(dirname "$(readlink -f "$0")")/../lib/nexacl/Nexa.Desktop" "$@"\n', encoding="utf-8")
    launcher.chmod(0o755)
    for relative in ("nexacl.desktop", "usr/share/applications/nexacl.desktop"):
        path = appdir / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(desktop, encoding="utf-8")
    for relative in ("nexacl.png", "usr/share/icons/hicolor/256x256/apps/nexacl.png"):
        path = appdir / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(ROOT / "Nexa.Desktop/Assets/icon.png", path)
    (appdir / "AppRun").write_text('#!/bin/sh\nHERE="$(dirname "$(readlink -f "$0")")"\nexec "$HERE/usr/bin/nexacl" "$@"\n', encoding="utf-8")
    (appdir / "AppRun").chmod(0o755)
    run("desktop-file-validate", appdir / "nexacl.desktop")
    for package_type, package_arch in (("deb", "arm64" if arch == "arm64" else "amd64"), ("rpm", "aarch64" if arch == "arm64" else "x86_64")):
        native_version = version.replace(".alpha.", "~alpha.").replace(".beta.", "~beta.").replace(".ci.", "~ci.")
        dependencies = ("libc6", "libgcc-s1", "libstdc++6", "zlib1g", "libfontconfig1", "libx11-6", "libice6", "libsm6") if package_type == "deb" else ("glibc", "libgcc", "libstdc++", "zlib", "fontconfig", "libX11", "libICE", "libSM")
        options = [item for dependency in dependencies for item in ("--depends", dependency)]
        options += [f"--{package_type}-user", "root", f"--{package_type}-group", "root"]
        run("fpm", "-s", "dir", "-t", package_type, "-n", "nexacl", "-v", native_version, "--iteration", "1",
            "-a", package_arch, "--maintainer", "Nexa", "--description", "Nexa Minecraft Launcher",
            "--url", "https://github.com/PCL-N-Edition/PCL-N", "--license", "Apache-2.0", *options,
            "-C", appdir, "-p", output / f"{base}.{package_type}", "usr")
    environment = dict(os.environ, ARCH="aarch64" if arch == "arm64" else "x86_64", APPIMAGE_EXTRACT_AND_RUN="1")
    run(required_tool("APPIMAGETOOL"), "--runtime-file", required_tool("APPIMAGE_RUNTIME"),
        appdir, output / f"{base}.AppImage", env=environment)
    archive(payload, output / f"{base}.portable.tar.gz", "Nexa")
    run("dpkg-deb", "--info", output / f"{base}.deb")
    run("rpm", "-qip", output / f"{base}.rpm")


def validate_payload(payload, platform):
    suffix = ".exe" if platform == "win" else ""
    required = ["Nexa.Desktop" + suffix]
    if platform != "osx":
        required.append("Nexa.Jvm.Host" + suffix)
    for name in required:
        path = payload / name
        if not path.is_file() or path.stat().st_size == 0:
            raise ValueError(f"Release payload is missing {name}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--payload", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--rid", required=True)
    parser.add_argument("--version", required=True)
    args = parser.parse_args()
    from metadata import identity
    data = identity("refs/tags/" + args.version, args.version.rsplit(".", 1)[-1].ljust(40, "0") if ".ci." in args.version else "0" * 40)
    platform, arch = args.rid.split("-")
    if platform not in ("win", "linux", "osx") or arch not in ("x64", "arm64"):
        raise ValueError("Unsupported RID")
    validate_payload(args.payload, platform)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    work = output.parent / f"package-{args.rid}"
    work.mkdir()  # A fresh staging directory prevents stale files entering packages.
    globals()[{"win": "windows", "osx": "macos", "linux": "linux"}[platform]](
        args.payload.resolve(), output, work, f"Nexa-{args.version}-{args.rid}", args.version, data["prefix"], arch)


if __name__ == "__main__":
    main()

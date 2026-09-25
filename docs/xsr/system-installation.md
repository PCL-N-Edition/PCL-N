# Installation scope

All native installers target the machine, not the account that ran the installer.

- Windows EXE: administrative installation into Program Files/NexaCL. Start menu
  and optional desktop shortcuts are shared. Installation mode cannot fall back to
  current-user mode. MSI uses perMachine, HKLM component key paths and the same
  Program Files destination. EXE and MSI remain alternative formats, not two
  packages to install together.
- Linux DEB/RPM: root-owned files under /usr, with system desktop integration.
- macOS DMG: contains a system-domain Installer package, installing Nexa.app in
  /Applications. Relocation and home-domain installation are disabled. The app
  archive remains the portable alternative.
- ZIP, tar.gz and AppImage remain portable artifacts. They do not imply a
  privileged or protected installation.

Settings, credentials, OOBE choices and logs remain per-user application data.
Installers never migrate or delete another account's data. Existing current-user
installations are not silently uninstalled by the new machine-wide packages;
users may remove those separately after verifying the new installation.

Machine-wide installation is a prerequisite, not sufficient proof, of a protected
automatic-update boundary. A privileged updater must still authenticate its own
executable, admit protected roots, and re-verify all untrusted inputs. It must not
elevate an executable staged by the unprivileged launcher.

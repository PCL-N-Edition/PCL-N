#!/usr/bin/env bash
# Build pcln-launcher + pcln-crash-handler for the current runner OS.
# Writes binaries under native/*/ (gitignored) and prints absolute paths via env file.

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out_env="${1:-}"

launcher_dir="$repo_root/native/pcln-launcher"
crash_dir="$repo_root/native/pcln-crash-handler"

is_windows=0
case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*|Windows_NT) is_windows=1 ;;
esac
if [[ "${OS:-}" == "Windows_NT" ]]; then
  is_windows=1
fi

if [[ "$is_windows" -eq 1 ]]; then
  if command -v powershell.exe >/dev/null 2>&1; then
    PS=(powershell.exe -NoProfile -ExecutionPolicy Bypass -File)
  elif command -v pwsh >/dev/null 2>&1; then
    PS=(pwsh -NoProfile -ExecutionPolicy Bypass -File)
  else
    echo "PowerShell is required to build native bootstrap on Windows." >&2
    exit 1
  fi
  "${PS[@]}" "$launcher_dir/build.ps1"
  "${PS[@]}" "$crash_dir/build.ps1"
  launcher_bin="$launcher_dir/pcln-launcher.exe"
  crash_bin="$crash_dir/pcln-crash-handler.exe"
else
  chmod +x "$launcher_dir/build.sh" "$crash_dir/build.sh"
  (cd "$launcher_dir" && ./build.sh)
  (cd "$crash_dir" && ./build.sh)
  launcher_bin="$launcher_dir/pcln-launcher"
  crash_bin="$crash_dir/pcln-crash-handler"
fi

test -s "$launcher_bin"
test -s "$crash_bin"

echo "PCLN_LAUNCHER_BIN=$launcher_bin"
echo "PCLN_CRASH_BIN=$crash_bin"

if [[ -n "$out_env" ]]; then
  {
    echo "PCLN_LAUNCHER_BIN=$launcher_bin"
    echo "PCLN_CRASH_BIN=$crash_bin"
  } >>"$out_env"
fi

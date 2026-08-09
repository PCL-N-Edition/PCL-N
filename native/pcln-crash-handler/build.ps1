# Build pcln-crash-handler for Windows.
# Prefer cl (MSVC) then gcc/clang if available.

param(
    [ValidateSet("x64", "arm64")]
    [string]$Architecture = $(if ($env:PCLN_NATIVE_ARCH) { $env:PCLN_NATIVE_ARCH.ToLowerInvariant() } else { "x64" })
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $here
$out = Join-Path $here "pcln-crash-handler.exe"

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
    $requiredComponent = if ($Architecture -eq "arm64") {
        "Microsoft.VisualStudio.Component.VC.Tools.ARM64"
    } else {
        "Microsoft.VisualStudio.Component.VC.Tools.x86.x64"
    }
    $install = & $vswhere -latest -products * -requires $requiredComponent -property installationPath 2>$null
    if ($install) {
        $bat = Join-Path $install "VC\Auxiliary\Build\vcvarsall.bat"
        if (Test-Path $bat) {
            $vcvarsArgument = if ($Architecture -eq "arm64") { "amd64_arm64" } else { "amd64" }
            cmd /c "`"$bat`" $vcvarsArgument && cd /d `"$here`" && cl /nologo /O2 /utf-8 /Fe:pcln-crash-handler.exe main.c user32.lib shell32.lib /link /SUBSYSTEM:WINDOWS"
            if ($LASTEXITCODE -eq 0 -and (Test-Path $out)) {
                Write-Host "Built $out (MSVC)"
                exit 0
            }
        }
    }
}

$cl = Get-Command cl -ErrorAction SilentlyContinue
if ($cl -and (
    (-not $env:VSCMD_ARG_TGT_ARCH -and $Architecture -eq "x64") -or
    $env:VSCMD_ARG_TGT_ARCH -eq $Architecture)) {
    & cl /nologo /O2 /utf-8 /Fe:$out main.c user32.lib shell32.lib /link /SUBSYSTEM:WINDOWS
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Built $out (MSVC)"
        exit 0
    }
}

$gcc = Get-Command gcc -ErrorAction SilentlyContinue
if ($gcc -and $Architecture -eq "x64") {
    & gcc -O2 -mwindows -o $out main.c -luser32 -lshell32
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Built $out (gcc)"
        exit 0
    }
}

$clang = Get-Command clang -ErrorAction SilentlyContinue
if ($clang -and $Architecture -eq "x64") {
    & clang -O2 -mwindows -o $out main.c -luser32 -lshell32
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Built $out (clang)"
        exit 0
    }
}

Write-Error "No C compiler found for Windows $Architecture. Install MSVC Build Tools or MinGW."
exit 1

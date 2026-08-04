# Build pcln-crash-handler for Windows.
# Prefer cl (MSVC) then gcc/clang if available.

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $here
$out = Join-Path $here "pcln-crash-handler.exe"

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
    $install = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>$null
    if ($install) {
        $bat = Join-Path $install "VC\Auxiliary\Build\vcvars64.bat"
        if (Test-Path $bat) {
            cmd /c "`"$bat`" && cd /d `"$here`" && cl /nologo /O2 /utf-8 /Fe:pcln-crash-handler.exe main.c user32.lib shell32.lib"
            if ($LASTEXITCODE -eq 0 -and (Test-Path $out)) {
                Write-Host "Built $out (MSVC)"
                exit 0
            }
        }
    }
}

$cl = Get-Command cl -ErrorAction SilentlyContinue
if ($cl) {
    & cl /nologo /O2 /utf-8 /Fe:$out main.c user32.lib shell32.lib
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Built $out (MSVC)"
        exit 0
    }
}

$gcc = Get-Command gcc -ErrorAction SilentlyContinue
if ($gcc) {
    & gcc -O2 -o $out main.c
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Built $out (gcc)"
        exit 0
    }
}

$clang = Get-Command clang -ErrorAction SilentlyContinue
if ($clang) {
    & clang -O2 -o $out main.c
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Built $out (clang)"
        exit 0
    }
}

Write-Error "No C compiler found (cl/gcc/clang). Install MSVC Build Tools or MinGW."
exit 1

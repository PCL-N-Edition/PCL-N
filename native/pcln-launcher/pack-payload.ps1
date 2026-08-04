# Assemble payload for pcln-launcher.
#
# Prefer the same zip artifacts AOT used to embed:
#   -NativeRuntimeZip  (PCL.NativeRuntime.<rid>.zip from pack_native_runtime.py)
#   -SidecarZip        (plugin sidecar zip)
# They may be DEFLATED; this script re-encodes them as store (method 0) so the
# C zip reader can extract them, preserving byte-identity only within the store
# rewrite (SHA256 content addressing uses the store zip beside the launcher).
#
# Usage:
#   ./pack-payload.ps1 -HostExe path\to\PCL-N-Host.exe `
#     -NativeRuntimeZip ..\..\artifacts\PCL.NativeRuntime.win-x64.zip `
#     -SidecarZip ..\..\artifacts\sidecar.zip `
#     -CrashExe ..\pcln-crash-handler\pcln-crash-handler.exe `
#     -Output payload.zip

param(
    [Parameter(Mandatory = $true)][string]$HostExe,
    [string]$NativeRuntimeZip = "",
    [string]$SidecarZip = "",
    [string]$NativeDir = "",
    [string]$SidecarDir = "",
    [string]$CrashExe = "",
    [string]$Output = "payload.zip"
)

$ErrorActionPreference = "Stop"

function Convert-ToStoreZip([string]$SourceZip, [string]$DestZip) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path $DestZip) { Remove-Item $DestZip -Force }
    $stage = Join-Path $env:TEMP ("pcln-rezip-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    try {
        [System.IO.Compression.ZipFile]::ExtractToDirectory($SourceZip, $stage)
        $zip = [System.IO.Compression.ZipFile]::Open($DestZip, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            Get-ChildItem $stage -Recurse -File | ForEach-Object {
                $rel = $_.FullName.Substring($stage.Length).TrimStart('\', '/')
                $entry = $zip.CreateEntry($rel.Replace('\', '/'), [System.IO.Compression.CompressionLevel]::NoCompression)
                $es = $entry.Open()
                try {
                    $fs = [System.IO.File]::OpenRead($_.FullName)
                    try { $fs.CopyTo($es) } finally { $fs.Dispose() }
                } finally { $es.Dispose() }
            }
        } finally {
            $zip.Dispose()
        }
    } finally {
        Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function New-StoreZipFromDirectory([string]$SourceDir, [string]$DestZip) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path $DestZip) { Remove-Item $DestZip -Force }
    $zip = [System.IO.Compression.ZipFile]::Open($DestZip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $root = (Resolve-Path $SourceDir).Path
        Get-ChildItem $root -Recurse -File | ForEach-Object {
            $rel = $_.FullName.Substring($root.Length).TrimStart('\', '/')
            $entry = $zip.CreateEntry($rel.Replace('\', '/'), [System.IO.Compression.CompressionLevel]::NoCompression)
            $es = $entry.Open()
            try {
                $fs = [System.IO.File]::OpenRead($_.FullName)
                try { $fs.CopyTo($es) } finally { $fs.Dispose() }
            } finally { $es.Dispose() }
        }
    } finally {
        $zip.Dispose()
    }
}

$stage = Join-Path $env:TEMP ("pcln-payload-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $stage | Out-Null
try {
    New-Item -ItemType Directory -Force -Path (Join-Path $stage "host") | Out-Null
    $hostName = if ($HostExe -match '\.exe$') { "PCL-N-Host.exe" } else { "PCL-N-Host" }
    Copy-Item $HostExe (Join-Path $stage "host\$hostName")

    if ($CrashExe -and (Test-Path $CrashExe)) {
        New-Item -ItemType Directory -Force -Path (Join-Path $stage "crash") | Out-Null
        $crashName = Split-Path $CrashExe -Leaf
        Copy-Item $CrashExe (Join-Path $stage "crash\$crashName")
    }

    # AOT-style zips (converted to store for the C extractor).
    if ($NativeRuntimeZip -and (Test-Path $NativeRuntimeZip)) {
        Convert-ToStoreZip $NativeRuntimeZip (Join-Path $stage "native-runtime.zip")
        Write-Host "Packed native-runtime.zip from $NativeRuntimeZip"
    }
    elseif ($NativeDir -and (Test-Path $NativeDir)) {
        New-StoreZipFromDirectory $NativeDir (Join-Path $stage "native-runtime.zip")
        Write-Host "Packed native-runtime.zip from directory $NativeDir"
    }

    if ($SidecarZip -and (Test-Path $SidecarZip)) {
        Convert-ToStoreZip $SidecarZip (Join-Path $stage "sidecar.zip")
        Write-Host "Packed sidecar.zip from $SidecarZip"
    }
    elseif ($SidecarDir -and (Test-Path $SidecarDir)) {
        New-StoreZipFromDirectory $SidecarDir (Join-Path $stage "sidecar.zip")
        Write-Host "Packed sidecar.zip from directory $SidecarDir"
    }

    if (Test-Path $Output) { Remove-Item $Output -Force }

    # Outer payload.zip also store-only.
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::Open($Output, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        Get-ChildItem $stage -Recurse -File | ForEach-Object {
            $rel = $_.FullName.Substring($stage.Length).TrimStart('\', '/')
            $entry = $zip.CreateEntry($rel.Replace('\', '/'), [System.IO.Compression.CompressionLevel]::NoCompression)
            $es = $entry.Open()
            try {
                $fs = [System.IO.File]::OpenRead($_.FullName)
                try { $fs.CopyTo($es) } finally { $fs.Dispose() }
            } finally { $es.Dispose() }
        }
    } finally {
        $zip.Dispose()
    }

    Write-Host "Packed $Output"
} finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}

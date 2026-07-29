# Copyright (c) 2026 PCL N contributors.
# Publish CoreCLR sidecar and pack a zip for embedding into the host single-file binary.

param(
    [ValidateSet('Debug', 'Release', 'Beta', 'CI')]
    [string]$Configuration = 'Release',
    [Parameter(Mandatory = $true)]
    [string]$Runtime,
    [string]$OutputZip = '',
    [string]$PluginTag = '',
    [bool]$SelfContained = $true,
    [switch]$SkipFetch
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $repoRoot "artifacts\sidecar-embed-$Runtime"
if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $stage | Out-Null

$mapConfig = if ($Configuration -in @('Beta', 'CI')) { 'Release' } else { $Configuration }

& (Join-Path $PSScriptRoot 'build-plugin-sidecar.ps1') `
    -Configuration $mapConfig `
    -Runtime $Runtime `
    -Output $stage `
    -PluginTag $PluginTag `
    -SelfContained:$SelfContained `
    -SkipFetch:$SkipFetch `
    -Publish
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exeName = if ($Runtime.StartsWith('win-')) { 'PCL.Plugin.Sidecar.exe' } else { 'PCL.Plugin.Sidecar' }
$exePath = Join-Path $stage $exeName
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Sidecar executable missing after publish: $exePath"
}

# Symbols are useful in CI artifacts, but the embedded runtime payload must not
# carry managed or native NuGet PDBs (Skia/HarfBuzz alone exceed 100 MB raw).
$symbols = @(Get-ChildItem -LiteralPath $stage -Recurse -File -Filter '*.pdb')
$symbolBytes = ($symbols | Measure-Object -Property Length -Sum).Sum
foreach ($symbol in $symbols) {
    Remove-Item -LiteralPath $symbol.FullName -Force
}
if ($symbols.Count -gt 0) {
    Write-Host "Removed $($symbols.Count) sidecar symbols ($([math]::Round($symbolBytes / 1MB, 1)) MB raw)."
}

# VideoLAN.LibVLC.Windows defaults to copying every native architecture for
# non-RID project references. Keep only the distribution matching this payload.
$libVlcRoot = Join-Path $stage 'libvlc'
$expectedVlcDirectory = if ($Runtime -in @('win-x64', 'win-x86', 'win-arm64')) { $Runtime } else { '' }
if (Test-Path -LiteralPath $libVlcRoot) {
    foreach ($directory in @(Get-ChildItem -LiteralPath $libVlcRoot -Directory)) {
        if ([string]::IsNullOrWhiteSpace($expectedVlcDirectory) -or
            -not [string]::Equals($directory.Name, $expectedVlcDirectory, [StringComparison]::OrdinalIgnoreCase)) {
            Write-Host "Removing non-target LibVLC runtime: $($directory.Name)"
            Remove-Item -LiteralPath $directory.FullName -Recurse -Force
        }
    }
}

if ([string]::IsNullOrWhiteSpace($OutputZip)) {
    $OutputZip = Join-Path $repoRoot "artifacts\PCL.Plugin.Sidecar.$Runtime.zip"
}

$zipDir = Split-Path -Parent $OutputZip
if (-not [string]::IsNullOrWhiteSpace($zipDir)) {
    New-Item -ItemType Directory -Force -Path $zipDir | Out-Null
}
if (Test-Path -LiteralPath $OutputZip) {
    Remove-Item -LiteralPath $OutputZip -Force
}

# Compress contents of stage (not the stage folder itself) so extract roots at the exe.
if (Get-Command Compress-Archive -ErrorAction SilentlyContinue) {
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $OutputZip -CompressionLevel Optimal -Force
} else {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $OutputZip)
}

if (-not (Test-Path -LiteralPath $OutputZip) -or ((Get-Item $OutputZip).Length -lt 1024)) {
    throw "Sidecar zip is missing or too small: $OutputZip"
}

Write-Host "Sidecar zip: $OutputZip ($([math]::Round((Get-Item $OutputZip).Length / 1MB, 1)) MB)"
Write-Host "Sidecar runtime: $(if ($SelfContained) { 'included' } else { 'not included (.NET 10 required)' })"
Write-Host "Host embed: -p:PclPluginSidecarZipPath=$OutputZip"
exit 0

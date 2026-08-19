# Copyright (c) 2026 PCL N contributors.
# Build/publish PCL.Plugin Sidecar full-package updates for Cloudflare.
# Sidecar updates intentionally do NOT use FastCDC block maps — only the zip +
# channels/plugin.json marker are uploaded to R2.
#
# Example:
#   .\scripts\publish_plugin_sidecar_cas.ps1 `
#     -Tag v0.20.1 -Version 0.20.1 -Runtime win-x64 `
#     -ReleaseNotesFile notes.md -Upload

param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Runtime = 'win-x64',
    [ValidateSet('SelfContained', 'NoRuntime')]
    [string]$RuntimeVariant = 'SelfContained',
    [string]$Configuration = 'Release',
    [string]$Channel = 'plugin',
    [string]$CommitSha = '',
    [string]$ReleaseNotes = '',
    [string]$ReleaseNotesFile = '',
    [string]$ReleaseNotesUrl = '',
    [string]$PluginTag = '',
    [switch]$Upload,
    [switch]$SkipFetch,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$normalizedTag = $Tag.Trim()
if (-not $normalizedTag.StartsWith('v')) { $normalizedTag = "v$normalizedTag" }
$normalizedVersion = $Version.Trim().TrimStart('v', 'V')
if ([string]::IsNullOrWhiteSpace($CommitSha)) {
    $CommitSha = (git -C $repoRoot rev-parse HEAD).Trim()
}
if (-not [string]::IsNullOrWhiteSpace($ReleaseNotesFile) -and (Test-Path -LiteralPath $ReleaseNotesFile)) {
    $ReleaseNotes = Get-Content -LiteralPath $ReleaseNotesFile -Raw
}

$selfContained = $RuntimeVariant -eq 'SelfContained'
$stem = "PCL_Plugin_Sidecar_${Runtime}_${RuntimeVariant}"
$outDir = Join-Path $repoRoot "artifacts\plugin-sidecar\$normalizedTag"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$zipPath = Join-Path $outDir "$stem.zip"

if (-not $SkipBuild) {
    $packScript = Join-Path $PSScriptRoot 'pack-plugin-sidecar-zip.ps1'
    & $packScript `
        -Configuration $Configuration `
        -Runtime $Runtime `
        -OutputZip $zipPath `
        -PluginTag $(if ($PluginTag) { $PluginTag } else { $normalizedTag }) `
        -SelfContained:$selfContained `
        -SkipFetch:$SkipFetch
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if (-not (Test-Path -LiteralPath $zipPath)) {
    throw "Sidecar zip missing: $zipPath"
}

$sha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item -LiteralPath $zipPath).Length
$metaPath = Join-Path $outDir "$stem.build.json"
$buildMeta = [ordered]@{
    formatVersion  = 1
    product        = 'plugin-sidecar'
    tag            = $normalizedTag
    version        = $normalizedVersion
    channel        = $Channel
    commitSha      = $CommitSha.ToLowerInvariant()
    runtimeId      = $Runtime
    runtimeVariant = $RuntimeVariant
    artifact       = "$stem.zip"
    packageSha256  = $sha256
    packageSize    = $size
    delivery       = 'full-package'
    builtAt        = (Get-Date).ToUniversalTime().ToString('o')
}
$buildMeta | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $metaPath -Encoding utf8

$channelPath = Join-Path $outDir 'plugin-channel.json'
$marker = [ordered]@{
    formatVersion   = 1
    tag             = $normalizedTag
    version         = $normalizedVersion
    channel         = $Channel
    commitSha       = $CommitSha.ToLowerInvariant()
    publishedAt     = (Get-Date).ToUniversalTime().ToString('o')
    manifestKey     = "channels/$Channel.json"
    releaseNotes    = $ReleaseNotes
    releaseNotesUrl = $ReleaseNotesUrl
    packageSha256   = $sha256
    packageSize     = $size
    delivery        = 'full-package'
}
$marker | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $channelPath -Encoding utf8

Write-Host "Prepared Sidecar full-package update under $outDir"
Write-Host "  zip:     $zipPath"
Write-Host "  sha256:  $sha256"
Write-Host "  size:    $size"
Write-Host "  meta:    $metaPath"
Write-Host "  channel: $channelPath"

if (-not $Upload) {
    Write-Host "Skip upload (pass -Upload to push to R2)."
    exit 0
}

$releasePrefix = "releases/$normalizedTag"
python (Join-Path $PSScriptRoot 'upload_r2_cas.py') put-files `
    --dir $outDir `
    --prefix $releasePrefix `
    --include "*.zip" `
    --include "*.build.json"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

python (Join-Path $PSScriptRoot 'upload_r2_cas.py') put `
    "channels/$Channel.json" `
    $channelPath `
    --content-type application/json
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Uploaded Sidecar channel marker channels/$Channel.json (full-package, no blocks)."

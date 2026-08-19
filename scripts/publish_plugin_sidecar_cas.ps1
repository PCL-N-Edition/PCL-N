# Copyright (c) 2026 PCL N contributors.
# Build/publish PCL.Plugin Sidecar CAS for Cloudflare (channels/plugin.json).
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
$outDir = Join-Path $repoRoot "artifacts\plugin-cas\$normalizedTag"
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

$casRoot = Join-Path $outDir 'cas'
New-Item -ItemType Directory -Force -Path $casRoot | Out-Null
python (Join-Path $PSScriptRoot 'generate_update_blockmap.py') `
    --archive $zipPath `
    --output $casRoot `
    --target-tag $normalizedTag `
    --target-version $normalizedVersion `
    --runtime-id $Runtime `
    --runtime-variant $RuntimeVariant `
    --configuration $Configuration `
    --profile v2
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$generatedMaps = @(Get-ChildItem -LiteralPath $casRoot -Filter "$stem.blockmap*.json" -File -ErrorAction SilentlyContinue)
if ($generatedMaps.Count -eq 0) {
    $generatedMaps = @(Get-ChildItem -LiteralPath $casRoot -Filter '*.blockmap*.json' -File -ErrorAction SilentlyContinue)
}
foreach ($map in $generatedMaps) {
    Copy-Item -LiteralPath $map.FullName -Destination (Join-Path $outDir $map.Name) -Force
}
$blockmap = Join-Path $outDir "$stem.blockmap.v2.json"
if (-not (Test-Path -LiteralPath $blockmap) -and $generatedMaps.Count -gt 0) {
    $blockmap = Join-Path $outDir $generatedMaps[0].Name
}

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
}
$marker | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $channelPath -Encoding utf8

Write-Host "Prepared Sidecar CAS under $outDir"
Write-Host "  zip:      $zipPath"
Write-Host "  blockmap: $blockmap"
Write-Host "  cas:      $casRoot"
Write-Host "  channel:  $channelPath"

if (-not $Upload) {
    Write-Host "Skip upload (pass -Upload to push to R2)."
    exit 0
}

$releasePrefix = "releases/$normalizedTag"
python (Join-Path $PSScriptRoot 'upload_r2_cas.py') put-files `
    --dir $outDir `
    --prefix $releasePrefix `
    --include "*.zip" `
    --include "*.blockmap.v2.json" `
    --include "*.blockmap.v2.json.asc"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Blocks live beside the blockmap generator output when using upload-tree from a CAS root.
# Prefer uploading the blockmap-linked tree if present.
$casTree = Join-Path $outDir 'cas'
if (Test-Path -LiteralPath (Join-Path $casTree 'block')) {
    python (Join-Path $PSScriptRoot 'upload_r2_cas.py') upload-tree --root $casTree
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

python (Join-Path $PSScriptRoot 'upload_r2_cas.py') put `
    "channels/$Channel.json" `
    $channelPath `
    --content-type application/json
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Uploaded Sidecar channel marker channels/$Channel.json"

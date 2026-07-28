# Copyright (c) 2026 PCL N contributors.
# Apply source-overlay inject (optional fetch) and run Desktop with plugin sources compiled in.

param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$Tag = '',
    [switch]$SkipFetch,
    [switch]$SkipOverlay
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $SkipOverlay) {
    $overlayArgs = @{}
    if (-not [string]::IsNullOrWhiteSpace($Tag)) { $overlayArgs['Tag'] = $Tag }
    if ($SkipFetch) { $overlayArgs['SkipFetch'] = $true }
    & (Join-Path $PSScriptRoot 'apply-plugin-overlay.ps1') @overlayArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$pluginProject = Join-Path $repoRoot 'PCL.Plugin\PCL.Plugin.csproj'
if (-not (Test-Path -LiteralPath $pluginProject -PathType Leaf)) {
    throw "PCL.Plugin not found at $pluginProject. Run apply-plugin-overlay.ps1 or clone the private plugin repo."
}

$overlayRewrite = Join-Path $repoRoot 'PCL.Desktop\Hosting\DesktopHost.Optional.cs'
$rewriteMarker = 'PclPluginHostModule'
if (-not (Select-String -LiteralPath $overlayRewrite -Pattern $rewriteMarker -Quiet)) {
    Write-Warning "Host rewrite not applied (DesktopHost.Optional.cs has no PclPluginHostModule). Re-run apply-plugin-overlay.ps1."
}

dotnet run --project (Join-Path $repoRoot 'PCL.Desktop\PCL.Desktop.csproj') `
    -c $Configuration `
    -p:PclWithPlugin=true `
    -m:1 `
    -nodeReuse:false
exit $LASTEXITCODE

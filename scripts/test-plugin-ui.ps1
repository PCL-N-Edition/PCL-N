# Copyright (c) 2026 PCL N contributors.
# Headless tests against a Desktop build with source-overlay plugin inject.

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
    throw "PCL.Plugin not found at $pluginProject. Run apply-plugin-overlay.ps1 first."
}

# Build Desktop with plugin sources compiled in (defines PclIncludesPlugin for tests).
dotnet build (Join-Path $repoRoot 'PCL.Desktop\PCL.Desktop.csproj') `
    -c $Configuration `
    -p:PclWithPlugin=true `
    -m:1 `
    -nodeReuse:false `
    -warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Plugin unit tests still build the standalone plugin project against the host.
dotnet test (Join-Path $repoRoot 'PCL.Plugin\PCL.Plugin.Test\PCL.Plugin.Test.csproj') `
    -c $Configuration `
    -p:PclNRoot=$repoRoot `
    --no-restore
if ($LASTEXITCODE -ne 0) {
    # restore then retest if needed
    dotnet test (Join-Path $repoRoot 'PCL.Plugin\PCL.Plugin.Test\PCL.Plugin.Test.csproj') `
        -c $Configuration `
        -p:PclNRoot=$repoRoot
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# Desktop headless suite — filter plugin-related when available.
dotnet test (Join-Path $repoRoot 'PCL.Desktop.Test\PCL.Desktop.Test.csproj') `
    -c $Configuration `
    -p:PclWithPlugin=true `
    --filter 'FullyQualifiedName~Plugin|FullyQualifiedName~HostSettings|FullyQualifiedName~DesktopArchitecture'
exit $LASTEXITCODE

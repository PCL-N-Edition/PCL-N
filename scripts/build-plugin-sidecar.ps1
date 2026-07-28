# Copyright (c) 2026 PCL N contributors.
# Build CoreCLR PCL.Plugin.Sidecar for out-of-process inject (host stays AOT-capable).

param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Runtime = '',
    [string]$Output = '',
    [string]$PluginTag = '',
    [switch]$SkipFetch,
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$pluginRoot = Join-Path $repoRoot 'PCL.Plugin'
$sidecarProject = Join-Path $pluginRoot 'PCL.Plugin.Sidecar\PCL.Plugin.Sidecar.csproj'
$sdkRoot = Join-Path (Split-Path -Parent $repoRoot) 'PCL-N-Plugin-SDK'
if (-not (Test-Path $sdkRoot)) {
    $sdkRoot = Join-Path $repoRoot 'PCL-N-Plugin-SDK'
}

if (-not $SkipFetch -or -not (Test-Path $sidecarProject)) {
    $overlay = @{ Channel = 'Stable'; SkipRewrite = $true }
    if ($PluginTag) { $overlay['Tag'] = $PluginTag }
    if ($SkipFetch) { $overlay['SkipFetch'] = $true }
    # Fetch plugin source tag only; do not rewrite host for in-process compile.
    & (Join-Path $PSScriptRoot 'apply-plugin-overlay.ps1') @overlay
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if (-not (Test-Path -LiteralPath $sidecarProject)) {
    throw "Sidecar project missing: $sidecarProject. Ensure PCL.Plugin tag includes PCL.Plugin.Sidecar/."
}

if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $repoRoot "artifacts\sidecar$(if ($Runtime) { "-$Runtime" })"
}

$common = @(
    $sidecarProject,
    '-c', $Configuration,
    "-p:PclNRoot=$repoRoot",
    "-p:PclNPluginSdkRoot=$sdkRoot",
    '-m:1',
    '--nologo'
)

if ($Publish) {
    if ([string]::IsNullOrWhiteSpace($Runtime)) {
        throw "Publish requires -Runtime (e.g. win-x64)."
    }
    & dotnet publish @common `
        -r $Runtime `
        --self-contained true `
        -p:PublishAot=false `
        -p:PublishTrimmed=false `
        -p:PublishSingleFile=false `
        -o $Output
} else {
    & dotnet build @common
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $built = Join-Path $pluginRoot "PCL.Plugin.Sidecar\bin\$Configuration\net10.0"
    New-Item -ItemType Directory -Force -Path $Output | Out-Null
    Copy-Item -Path (Join-Path $built '*') -Destination $Output -Recurse -Force
}

Write-Host "Sidecar output: $Output"
Write-Host "Host resolves: {appBase}/sidecar/PCL.Plugin.Sidecar(.exe) or PCL_PLUGIN_SIDECAR_PATH"
exit $LASTEXITCODE

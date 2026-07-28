# Copyright (c) 2026 PCL N contributors.
# Reliable local Desktop build — avoids MSB4166 multi-node crashes and file locks.

param(
    [string]$Configuration = "Debug",
    [switch]$Publish,
    [string]$Runtime = "win-x64",
    [switch]$WriteSecrets,
    # Native AOT host-only publish. Direct-run multi-file; no single-file self-extract.
    [switch]$Aot,
    # Compile PCL.Plugin sources into Desktop after apply-plugin-overlay.ps1 (source-overlay inject).
    [switch]$WithPlugin,
    [string]$PluginTag = '',
    [switch]$SkipPluginFetch
)

$ErrorActionPreference = "Stop"

# Stop running app that locks bin outputs.
Get-Process PCL.Desktop -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

$env:MSBUILDDISABLENODEREUSE = "1"
$env:DOTNET_CLI_UI_LANGUAGE = "en"

$project = Join-Path $PSScriptRoot "..\PCL.Desktop\PCL.Desktop.csproj"

if ($WriteSecrets) {
    $env:PCL_WRITE_SECRET = "1"
}

if ($WithPlugin) {
    if ($Aot) {
        throw "WithPlugin builds disable AOT (Harmony / AssemblyLoadContext). Omit -Aot."
    }
    $overlayArgs = @{}
    if (-not [string]::IsNullOrWhiteSpace($PluginTag)) { $overlayArgs['Tag'] = $PluginTag }
    if ($SkipPluginFetch) { $overlayArgs['SkipFetch'] = $true }
    & (Join-Path $PSScriptRoot 'apply-plugin-overlay.ps1') @overlayArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$common = @(
    $project,
    "-c", $Configuration,
    "-m:1",
    "-nodeReuse:false",
    "--nologo"
)

if ($WithPlugin) {
    $common += "-p:PclWithPlugin=true"
}

if ($Publish) {
    if ($Aot) {
        & dotnet publish @common `
            -r $Runtime `
            --self-contained true `
            -p:PublishAot=true `
            -p:PublishTrimmed=true `
            -p:PublishSingleFile=false `
            -p:DebugType=None `
            -p:DebugSymbols=false `
            -p:PclWriteSecret=1
    } else {
        & dotnet publish @common `
            -r $Runtime `
            --self-contained true `
            -p:PublishAot=false `
            -p:PublishTrimmed=false `
            -p:PublishSingleFile=true `
            -p:PclWriteSecret=1
    }
} else {
    & dotnet build @common
}

exit $LASTEXITCODE

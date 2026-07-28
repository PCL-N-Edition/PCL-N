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

# -WithPlugin builds/publishes the CoreCLR sidecar next to the host (host may remain AOT).
# In-process PclWithPlugin compile-into-Desktop is no longer the product path.
if ($WithPlugin) {
    $sidecarArgs = @{
        Configuration = $Configuration
        SkipFetch     = $SkipPluginFetch
    }
    if (-not [string]::IsNullOrWhiteSpace($PluginTag)) { $sidecarArgs['PluginTag'] = $PluginTag }
    if ($Publish) {
        $sidecarArgs['Publish'] = $true
        $sidecarArgs['Runtime'] = $Runtime
        $sidecarArgs['Output'] = Join-Path $PSScriptRoot "..\artifacts\desktop-$Runtime\sidecar"
    } else {
        $sidecarArgs['Output'] = Join-Path $PSScriptRoot "..\PCL.Desktop\bin\$Configuration\net10.0\sidecar"
    }
    & (Join-Path $PSScriptRoot 'build-plugin-sidecar.ps1') @sidecarArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$common = @(
    $project,
    "-c", $Configuration,
    "-m:1",
    "-nodeReuse:false",
    "--nologo"
)

if ($Publish) {
    $outDir = Join-Path $PSScriptRoot "..\artifacts\desktop-$Runtime"
    if ($Aot) {
        & dotnet publish @common `
            -r $Runtime `
            --self-contained true `
            -p:PublishAot=true `
            -p:PublishTrimmed=true `
            -p:PublishSingleFile=false `
            -p:DebugType=None `
            -p:DebugSymbols=false `
            -p:PclWriteSecret=1 `
            -o $outDir
    } else {
        & dotnet publish @common `
            -r $Runtime `
            --self-contained true `
            -p:PublishAot=false `
            -p:PublishTrimmed=false `
            -p:PublishSingleFile=true `
            -p:PclWriteSecret=1 `
            -o $outDir
    }
    if ($LASTEXITCODE -eq 0 -and $WithPlugin) {
        $sidecarSrc = Join-Path $outDir 'sidecar'
        if (Test-Path $sidecarSrc) {
            Write-Host "Sidecar staged at $sidecarSrc (host resolves sidecar/ next to publish output)."
        }
    }
} else {
    & dotnet build @common
    if ($LASTEXITCODE -eq 0 -and $WithPlugin) {
        $devSidecar = Join-Path $PSScriptRoot "..\PCL.Desktop\bin\$Configuration\net10.0\sidecar"
        if (Test-Path $devSidecar) {
            Write-Host "Dev sidecar at $devSidecar"
        }
    }
}

exit $LASTEXITCODE

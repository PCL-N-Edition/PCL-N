# Copyright (c) 2026 PCL N contributors.
# Build CoreCLR PCL.Plugin.Sidecar for out-of-process inject (host stays AOT-capable).

param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Runtime = '',
    [string]$Output = '',
    [string]$PluginTag = '',
    [bool]$SelfContained = $true,
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

function Invoke-PclPluginSidecarObfuscation {
    param(
        [Parameter(Mandatory = $true)][string]$PublishDir,
        [Parameter(Mandatory = $true)][string]$PluginRoot
    )

    $pluginDll = Join-Path $PublishDir 'PCL.Plugin.dll'
    $sidecarDll = Join-Path $PublishDir 'PCL.Plugin.Sidecar.dll'
    if (-not (Test-Path -LiteralPath $pluginDll)) {
        Write-Warning "Skip obfuscation: PCL.Plugin.dll missing under $PublishDir"
        return
    }

    $template = Join-Path $PluginRoot 'obfuscar\sidecar.release.xml'
    if (-not (Test-Path -LiteralPath $template)) {
        Write-Warning "Skip obfuscation: missing $template"
        return
    }

    $toolManifest = Join-Path $PluginRoot '.config\dotnet-tools.json'
    Push-Location $PluginRoot
    try {
        if (-not (Test-Path $toolManifest)) {
            & dotnet new tool-manifest --force | Out-Null
            & dotnet tool install Obfuscar.GlobalTool --version 2.2.49 | Out-Null
        } else {
            & dotnet tool restore | Out-Null
        }

        $work = Join-Path $PublishDir '_obfuscar_work'
        $out = Join-Path $PublishDir '_obfuscar_out'
        Remove-Item -Recurse -Force $work, $out -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force -Path $work, $out | Out-Null
        Copy-Item $pluginDll $work -Force
        if (Test-Path $sidecarDll) { Copy-Item $sidecarDll $work -Force }

        $cfgPath = Join-Path $work 'obfuscar.xml'
        $xml = Get-Content -Raw -LiteralPath $template
        $xml = $xml.Replace('value="."', "value=`"$($work.Replace('\', '/'))`"")
        $xml = $xml.Replace('value="./obfuscar-out"', "value=`"$($out.Replace('\', '/'))`"")
        if (-not (Test-Path (Join-Path $work 'PCL.Plugin.Sidecar.dll'))) {
            $xml = $xml -replace '(?s)<Module file="\$\(InPath\)/PCL\.Plugin\.Sidecar\.dll">.*?</Module>', ''
        }
        Set-Content -LiteralPath $cfgPath -Value $xml -Encoding UTF8

        & dotnet tool run obfuscar.console -- $cfgPath
        if ($LASTEXITCODE -ne 0) {
            throw "Obfuscar failed with exit code $LASTEXITCODE"
        }

        Get-ChildItem -LiteralPath $out -Filter '*.dll' -ErrorAction SilentlyContinue | ForEach-Object {
            Copy-Item $_.FullName (Join-Path $PublishDir $_.Name) -Force
        }
        Write-Host "Obfuscar applied to plugin assemblies under $PublishDir"
    }
    finally {
        Pop-Location
        Remove-Item -Recurse -Force (Join-Path $PublishDir '_obfuscar_work'), (Join-Path $PublishDir '_obfuscar_out') -ErrorAction SilentlyContinue
        Get-ChildItem -LiteralPath $PublishDir -Filter '*.pdb' -Recurse -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }
}

if (-not $SkipFetch -or -not (Test-Path $sidecarProject)) {
    $overlay = @{ Channel = 'Latest'; SkipRewrite = $true }
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
    '-p:PclPluginHeadless=true',
    '-m:1',
    '--nologo'
)

if ($Publish) {
    if ([string]::IsNullOrWhiteSpace($Runtime)) {
        throw "Publish requires -Runtime (e.g. win-x64)."
    }
    $selfContainedValue = $SelfContained.ToString().ToLowerInvariant()
    & dotnet publish @common `
        -r $Runtime `
        --self-contained $selfContainedValue `
        -p:PublishAot=false `
        -p:PublishTrimmed=false `
        -p:PublishSingleFile=false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $Output
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    if ($Configuration -eq 'Release') {
        Invoke-PclPluginSidecarObfuscation -PublishDir $Output -PluginRoot $pluginRoot
    }
} else {
    & dotnet build @common
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $built = Join-Path $pluginRoot "PCL.Plugin.Sidecar\bin\$Configuration\net10.0"
    New-Item -ItemType Directory -Force -Path $Output | Out-Null
    Copy-Item -Path (Join-Path $built '*') -Destination $Output -Recurse -Force
}

Write-Host "Sidecar output: $Output"
Write-Host "Sidecar runtime: $(if ($SelfContained) { 'self-contained CoreCLR' } else { 'framework-dependent (.NET 10 required)' })"
Write-Host "Host resolves: {appBase}/sidecar/PCL.Plugin.Sidecar(.exe) or PCL_PLUGIN_SIDECAR_PATH"
Write-Host "Product policy: no PDBs; Release publish runs Obfuscar on PCL.Plugin*.dll (no host symbol table)."
exit $LASTEXITCODE

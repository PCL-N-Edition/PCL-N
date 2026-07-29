# Copyright (c) 2026 PCL N contributors.
# Reliable local Desktop build — avoids MSB4166 multi-node crashes and file locks.

param(
    [string]$Configuration = "Debug",
    [switch]$Publish,
    [string]$Runtime = "win-x64",
    [switch]$WriteSecrets,
    # Legacy compatibility switch. Publish is always NativeAOT now.
    [switch]$Aot,
    # Compile PCL.Plugin sources into Desktop after apply-plugin-overlay.ps1 (source-overlay inject).
    [switch]$WithPlugin,
    # Keep the plugin sidecar framework-dependent (.NET 10 required).
    [switch]$PluginNoRuntime,
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

# -WithPlugin embeds the CoreCLR sidecar into the NativeAOT host.
# In-process PclWithPlugin compile-into-Desktop is no longer the product path.
$sidecarZip = ''
if ($WithPlugin) {
    if ($Publish) {
        $sidecarZip = Join-Path $PSScriptRoot "..\artifacts\PCL.Plugin.Sidecar.$Runtime.local.zip"
        $sidecarArgs = @{
            Configuration = $Configuration
            Runtime       = $Runtime
            OutputZip     = $sidecarZip
            SelfContained = -not $PluginNoRuntime
            SkipFetch     = $SkipPluginFetch
        }
        if (-not [string]::IsNullOrWhiteSpace($PluginTag)) { $sidecarArgs['PluginTag'] = $PluginTag }
        & (Join-Path $PSScriptRoot 'pack-plugin-sidecar-zip.ps1') @sidecarArgs
    } else {
        $sidecarArgs = @{
            Configuration = $Configuration
            Output        = Join-Path $PSScriptRoot "..\PCL.Desktop\bin\$Configuration\net10.0\sidecar"
            SkipFetch     = $SkipPluginFetch
        }
        if (-not [string]::IsNullOrWhiteSpace($PluginTag)) { $sidecarArgs['PluginTag'] = $PluginTag }
        & (Join-Path $PSScriptRoot 'build-plugin-sidecar.ps1') @sidecarArgs
    }
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
    $nativeStage = Join-Path $PSScriptRoot "..\artifacts\desktop-$Runtime-native-stage"
    $nativeZip = Join-Path $PSScriptRoot "..\artifacts\PCL.NativeRuntime.$Runtime.local.zip"
    $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    foreach ($directory in @($outDir, $nativeStage)) {
        $full = [IO.Path]::GetFullPath($directory)
        $artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts')) +
            [IO.Path]::DirectorySeparatorChar
        if (-not $full.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean publish directory outside artifacts: $full"
        }
        if (Test-Path -LiteralPath $full) {
            Remove-Item -LiteralPath $full -Recurse -Force
        }
        New-Item -ItemType Directory -Force -Path $full | Out-Null
    }

    & dotnet publish @common `
        -r $Runtime `
        --self-contained true `
        -p:PublishAot=true `
        -p:PublishTrimmed=true `
        -p:PublishSingleFile=false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:PclWriteSecret=1 `
        -p:PclPluginSidecarZipPath= `
        -p:PclNativeRuntimeZipPath= `
        -o $nativeStage
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $binaryName = if ($Runtime.StartsWith('win-')) { 'PCL-N-Edition.exe' } else { 'PCL-N-Edition' }
    & python (Join-Path $PSScriptRoot 'pack_native_runtime.py') `
        $nativeStage `
        $nativeZip `
        --binary $binaryName
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $embedArgs = @("-p:PclNativeRuntimeZipPath=$nativeZip")
    if (-not [string]::IsNullOrWhiteSpace($sidecarZip)) {
        $embedArgs += "-p:PclPluginSidecarZipPath=$sidecarZip"
    }

    & dotnet publish @common `
        -r $Runtime `
        --self-contained true `
        -p:PublishAot=true `
        -p:PublishTrimmed=true `
        -p:PublishSingleFile=false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:PclWriteSecret=1 `
        @embedArgs `
        -o $outDir
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    Get-ChildItem -LiteralPath $outDir -Force |
        Where-Object { $_.Name -ne $binaryName } |
        Remove-Item -Recurse -Force
    Write-Host "NativeAOT desktop output: $(Join-Path $outDir $binaryName)"
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

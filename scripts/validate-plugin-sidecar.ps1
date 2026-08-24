# Copyright (c) 2026 PCL N contributors.
# Validate that a sidecar publish really matches the advertised runtime variant.

param(
    [Parameter(Mandatory = $true)]
    [string]$Stage,
    [Parameter(Mandatory = $true)]
    [string]$Runtime,
    [Parameter(Mandatory = $true)]
    [bool]$SelfContained,
    [switch]$WriteMarker
)

$ErrorActionPreference = 'Stop'
$runtimeConfigPath = Join-Path $Stage 'PCL.Plugin.Sidecar.runtimeconfig.json'
if (-not (Test-Path -LiteralPath $runtimeConfigPath)) {
    throw "Sidecar runtime config missing: $runtimeConfigPath"
}

$runtimeConfig = Get-Content -Raw -LiteralPath $runtimeConfigPath | ConvertFrom-Json
$runtimeOptions = $runtimeConfig.runtimeOptions
if ($null -eq $runtimeOptions) {
    throw "Sidecar runtime config has no runtimeOptions: $runtimeConfigPath"
}

$framework = $runtimeOptions.PSObject.Properties['framework']
$includedFrameworks = $runtimeOptions.PSObject.Properties['includedFrameworks']
$nativeRuntimeFiles = if ($Runtime.StartsWith('win-')) {
    @('hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll')
} elseif ($Runtime.StartsWith('osx-')) {
    @('libhostfxr.dylib', 'libhostpolicy.dylib', 'libcoreclr.dylib')
} else {
    @('libhostfxr.so', 'libhostpolicy.so', 'libcoreclr.so')
}
if ($SelfContained) {
    if ($null -ne $framework -and $null -ne $framework.Value) {
        throw "SelfContained sidecar is framework-dependent: $runtimeConfigPath"
    }
    if ($null -eq $includedFrameworks -or @($includedFrameworks.Value).Count -eq 0) {
        throw "SelfContained sidecar has no includedFrameworks: $runtimeConfigPath"
    }

    foreach ($fileName in $nativeRuntimeFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $Stage $fileName))) {
            throw "SelfContained sidecar runtime file missing: $fileName under $Stage"
        }
    }
} else {
    if ($null -eq $framework -or $null -eq $framework.Value) {
        throw "NoRuntime sidecar does not declare a framework dependency: $runtimeConfigPath"
    }
    if ($null -ne $includedFrameworks -and @($includedFrameworks.Value).Count -gt 0) {
        throw "NoRuntime sidecar unexpectedly includes a framework: $runtimeConfigPath"
    }
    foreach ($fileName in $nativeRuntimeFiles) {
        if (Test-Path -LiteralPath (Join-Path $Stage $fileName)) {
            throw "NoRuntime sidecar contains app-local runtime host '$fileName'; it would shadow the installed .NET runtime."
        }
    }
}

$variant = if ($SelfContained) { 'SelfContained' } else { 'NoRuntime' }
if ($WriteMarker) {
    Set-Content -LiteralPath (Join-Path $Stage 'pcln-sidecar-runtime') -Value $variant -Encoding ascii -NoNewline
}
Write-Host "Validated sidecar runtime: $variant ($Runtime)"

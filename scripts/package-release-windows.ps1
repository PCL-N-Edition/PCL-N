# Copyright (c) 2026 PCL N contributors.
# Licensed under the Apache License, Version 2.0.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactDirectory,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [Parameter(Mandatory = $true)]
    [string]$BaseName,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifact = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
$sourceExecutable = Join-Path $artifact 'PCL-N-Edition.exe'
if (-not (Test-Path -LiteralPath $sourceExecutable -PathType Leaf)) {
    throw "Published launcher is missing: $sourceExecutable"
}

$match = [regex]::Match($Version, '(?<!\d)(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?')
if (-not $match.Success) {
    throw "Version '$Version' cannot be converted to a Windows installer version."
}
$productVersion = '{0}.{1}.{2}' -f $match.Groups[1].Value, $match.Groups[2].Value, $match.Groups[3].Value

New-Item -ItemType Directory -Force -Path $output | Out-Null
$working = Join-Path ([System.IO.Path]::GetTempPath()) ("pcln-package-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $working | Out-Null

try {
    $portable = Join-Path $output "${BaseName}_Portable.exe"
    Copy-Item -LiteralPath $sourceExecutable -Destination $portable

    $canonical = Join-Path $output "${BaseName}.zip"
    if (Test-Path -LiteralPath $canonical) { Remove-Item -LiteralPath $canonical -Force }
    Compress-Archive -Path (Join-Path $artifact '*') -DestinationPath $canonical -CompressionLevel Optimal

    $msiMarker = Join-Path $working 'msi-install-kind'
    $exeMarker = Join-Path $working 'exe-install-kind'
    [System.IO.File]::WriteAllText($msiMarker, "windows-msi`n", [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText($exeMarker, "windows-exe`n", [System.Text.UTF8Encoding]::new($false))

    $wix = Get-Command wix -ErrorAction Stop
    $wixSource = Join-Path $repoRoot 'installer/windows/PCLN.wxs'
    $msi = Join-Path $output "${BaseName}_Installer.msi"
    & $wix.Source build $wixSource `
        -arch $Architecture `
        -d "ProductVersion=$productVersion" `
        -d "SourceExecutable=$sourceExecutable" `
        -d "InstallKindMarker=$msiMarker" `
        -pdbtype none `
        -out $msi
    if ($LASTEXITCODE -ne 0) { throw "WiX failed with exit code $LASTEXITCODE." }

    $isccCandidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6/ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7/ISCC.exe')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) }
    $iscc = $isccCandidates | Select-Object -First 1
    if (-not $iscc) {
        $iscc = (Get-Command ISCC.exe -ErrorAction Stop).Source
    }

    $architecturesAllowed = if ($Architecture -eq 'arm64') { 'arm64' } else { 'x64compatible' }
    $innoSource = Join-Path $repoRoot 'installer/windows/PCLN.iss'
    $outputBaseName = "${BaseName}_Installer"
    & $iscc `
        "/DProductVersion=$productVersion" `
        "/DSourceExecutable=$sourceExecutable" `
        "/DInstallKindMarker=$exeMarker" `
        "/DOutputDirectory=$output" `
        "/DOutputBaseName=$outputBaseName" `
        "/DArchitecturesAllowed=$architecturesAllowed" `
        "/DArchitecturesInstallIn64BitMode=$architecturesAllowed" `
        $innoSource
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }

    foreach ($path in @($canonical, $portable, $msi, (Join-Path $output "$outputBaseName.exe"))) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Expected release package was not created: $path"
        }
    }
}
finally {
    if (Test-Path -LiteralPath $working) {
        Remove-Item -LiteralPath $working -Recurse -Force
    }
}

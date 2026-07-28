# Copyright (c) 2026 PCL N contributors.
# Pull PCL.Plugin at a release tag, place sources under PCL.Plugin/, and apply
# host-overlay rewrites so a subsequent Desktop build with -p:PclWithPlugin=true
# compiles the plugin product into the host.

param(
    [string]$Tag = '',
    [string]$Repo = 'PCL-N-Edition/PCL.Plugin',
    [string]$PluginRoot = '',
    [switch]$SkipFetch,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($PluginRoot)) {
    $PluginRoot = Join-Path $repoRoot 'PCL.Plugin'
}
$PluginRoot = [System.IO.Path]::GetFullPath($PluginRoot)

function Resolve-LatestPluginTag {
    param([string]$Repository)
    $tag = & gh api "repos/$Repository/releases/latest" --jq '.tag_name' 2>$null
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($tag)) {
        return $tag.Trim()
    }
    $tag = & gh api "repos/$Repository/tags" --jq '.[0].name' 2>$null
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($tag)) {
        return $tag.Trim()
    }
    throw "Could not resolve latest tag for $Repository. Pass -Tag explicitly or ensure gh is authenticated."
}

function Ensure-PluginSources {
    param(
        [string]$Root,
        [string]$Repository,
        [string]$Ref,
        [switch]$SkipFetch
    )

    if ($SkipFetch -and (Test-Path -LiteralPath (Join-Path $Root 'PCL.Plugin.csproj'))) {
        Write-Host "SkipFetch: using existing sources at $Root"
        return
    }

    $gitDir = Join-Path $Root '.git'
    if (Test-Path -LiteralPath $gitDir) {
        Write-Host "Fetching $Repository @$Ref into existing clone $Root"
        Push-Location $Root
        try {
            & git fetch --tags --force origin
            if ($LASTEXITCODE -ne 0) { throw "git fetch failed for $Root" }
            & git checkout --force $Ref
            if ($LASTEXITCODE -ne 0) { throw "git checkout $Ref failed" }
            & git reset --hard $Ref
            if ($LASTEXITCODE -ne 0) { throw "git reset --hard $Ref failed" }
        }
        finally {
            Pop-Location
        }
        return
    }

    if (Test-Path -LiteralPath $Root) {
        $hasProject = Test-Path -LiteralPath (Join-Path $Root 'PCL.Plugin.csproj')
        if (-not $hasProject) {
            throw "PCL.Plugin path exists but is not a git clone and has no PCL.Plugin.csproj: $Root"
        }
        Write-Host "Using non-git plugin tree at $Root (no fetch)."
        return
    }

    $url = "https://github.com/$Repository.git"
    Write-Host "Cloning $url ($Ref) -> $Root"
    & git clone --depth 1 --branch $Ref $url $Root
    if ($LASTEXITCODE -ne 0) {
        # Fallback when shallow clone by tag fails (some tags need full history).
        & git clone $url $Root
        if ($LASTEXITCODE -ne 0) { throw "git clone failed for $url" }
        Push-Location $Root
        try {
            & git checkout --force $Ref
            if ($LASTEXITCODE -ne 0) { throw "git checkout $Ref failed after full clone" }
        }
        finally {
            Pop-Location
        }
    }
}

function Apply-HostOverlayRewrites {
    param(
        [string]$Root,
        [string]$HostRepoRoot,
        [switch]$WhatIf
    )

    $overlayRoot = Join-Path $Root 'host-overlay'
    $rewriteRoot = Join-Path $overlayRoot 'rewrite'
    $manifestPath = Join-Path $overlayRoot 'manifest.json'

    if (-not (Test-Path -LiteralPath $overlayRoot)) {
        throw "Plugin host-overlay missing at $overlayRoot. Upgrade PCL.Plugin to a tag that ships source-overlay support."
    }

    if (Test-Path -LiteralPath $manifestPath) {
        Write-Host "Overlay manifest: $manifestPath"
        Get-Content -LiteralPath $manifestPath -Raw | Write-Host
    }

    if (-not (Test-Path -LiteralPath $rewriteRoot)) {
        Write-Host "No rewrite/ directory under host-overlay; skip host source rewrites."
        return
    }

    $files = Get-ChildItem -LiteralPath $rewriteRoot -Recurse -File
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($rewriteRoot.Length).TrimStart('\', '/')
        $destination = Join-Path $HostRepoRoot $relative
        $destinationDir = Split-Path -Parent $destination
        Write-Host "Rewrite: $relative"
        if ($WhatIf) { continue }
        if (-not (Test-Path -LiteralPath $destinationDir)) {
            New-Item -ItemType Directory -Path $destinationDir -Force | Out-Null
        }
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    }
}

if ([string]::IsNullOrWhiteSpace($Tag)) {
    $Tag = Resolve-LatestPluginTag -Repository $Repo
}
Write-Host "Plugin tag: $Tag"
Write-Host "Plugin root: $PluginRoot"

if (-not $WhatIf) {
    Ensure-PluginSources -Root $PluginRoot -Repository $Repo -Ref $Tag -SkipFetch:$SkipFetch
}

Apply-HostOverlayRewrites -Root $PluginRoot -HostRepoRoot $repoRoot -WhatIf:$WhatIf

Write-Host ""
Write-Host "Overlay ready. Build with plugin sources compiled into Desktop:"
Write-Host "  .\scripts\build-desktop.ps1 -WithPlugin"
Write-Host "  # or: dotnet build PCL.Desktop/PCL.Desktop.csproj -p:PclWithPlugin=true"
Write-Host ""
Write-Host "UI only: .\scripts\run-plugin-ui.ps1"

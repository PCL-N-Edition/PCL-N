# Copyright (c) 2026 PCL N contributors.
#
# Source-overlay inject for PCL.Plugin (NOT DLL embed).
#
# Release product of PCL.Plugin is a git tag whose tree contains host-overlay/.
# This script:
#   1) Resolves a source tag from the private repository's v* git tags
#   2) Checks out that tag's SOURCE into PCL.Plugin/
#   3) Applies host-overlay/rewrite/** onto the host worktree
#   4) Leaves the tree ready for: dotnet build -p:PclWithPlugin=true
#
# No download of PCL.Plugin.dll. Host compiles plugin sources into Desktop.

param(
    # Explicit source tag, e.g. v0.17.0. Empty = resolve by -Channel.
    [string]$Tag = '',

    # Latest  = newest git tag matching ^v\d+\.\d+(\.\d+)? (default for host publish)
    # Stable  = newest stable v* git tag (no prerelease suffix)
    [ValidateSet('Stable', 'Latest')]
    [string]$Channel = 'Latest',

    [string]$Repo = 'PCL-N-Edition/PCL.Plugin',
    [string]$PluginRoot = '',

    # Reuse existing PCL.Plugin/ tree; still apply rewrites unless -SkipRewrite.
    [switch]$SkipFetch,

    # Only fetch/checkout source; do not copy rewrite/** onto the host.
    [switch]$SkipRewrite,

    # Dry-run: resolve tag and list rewrite files without mutating the worktree.
    [switch]$WhatIf,

    # After a successful overlay, restore host rewrite targets from git (clean host tree).
    # Use after a WithPlugin build when you do not want DesktopHost.Optional.cs left dirty.
    [switch]$RestoreHostRewrites
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($PluginRoot)) {
    $PluginRoot = Join-Path $repoRoot 'PCL.Plugin'
}
$PluginRoot = [System.IO.Path]::GetFullPath($PluginRoot)
$statePath = Join-Path $repoRoot '.pcl-plugin-overlay.state.json'

function Ensure-GitHubToken {
    if (-not [string]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
        return
    }
    if (-not [string]::IsNullOrWhiteSpace($env:PCL_PLUGIN_TOKEN)) {
        $env:GH_TOKEN = $env:PCL_PLUGIN_TOKEN
        return
    }
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) {
        $env:GH_TOKEN = $env:GITHUB_TOKEN
    }
}

function Get-AuthenticatedGitHubUrl {
    param([string]$Repository)

    Ensure-GitHubToken
    if (-not [string]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
        # x-access-token works for PATs and GITHUB_TOKEN when the token can read the repo.
        return "https://x-access-token:$($env:GH_TOKEN)@github.com/$Repository.git"
    }
    return "https://github.com/$Repository.git"
}

function Resolve-PluginSourceTag {
    param(
        [string]$Repository,
        [string]$Channel
    )

    $url = Get-AuthenticatedGitHubUrl -Repository $Repository
    $remoteLines = & git ls-remote --tags --refs $url 2>$null
    $names = @()
    if ($LASTEXITCODE -eq 0 -and $remoteLines) {
        foreach ($line in @($remoteLines)) {
            if ($line -match 'refs/tags/(\S+)\s*$') {
                $names += $Matches[1]
            }
        }
    }

    $versionTags = @(
        $names | Where-Object { $_ -match '^v\d+\.\d+(\.\d+)?(-.+)?' }
    )
    if ($Channel -eq 'Stable') {
        $versionTags = @($versionTags | Where-Object { $_ -notmatch '-' })
    }
    if ($versionTags.Count -eq 0 -and $names.Count -gt 0) {
        $versionTags = @($names[0])
    }
    if ($versionTags.Count -eq 0) {
        throw "Could not list private v* tags for $Repository. Pass -Tag or set PCL_PLUGIN_TOKEN/GH_TOKEN with contents:read access."
    }

    $sorted = $versionTags | Sort-Object {
        $t = $_ -replace '^v', '' -replace '-.*$', ''
        try { [version]$t } catch { [version]'0.0.0' }
    } -Descending

    $chosen = $sorted | Select-Object -First 1
    Write-Host "Resolved $Channel channel via git tag: $chosen"
    return $chosen
}

function Ensure-PluginSources {
    param(
        [string]$Root,
        [string]$Repository,
        [string]$Ref,
        [switch]$SkipFetch
    )

    if ($SkipFetch) {
        if (-not (Test-Path -LiteralPath (Join-Path $Root 'PCL.Plugin.csproj'))) {
            throw "SkipFetch set but PCL.Plugin.csproj not found at $Root"
        }
        Write-Host "SkipFetch: using existing sources at $Root (ref not updated)"
        return
    }

    $gitDir = Join-Path $Root '.git'
    if (Test-Path -LiteralPath $gitDir) {
        Write-Host "Fetching $Repository source tag $Ref into $Root"
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
        if (-not (Test-Path -LiteralPath (Join-Path $Root 'PCL.Plugin.csproj'))) {
            throw "PCL.Plugin path exists but is not a git clone and has no PCL.Plugin.csproj: $Root"
        }
        Write-Warning "Using non-git plugin tree at $Root (no fetch). Prefer a clone so -Tag can pin source contracts."
        return
    }

    $url = Get-AuthenticatedGitHubUrl -Repository $Repository
    Write-Host "Cloning source https://github.com/$Repository.git @ $Ref -> $Root"
    & git clone --depth 1 --branch $Ref $url $Root
    if ($LASTEXITCODE -ne 0) {
        & git clone $url $Root
        if ($LASTEXITCODE -ne 0) {
            throw "git clone failed for private repository $Repository. Set PCL_PLUGIN_TOKEN or GH_TOKEN with contents:read access."
        }
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

function Test-HostOverlayContract {
    param([string]$Root)

    $overlayRoot = Join-Path $Root 'host-overlay'
    $manifestPath = Join-Path $overlayRoot 'manifest.json'
    $targetsPath = Join-Path $overlayRoot 'msbuild\PclPlugin.overlay.targets'

    if (-not (Test-Path -LiteralPath $overlayRoot)) {
        throw "Source tag is missing host-overlay/ at $overlayRoot. This tag cannot inject via source overlay."
    }
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "host-overlay/manifest.json missing. Refuse inject for incomplete source contract."
    }
    if (-not (Test-Path -LiteralPath $targetsPath)) {
        throw "host-overlay/msbuild/PclPlugin.overlay.targets missing. Refuse inject for incomplete source contract."
    }

    Write-Host "host-overlay contract OK: $manifestPath"
    return $overlayRoot
}

function Apply-HostOverlayRewrites {
    param(
        [string]$Root,
        [string]$HostRepoRoot,
        [string]$ResolvedTag,
        [switch]$WhatIf
    )

    $overlayRoot = Test-HostOverlayContract -Root $Root
    $rewriteRoot = Join-Path $overlayRoot 'rewrite'
    $manifestPath = Join-Path $overlayRoot 'manifest.json'

    Write-Host "--- host-overlay/manifest.json ---"
    Get-Content -LiteralPath $manifestPath -Raw | Write-Host
    Write-Host "---------------------------------"

    $applied = [System.Collections.Generic.List[string]]::new()
    if (-not (Test-Path -LiteralPath $rewriteRoot)) {
        Write-Host "No rewrite/ under host-overlay; only MSBuild import will pull plugin sources."
    }
    else {
        $files = Get-ChildItem -LiteralPath $rewriteRoot -Recurse -File
        foreach ($file in $files) {
            $relative = $file.FullName.Substring($rewriteRoot.Length).TrimStart('\', '/')
            $destination = Join-Path $HostRepoRoot $relative
            $destinationDir = Split-Path -Parent $destination
            Write-Host "Rewrite (source overlay): $relative"
            $applied.Add(($relative -replace '\\', '/')) | Out-Null
            if ($WhatIf) { continue }
            if (-not (Test-Path -LiteralPath $destinationDir)) {
                New-Item -ItemType Directory -Path $destinationDir -Force | Out-Null
            }
            Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
        }
    }

    $state = [ordered]@{
        schemaVersion     = 1
        mode              = 'source-overlay'
        pluginTag         = $ResolvedTag
        pluginRoot        = $Root
        appliedAtUtc      = [DateTime]::UtcNow.ToString('o')
        rewrittenRelative = @($applied)
        note              = 'Host rewrites dirty the worktree. Use -RestoreHostRewrites or: git restore -- <paths>. Inject is source compile (-p:PclWithPlugin=true), not DLL embed.'
    }

    if (-not $WhatIf) {
        ($state | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath $statePath -Encoding utf8
        Write-Host "Wrote overlay state: $statePath"
    }

    return @($applied)
}

function Restore-HostRewrites {
    param(
        [string]$HostRepoRoot,
        [string]$StatePath
    )

    if (-not (Test-Path -LiteralPath $StatePath)) {
        Write-Warning "No overlay state at $StatePath; restoring known default rewrite only."
        $paths = @('PCL.Desktop/Hosting/DesktopHost.Optional.cs')
    }
    else {
        $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
        $paths = @($state.rewrittenRelative)
        if ($paths.Count -eq 0) {
            Write-Host "State has no rewritten paths; nothing to restore."
            return
        }
    }

    Push-Location $HostRepoRoot
    try {
        foreach ($rel in $paths) {
            $norm = $rel -replace '/', [IO.Path]::DirectorySeparatorChar
            Write-Host "git restore $norm"
            & git restore --source=HEAD --worktree --staged -- $norm 2>$null
            if ($LASTEXITCODE -ne 0) {
                & git checkout HEAD -- $norm 2>$null
            }
        }
    }
    finally {
        Pop-Location
    }

    if (Test-Path -LiteralPath $StatePath) {
        Remove-Item -LiteralPath $StatePath -Force
        Write-Host "Removed $StatePath"
    }
}

# --- main ---

Write-Host "PCL.Plugin source-overlay inject (no DLL embed)"
Write-Host "Host repo:   $repoRoot"
Write-Host "Plugin root: $PluginRoot"
Write-Host "Channel:     $Channel"

if ($RestoreHostRewrites -and -not $WhatIf -and [string]::IsNullOrWhiteSpace($Tag) -and $SkipFetch) {
    # Allow restore-only invocation: -RestoreHostRewrites -SkipFetch
    Restore-HostRewrites -HostRepoRoot $repoRoot -StatePath $statePath
    exit 0
}

if ([string]::IsNullOrWhiteSpace($Tag)) {
    $Tag = Resolve-PluginSourceTag -Repository $Repo -Channel $Channel
}
else {
    Write-Host "Using explicit source tag: $Tag"
}

if (-not $WhatIf) {
    Ensure-PluginSources -Root $PluginRoot -Repository $Repo -Ref $Tag -SkipFetch:$SkipFetch
}
else {
    Write-Host "WhatIf: skip fetch/checkout"
}

$rewritten = @()
if (-not $SkipRewrite) {
    $rewritten = Apply-HostOverlayRewrites -Root $PluginRoot -HostRepoRoot $repoRoot -ResolvedTag $Tag -WhatIf:$WhatIf
}
else {
    Test-HostOverlayContract -Root $PluginRoot | Out-Null
    Write-Host "SkipRewrite: host files left unchanged"
}

if ($RestoreHostRewrites -and -not $WhatIf) {
    Restore-HostRewrites -HostRepoRoot $repoRoot -StatePath $statePath
    Write-Host "Host rewrites restored after overlay (source still at $PluginRoot @$Tag)."
}

Write-Host ""
Write-Host "Source overlay ready (tag=$Tag)."
Write-Host "  Compile inject:  .\scripts\build-desktop.ps1 -WithPlugin -SkipPluginFetch"
Write-Host "                   # or: dotnet build PCL.Desktop/PCL.Desktop.csproj -p:PclWithPlugin=true"
Write-Host "  Clean host tree: .\scripts\apply-plugin-overlay.ps1 -RestoreHostRewrites -SkipFetch"
if ($rewritten.Count -gt 0 -and -not $RestoreHostRewrites) {
    Write-Host "  Dirty host files from rewrite (do not commit by accident):"
    foreach ($r in $rewritten) { Write-Host "    - $r" }
}

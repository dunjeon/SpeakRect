#Requires -Version 5.1
<#
.SYNOPSIS
  Bootstrap Local-LLM host + GLM-OCR GGUFs into .\koboldcpp\

.DESCRIPTION
  Developer / OSS path: clone source without multi-GB LFS, then fetch binaries.

  Default source for pinned files is the public SpeakRect GitHub Releases page
  (complete install zip or split model assets). Override -ReleaseTag / pin table
  when shipping a new host or model.

  On-disk layout stays koboldcpp\ (install compatibility). UI brand is Local-LLM.

.PARAMETER ReleaseTag
  GitHub release tag to pull assets from (e.g. v1.4.19). Empty = latest.

.PARAMETER SkipVerify
  Skip SHA256 checks when pins are still TBD.

.EXAMPLE
  .\scripts\Fetch-LocalLlm.ps1 -ReleaseTag v1.4.19
#>
[CmdletBinding()]
param(
    [switch] $SkipVerify,
    [string] $DestDir = "",
    [string] $ReleaseTag = "",
    [string] $GitHubRepo = "dunjeon/SpeakRect"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $DestDir) {
    $DestDir = Join-Path $repoRoot "koboldcpp"
}

# ---------------------------------------------------------------------------
# Pin table — update when you cut a release that publishes these assets.
# Prefer release asset URLs (not git LFS). SHA256: fail-closed when set.
# ---------------------------------------------------------------------------
$Artifacts = @(
    @{
        Name     = "koboldcpp.exe"
        # Upstream host: https://github.com/LostRuins/koboldcpp/releases
        # Prefer the build SpeakRect QA'd; override Url after publishing to Releases.
        Url      = ""  # filled below from GitHub release if empty
        AssetHint = "koboldcpp.exe"
        Sha256   = "TBD"
    }
    @{
        Name      = "glmocr-Q8_0.gguf"
        Url       = ""
        AssetHint = "glmocr-Q8_0.gguf"
        Sha256    = "TBD"
    }
    @{
        Name      = "mmproj-glmocr-Q8_0.gguf"
        Url       = ""
        AssetHint = "mmproj-glmocr-Q8_0.gguf"
        Sha256    = "TBD"
    }
)

function Test-Sha256([string] $Path, [string] $Expected) {
    if ($Expected -eq "TBD" -or [string]::IsNullOrWhiteSpace($Expected)) {
        Write-Warning "No SHA256 pin for $(Split-Path $Path -Leaf) — skipping verify."
        return $true
    }
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
    if ($hash -ne $Expected.ToUpperInvariant()) {
        Write-Error "SHA256 mismatch for $Path`n  expected $Expected`n  actual   $hash"
        return $false
    }
    return $true
}

function Get-ReleaseAssets {
    param([string] $Repo, [string] $Tag)
    $headers = @{ "User-Agent" = "SpeakRect-Fetch-LocalLlm" }
    if ($Tag) {
        $api = "https://api.github.com/repos/$Repo/releases/tags/$Tag"
    }
    else {
        $api = "https://api.github.com/repos/$Repo/releases/latest"
    }
    try {
        return Invoke-RestMethod -Uri $api -Headers $headers
    }
    catch {
        Write-Warning "Could not query GitHub releases ($api): $($_.Exception.Message)"
        return $null
    }
}

New-Item -ItemType Directory -Force -Path $DestDir | Out-Null
Write-Host "Local-LLM payload directory: $DestDir"

$release = Get-ReleaseAssets -Repo $GitHubRepo -Tag $ReleaseTag
if ($release) {
    Write-Host "Using release: $($release.tag_name)"
    foreach ($a in $Artifacts) {
        if ($a.Url) { continue }
        $match = @($release.assets | Where-Object { $_.name -eq $a.AssetHint -or $_.name -like "*$($a.AssetHint)*" }) | Select-Object -First 1
        if ($match) {
            $a.Url = $match.browser_download_url
            Write-Host "  Resolved $($a.Name) -> $($a.Url)"
        }
    }
}

$missing = $false
foreach ($a in $Artifacts) {
    $out = Join-Path $DestDir $a.Name
    if (Test-Path -LiteralPath $out) {
        Write-Host "Exists: $($a.Name)"
        if (-not $SkipVerify) { [void](Test-Sha256 $out $a.Sha256) }
        continue
    }

    if (-not $a.Url) {
        Write-Warning "No URL for $($a.Name). Place it manually in $DestDir or publish it on $GitHubRepo releases."
        $missing = $true
        continue
    }

    Write-Host "Downloading $($a.Name) …"
    Invoke-WebRequest -Uri $a.Url -OutFile $out -UseBasicParsing
    if (-not $SkipVerify) {
        if (-not (Test-Sha256 $out $a.Sha256)) { exit 2 }
    }
}

$cfg = Join-Path $DestDir "ocr.kcpps"
if (-not (Test-Path -LiteralPath $cfg)) {
    $src = Join-Path $repoRoot "koboldcpp\ocr.kcpps"
    if (Test-Path -LiteralPath $src) {
        Copy-Item -LiteralPath $src -Destination $cfg
        Write-Host "Copied ocr.kcpps"
    }
    else {
        Write-Warning "ocr.kcpps missing — add model_param/mmproj config under koboldcpp\"
    }
}

if ($missing) {
    Write-Host ""
    Write-Host "Some artifacts were not downloaded. Options:"
    Write-Host "  1) Extract koboldcpp\ from a SpeakRect-*-win-x64.zip release"
    Write-Host "  2) Publish individual assets on GitHub Releases and re-run this script"
    Write-Host "  3) Fill Sha256 pins in this script for fail-closed verify"
    exit 1
}

Write-Host "Local-LLM bootstrap complete."
exit 0

#Requires -Version 5.1
<#
.SYNOPSIS
  LEGACY one-time dual-repo setup (private SpeakRect-src + public docs face).

.DESCRIPTION
  OBSOLETE for day-to-day work after R6 public full-source flip.
  Source of truth is public dunjeon/SpeakRect only — see docs/GITHUB_REPOS.md
  and publish/AGENT_RELEASE.md.

  Historical behavior (kept for reference):
    1) git init in this source tree (if needed) and make initial commit
    2) Create private dunjeon/SpeakRect-src and push
    3) Create public  dunjeon/SpeakRect from public-repo\ and push

  Does NOT upload release zips (see publish/AGENT_RELEASE.md).

.EXAMPLE
  .\scripts\Init-GitHubRepos.ps1
.EXAMPLE
  .\scripts\Init-GitHubRepos.ps1 -Owner dunjeon -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $Owner = 'dunjeon',
    [string] $PrivateRepo = 'SpeakRect-src',
    [string] $PublicRepo = 'SpeakRect',
    [switch] $SkipPrivate,
    [switch] $SkipPublic
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-RepoRoot {
    $here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    return (Resolve-Path (Join-Path $here '..')).Path
}

function Ensure-GitIdentity {
    $name = git config user.name 2>$null
    $email = git config user.email 2>$null
    if (-not $name) {
        git config user.name 'dunjeon'
        Write-Host "Set local git user.name = dunjeon"
    }
    if (-not $email) {
        # GitHub noreply for account id 30352875 / dunjeon
        git config user.email '30352875+dunjeon@users.noreply.github.com'
        Write-Host "Set local git user.email = 30352875+dunjeon@users.noreply.github.com"
    }
}

$repoRoot = Get-RepoRoot
$publicDir = Join-Path $repoRoot 'public-repo'

$gh = Get-Command gh -ErrorAction SilentlyContinue
if (-not $gh) { throw 'GitHub CLI (gh) not found. Install from https://cli.github.com/ then: gh auth login' }

& gh auth status 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw 'Not logged in. Run: gh auth login'
}

Write-Host "Owner:          $Owner" -ForegroundColor Cyan
Write-Host "Private source: $Owner/$PrivateRepo" -ForegroundColor Cyan
Write-Host "Public product: $Owner/$PublicRepo" -ForegroundColor Cyan
Write-Host "Source root:    $repoRoot"
Write-Host "Public face:    $publicDir"
Write-Host ''

# --- Private source ---
if (-not $SkipPrivate) {
    Push-Location $repoRoot
    try {
        if (-not (Test-Path -LiteralPath (Join-Path $repoRoot '.git'))) {
            if ($PSCmdlet.ShouldProcess($repoRoot, 'git init')) {
                git init -b main
            }
        }

        Ensure-GitIdentity

        if (-not (Test-Path -LiteralPath (Join-Path $repoRoot '.gitignore'))) {
            throw '.gitignore missing at repo root - aborting so models/binaries are not committed.'
        }

        git add -A
        $status = git status --porcelain
        if ($status) {
            if ($PSCmdlet.ShouldProcess($repoRoot, 'git commit')) {
                git commit -m 'Initial private source commit (SpeakRect closed source).'
            }
        }
        else {
            Write-Host 'Private tree: nothing new to commit.'
        }

        $fullPrivate = "$Owner/$PrivateRepo"
        $exists = $false
        # gh writes errors to stderr; with $ErrorActionPreference Stop that becomes terminating
        $prevEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        & gh repo view $fullPrivate 1>$null 2>$null
        if ($LASTEXITCODE -eq 0) { $exists = $true }
        $ErrorActionPreference = $prevEap

        if (-not $exists) {
            if ($PSCmdlet.ShouldProcess($fullPrivate, 'gh repo create (private)')) {
                $desc = "SpeakRect private source (closed source). Public product: https://github.com/$Owner/$PublicRepo"
                & gh repo create $fullPrivate --private --source=. --remote=origin --description $desc
                if ($LASTEXITCODE -ne 0) { throw "gh repo create $fullPrivate failed" }
            }
        }
        else {
            Write-Host "Private repo already exists: $fullPrivate"
            $remote = git remote get-url origin 2>$null
            if (-not $remote) {
                git remote add origin "https://github.com/$fullPrivate.git"
            }
        }

        if ($PSCmdlet.ShouldProcess('origin/main', 'git push private source')) {
            git push -u origin main
            if ($LASTEXITCODE -ne 0) { throw 'git push private failed' }
        }

        Write-Host "Private source: https://github.com/$fullPrivate" -ForegroundColor Green
    }
    finally {
        Pop-Location
    }
}

# --- Public product (docs only) ---
if (-not $SkipPublic) {
    if (-not (Test-Path -LiteralPath (Join-Path $publicDir 'README.md'))) {
        throw "public-repo\README.md missing. Expected product face under $publicDir"
    }

    $fullPublic = "$Owner/$PublicRepo"

    Push-Location $publicDir
    try {
        if (-not (Test-Path -LiteralPath (Join-Path $publicDir '.git'))) {
            if ($PSCmdlet.ShouldProcess($publicDir, 'git init public face')) {
                git init -b main
            }
        }

        Ensure-GitIdentity

        $allow = @('README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md', '.gitattributes', '.gitignore', '.github')
        Get-ChildItem -Force | Where-Object { $_.Name -ne '.git' } | ForEach-Object {
            if ($_.Name -notin $allow) {
                Write-Warning "Unexpected path in public-repo (not auto-deleted): $($_.Name)"
            }
        }

        $pubIgnore = @'
# Public product repo: docs only. Never add source or binaries.
*
!.gitignore
!.gitattributes
!README.md
!LICENSE
!THIRD_PARTY_NOTICES.md
!.github/
!.github/**
'@
        Set-Content -LiteralPath (Join-Path $publicDir '.gitignore') -Value $pubIgnore -Encoding UTF8

        git add -A
        $status = git status --porcelain
        if ($status) {
            if ($PSCmdlet.ShouldProcess($publicDir, 'git commit public face')) {
                git commit -m 'Public product page: docs and license only (no source).'
            }
        }

        $exists = $false
        $prevEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        & gh repo view $fullPublic 1>$null 2>$null
        if ($LASTEXITCODE -eq 0) { $exists = $true }
        $ErrorActionPreference = $prevEap

        if (-not $exists) {
            if ($PSCmdlet.ShouldProcess($fullPublic, 'gh repo create (public)')) {
                $desc = 'SpeakRect - accessibility screen reader for Windows. Closed source; download builds from Releases.'
                & gh repo create $fullPublic --public --source=. --remote=origin --description $desc
                if ($LASTEXITCODE -ne 0) { throw "gh repo create $fullPublic failed" }
            }
        }
        else {
            Write-Host "Public repo already exists: $fullPublic"
            $remote = git remote get-url origin 2>$null
            if (-not $remote) {
                git remote add origin "https://github.com/$fullPublic.git"
            }
        }

        if ($PSCmdlet.ShouldProcess('origin/main', 'git push public face')) {
            git push -u origin main
            if ($LASTEXITCODE -ne 0) { throw 'git push public failed' }
        }

        & gh repo edit $fullPublic --homepage "https://github.com/$fullPublic/releases" 2>$null
        & gh repo edit $fullPublic --add-topic accessibility --add-topic windows --add-topic screen-reader --add-topic tts --add-topic ocr 2>$null

        Write-Host "Public product: https://github.com/$fullPublic" -ForegroundColor Green
        Write-Host "Releases:       https://github.com/$fullPublic/releases" -ForegroundColor Green
    }
    finally {
        Pop-Location
    }
}

Write-Host ''
Write-Host 'Next:' -ForegroundColor Cyan
Write-Host '  1. Ensure koboldcpp\ has koboldcpp.exe + Q8_0 ggufs + ocr.kcpps (gitignored)'
Write-Host '  2. .\scripts\Publish-Release.ps1'
Write-Host '  3. If zip under 2 GiB:  .\scripts\Publish-Release.ps1 -SkipPublish -CreateGitHubRelease'
Write-Host '     If zip at/over 2 GiB: host the zip (itch.io / R2 / etc.) and open a Release with the download link'
Write-Host ''
Write-Host 'Done.'

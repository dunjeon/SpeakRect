#Requires -Version 5.1
<#
.SYNOPSIS
  Publish SpeakRect and pack ONE complete zip (app + models).

.DESCRIPTION
  OWNER / NEW-VERSION tool only. Agents shipping an already-provided archive
  must follow publish\AGENT_RELEASE.md and MUST NOT rebuild that zip.

  1) dotnet publish (single-file self-contained; no obfuscation — open source)
  2) Stage SpeakRect.exe + koboldcpp payload (Q8_0 GGUFs + host + config)
  3) Zip to publish\SpeakRect-<version>-win-x64.zip

  Models are NOT in git -- they must exist under koboldcpp\ (or -KoboldCppSourceDir).

  GitHub Releases hard-limit each asset to 2 GiB. Use -CreateGitHubRelease only
  when the zip is under the limit, or host the zip elsewhere and link it.

  Public source of truth: dunjeon/SpeakRect (full source).

.EXAMPLE
  .\scripts\Publish-Release.ps1
.EXAMPLE
  .\scripts\Publish-Release.ps1 -Version 1.1.0 -CreateGitHubRelease
#>
[CmdletBinding()]
param(
    [string] $Version,
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [string] $KoboldCppSourceDir,
    [string] $ProjectPath,
    [string] $OutDir,
    [switch] $SkipPublish,
    [switch] $CreateGitHubRelease,
    [string] $PublicRepo = 'dunjeon/SpeakRect',
    [string] $ReleaseNotes
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-RepoRoot {
    $here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    return (Resolve-Path (Join-Path $here '..')).Path
}

function Format-GiB([long] $Bytes) {
    return ('{0:N3} GiB' -f ($Bytes / 1GB))
}

$repoRoot = Get-RepoRoot
if (-not $ProjectPath) { $ProjectPath = Join-Path $repoRoot 'SpeakRect.csproj' }
if (-not (Test-Path -LiteralPath $ProjectPath)) {
    throw "Project not found: $ProjectPath"
}

if (-not $Version) {
    [xml] $proj = Get-Content -LiteralPath $ProjectPath -Raw
    $Version = $proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $Version) { $Version = '0.0.0' }
}

if (-not $KoboldCppSourceDir) {
    $KoboldCppSourceDir = Join-Path $repoRoot 'koboldcpp'
}
$KoboldCppSourceDir = (Resolve-Path -LiteralPath $KoboldCppSourceDir).Path

if (-not $OutDir) {
    $OutDir = Join-Path $repoRoot 'publish'
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$stageName = "SpeakRect-$Version-$Runtime"
$stageDir = Join-Path $OutDir $stageName
$zipPath = Join-Path $OutDir "$stageName.zip"
$publishDir = Join-Path $OutDir "_publish-temp-$Runtime"

# Required complete-package payload (Q8_0 matches README / csproj comments)
$requiredKobold = @(
    'koboldcpp.exe',
    'glmocr-Q8_0.gguf',
    'mmproj-glmocr-Q8_0.gguf',
    'ocr.kcpps'
)

Write-Host '=== SpeakRect release pack ===' -ForegroundColor Cyan
Write-Host "Version:  $Version"
Write-Host "Runtime:  $Runtime"
Write-Host "Kobold:   $KoboldCppSourceDir"
Write-Host "Zip:      $zipPath"
Write-Host ''

foreach ($name in $requiredKobold) {
    $p = Join-Path $KoboldCppSourceDir $name
    if (-not (Test-Path -LiteralPath $p)) {
        $msg = @"
Missing required release file: $p

Complete single-zip releases need the local LLM payload next to the app.
Place these under koboldcpp\ (or pass -KoboldCppSourceDir):
  koboldcpp.exe
  glmocr-Q8_0.gguf
  mmproj-glmocr-Q8_0.gguf
  ocr.kcpps

These files are gitignored; they ship only on the public release zip.
"@
        throw $msg
    }
}

if (-not $SkipPublish) {
    if (Test-Path -LiteralPath $publishDir) {
        Remove-Item -LiteralPath $publishDir -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

    Write-Host 'Publishing (single-file self-contained, no obfuscation)...' -ForegroundColor Yellow
    $publishArgs = @(
        'publish', $ProjectPath
        '-c', $Configuration
        '-r', $Runtime
        '-o', $publishDir
        "-p:KoboldCppSourceDir=$KoboldCppSourceDir"
        '--self-contained', 'true'
    )
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }

    $exe = Join-Path $publishDir 'SpeakRect.exe'
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "Publish did not produce SpeakRect.exe under $publishDir"
    }
}
else {
    $exe = Join-Path $publishDir 'SpeakRect.exe'
    if (-not (Test-Path -LiteralPath $exe)) {
        $fallback = Join-Path $stageDir 'SpeakRect.exe'
        if (Test-Path -LiteralPath $fallback) {
            $publishDir = $stageDir
            $exe = $fallback
        }
        else {
            throw "SkipPublish set but no SpeakRect.exe at $exe - run without -SkipPublish first."
        }
    }
}

# Fresh stage: complete product tree only (no source, no Mapping.txt, no PDBs)
if (Test-Path -LiteralPath $stageDir) {
    Remove-Item -LiteralPath $stageDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path (Join-Path $stageDir 'koboldcpp') | Out-Null

Copy-Item -LiteralPath (Join-Path $publishDir 'SpeakRect.exe') -Destination (Join-Path $stageDir 'SpeakRect.exe') -Force

foreach ($name in $requiredKobold) {
    Copy-Item -LiteralPath (Join-Path $KoboldCppSourceDir $name) `
        -Destination (Join-Path $stageDir "koboldcpp\$name") -Force
}

foreach ($doc in @('LICENSE', 'README.md', 'THIRD_PARTY_NOTICES.md')) {
    $srcDoc = Join-Path $repoRoot $doc
    if (Test-Path -LiteralPath $srcDoc) {
        Copy-Item -LiteralPath $srcDoc -Destination (Join-Path $stageDir $doc) -Force
    }
}

$installText = @"
SpeakRect $Version - complete Windows x64 release

Contents (single package - no other downloads required for the default setup):
  SpeakRect.exe              App (single-file, self-contained)
  koboldcpp\koboldcpp.exe    Local LLM host
  koboldcpp\glmocr-Q8_0.gguf
  koboldcpp\mmproj-glmocr-Q8_0.gguf
  koboldcpp\ocr.kcpps
  LICENSE / README.md / THIRD_PARTY_NOTICES.md

Install:
  1. Extract this zip anywhere (about 2.5 GB free disk recommended)
  2. Run SpeakRect.exe

Source is available under the project LICENSE (GPLv2 app source; see notices for host).

Windows SmartScreen / "Unknown publisher" (expected)
  SpeakRect is free and not code-signed. Windows may show "Windows protected
  your PC" or "Unknown publisher". That means Microsoft does not know the
  publisher -- not that the file was found to be malware.

  To run:
    1. Click More info
    2. Click Run anyway

  If the file stays blocked: right-click SpeakRect.exe -> Properties -> Unblock
  (if shown) -> OK. Or in PowerShell from this folder:
    Unblock-File -Path .\SpeakRect.exe
    Get-ChildItem -Recurse | Unblock-File

  Only download from the official GitHub Releases page for this project.
  See README.md for the full note.
"@
Set-Content -LiteralPath (Join-Path $stageDir 'INSTALL.txt') -Value $installText -Encoding UTF8

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Write-Host 'Creating single complete zip...' -ForegroundColor Yellow
$tar = Get-Command tar.exe -ErrorAction SilentlyContinue
if ($tar) {
    Push-Location $OutDir
    try {
        & tar.exe -a -c -f $zipPath $stageName
        if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE" }
    }
    finally {
        Pop-Location
    }
}
else {
    Compress-Archive -Path $stageDir -DestinationPath $zipPath -CompressionLevel Optimal -Force
}

$zipItem = Get-Item -LiteralPath $zipPath
$zipBytes = $zipItem.Length
$limit = [long]2GB  # GitHub Releases asset max (2 GiB)

Write-Host ''
Write-Host "Staged folder: $stageDir" -ForegroundColor Green
Write-Host "Zip:           $zipPath" -ForegroundColor Green
Write-Host ("Zip size:      {0} ({1} bytes)" -f (Format-GiB $zipBytes), $zipBytes) -ForegroundColor Green

if ($zipBytes -ge $limit) {
    Write-Host ''
    Write-Host 'WARNING: Zip is >= GitHub 2 GiB release-asset limit.' -ForegroundColor Red
    Write-Host '  GitHub will reject this file as a Release asset.' -ForegroundColor Red
    Write-Host '  Options:' -ForegroundColor Yellow
    Write-Host '    * Host the zip on itch.io / Cloudflare R2 / similar and link it from the public release notes'
    Write-Host '    * Keep producing this single zip for off-GitHub distribution (recommended UX)'
    Write-Host "  Public repo: https://github.com/$PublicRepo"
}

if ($CreateGitHubRelease) {
    if ($zipBytes -ge $limit) {
        throw 'Refusing -CreateGitHubRelease: zip exceeds 2 GiB. Host the zip elsewhere, then create a release with notes that link to it.'
    }

    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if (-not $gh) { throw 'GitHub CLI (gh) not found. Install it and run: gh auth login' }

    $tag = "v$Version"
    if (-not $ReleaseNotes) {
        $ReleaseNotes = @"
## SpeakRect $Version

Complete Windows x64 package (single zip):

- ``SpeakRect.exe`` (single-file self-contained; not obfuscated)
- KoboldCpp host + **Q8_0** GLM-OCR model files
- LICENSE, README, third-party notices

### Install
1. Download ``$stageName.zip``
2. Extract (about 2.5 GB free disk recommended)
3. Run ``SpeakRect.exe``

App source is GPLv2; the bundled Local-LLM host remains third-party (see THIRD_PARTY_NOTICES).

### Windows SmartScreen / “Unknown publisher”

Windows may block or warn when you first run **SpeakRect.exe** (or open the zip). Common messages:

- **“Windows protected your PC”** / SmartScreen
- **“Unknown publisher”**
- Browser download warnings for an unsigned app

**That is expected.** SpeakRect is free and distributed as an unsigned zip. It is **not** code-signed with a paid certificate, so Windows cannot show a verified publisher name. The warning means “Microsoft doesn’t know this publisher,” **not** that the file was found to be malware.

**How to run it anyway**

1. On the SmartScreen window, click **More info**.
2. Click **Run anyway**.

If Explorer still treats the file as blocked after download:

1. Right-click **SpeakRect.exe** (or the zip) → **Properties**.
2. If you see **Unblock** at the bottom, check it → **OK**.
3. Run the app again.

Or in PowerShell, from the folder you extracted to:

``````powershell
Unblock-File -Path .\SpeakRect.exe
Get-ChildItem -Recurse | Unblock-File
``````

Only download from the official Releases page on this repository. Full notes: see **Windows SmartScreen / “Unknown publisher”** in the repository README.
"@
    }

    Write-Host "Creating GitHub release $tag on $PublicRepo ..." -ForegroundColor Yellow
    $notesFile = Join-Path $OutDir "release-notes-$Version.md"
    Set-Content -LiteralPath $notesFile -Value $ReleaseNotes -Encoding UTF8

    & gh release view $tag --repo $PublicRepo 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Release $tag already exists -- uploading/replacing asset..."
        & gh release upload $tag $zipPath --repo $PublicRepo --clobber
    }
    else {
        & gh release create $tag $zipPath --repo $PublicRepo --title "SpeakRect $Version" --notes-file $notesFile
    }
    if ($LASTEXITCODE -ne 0) {
        throw "gh release failed with exit code $LASTEXITCODE"
    }
    Write-Host "Published: https://github.com/$PublicRepo/releases/tag/$tag" -ForegroundColor Green
}

Write-Host ''
Write-Host 'Done.' -ForegroundColor Cyan

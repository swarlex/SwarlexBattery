# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  One command from local change to GitHub: every installed copy then offers the update.

  1. raises VERSION (patch by default, or -Version X.Y.Z)
  2. builds dist\SwarlexBattery.exe + .sha256
  3. commits every change, pushes to origin/main
  4. creates the GitHub release vX.Y.Z with the exe and its checksum (GitHub CLI "gh", signed in)

.EXAMPLE
  .\tools\release.ps1 -Notes "Fix headset charging detection"          # 1.1.0 -> 1.1.1
  .\tools\release.ps1 -Version 1.2.0 -Notes "Logitech support"
#>
param(
    [string]$Version,
    [Parameter(Mandatory)] [string]$Notes,
    [string]$Repo = 'swarlex/SwarlexBattery',
    [switch]$ByClaude          # adds the Co-Authored-By trailer to the commit
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh -and (Test-Path "$env:ProgramFiles\GitHub CLI\gh.exe")) { $gh = "$env:ProgramFiles\GitHub CLI\gh.exe" }
if (-not $gh) { throw 'GitHub CLI (gh) not found. Install it with: winget install GitHub.cli, then: gh auth login' }
& $gh auth status *> $null
if ($LASTEXITCODE -ne 0) { throw 'GitHub CLI is not signed in: gh auth login --web' }

# nothing to publish?
$changes = git status --porcelain
if (-not $changes -and -not $Version) { Write-Host 'No changes; nothing to publish.'; return }

# 1. version
$vf = Join-Path $root 'VERSION'
$cur = [version](Get-Content -LiteralPath $vf -Raw).Trim()
if (-not $Version) { $Version = "$($cur.Major).$($cur.Minor).$($cur.Build + 1)" }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be X.Y.Z: $Version" }
if ([version]$Version -le $cur) { throw "The new version ($Version) must be greater than the current one ($cur)" }
Set-Content -LiteralPath $vf -Value $Version -Encoding ASCII

# 2. build (a failed build stops here: nothing is pushed or published)
try { & (Join-Path $root 'build.ps1') }
catch { Set-Content -LiteralPath $vf -Value "$cur" -Encoding ASCII; throw "Build failed, nothing was published: $_" }
$exe = Join-Path $root 'dist\SwarlexBattery.exe'
$sha = "$exe.sha256"

# 3. commit + push
$msg = "v$Version`: $Notes"
if ($ByClaude) { $msg += "`n`nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>" }
git add -A
git commit -q -m $msg
if ($LASTEXITCODE -ne 0) { throw 'git commit failed' }
git push -q origin HEAD:main
if ($LASTEXITCODE -ne 0) { throw 'git push failed' }

# 4. GitHub release (the tag is created on the pushed commit)
$setup = Join-Path $root 'dist\SwarlexBattery-Setup.exe'
& $gh release create "v$Version" $setup $exe $sha --repo $Repo --target main --title "SwarlexBattery v$Version" --notes $Notes
if ($LASTEXITCODE -ne 0) { throw 'gh release create failed (the code was pushed, the release was not created)' }
Write-Host "Published: v$Version  https://github.com/$Repo/releases/tag/v$Version"
Write-Host 'Installed copies see the update within 6 hours (or right away with right-click > Check for updates).'

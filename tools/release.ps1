# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  One command from local change to GitHub: every installed copy then offers the update.

  1. raises VERSION (patch by default, or -Version X.Y.Z)
  2. builds dist\SwarlexBattery.exe + .sha256
  3. commits every change on a branch vX.Y.Z, opens a pull request and merges it into main
     (main's history then shows one "Merge pull request #N" per release)
  4. creates the GitHub release vX.Y.Z with the setup, the exe and its checksum (GitHub CLI "gh", signed in)

  Release channel: small fixes go out with -Beta as a pre-release (offered only to copies with
  "Get beta versions" on); a stable release (no -Beta) collects them and reaches everyone.

.EXAMPLE
  .\tools\release.ps1 -Beta -Notes "Fix headset charging detection"    # 1.9.0 -> 1.9.1, pre-release
  .\tools\release.ps1 -Version 1.10.0 -Notes "Logitech support"        # stable
#>
param(
    [string]$Version,
    [Parameter(Mandatory)] [string]$Notes,
    [string]$Repo = 'swarlex/SwarlexBattery',
    [switch]$Beta,             # publish as a pre-release (beta channel only)
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

# 3. commit on a release branch, pull request, merge into main
$msg = "v$Version`: $Notes"
if ($ByClaude) { $msg += "`n`nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>" }
# git writes harmless notes (e.g. line-ending warnings) to stderr; only exit codes count from here on
$ErrorActionPreference = 'Continue'
$branch = "v$Version"
git checkout -q -B $branch
git add -A
git commit -q -m $msg
if ($LASTEXITCODE -ne 0) { git checkout -q main; throw 'git commit failed' }
git push -q -u origin $branch
if ($LASTEXITCODE -ne 0) { git checkout -q main; throw 'git push failed' }
$kind = if ($Beta) { 'Pre-release' } else { 'Release' }
& $gh pr create --repo $Repo --base main --head $branch --title "$kind v$Version" --body $Notes
if ($LASTEXITCODE -ne 0) { git checkout -q main; throw 'gh pr create failed' }
& $gh pr merge $branch --repo $Repo --merge --delete-branch
if ($LASTEXITCODE -ne 0) { git checkout -q main; throw 'gh pr merge failed (the pull request is open; merge it on GitHub)' }
git checkout -q main
git pull -q --ff-only origin main
git branch -q -D $branch 2>$null

# 4. GitHub release (the tag is created on the pushed commit). The release text is this version's section
#    of docs/CHANGELOG.md (summary paragraph, then ### Added / ### Fixed / ...); -Notes when there is none.
$setup = Join-Path $root 'dist\SwarlexBattery-Setup.exe'
$body = $Notes
$log = Join-Path $root 'docs\CHANGELOG.md'
if (Test-Path -LiteralPath $log) {
    $m = [regex]::Match([IO.File]::ReadAllText($log), "(?ms)^## $([regex]::Escape($Version))\s*\r?\n(.*?)(?=^## |\z)")
    if ($m.Success -and $m.Groups[1].Value.Trim()) { $body = $m.Groups[1].Value.Trim() }
}
$title = "SwarlexBattery $Version"
if ($Beta) {
    $title += ' beta'
    $body = "> [!NOTE]`n> **Beta (pre-release).** Offered only to copies with right-click > *Get beta versions* turned on. Everyone else gets these changes with the next stable release.`n`n" + $body
}
$notesFile = Join-Path ([IO.Path]::GetTempPath()) "swarlexbattery-notes-$Version.md"
[IO.File]::WriteAllText($notesFile, $body +"`n`n---`nDownload **SwarlexBattery-Setup.exe** to install, or **SwarlexBattery.exe** to run it without installing. Installed copies update themselves (right-click > *Check for updates*).", (New-Object Text.UTF8Encoding $false))
$pre = @(); if ($Beta) { $pre = @('--prerelease') }
& $gh release create "v$Version" $setup $exe $sha --repo $Repo --target main --title $title --notes-file $notesFile @pre
Remove-Item -LiteralPath $notesFile -ErrorAction SilentlyContinue
if ($LASTEXITCODE -ne 0) { throw 'gh release create failed (the code was pushed, the release was not created)' }
Write-Host "Published: v$Version  https://github.com/$Repo/releases/tag/v$Version"
Write-Host 'Installed copies see the update within 6 hours (or right away with right-click > Check for updates).'

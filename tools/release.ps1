<#
.SYNOPSIS
  One command from local change to GitHub: every installed copy then offers "Guncelle".

  1. raises VERSION (patch by default, or -Version X.Y.Z)
  2. builds dist\SwarlexBattery.exe + .sha256
  3. commits every change, pushes to origin/main
  4. creates the GitHub release vX.Y.Z with the exe and its checksum (GitHub CLI "gh", signed in)

.EXAMPLE
  .\tools\release.ps1 -Notes "Kulaklik sarj algilama duzeltildi"          # 1.1.0 -> 1.1.1
  .\tools\release.ps1 -Version 1.2.0 -Notes "Logitech destegi"
#>
param(
    [string]$Version,
    [Parameter(Mandatory)] [string]$Notes,
    [string]$Repo = 'yukicanclaude/SwarlexBattery',
    [switch]$ByClaude          # adds the Co-Authored-By trailer to the commit
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh -and (Test-Path "$env:ProgramFiles\GitHub CLI\gh.exe")) { $gh = "$env:ProgramFiles\GitHub CLI\gh.exe" }
if (-not $gh) { throw 'GitHub CLI (gh) bulunamadi. Kur: winget install GitHub.cli, sonra: gh auth login' }
& $gh auth status *> $null
if ($LASTEXITCODE -ne 0) { throw 'GitHub CLI giris yapmamis: gh auth login --web' }

# nothing to publish?
$changes = git status --porcelain
if (-not $changes -and -not $Version) { Write-Host 'Degisiklik yok; yayinlanacak bir sey yok.'; return }

# 1. version
$vf = Join-Path $root 'VERSION'
$cur = [version](Get-Content -LiteralPath $vf -Raw).Trim()
if (-not $Version) { $Version = "$($cur.Major).$($cur.Minor).$($cur.Build + 1)" }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Surum X.Y.Z olmali: $Version" }
if ([version]$Version -le $cur) { throw "Yeni surum ($Version) mevcut surumden ($cur) buyuk olmali" }
Set-Content -LiteralPath $vf -Value $Version -Encoding ASCII

# 2. build (a failed build stops here: nothing is pushed or published)
try { & (Join-Path $root 'build.ps1') }
catch { Set-Content -LiteralPath $vf -Value "$cur" -Encoding ASCII; throw "Derleme basarisiz, yayinlanmadi: $_" }
$exe = Join-Path $root 'dist\SwarlexBattery.exe'
$sha = "$exe.sha256"

# 3. commit + push
$msg = "v$Version`: $Notes"
if ($ByClaude) { $msg += "`n`nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>" }
git add -A
git commit -q -m $msg
if ($LASTEXITCODE -ne 0) { throw 'git commit basarisiz' }
git push -q origin HEAD:main
if ($LASTEXITCODE -ne 0) { throw 'git push basarisiz' }

# 4. GitHub release (the tag is created on the pushed commit)
& $gh release create "v$Version" $exe $sha --repo $Repo --target main --title "SwarlexBattery v$Version" --notes $Notes
if ($LASTEXITCODE -ne 0) { throw 'gh release create basarisiz (kod gonderildi, surum yayinlanmadi)' }
Write-Host "Yayinlandi: v$Version  https://github.com/$Repo/releases/tag/v$Version"
Write-Host 'Kurulu surumler 6 saat icinde (ya da sag tik > Guncellemeleri denetle ile hemen) guncellemeyi gorur.'

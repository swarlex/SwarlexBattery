<#
.SYNOPSIS
  Publishes a new SwarlexBattery version so that every installed copy offers "Guncelle".

  1. raises VERSION (or uses -Version), 2. builds dist\SwarlexBattery.exe + .sha256,
  3. creates the GitHub release v<version> on <repo> with both files (needs the GitHub CLI "gh",
     signed in with `gh auth login`). Without gh it prints the two files to upload by hand.

.EXAMPLE
  .\tools\release.ps1                 # 1.1.0 -> 1.1.1
  .\tools\release.ps1 -Version 1.2.0 -Notes "Logitech destegi"
#>
param([string]$Version, [string]$Notes = '', [string]$Repo = 'yukicanclaude/SwarlexBattery')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$vf = Join-Path $root 'VERSION'
$cur = [version](Get-Content -LiteralPath $vf -Raw).Trim()
if (-not $Version) { $Version = "$($cur.Major).$($cur.Minor).$($cur.Build + 1)" }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Surum X.Y.Z olmali: $Version" }
if ([version]$Version -le $cur) { throw "Yeni surum ($Version) mevcut surumden ($cur) buyuk olmali" }
Set-Content -LiteralPath $vf -Value $Version -Encoding ASCII

& (Join-Path $root 'build.ps1')
$exe = Join-Path $root 'dist\SwarlexBattery.exe'
$sha = "$exe.sha256"

$gh = Get-Command gh -ErrorAction SilentlyContinue
if (-not $gh) {
    Write-Host ''
    Write-Host "GitHub CLI (gh) yok. Elle yayinla: https://github.com/$Repo/releases/new"
    Write-Host "  Tag: v$Version"
    Write-Host "  Dosyalar: $exe"
    Write-Host "            $sha"
    return
}
& gh release create "v$Version" $exe $sha --repo $Repo --title "SwarlexBattery v$Version" --notes $(if ($Notes) { $Notes } else { "SwarlexBattery v$Version" })
if ($LASTEXITCODE -ne 0) { throw 'gh release create basarisiz' }
Write-Host "Yayinlandi: v$Version. Kurulu surumler 6 saat icinde (ya da sag tik > Guncellemeleri denetle ile hemen) guncellemeyi gorur."

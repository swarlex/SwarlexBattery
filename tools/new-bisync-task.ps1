<#
.SYNOPSIS
  Creates a per-user Task Scheduler job that runs `rclone bisync` every N minutes
  (Windows replacement for the systemd --user timer omabisync watches).

.EXAMPLE
  .\new-bisync-task.ps1 -Path1 "$env:USERPROFILE\Sync" -Path2 "pcloud:Sync" -Minutes 30 -Resync
  -Resync runs the mandatory first `--resync` once, in this window, before scheduling.
#>
param(
    [Parameter(Mandatory)] [string]$Path1,
    [Parameter(Mandatory)] [string]$Path2,
    [int]$Minutes = 30,
    [string]$TaskName = 'rclone-bisync',
    [string]$LogFile = (Join-Path $env:LOCALAPPDATA 'SwarlexBattery\rclone-bisync.log'),
    [switch]$Resync
)
$ErrorActionPreference = 'Stop'

$rclone = (Get-Command rclone.exe -ErrorAction SilentlyContinue).Source
if (-not $rclone) { throw 'rclone.exe PATH icinde bulunamadi. Kur: winget install Rclone.Rclone' }
$null = New-Item -ItemType Directory -Force -Path (Split-Path $LogFile)

# --fast-list: lists the remote in one go (omabisync notes: 25-55 min -> 2 min on large pCloud trees).
# --resilient/--recover: an interrupted run doesn't need a manual --resync.
$common = @('bisync', $Path1, $Path2, '--fast-list', '--resilient', '--recover', '--max-lock', '2m',
            '--create-empty-src-dirs', '--log-file', $LogFile, '--log-level', 'INFO')

if ($Resync) {
    Write-Host 'Ilk --resync calisiyor (bu bir kerelik, uzun surebilir)...'
    & $rclone @common --resync
    if ($LASTEXITCODE -ne 0) { throw "rclone --resync basarisiz (kod $LASTEXITCODE). Log: $LogFile" }
}

function Quote([string]$s) { '"' + ($s -replace '"', '\"') + '"' }
$argLine = ($common | ForEach-Object { Quote $_ }) -join ' '

# conhost --headless keeps the console window from flashing every interval (Windows 11 / 10 22H2+).
$conhost = Join-Path $env:WINDIR 'System32\conhost.exe'
$action = New-ScheduledTaskAction -Execute $conhost -Argument "--headless $(Quote $rclone) $argLine"
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes $Minutes)
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 3)
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal `
    -Description "rclone bisync $Path1 <-> $Path2 (SwarlexBattery)" -Force | Out-Null
Write-Host "Gorev '$TaskName' olusturuldu: her $Minutes dakikada bir. Log: $LogFile"

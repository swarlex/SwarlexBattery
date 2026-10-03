<#
  Pocket for Windows (port of io.github.oidium.pocket).
  Talks to the official KDE Connect for Windows through kdeconnect-cli.exe
  (and qdbus.exe for battery when present). Device ids are validated and
  passed as separate argv items: no shell string is ever built.
#>
param([string]$Action = 'poll', [string]$Arg = '', $Config, [string]$PluginDir, [string]$StateDir)

$cli = @($Config.cliPath,
    "$env:ProgramFiles\KDE Connect\bin\kdeconnect-cli.exe",
    "${env:ProgramFiles(x86)}\KDE Connect\bin\kdeconnect-cli.exe",
    "$env:LOCALAPPDATA\Programs\KDE Connect\bin\kdeconnect-cli.exe",
    (Get-Command kdeconnect-cli.exe -ErrorAction SilentlyContinue).Source) |
    Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1

if (-not $cli) {
    if ($Action -eq 'install') { Start-Process 'https://kdeconnect.kde.org/download.html' }
    return @{
        title = 'Pocket'
        pill = @{ state = 'off'; tooltip = 'KDE Connect kurulu degil' }
        sections = @(@{ items = @(
            @{ t = 'text'; text = 'KDE Connect for Windows bulunamadi. Microsoft Store veya kdeconnect.kde.org uzerinden kur, telefonunda KDE Connect uygulamasiyla esle.' },
            @{ t = 'buttons'; items = @(@{ text = "Store'da ac"; icon = 'E719'; action = 'install'; primary = $true }) }) })
    }
}
$bin = Split-Path $cli
$qdbus = Join-Path $bin 'qdbus.exe'

function Kdc([string[]]$a) {
    $r = Invoke-Native $cli $a
    @($r.out) + @($r.err -split "`r?`n" | Where-Object { $_ })
}
function Check-Id([string]$id) { if ($id -notmatch '^[A-Za-z0-9_]+$') { throw 'Gecersiz cihaz kimligi' }; $id }

$toast = $null
$parts = $Arg -split '\|', 2
switch ($Action) {
    'ping'   { Kdc @('-d', (Check-Id $Arg), '--ping-msg', 'SwarlexBattery') | Out-Null; $toast = 'Ping gonderildi' }
    'ring'   { Kdc @('-d', (Check-Id $Arg), '--ring') | Out-Null; $toast = 'Telefon caliyor' }
    'clip'   {
        $text = Get-Clipboard -Raw
        if ($text) { Kdc @('-d', (Check-Id $Arg), '--share-text', $text) | Out-Null; $toast = 'Pano gonderildi' } else { $toast = 'Pano bos' }
    }
    'file'   {
        Add-Type -AssemblyName System.Windows.Forms
        $owner = New-Object Windows.Forms.Form -Property @{ TopMost = $true; ShowInTaskbar = $false; Opacity = 0 }
        $owner.Show(); $owner.Activate()
        $dlg = New-Object Windows.Forms.OpenFileDialog -Property @{ Title = 'Telefona dosya gonder'; Multiselect = $true }
        if ($dlg.ShowDialog($owner) -eq 'OK') {
            $id = Check-Id $Arg
            foreach ($f in $dlg.FileNames) { Kdc @('-d', $id, '--share', $f) | Out-Null }
            $toast = "$($dlg.FileNames.Count) dosya gonderildi"
        }
        $owner.Close()
    }
    'refresh' { Kdc @('--refresh') | Out-Null; Start-Sleep 2 }
    'app'     { $ind = Join-Path $bin 'kdeconnect-app.exe'; if (Test-Path -LiteralPath $ind) { Start-Process $ind } }
    'daemon'  { $ind = Join-Path $bin 'kdeconnect-indicator.exe'; if (Test-Path -LiteralPath $ind) { Start-Process $ind; Start-Sleep 3 } }
}

# ---- status
$list = Kdc @('-a', '--id-name-only')
$devices = @()
foreach ($l in $list) { if ($l -match '^([A-Za-z0-9_]+) (.+)$') { $devices += @{ id = $Matches[1]; name = $Matches[2].Trim() } } }
$daemonDown = (-not $devices) -and (($list -join ' ') -match 'dbus|daemon|connect|Could not|not running')

$sections = @()
foreach ($d in $devices) {
    $items = @()
    $bat = $null; $chg = $false
    if (Test-Path -LiteralPath $qdbus) {
        try {
            $path = "/modules/kdeconnect/devices/$($d.id)/battery"
            $v = "$((Invoke-Native $qdbus @('org.kde.kdeconnect', $path, 'org.kde.kdeconnect.device.battery.charge') 4000).out)".Trim()
            if ($v -match '^\d+$' -and [int]$v -ge 0) {
                $bat = [int]$v
                $chg = "$((Invoke-Native $qdbus @('org.kde.kdeconnect', $path, 'org.kde.kdeconnect.device.battery.isCharging') 4000).out)".Trim() -eq 'true'
            }
        } catch {}
    }
    $d.battery = $bat; $d.charging = $chg
    if ($null -ne $bat) {
        $items += @{ t = 'bar'; icon = 'E8EA'; label = $d.name; value = "$bat%"; pct = $bat / 100.0; sub = $(if ($chg) { 'Bagli - sarj oluyor' } else { 'Bagli' }); state = $(if ($bat -le 15 -and -not $chg) { 'error' } else { 'ok' }) }
    } else {
        $items += @{ t = 'row'; icon = 'E8EA'; label = $d.name; value = 'Bagli'; state = 'ok' }
    }
    $notes = @(Kdc @('-d', $d.id, '--list-notifications') | Where-Object { $_.Trim() } | Select-Object -First 8)
    $d.notes = $notes.Count
    if ($notes) {
        $items += @{ t = 'sep' }
        foreach ($n in $notes) {
            $n = $n.Trim().TrimStart('-').Trim()
            $app, $rest = $n -split ':\s*', 2
            $items += @{ t = 'row'; icon = 'EA8F'; label = $(if ($rest) { $rest } else { $n }); sub = $(if ($rest) { $app } else { '' }) }
        }
    }
    $items += @{ t = 'buttons'; items = @(
        @{ text = 'Dosya gonder'; icon = 'E724'; action = 'file'; arg = $d.id; primary = $true },
        @{ text = 'Pano gonder'; icon = 'E77F'; action = 'clip'; arg = $d.id },
        @{ text = 'Caldir'; icon = 'EA8F'; action = 'ring'; arg = $d.id },
        @{ text = 'Ping'; icon = 'E724'; action = 'ping'; arg = $d.id }) }
    $sections += @{ title = $d.name; items = $items }
}
if (-not $devices) {
    $msg = if ($daemonDown) { 'KDE Connect arka plan servisi calismiyor.' } else { 'Erisilebilir esli telefon yok. Telefon ve PC ayni agda mi?' }
    $btns = @(@{ text = 'Yenile'; icon = 'E72C'; action = 'refresh' }, @{ text = 'KDE Connect'; icon = 'E8A7'; action = 'app' })
    if ($daemonDown) { $btns = @(@{ text = 'Servisi baslat'; icon = 'E768'; action = 'daemon'; primary = $true }) + $btns }
    $sections += @{ items = @(@{ t = 'text'; text = $msg }, @{ t = 'buttons'; items = $btns }) }
} else {
    $sections += @{ items = @(@{ t = 'buttons'; items = @(@{ text = 'Yenile'; icon = 'E72C'; action = 'refresh' }, @{ text = 'KDE Connect'; icon = 'E8A7'; action = 'app' }) }) }
}

$first = $devices | Select-Object -First 1
$noteCount = ($devices | ForEach-Object { $_.notes } | Measure-Object -Sum).Sum
@{
    title = 'Pocket'
    toast = $toast
    notify = @($devices | Where-Object { $null -ne $_.battery -and $_.battery -le 15 } | ForEach-Object { @{ key = "low-$($_.id)"; title = "$($_.name) pili azaldi"; body = "%$($_.battery)" } })
    pill = @{
        state = $(if (-not $first) { 'off' } elseif ($null -ne $first.battery -and $first.battery -le 15 -and -not $first.charging) { 'error' } elseif ($noteCount) { 'busy' } else { 'ok' })
        ring = $(if ($first -and $null -ne $first.battery) { $first.battery / 100.0 } else { $null })
        charging = [bool]($first -and $first.charging)
        dim = (-not $first)
        tooltip = $(if ($first) { "$($first.name)$(if ($null -ne $first.battery) { " $($first.battery)%" })$(if ($noteCount) { " - $noteCount bildirim" })" } else { 'Telefon bagli degil' })
    }
    sections = $sections
}

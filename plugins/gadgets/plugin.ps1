<#
  Gadget Batteries for Windows (port of io.github.69harold69.gadget-batteries).
  Sources: laptop battery (CIM), Bluetooth devices (the battery property Windows
  Settings shows), Xbox/XInput pads, external.json and collectors.d scripts using
  the original plugin's external JSON format:
    [{"id","name","kind","pct","charging","ts","ttl","left","right","case"}]
#>
param([string]$Action = 'poll', [string]$Arg = '', $Config, [string]$PluginDir, [string]$StateDir)

if ($Action -eq 'bt-settings') { Start-Process 'ms-settings:bluetooth' }

$BATTERY_KEY = '{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2'   # DEVPKEY_Bluetooth_Battery
$CONNECTED_KEY = '{83DA6326-97A6-4088-9453-A1923F573B29} 15' # DEVPKEY_Device_IsConnected (BT)
$gadgets = New-Object Collections.ArrayList
$errors = @()

function Guess-Kind([string]$name, [string]$cls) {
    switch -Regex ($name) {
        'buds|airpods|earbud|wf-|freebuds|pods' { return 'earbuds' }
        'headset|headphone|wh-|bose|qc\d|jbl|sony|arctis|hyperx|corsair' { return 'headphones' }
        'mouse|mx master|mx anywhere|g\d{3}|viper|deathadder|razer' { return 'mouse' }
        'keyboard|keys|mx keys|k\d{3}' { return 'keyboard' }
        'controller|xbox|dualsense|dualshock|gamepad|pro controller' { return 'gamepad' }
        'pen|stylus' { return 'pen' }
        'watch' { return 'watch' }
        'speaker|soundcore|boom|flip|charge' { return 'speaker' }
        'phone|iphone|galaxy|pixel' { return 'phone' }
    }
    if ($cls -eq 'Mouse') { return 'mouse' }; if ($cls -eq 'Keyboard') { return 'keyboard' }
    return 'other'
}

function Add-Gadget($g) {
    if ($Config.names -and $Config.names.($g.name)) { $g.name = $Config.names.($g.name) }
    [void]$gadgets.Add($g)
}

# Slow sources (CIM / PnP queries, ~1 s) are refreshed once a minute; the 2.4 GHz HID devices
# below are read on every poll (every 10 s, and right after a device is plugged in or out).
$slowFile = Join-Path $StateDir 'slow.json'
$slowFresh = (Test-Path -LiteralPath $slowFile) -and ((Get-Date) - (Get-Item -LiteralPath $slowFile).LastWriteTime).TotalSeconds -lt 55
if ($slowFresh -and $Action -eq 'poll') {
    try {
        foreach ($o in @(Get-Content -LiteralPath $slowFile -Raw | ConvertFrom-Json)) {
            if (-not $o) { continue }
            $h = @{}; foreach ($p in $o.PSObject.Properties) { $h[$p.Name] = $p.Value }; [void]$gadgets.Add($h)
        }
    } catch { $slowFresh = $false }
}
if (-not ($slowFresh -and $Action -eq 'poll')) {
$gadgets.Clear()

# ---- laptop / UPS battery
if ($Config.systemBattery) {
    try {
        # GetSystemPowerStatus: BatteryFlag 128 = no battery (desktop), 8 = really charging.
        # (Win32_Battery's "on AC" status is not "charging": a full battery on AC is not charging.)
        $ps = [System.Windows.Forms.SystemInformation]::PowerStatus
        if ($ps -and -not ($ps.BatteryChargeStatus -band [System.Windows.Forms.BatteryChargeStatus]::NoSystemBattery) -and $ps.BatteryLifePercent -le 1) {
            Add-Gadget @{ id = 'system'; name = 'Bu bilgisayar'; kind = 'laptop'; pct = [int][Math]::Round($ps.BatteryLifePercent * 100)
                          charging = [bool]($ps.BatteryChargeStatus -band [System.Windows.Forms.BatteryChargeStatus]::Charging); online = $true }
        }
    } catch { $errors += "Sistem pili: $_" }
}

# ---- Bluetooth (classic + LE). One CIM round trip for all property reads.
if ($Config.bluetooth) {
    try {
        $devs = @(Get-PnpDevice -PresentOnly -ErrorAction Stop | Where-Object { $_.InstanceId -match '^(BTHENUM|BTHLE|BTHLEDEVICE)\\' })
        if ($devs.Count) {
            $props = @(Get-PnpDeviceProperty -InstanceId $devs.InstanceId -KeyName $BATTERY_KEY, $CONNECTED_KEY -ErrorAction SilentlyContinue)
            $byId = @{}
            foreach ($p in $props) { if (-not $byId[$p.InstanceId]) { $byId[$p.InstanceId] = @{} }; $byId[$p.InstanceId][$p.KeyName] = $p.Data }
            $seen = @{}
            foreach ($d in $devs) {
                $pp = $byId[$d.InstanceId]; if (-not $pp) { continue }
                $lvl = $pp[$BATTERY_KEY]; if ($null -eq $lvl -or "$lvl" -eq '') { continue }
                $conn = $pp[$CONNECTED_KEY]
                $online = -not ($conn -is [bool] -and -not $conn)
                if (-not $online -and -not $Config.showDisconnected) { continue }
                # one physical device shows up as several service nodes: dedupe on MAC
                $mac = if ($d.InstanceId -match '([0-9A-F]{12})') { $Matches[1] } else { $d.FriendlyName }
                if ($seen[$mac]) { continue }; $seen[$mac] = 1
                Add-Gadget @{ id = "bt-$mac"; name = $d.FriendlyName; kind = (Guess-Kind $d.FriendlyName $d.Class); pct = [int]$lvl; charging = $false; online = $online }
            }
        }
    } catch { $errors += "Bluetooth: $_" }
}
Set-Content -LiteralPath $slowFile -Encoding UTF8 -Value $(if ($gadgets.Count) { ConvertTo-Json @($gadgets) -Depth 4 } else { '[]' })
}   # end of slow sources

# ---- wireless mice / keyboards / headsets read directly over HID (core\Devices.cs):
#   Razer, Logitech (HID++), SteelSeries, HyperX, Corsair, ATK / VXE / Pulsar / Hitscan.
# Honesty rules: a value is shown only when the device itself answered. One missed answer keeps
# the last value for 45 s (no flicker); after that the device is shown dimmed as asleep with its
# last value and the age of that reading; after 24 h it disappears.
if ($Config.hid -and ('SwarlexBattery.Hid' -as [type])) {
    $lastFile = Join-Path $StateDir 'hid-last.json'
    $last = @{}
    if (Test-Path -LiteralPath $lastFile) { try { (Get-Content -LiteralPath $lastFile -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $last[$_.Name] = $_.Value } } catch {} }
    try {
        $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        $byId = [ordered]@{}
        foreach ($r in [SwarlexBattery.Hid]::ReadAll()) {
            $cur = $byId[$r.Id]
            # one device seen twice (receiver + cable): a real reading beats none, a charging one wins
            if (-not $cur -or ($cur.Level -lt 0 -and $r.Level -ge 0) -or ($r.Level -ge 0 -and $r.Charging)) { $byId[$r.Id] = $r }
            # the device on its cable reports its real model name; the receiver only its own
            if (-not $r.Receiver -and $r.Name) { $byId["name:$($r.Id)"] = $r.Name }
        }
        foreach ($id in @($byId.Keys | Where-Object { $_ -notlike 'name:*' })) {
            $r = $byId[$id]
            $prev = $last[$id]
            $name = if ($byId["name:$id"]) { $byId["name:$id"] } elseif ($prev -and $prev.realName) { $prev.realName } else { $r.Name }
            $real = if ($byId["name:$id"]) { $byId["name:$id"] } elseif ($prev) { $prev.realName } else { $null }
            if ($r.Level -ge 0) {
                $last[$id] = @{ pct = $r.Level; charging = $r.Charging; approx = $r.Approx; ts = $now; name = $name; kind = $r.Kind; realName = $real }
                Add-Gadget @{ id = $id; name = $name; kind = $r.Kind; pct = $r.Level; charging = $r.Charging; approx = $r.Approx; online = $true }
            } elseif ($prev) {
                $age = $now - [long]$prev.ts
                if ($age -lt 45) {
                    Add-Gadget @{ id = $id; name = $name; kind = $r.Kind; pct = [int]$prev.pct; charging = [bool]$prev.charging; approx = [bool]$prev.approx; online = $true }
                } elseif ($age -lt 86400) {
                    $mins = [int]($age / 60)
                    Add-Gadget @{ id = $id; name = $name; kind = $r.Kind; pct = [int]$prev.pct; charging = $false; approx = [bool]$prev.approx; online = $false; asleep = $true
                                  detail = "uyku modunda - son okuma $(if ($mins -lt 60) { "$mins dk" } else { "$([int]($mins / 60)) sa" }) once" }
                }
            }
        }
        # forget devices not seen for a week
        foreach ($k in @($last.Keys)) { if (($now - [long]$last[$k].ts) -gt 604800) { $last.Remove($k) } }
        $last | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $lastFile -Encoding UTF8
    } catch { $errors += "HID: $_" }
}

# ---- Xbox / XInput controllers
if ($Config.xinput -and ('SwarlexBattery.Gamepad' -as [type])) {
    foreach ($s in [SwarlexBattery.Gamepad]::List()) {
        $i, $type, $pct = $s -split '\|'
        if ($type -eq 'wired') { continue }
        Add-Gadget @{ id = "xinput-$i"; name = "Kumanda $([int]$i + 1)"; kind = 'gamepad'; pct = [int]$pct; charging = $false; online = $true; approx = $true }
    }
}

# ---- external JSON + collectors.d (same format as the Linux plugin)
function Import-External($arr, [string]$src) {
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    foreach ($e in @($arr)) {
        if (-not $e -or -not $e.name) { continue }
        $online = -not ($e.ts -and $e.ttl -and ($now - [long]$e.ts) -gt [long]$e.ttl)
        if (-not $online -and -not $Config.showDisconnected) { continue }
        $parts = @(); foreach ($k in 'left', 'right', 'case') { if ($null -ne $e.$k -and [int]$e.$k -ge 0) { $parts += "$k $($e.$k)%" } }
        Add-Gadget @{ id = $(if ($e.id) { $e.id } else { $e.name }); name = $e.name; kind = $(if ($e.kind) { $e.kind } else { 'other' })
                      pct = [int]$e.pct; charging = [bool]$e.charging; online = $online; detail = ($parts -join ', '); src = $src }
    }
}
$ext = if ($Config.externalPath) { [Environment]::ExpandEnvironmentVariables($Config.externalPath) } else { Join-Path $env:APPDATA 'SwarlexBattery\gadgets\external.json' }
if (Test-Path -LiteralPath $ext) { try { Import-External (Get-Content -LiteralPath $ext -Raw | ConvertFrom-Json) 'external.json' } catch { $errors += "external.json: $_" } }

$cache = Join-Path $StateDir 'collectors.json'
$fresh = if ($Action -eq 'poll') { 45 } else { 0 }
$colDirs = @((Join-Path $PluginDir 'collectors.d'), (Join-Path $env:APPDATA 'SwarlexBattery\gadgets\collectors.d'))
$scripts = @(foreach ($d in $colDirs) { if (Test-Path -LiteralPath $d) { Get-ChildItem -LiteralPath $d -File | Where-Object { $_.Extension -in '.ps1', '.exe', '.cmd', '.bat' } } })
if ($scripts.Count) {
    if ((Test-Path -LiteralPath $cache) -and ((Get-Date) - (Get-Item -LiteralPath $cache).LastWriteTime).TotalSeconds -lt $fresh) {
        $collected = Get-Content -LiteralPath $cache -Raw | ConvertFrom-Json
    } else {
        $collected = @()
        foreach ($s in $scripts) {
            # each collector in its own process with a 20 s timeout, so one broken script never hides the rest
            $psi = New-Object Diagnostics.ProcessStartInfo
            if ($s.Extension -eq '.ps1') { $psi.FileName = 'powershell.exe'; $psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$($s.FullName)`"" }
            else { $psi.FileName = $s.FullName }
            $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.CreateNoWindow = $true
            $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
            try {
                $proc = [Diagnostics.Process]::Start($psi)
                $task = $proc.StandardOutput.ReadToEndAsync()
                if (-not $proc.WaitForExit(20000)) { $proc.Kill(); $errors += "$($s.Name): zaman asimi"; continue }
                $data = $task.Result | ConvertFrom-Json
                $collected += @($data | Where-Object { $_ -and $_.name })
            } catch { $errors += "$($s.Name): $_" }
        }
        ConvertTo-Json @($collected) -Depth 4 | Set-Content -LiteralPath $cache -Encoding UTF8
    }
    Import-External $collected 'collector'
}

# ---------------------------------------------------------------- output
$icons = @{ earbuds = 'E7F6'; headphones = 'E7F6'; mouse = 'E962'; keyboard = 'E765'; gamepad = 'E7FC'; pen = 'EDC6'; watch = 'E916'; speaker = 'E7F5'; phone = 'E8EA'; laptop = 'E7F8'; other = 'E702' }
function Battery-Glyph([int]$pct, [bool]$charging) {
    $step = [Math]::Max(0, [Math]::Min(10, [int][Math]::Round($pct / 10)))
    if ($charging) { if ($step -eq 10) { 'E83E' } else { '{0:X}' -f (0xE85A + $step) } }
    else { if ($step -eq 10) { 'E83F' } else { '{0:X}' -f (0xE850 + $step) } }
}

$low = [int]$Config.lowThreshold
$items = @()
$notify = @()
# simple list: mouse first, then headset, then the rest; sleeping devices last
$order = @{ mouse = 0; headphones = 1; earbuds = 1; keyboard = 2; gamepad = 3 }
$sorted = @($gadgets | Sort-Object @{ e = { -not ($_.online -or $_.asleep) } }, @{ e = { if ($order.ContainsKey($_.kind)) { $order[$_.kind] } else { 9 } } }, @{ e = { $_.name } })
$shown = @($sorted | Where-Object { $_.online -or $_.asleep -or $Config.showDisconnected })
foreach ($g in $shown) {
    $state = if (-not $g.online) { 'off' } elseif ($g.pct -le $low -and -not $g.charging) { 'error' } else { '' }
    # wording: a full device on its cable is 'dolu', not 'charging'; coarse levels say so
    $sub = if ($g.charging -and $g.pct -ge 100) { 'dolu (kabloda)' } elseif ($g.charging) { 'sarj oluyor' } elseif ($g.detail) { $g.detail } elseif (-not $g.online) { 'bagli degil' } else { '' }
    if ($g.approx) { $sub = (@($sub, 'yaklasik deger') | Where-Object { $_ }) -join ' - ' }
    $items += @{ t = 'bar'; icon = $icons[$(if ($icons[$g.kind]) { $g.kind } else { 'other' })]; label = $g.name; value = "$($g.pct)%"; pct = ($g.pct / 100.0); state = $state; sub = $sub }
    if ($low -gt 0 -and $g.online -and -not $g.charging -and $g.pct -le $low) {
        $notify += @{ key = "low-$($g.id)"; title = "$($g.name) pili azaldi"; body = "%$($g.pct) kaldi." }
    }
}
if (-not $items) {
    $items += @{ t = 'text'; text = 'Pil bilgisi veren cihaz bulunamadi. Mouse veya kulakligi acip biraz kullan; birkac saniye icinde gorunur.' }
    foreach ($e in $errors) { $items += @{ t = 'text'; text = $e; state = 'warn' } }
}

# one tray icon per gadget: ring = battery, glyph = device type (HaloBattery style)
$trayIcons = @(foreach ($g in $shown) {
    $st = if ($g.pct -le $low -and -not $g.charging) { 'error' } elseif ($g.pct -le $low + 10 -and -not $g.charging) { 'warn' } else { 'ok' }
    $tip = "$($g.name): $(if ($g.approx) { '~' })$($g.pct)%" + $(if ($g.charging -and $g.pct -ge 100) { ' - dolu' } elseif ($g.charging) { ' - sarj oluyor' } elseif ($g.asleep) { ' - uyku modunda' } elseif (-not $g.online) { ' - bagli degil' } else { '' })
    @{ id = $g.id; icon = $icons[$(if ($icons[$g.kind]) { $g.kind } else { 'other' })]; ring = ($g.pct / 100.0); state = $st
       charging = [bool]$g.charging; dim = (-not $g.online); tooltip = $tip }
})
# combine (default): one tray icon for everything. Two devices -> the ring is split,
# left half = first device (mouse), right half = second (headset); one device -> full ring.
if ($Config.combine -and $shown.Count -ge 1) {
    $label = @{ mouse = 'Mouse'; headphones = 'Kulaklik'; earbuds = 'Kulaklik'; keyboard = 'Klavye'; gamepad = 'Kumanda' }
    $pair = @($shown | Select-Object -First 2)
    $parts = foreach ($g in $shown) {
        $n = if ($label[$g.kind]) { $label[$g.kind] } else { $g.name }
        "$n $(if ($g.approx) { '~' })$($g.pct)%" + $(if ($g.charging -and $g.pct -ge 100) { ' (dolu)' } elseif ($g.charging) { ' (sarj)' } elseif (-not $g.online) { ' (uyku)' } else { '' })
    }
    $lowest = $pair | Where-Object { $_.online -and -not $_.charging } | Sort-Object pct | Select-Object -First 1
    $trayIcons = @(@{
        id = 'all'
        icon = $(if ($pair.Count -eq 1) { $icons[$(if ($icons[$pair[0].kind]) { $pair[0].kind } else { 'other' })] } else { 'E83F' })
        rings = @($pair | ForEach-Object { $_.pct / 100.0 })
        state = $(if ($lowest -and $lowest.pct -le $low) { 'error' } elseif ($lowest -and $lowest.pct -le $low + 10) { 'warn' } else { 'ok' })
        charging = [bool]($pair | Where-Object { $_.charging })
        dim = -not ($pair | Where-Object { $_.online })
        tooltip = ($parts -join '  |  ')
    })
}
# nothing found yet: keep one dim battery icon so the menu (and Exit) stays reachable
if (-not $trayIcons) { $trayIcons = @(@{ id = 'none'; icon = 'E83F'; state = 'off'; dim = $true; tooltip = 'Piller - cihaz bulunamadi' }) }

@{
    title = 'Piller'
    notify = $notify
    icons = $trayIcons
    sections = @(@{ items = $items })
}

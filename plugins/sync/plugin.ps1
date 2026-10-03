<#
  Sync: Syncthing (REST API) + rclone bisync (Task Scheduler + log file) in one pill.
  Windows port + merge of syncthing.bar (BeringLogic) and omabisync (hverbruggen).
  Read-only on poll; every write is an explicit button.
#>
param([string]$Action = 'poll', [string]$Arg = '', $Config, [string]$PluginDir, [string]$StateDir)

$st = $Config.syncthing
$rc = $Config.rclone

# ================================================================ Syncthing
function Get-StKey {
    if ($st.apiKey) { return $st.apiKey }
    $paths = @($st.configPath,
        "$env:LOCALAPPDATA\Syncthing\config.xml",
        "$env:APPDATA\Syncthing\config.xml",
        "$env:LOCALAPPDATA\SyncTrayzor\syncthing\config.xml",
        "$env:APPDATA\SyncTrayzor\syncthing\config.xml") | Where-Object { $_ }
    foreach ($p in $paths) {
        $p = [Environment]::ExpandEnvironmentVariables($p)
        if (Test-Path -LiteralPath $p) {
            try { $k = ([xml](Get-Content -LiteralPath $p -Raw)).configuration.gui.apikey; if ($k) { return "$k".Trim() } } catch {}
        }
    }
    return $null
}

$StBase = ([string]$st.apiBase).TrimEnd('/')
$StKey = $null
function St([string]$path, [string]$method = 'GET', $body = $null) {
    $p = @{ Uri = "$StBase$path"; Method = $method; TimeoutSec = 4; UseBasicParsing = $true; Headers = @{ 'X-API-Key' = $StKey } }
    if ($null -ne $body) { $p.Body = ($body | ConvertTo-Json -Compress); $p.ContentType = 'application/json' }
    Invoke-RestMethod @p
}
function Esc([string]$s) { [uri]::EscapeDataString($s) }

function Format-Bytes([double]$b) {
    if ($b -ge 1GB) { '{0:N1} GB' -f ($b / 1GB) } elseif ($b -ge 1MB) { '{0:N1} MB' -f ($b / 1MB) } elseif ($b -ge 1KB) { '{0:N0} KB' -f ($b / 1KB) } else { "$([int]$b) B" }
}
function Ago($t) {
    if (-not $t) { return 'hic' }
    $d = (Get-Date) - $t
    if ($d.TotalSeconds -lt 0) { $d = $t - (Get-Date); $pre = ''; $suf = ' sonra' } else { $pre = ''; $suf = ' once' }
    if ($d.TotalMinutes -lt 1) { return 'simdi' }
    if ($d.TotalHours -lt 1) { return "$pre$([int]$d.TotalMinutes) dk$suf" }
    if ($d.TotalDays -lt 1) { return "$pre$([int]$d.TotalHours) sa$suf" }
    return "$pre$([int]$d.TotalDays) gun$suf"
}

function Get-Syncthing {
    $r = @{ state = 'off'; items = @(); summary = '' }
    if (-not $st.enabled) { return $null }
    $script:StKey = Get-StKey
    try { $sys = St '/rest/system/status' }
    catch {
        $msg = $_.Exception.Message
        if ($msg -match '403|Forbidden') { $r.state = 'error'; $r.summary = 'API anahtari reddedildi'; $r.items += @{ t = 'text'; text = 'API anahtari bulunamadi/yanlis. config.json > plugins.sync.syncthing.apiKey ayarla.'; state = 'error' } }
        else { $r.summary = 'Syncthing calismiyor'; $r.items += @{ t = 'text'; text = "Syncthing'e ulasilamadi ($StBase)." } }
        $btn = @()
        if ($st.dockerContainer -and (Get-Command docker -ErrorAction SilentlyContinue)) { $btn += @{ text = 'Container baslat'; icon = 'E768'; action = 'st-docker'; arg = 'start'; primary = $true } }
        else {
            $exe = @("$env:LOCALAPPDATA\Programs\Syncthing\syncthing.exe", "$env:ProgramFiles\Syncthing\syncthing.exe") + @((Get-Command syncthing -ErrorAction SilentlyContinue).Source) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
            if ($exe) { $btn += @{ text = 'Syncthing baslat'; icon = 'E768'; action = 'st-launch'; arg = $exe; primary = $true } }
        }
        if ($btn) { $r.items += @{ t = 'buttons'; items = $btn } }
        return $r
    }

    $myId = $sys.myID
    $folders = @(St '/rest/config/folders')
    $devices = @(St '/rest/config/devices' | Where-Object { $_.deviceID -ne $myId })
    $conns = (St '/rest/system/connections').connections
    $pendDev = St '/rest/cluster/pending/devices'
    $pendFol = St '/rest/cluster/pending/folders'
    $pending = @($pendDev.PSObject.Properties).Count + @($pendFol.PSObject.Properties).Count

    $anyErr = $false; $anySync = $false; $allPaused = $folders.Count -gt 0; $need = 0.0; $totalBytes = 0.0
    $fItems = @()
    foreach ($f in $folders) {
        $label = if ($f.label) { $f.label } else { $f.id }
        if ($f.paused) {
            $fItems += @{ t = 'row'; icon = 'E769'; label = $label; value = 'Duraklatildi'; state = 'off'; sub = $f.path }
            continue
        }
        $allPaused = $false
        try { $db = St "/rest/db/status?folder=$(Esc $f.id)" } catch { $db = $null }
        $state = if ($db) { $db.state } else { 'unknown' }
        $pct = 1.0
        if ($db) {
            if ($db.globalBytes -gt 0) { $pct = 1 - ($db.needBytes / $db.globalBytes) }
            elseif ($db.globalFiles -gt 0) { $pct = 1 - ($db.needFiles / $db.globalFiles) }
            $need += [double]$db.needBytes
            $totalBytes += [double]$db.globalBytes
        }
        $pct = [Math]::Max(0.0, [Math]::Min(1.0, $pct))
        if ($state -eq 'error' -or ($db -and $db.pullErrors -gt 0)) {
            $anyErr = $true
            $fItems += @{ t = 'row'; icon = 'E783'; label = $label; value = 'Hata'; state = 'error'; sub = $(if ($db.error) { $db.error } else { "$($db.pullErrors) dosya senkronize edilemedi" }) }
        } elseif ($state -match 'sync|scan|clean') {
            $anySync = $true
            $txt = if ($state -match 'scan') { 'Taraniyor' } else { 'Senkronize ediliyor' }
            $fItems += @{ t = 'bar'; icon = 'E895'; label = $label; value = '{0:P0}' -f $pct; pct = $pct; state = 'busy'; sub = "$txt - $(Format-Bytes $db.needBytes) kaldi" }
        } else {
            $fItems += @{ t = 'row'; icon = 'E73E'; label = $label; value = 'Guncel'; state = 'ok'; sub = "$(Format-Bytes $db.globalBytes) - $($db.globalFiles) dosya" }
        }
        $fItems += @{ t = 'buttons'; items = @(
            @{ text = 'Tara'; icon = 'E72C'; action = 'st-rescan'; arg = $f.id },
            @{ text = 'Duraklat'; icon = 'E769'; action = 'st-pause-folder'; arg = $f.id }) }
    }
    # paused folders get a resume button
    foreach ($f in $folders | Where-Object { $_.paused }) {
        $fItems += @{ t = 'buttons'; items = @(@{ text = "Devam: $(if ($f.label) { $f.label } else { $f.id })"; icon = 'E768'; action = 'st-resume-folder'; arg = $f.id }) }
    }

    $dItems = @(); $online = 0
    foreach ($d in $devices) {
        $c = $conns.($d.deviceID)
        $on = $c -and $c.connected
        if ($on) { $online++ }
        $v = if ($d.paused) { 'Duraklatildi' } elseif ($on) { 'Bagli' } else { 'Cevrimdisi' }
        $s = if ($d.paused) { 'off' } elseif ($on) { 'ok' } else { 'off' }
        $sub = if ($on) { "$($c.address) - $($c.type)" } else { $d.deviceID.Substring(0, 7) }
        $dItems += @{ t = 'row'; icon = 'E772'; label = $d.name; value = $v; state = $s; sub = $sub }
    }

    $r.state = if ($anyErr) { 'error' } elseif ($pending -gt 0) { 'warn' } elseif ($anySync) { 'busy' } elseif ($allPaused) { 'off' } else { 'ok' }
    $r.summary = switch ($r.state) { 'error' { 'Klasor hatasi' } 'warn' { "$pending onay bekliyor" } 'busy' { "$(Format-Bytes $need) kaldi" } 'off' { 'Tumu duraklatildi' } default { 'Guncel' } }
    $r.items = @(@{ t = 'row'; icon = 'E753'; label = 'Syncthing'; value = $r.summary; state = $r.state; sub = "$online/$($devices.Count) cihaz bagli - $($sys.myID.Substring(0,7))" })
    if ($pending -gt 0) { $r.items += @{ t = 'text'; text = "$pending cihaz/klasor onay bekliyor - Web arayuzunden onayla."; state = 'warn' } }
    $r.items += $fItems
    if ($dItems) { $r.items += @{ t = 'sep' }; $r.items += $dItems }
    $btns = @(@{ text = 'Web arayuzu'; icon = 'E8A7'; action = 'st-web' }, @{ text = 'Tumunu tara'; icon = 'E72C'; action = 'st-rescan'; arg = '' })
    if ($st.dockerContainer) { $btns += @{ text = 'Container durdur'; icon = 'E71A'; action = 'st-docker'; arg = 'stop' } }
    $r.items += @{ t = 'buttons'; items = $btns }
    $r.syncing = $anySync
    if ($anySync -and $totalBytes -gt 0) { $r.pct = [Math]::Max(0.0, [Math]::Min(1.0, 1 - $need / $totalBytes)) }
    return $r
}

function Invoke-StAction {
    $script:StKey = Get-StKey
    switch ($Action) {
        'st-rescan' { if ($Arg) { St "/rest/db/scan?folder=$(Esc $Arg)" 'POST' | Out-Null } else { St '/rest/db/scan' 'POST' | Out-Null }; 'Tarama baslatildi' }
        # PATCH a single sub-resource: never round-trip /rest/config (it redacts gui.apiKey).
        'st-pause-folder' { St "/rest/config/folders/$(Esc $Arg)" 'PATCH' @{ paused = $true } | Out-Null; $null }
        'st-resume-folder' { St "/rest/config/folders/$(Esc $Arg)" 'PATCH' @{ paused = $false } | Out-Null; $null }
        'st-web' {
            $url = if ($st.webUrl) { $st.webUrl } else { "$StBase/" }
            if ($url -match '^https?://') { Start-Process $url }; $null
        }
        'st-launch' { if ($Arg -match 'syncthing\.exe$' -and (Test-Path -LiteralPath $Arg)) { Start-Process -FilePath $Arg -ArgumentList '--no-browser' -WindowStyle Hidden; Start-Sleep 3 }; 'Syncthing baslatiliyor' }
        'st-docker' {
            if ($Arg -notin 'start', 'stop' -or $st.dockerContainer -notmatch '^[A-Za-z0-9_.-]+$') { throw 'Gecersiz docker istegi' }
            $null = Invoke-Native 'docker' @($Arg, $st.dockerContainer) 30000; Start-Sleep 2
            "Container: $Arg"
        }
    }
}

# ================================================================ rclone bisync
$LogFile = if ($rc.logFile) { [Environment]::ExpandEnvironmentVariables($rc.logFile) } else { Join-Path $env:LOCALAPPDATA 'SwarlexBattery\rclone-bisync.log' }

$CHANGES_RE = 'Path([12]): +(\d+) changes: +(\d+) new, +(\d+) modified, +(\d+) deleted'
$QUEUE_RE = '- Path([12]) +Do queued (copies|deletes|renames)? ?(to|on)? +- Path([12])'
$ACTION_RE = '(?:INFO|NOTICE)\s*: (.+): (Copied \([^)]*\)|Deleted|Moved[^:]*|Updated[^:]*)$'
$TS_RE = '^(\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2})'

function Read-Tail([string]$path, [int]$bytes = 262144) {
    # rclone may be writing: open shared, read only the tail.
    $fs = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
    try {
        $start = [Math]::Max(0, $fs.Length - $bytes); $null = $fs.Seek($start, 'Begin')
        $sr = New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8)
        $text = $sr.ReadToEnd()
    } finally { $fs.Dispose() }
    $lines = $text -split "`r?`n"
    if ($start -gt 0) { $lines = $lines | Select-Object -Skip 1 }
    return $lines
}

function Parse-Bisync([string[]]$lines) {
    $runs = New-Object Collections.ArrayList; $cur = $null; $dir = 'up'
    foreach ($raw in $lines) {
        $line = $raw -replace "$([char]27)\[[0-9;]*m", ''
        $ts = $null; if ($line -match $TS_RE) { $ts = [datetime]::ParseExact($Matches[1], 'yyyy/MM/dd HH:mm:ss', $null) }
        if ($line -match 'Synching Path1|Bisyncing with') {
            $cur = @{ start = $ts; end = $null; result = 'running'; up = 0; down = 0; delL = 0; delR = 0; conflicts = 0; errors = @(); changes = @{}; files = New-Object Collections.ArrayList }
            [void]$runs.Add($cur); $dir = 'up'; continue
        }
        if (-not $cur) { continue }
        if ($line -match 'Bisync successful') { $cur.result = 'success'; $cur.end = $ts }
        elseif ($line -match 'Bisync critical error|Bisync aborted') { $cur.result = 'failed'; $cur.end = $ts; $cur.errors += ($line -replace '^.*?: ', '') }
        elseif ($line -match 'ERROR\s*: (.+)$') { $cur.errors += $Matches[1] }
        elseif ($line -match 'No changes found') { $cur.changes = @{ '1' = @(0, 0, 0); '2' = @(0, 0, 0) } }
        elseif ($line -match $CHANGES_RE) { $cur.changes[$Matches[1]] = @([int]$Matches[3], [int]$Matches[4], [int]$Matches[5]) }
        elseif ($line -match $QUEUE_RE) {
            $verb = $Matches[2]; $target = $Matches[4]
            $dir = if ($verb -eq 'deletes') { if ($target -eq '1') { 'delL' } else { 'delR' } } else { if ($target -eq '1') { 'down' } else { 'up' } }
        }
        elseif ($line -match 'changed in both paths') { $cur.conflicts++; [void]$cur.files.Add(@{ k = 'conflict'; p = ($line -split ' - ')[-1] }) }
        elseif ($line -match $ACTION_RE) {
            $path = $Matches[1]; $detail = $Matches[2]; $k = $dir
            if ($detail -eq 'Deleted') { $k = if ($dir -in 'down', 'delL') { 'delL' } else { 'delR' } }
            $cur[$k]++
            [void]$cur.files.Add(@{ k = $k; p = $path })
        }
    }
    # a run without a final line whose log stopped long ago crashed
    foreach ($r in $runs) { if ($r.result -eq 'running' -and $r -ne $runs[-1]) { $r.result = 'failed' } }
    return $runs
}

function Get-Rclone {
    if (-not $rc.enabled) { return $null }
    $r = @{ state = 'off'; items = @(); summary = '' }
    $task = Get-ScheduledTask -TaskName $rc.taskName -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $task) {
        $r.summary = 'Kurulmamis'
        $r.items += @{ t = 'row'; icon = 'E753'; label = 'rclone bisync'; value = 'Kurulmamis'; state = 'off' }
        $r.items += @{ t = 'text'; text = "Gorev '$($rc.taskName)' yok. Kurulum: tools\new-bisync-task.ps1 -Path1 C:\Sync -Path2 remote:Sync" }
        return $r
    }
    $info = $task | Get-ScheduledTaskInfo
    $running = $task.State -eq 'Running'
    $disabled = $task.State -eq 'Disabled'
    $runs = @(); if (Test-Path -LiteralPath $LogFile) { $runs = @(Parse-Bisync (Read-Tail $LogFile)) }
    $last = if ($runs.Count) { $runs[-1] } else { $null }
    $lastDone = @($runs | Where-Object { $_.result -ne 'running' }) | Select-Object -Last 1

    $r.state = if ($running) { 'busy' } elseif ($lastDone -and $lastDone.result -eq 'failed') { 'error' } elseif ($info.LastTaskResult -notin 0, 267009, 267011, 267014) { 'error' } elseif ($disabled) { 'off' } else { 'ok' }
    $r.summary = if ($running) { 'Senkronize ediliyor' } elseif ($r.state -eq 'error') { 'Son senkron basarisiz' } elseif ($disabled) { 'Zamanlayici kapali' } else { 'Guncel' }

    $lastTxt = if ($info.LastRunTime -and $info.LastRunTime.Year -gt 2000) { Ago $info.LastRunTime } else { 'hic' }
    $nextTxt = if ($disabled) { 'kapali' } elseif ($info.NextRunTime) { Ago $info.NextRunTime } else { '-' }
    $r.items += @{ t = 'row'; icon = 'E753'; label = 'rclone bisync'; value = $r.summary; state = $r.state; sub = "Son: $lastTxt - Sonraki: $nextTxt" }
    if ($lastDone) {
        $c1 = $lastDone.changes['1']; $c2 = $lastDone.changes['2']
        $chg = @(); if ($c1) { $chg += "yerel $($c1[0]) yeni, $($c1[1]) degisti, $($c1[2]) silindi" }; if ($c2) { $chg += "uzak $($c2[0]) yeni, $($c2[1]) degisti, $($c2[2]) silindi" }
        $r.items += @{ t = 'row'; label = "Yukari $($lastDone.up) - Asagi $($lastDone.down) - Cakisma $($lastDone.conflicts)"; sub = ($chg -join ' / '); value = $(if ($lastDone.result -eq 'success') { 'Basarili' } else { 'Basarisiz' }); state = $(if ($lastDone.result -eq 'success') { 'ok' } else { 'error' }) }
        foreach ($e in @($lastDone.errors | Select-Object -Last 3)) { $r.items += @{ t = 'text'; text = $e; state = 'error' } }
        $icon = @{ up = 'E898'; down = 'E896'; delL = 'E74D'; delR = 'E74D'; conflict = 'E7BA' }
        foreach ($f in @($lastDone.files | Select-Object -Last 6)) { $r.items += @{ t = 'row'; icon = $icon[$f.k]; label = [IO.Path]::GetFileName($f.p); sub = $f.p; state = $(if ($f.k -eq 'conflict') { 'warn' } else { 'off' }) } }
    }
    if ($running -and $last -and $last.result -eq 'running') { $r.items += @{ t = 'text'; text = "Calisiyor: $($last.up + $last.down) dosya islendi" } }
    $r.items += @{ t = 'buttons'; items = @(
        @{ text = $(if ($running) { 'Calisiyor...' } else { 'Simdi senkronize et' }); icon = 'E895'; action = 'rc-sync'; primary = $true },
        @{ text = $(if ($disabled) { 'Zamanlayiciyi ac' } else { 'Zamanlayiciyi durdur' }); icon = $(if ($disabled) { 'E768' } else { 'E769' }); action = 'rc-timer' },
        @{ text = 'Log'; icon = 'E8A5'; action = 'rc-log' }) }
    return $r
}

function Invoke-RcAction {
    switch ($Action) {
        'rc-sync' { Start-ScheduledTask -TaskName $rc.taskName; Start-Sleep -Milliseconds 800; 'Senkron baslatildi' }
        'rc-timer' {
            $t = Get-ScheduledTask -TaskName $rc.taskName
            if ($t.State -eq 'Disabled') { $null = Enable-ScheduledTask -TaskName $rc.taskName; 'Zamanlayici acildi' } else { $null = Disable-ScheduledTask -TaskName $rc.taskName; 'Zamanlayici durduruldu' }
        }
        'rc-log' { if (Test-Path -LiteralPath $LogFile) { Start-Process notepad.exe -ArgumentList "`"$LogFile`"" }; $null }
    }
}

# ================================================================ main
$toast = $null
if ($Action -like 'st-*') { $toast = Invoke-StAction }
elseif ($Action -like 'rc-*') { $toast = Invoke-RcAction }

$a = Get-Syncthing
$b = Get-Rclone
$parts = @($a, $b) | Where-Object { $_ }
$rank = @{ error = 4; warn = 3; busy = 2; ok = 1; off = 0 }
$worst = ($parts | Sort-Object { $rank[$_.state] } -Descending | Select-Object -First 1)
$state = if ($worst) { $worst.state } else { 'off' }

$sections = @()
if ($a) { $sections += @{ title = 'Syncthing'; items = $a.items } }
if ($b) { $sections += @{ title = 'Bulut (rclone bisync)'; items = $b.items } }

@{
    title = 'Sync'
    toast = $toast
    pill = @{
        icon = $(if ($state -eq 'error') { 'EA6A' } else { 'E895' })
        state = $state
        ring = $(if ($a -and $null -ne $a.pct) { $a.pct } else { $null })   # sync progress as the ring
        tooltip = (($parts | ForEach-Object { $_.summary }) -join ' | ')
    }
    sections = $sections
}

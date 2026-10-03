#Requires -Version 5.1
# SPDX-License-Identifier: GPL-3.0-or-later
<#
  SwarlexBattery - wireless mouse / keyboard / headset batteries in the Windows system tray.

  Host: tray icons (a ring for the battery level around a glyph, matching the taskbar theme),
  the flyout (left click), the menu (right click), languages (lang\*.json) and the updater.

  Battery data comes from plugins: a folder in .\plugins with manifest.json + plugin.ps1.
  plugin.ps1 runs in a background runspace (never on the UI thread), gets
  -Action/-Arg/-Config/-PluginDir/-StateDir/-Lang and returns one hashtable:
    @{ pill = @{ icon; state; ring; charging; tooltip; hidden }                 # one tray icon
       icons = @(@{ id; icon; state; ring; rings; charging; dim; tooltip })   # or several
       title; sections = @(...); notify = @(@{ key; title; body }); toast }
#>
param([switch]$Debug, [string]$ExePath = '')

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot

# WPF needs STA (SwarlexBattery.exe already runs on an STA thread).
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    Start-Process powershell.exe -WindowStyle Hidden -ArgumentList @('-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    return
}

$mutex = New-Object Threading.Mutex($false, 'Local\SwarlexBattery.SingleInstance')
# right after a self-update the previous version is still closing: wait for it instead of quitting
$justUpdated = $ExePath -and (Test-Path -LiteralPath "$ExePath.old")
try { $got = $mutex.WaitOne($(if ($justUpdated) { 20000 } else { 0 })) } catch [Threading.AbandonedMutexException] { $got = $true }
if (-not $got) { return }
if ($justUpdated) { Start-Sleep -Milliseconds 500; Remove-Item -LiteralPath "$ExePath.old" -Force -ErrorAction SilentlyContinue }

# version of this build (stamped from the VERSION file); script runs report the VERSION file
$AppVersion = [version]'0.0.0'
try {
    if ($ExePath) { $AppVersion = [version]([Diagnostics.FileVersionInfo]::GetVersionInfo($ExePath).FileVersion) }
    else { $AppVersion = [version]((Get-Content -LiteralPath (Join-Path $Root 'VERSION') -Raw).Trim()) }
} catch {}

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Windows.Forms, System.Drawing

$DataDir  = Join-Path $env:APPDATA 'SwarlexBattery'
$CacheDir = Join-Path $env:LOCALAPPDATA 'SwarlexBattery'
$null = New-Item -ItemType Directory -Force -Path $DataDir, $CacheDir, (Join-Path $CacheDir 'state')
$LogFile = Join-Path $CacheDir 'swarlexbattery.log'
if ((Test-Path -LiteralPath $LogFile) -and (Get-Item -LiteralPath $LogFile).Length -gt 1MB) { Remove-Item -LiteralPath $LogFile -Force }

function Write-Log([string]$msg) {
    try { Add-Content -LiteralPath $LogFile -Value ("{0:u} {1}" -f (Get-Date), $msg) -Encoding UTF8 } catch {}
    if ($Debug) { Write-Host $msg }
}

# ---------------------------------------------------------------- native helpers
# SwarlexBattery.exe already contains these types; only the plain-script launch compiles them (cached).
if (-not ('SwarlexBattery.Win' -as [type])) {
    $nativeSrc = @((Join-Path $Root 'core\Native.cs'), (Join-Path $Root 'core\Hid.cs'), (Join-Path $Root 'core\Devices.cs'))
    $hash = ((Get-FileHash -LiteralPath $nativeSrc -Algorithm SHA256).Hash -join '').Substring(0, 12)
    $nativeDll = Join-Path $CacheDir "SwarlexBattery.Native.$hash.dll"
    if (-not (Test-Path -LiteralPath $nativeDll)) {
        Get-ChildItem -LiteralPath $CacheDir -Filter 'SwarlexBattery.Native.*.dll' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
        Add-Type -Path $nativeSrc -OutputAssembly $nativeDll -OutputType Library
    }
    Add-Type -Path $nativeDll
}

# ---------------------------------------------------------------- config
function ConvertTo-Hashtable($obj) {
    if ($null -eq $obj) { return $null }
    if ($obj -is [System.Collections.IDictionary]) { $h = @{}; foreach ($k in $obj.Keys) { $h[$k] = ConvertTo-Hashtable $obj[$k] }; return $h }
    if ($obj -is [Management.Automation.PSCustomObject]) { $h = @{}; foreach ($p in $obj.PSObject.Properties) { $h[$p.Name] = ConvertTo-Hashtable $p.Value }; return $h }
    if ($obj -is [array]) { return , @($obj | ForEach-Object { ConvertTo-Hashtable $_ }) }
    return $obj
}
function Merge-Hashtable($base, $over) {
    $r = @{}; foreach ($k in $base.Keys) { $r[$k] = $base[$k] }
    if ($over) { foreach ($k in $over.Keys) {
        if ($r[$k] -is [hashtable] -and $over[$k] -is [hashtable]) { $r[$k] = Merge-Hashtable $r[$k] $over[$k] } else { $r[$k] = $over[$k] }
    } }
    return $r
}

$ConfigFile = Join-Path $DataDir 'config.json'
if (-not (Test-Path -LiteralPath $ConfigFile)) {
    # the app used to be called WinBar: keep a config made with that version
    $oldConfig = Join-Path $env:APPDATA 'WinBar\config.json'
    if (Test-Path -LiteralPath $oldConfig) { Copy-Item -LiteralPath $oldConfig -Destination $ConfigFile }
    else { Copy-Item -LiteralPath (Join-Path $Root 'config.default.json') -Destination $ConfigFile }
}
function Read-Config {
    $def = ConvertTo-Hashtable (Get-Content -LiteralPath (Join-Path $Root 'config.default.json') -Raw -Encoding UTF8 | ConvertFrom-Json)
    try { $user = ConvertTo-Hashtable (Get-Content -LiteralPath $ConfigFile -Raw -Encoding UTF8 | ConvertFrom-Json) }
    catch { Write-Log "could not read config.json: $_"; $user = @{} }
    return Merge-Hashtable $def $user
}
$Config = Read-Config

# ---------------------------------------------------------------- language (lang\en.json, lang\tr.json)
# "language": "auto" follows the Windows display language (Turkish -> tr, anything else -> en).
function Resolve-Language {
    $l = "$($Config.language)".ToLowerInvariant()
    if ($l -in 'en', 'tr') { return $l }
    if ([Globalization.CultureInfo]::CurrentUICulture.TwoLetterISOLanguageName -eq 'tr') { 'tr' } else { 'en' }
}
function Load-Strings {
    $script:Lang = Resolve-Language
    $en = ConvertTo-Hashtable (Get-Content -LiteralPath (Join-Path $Root 'lang\en.json') -Raw -Encoding UTF8 | ConvertFrom-Json)
    $script:S = $en
    if ($Lang -ne 'en') {
        $p = Join-Path $Root "lang\$Lang.json"
        if (Test-Path -LiteralPath $p) { $script:S = Merge-Hashtable $en (ConvertTo-Hashtable (Get-Content -LiteralPath $p -Raw -Encoding UTF8 | ConvertFrom-Json)) }
    }
}
# T 'key' [args...]: the string for the current language, {0}.. filled from args
function T([string]$key) {
    $f = $S[$key]; if (-not $f) { $f = $key }
    if ($args.Count) { [string]::Format([string]$f, [object[]]$args) } else { [string]$f }
}
# menu "Language": toggles en <-> tr, saves it in config.json and refreshes the plugins' texts
function Switch-Language {
    $new = if ($Lang -eq 'tr') { 'en' } else { 'tr' }
    try {
        $user = ConvertTo-Hashtable (Get-Content -LiteralPath $ConfigFile -Raw -Encoding UTF8 | ConvertFrom-Json)
        if (-not $user) { $user = @{} }
        $user.language = $new
        Set-Content -LiteralPath $ConfigFile -Value ($user | ConvertTo-Json -Depth 8) -Encoding UTF8
    } catch { Write-Log "language save: $_" }
    $script:Config.language = $new
    Load-Strings
    foreach ($p in $Plugins.Values) { $p.next = [datetime]::MinValue }   # re-poll now, in the new language
}
function Plugin-Name($p) { $n = $p.manifest["name_$Lang"]; if ($n) { $n } else { $p.manifest.name } }
Load-Strings

# ---------------------------------------------------------------- theme (flyout)
$conv = New-Object Windows.Media.BrushConverter
function Brush([string]$c) { $b = $conv.ConvertFromString($c); $b.Freeze(); $b }
$Theme = @{
    fg = Brush $Config.theme.foreground; muted = Brush $Config.theme.muted; accent = Brush $Config.theme.accent
    ok = Brush $Config.theme.ok; warn = Brush $Config.theme.warn; error = Brush $Config.theme.error
    panel = Brush $Config.theme.panel; hover = Brush '#1FFFFFFF'; track = Brush '#2EFFFFFF'
}
function StateBrush([string]$s) {
    switch ($s) { 'ok' { $Theme.ok } 'warn' { $Theme.warn } 'error' { $Theme.error } 'busy' { $Theme.accent } 'off' { $Theme.muted } default { $Theme.fg } }
}
$IconFont = New-Object Windows.Media.FontFamily('Segoe Fluent Icons, Segoe MDL2 Assets')
$TextFont = New-Object Windows.Media.FontFamily('Segoe UI Variable Text, Segoe UI')
function IconChar([string]$hex) { if (-not $hex) { return '' }; [string][char][Convert]::ToInt32($hex, 16) }

# ---------------------------------------------------------------- plugin discovery
$Plugins = [ordered]@{}
function Load-Plugins {
    $Plugins.Clear()
    $dirs = @(Get-ChildItem -LiteralPath (Join-Path $Root 'plugins') -Directory)
    $userPluginDir = Join-Path $DataDir 'plugins'
    if (Test-Path -LiteralPath $userPluginDir) { $dirs += @(Get-ChildItem -LiteralPath $userPluginDir -Directory) }
    $found = @{}
    foreach ($d in $dirs) {
        $mf = Join-Path $d.FullName 'manifest.json'
        $pf = Join-Path $d.FullName 'plugin.ps1'
        if (-not ((Test-Path -LiteralPath $mf) -and (Test-Path -LiteralPath $pf))) { continue }
        try { $m = ConvertTo-Hashtable (Get-Content -LiteralPath $mf -Raw -Encoding UTF8 | ConvertFrom-Json) } catch { Write-Log "invalid manifest: $mf"; continue }
        $found[$m.id] = @{ manifest = $m; dir = $d.FullName; file = $pf }
    }
    $order = @($Config.order) + @($found.Keys | Sort-Object | Where-Object { $Config.order -notcontains $_ })
    foreach ($id in $order) {
        if (-not $found.ContainsKey($id)) { continue }
        $p = $found[$id]
        $cfg = Merge-Hashtable ($(if ($p.manifest.settings) { $p.manifest.settings } else { @{} })) $Config.plugins[$id]
        if ($cfg.ContainsKey('enabled') -and -not $cfg.enabled) { continue }
        $state = Join-Path $CacheDir "state\$id"
        $null = New-Item -ItemType Directory -Force -Path $state
        $Plugins[$id] = @{
            id = $id; manifest = $p.manifest; dir = $p.dir; file = $p.file; stateDir = $state
            cfgJson = ($cfg | ConvertTo-Json -Depth 8 -Compress)
            interval = [double]$(if ($p.manifest.interval) { $p.manifest.interval } else { 15 })
            intervalOpen = [double]$(if ($p.manifest.intervalOpen) { $p.manifest.intervalOpen } else { 3 })
            next = [datetime]::MinValue; job = $null; queue = New-Object Collections.Queue
            result = $null; icons = [ordered]@{}
        }
    }
    Write-Log ("plugins: " + (($Plugins.Keys) -join ', '))
}

# ---------------------------------------------------------------- background execution
$iss = [Management.Automation.Runspaces.InitialSessionState]::CreateDefault()
$iss.ExecutionPolicy = 'Bypass'
$Pool = [RunspaceFactory]::CreateRunspacePool(1, [Math]::Max(2, [Environment]::ProcessorCount / 2), $iss, $Host)
$Pool.ApartmentState = 'STA'
$Pool.Open()

$Runner = {
    param($file, $action, $arg, $cfgJson, $dir, $stateDir, $lang)
    $ErrorActionPreference = 'Stop'
    try {
        $cfg = $cfgJson | ConvertFrom-Json
        $out = & $file -Action $action -Arg $arg -Config $cfg -PluginDir $dir -StateDir $stateDir -Lang $lang
        if ($out -is [array]) { $out = $out[-1] }
        $out | ConvertTo-Json -Depth 10 -Compress
    } catch {
        @{ error = "$($_.Exception.Message)"; at = "$($_.InvocationInfo.ScriptName):$($_.InvocationInfo.ScriptLineNumber)" } | ConvertTo-Json -Compress
    }
}

function Start-PluginJob($p, [string]$action = 'poll', $arg = '') {
    if (-not $p) { return }
    if ($p.job) { if ($action -ne 'poll') { $p.queue.Enqueue(@($action, $arg)) }; return }
    $ps = [PowerShell]::Create()
    $ps.RunspacePool = $Pool
    $null = $ps.AddScript($Runner).AddArgument($p.file).AddArgument($action).AddArgument([string]$arg).AddArgument($p.cfgJson).AddArgument($p.dir).AddArgument($p.stateDir).AddArgument($Lang)
    $p.job = @{ ps = $ps; handle = $ps.BeginInvoke(); action = $action; started = Get-Date }
}

function Complete-PluginJob($p) {
    $j = $p.job
    try {
        $json = ($j.ps.EndInvoke($j.handle) | Select-Object -Last 1)
        $res = if ($json) { ConvertTo-Hashtable ($json | ConvertFrom-Json) } else { @{ error = 'plugin returned nothing' } }
        foreach ($e in $j.ps.Streams.Error) { Write-Log "[$($p.id)] $e" }
    } catch { $res = @{ error = "$_" } }
    finally { $j.ps.Dispose(); $p.job = $null }
    if ($res.error) { Write-Log "[$($p.id)] $($j.action): $($res.error) $($res.at)" }
    Apply-Result $p $res
    $p.next = (Get-Date).AddSeconds($(if ($OpenPanel -and $OpenPanel.id -eq $p.id) { $p.intervalOpen } else { $p.interval }))
    if ($p.queue.Count) { $a = $p.queue.Dequeue(); Start-PluginJob $p $a[0] $a[1] }
}

# ---------------------------------------------------------------- tray icons (ring around a glyph)
$IconSize = [Math]::Max(16, [Windows.Forms.SystemInformation]::SmallIconSize.Width)
$GlyphFont = 'Segoe MDL2 Assets'
if ((New-Object Drawing.Text.InstalledFontCollection).Families.Name -contains 'Segoe Fluent Icons') { $GlyphFont = 'Segoe Fluent Icons' }
$PersonalizeKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
function Test-LightTaskbar { try { [int](Get-ItemPropertyValue -Path $PersonalizeKey -Name SystemUsesLightTheme) -eq 1 } catch { $false } }
$LightTaskbar = Test-LightTaskbar
$IconCache = @{}
$Frame = 0

function Get-TrayColor([string]$state, $charging) {
    # monochrome (default): white like the Windows network / volume icons (black on a light taskbar);
    # charging shows a bolt, sleeping devices are dimmed.
    if ($Config.monochrome) { if ($LightTaskbar) { return [Drawing.Color]::FromArgb(28, 28, 28) } else { return [Drawing.Color]::FromArgb(255, 255, 255) } }
    if ($charging) { return [Drawing.Color]::FromArgb(63, 209, 106) }
    switch ($state) {
        'warn'  { [Drawing.Color]::FromArgb(245, 165, 36) }
        'error' { [Drawing.Color]::FromArgb(240, 74, 74) }
        'busy'  { [Drawing.Color]::FromArgb(76, 194, 255) }
        default { if ($LightTaskbar) { [Drawing.Color]::FromArgb(28, 28, 28) } else { [Drawing.Color]::FromArgb(245, 245, 245) } }
    }
}

function Get-TrayIcon($spec) {
    $ring = $spec.ring
    $rings = @($spec.rings | Where-Object { $null -ne $_ })   # several values: one ring split into segments
    $hasRing = ($null -ne $ring -and "$ring" -ne '') -or $rings.Count -gt 0
    $f = 0   # static icons: charging is shown with a bolt, never by blinking
    # invariant numbers: in a Turkish locale 0.3 prints as "0,3" and would clash with the list separator
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $key = "$($spec.icon)|$(if ($null -ne $ring -and "$ring" -ne '') { ([double]$ring).ToString('0.00', $inv) })|$(($rings | ForEach-Object { ([double]$_).ToString('0.00', $inv) }) -join ';')|$($spec.state)|$($spec.charging)|$($spec.dim)|$LightTaskbar|$f"
    if ($IconCache.ContainsKey($key)) { return $IconCache[$key] }

    # drawn 4x larger, then scaled down: smooth ring and a glyph that is bold enough at 16-24 px
    $s = $IconSize; $k = 4; $B = $s * $k
    $big = New-Object Drawing.Bitmap $B, $B
    $g = [Drawing.Graphics]::FromImage($big)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $alpha = if ($spec.dim -or $spec.state -eq 'off') { 150 } else { 255 }
    $c = Get-TrayColor $spec.state $spec.charging
    $main = [Drawing.Color]::FromArgb(255, $c)   # alpha is applied once, at the end
    $base = Get-TrayColor 'ok' $false
    $glyphColor = $main
    if ($hasRing) {
        $w = [float]($B * 0.13)
        $pad = $w / 2 + $k * 0.4
        $rect = New-Object Drawing.RectangleF $pad, $pad, ($B - 2 * $pad), ($B - 2 * $pad)
        $track = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(77, $base)), $w
        $pen = New-Object Drawing.Pen $main, $w
        if ($rings.Count -ge 2) {
            # two values: left half = first (e.g. mouse), right half = second (e.g. headset),
            # each filling from the bottom up, with a small gap at the top and bottom
            $gap = 16.0; $span = 180.0 - 2 * $gap
            for ($i = 0; $i -lt 2; $i++) {
                $dir = if ($i -eq 0) { 1.0 } else { -1.0 }
                $start = [float](90 + $dir * $gap)
                $g.DrawArc($track, $rect, $start, [float]($dir * $span))
                $lvl = [Math]::Max(0.0, [Math]::Min(1.0, [double]$rings[$i]))
                if ($lvl -gt 0) { $g.DrawArc($pen, $rect, $start, [float]($dir * $span * $lvl)) }
            }
        } else {
            $g.DrawEllipse($track, $rect)
            if ($rings.Count -eq 1) { $ring = $rings[0] }
            $sweep = [float](360 * [Math]::Max(0.0, [Math]::Min(1.0, [double]$ring)))
            if ($sweep -gt 0) {
                $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
                $g.DrawArc($pen, $rect, -90, $sweep)   # clockwise from the top
            }
        }
        $track.Dispose(); $pen.Dispose()
        if (-not ($spec.charging -or $spec.state -in 'warn', 'error')) { $glyphColor = [Drawing.Color]::FromArgb(255, $base) }
        $gs = $B * 0.5
    } else { $gs = $B * 0.9 }

    # glyph as a path, filled and outlined: line-style Fluent glyphs read as solid in the tray
    $fam = New-Object Drawing.FontFamily $GlyphFont
    $sf = New-Object Drawing.StringFormat; $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    if ($spec.charging -and $hasRing) {
        # charging: a solid lightning bolt drawn as a shape (font glyphs blur at 16 px)
        $pts = @(@(0.60, 0.04), @(0.18, 0.56), @(0.46, 0.56), @(0.38, 0.96), @(0.82, 0.42), @(0.54, 0.42), @(0.64, 0.04))
        $o = ($B - $gs) / 2
        $path.AddPolygon([Drawing.PointF[]]@($pts | ForEach-Object { New-Object Drawing.PointF ([float]($o + $_[0] * $gs)), ([float]($o + $_[1] * $gs)) }))
    } else {
        $path.AddString((IconChar $spec.icon), $fam, 0, [float]$gs, (New-Object Drawing.RectangleF 0, 0, $B, $B), $sf)
    }
    $brush = New-Object Drawing.SolidBrush $glyphColor
    $outline = New-Object Drawing.Pen $glyphColor, ([float]($B * 0.035)); $outline.LineJoin = 'Round'
    $g.FillPath($brush, $path); $g.DrawPath($outline, $path)
    if (-not $hasRing -and $spec.state -in 'warn', 'error') {
        $d = $B * 0.36; $dot = New-Object Drawing.SolidBrush $c
        $g.FillEllipse($dot, $B - $d, $B - $d, $d - $k, $d - $k); $dot.Dispose()
    }
    $brush.Dispose(); $outline.Dispose(); $path.Dispose(); $sf.Dispose(); $fam.Dispose(); $g.Dispose()

    $bmp = New-Object Drawing.Bitmap $s, $s
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'; $g.CompositingQuality = 'HighQuality'
    # dim / breathing alpha applied to the finished image, so overlapping strokes do not double up
    $ia = New-Object Drawing.Imaging.ImageAttributes
    $cm = New-Object Drawing.Imaging.ColorMatrix; $cm.Matrix33 = [float]($alpha / 255.0); $ia.SetColorMatrix($cm)
    $g.DrawImage($big, (New-Object Drawing.Rectangle 0, 0, $s, $s), 0, 0, $big.Width, $big.Height, [Drawing.GraphicsUnit]::Pixel, $ia)
    $ia.Dispose(); $g.Dispose(); $big.Dispose()
    $h = $bmp.GetHicon(); $bmp.Dispose()
    $entry = @{ icon = [Drawing.Icon]::FromHandle($h); h = $h }
    if ($IconCache.Count -gt 300) { Clear-IconCache }
    $IconCache[$key] = $entry
    return $entry
}

function Clear-IconCache([switch]$All) {
    $inUse = @{}
    if (-not $All) { foreach ($p in $Plugins.Values) { foreach ($t in $p.icons.Values) { $inUse[$t.handle] = 1 } } }
    foreach ($k in @($IconCache.Keys)) {
        $e = $IconCache[$k]
        if (-not $inUse.ContainsKey($e.h)) { $e.icon.Dispose(); [void][SwarlexBattery.IconUtil]::Destroy($e.h); $IconCache.Remove($k) }
    }
}

function Get-IconSpecs($p) {
    $res = $p.result; $name = Plugin-Name $p
    if (-not $res) { return , @(@{ id = 'main'; icon = $p.manifest.icon; state = 'off'; tooltip = (T 'tipLoading' $name) }) }
    if ($res.error) { return , @(@{ id = 'main'; icon = $p.manifest.icon; state = 'error'; tooltip = (T 'tipError' $name $res.error) }) }
    if ($res.ContainsKey('icons')) { return , @($res.icons | Where-Object { $_ }) }
    $pill = $res.pill; if (-not $pill) { $pill = @{} }
    if ($pill.hidden) { return , @() }
    return , @(@{ id = 'main'; icon = $(if ($pill.icon) { $pill.icon } else { $p.manifest.icon }); state = $pill.state
                  ring = $pill.ring; charging = $pill.charging; dim = $pill.dim
                  tooltip = $(if ($pill.tooltip) { "$name - $($pill.tooltip)" } else { $name }) })
}

function Paint-TrayIcon($t) {
    $e = Get-TrayIcon $t.spec
    if ($t.handle -ne $e.h) { $t.ni.Icon = $e.icon; $t.handle = $e.h }
}

function Sync-TrayIcons($p) {
    $seen = @{}
    foreach ($sp in (Get-IconSpecs $p)) {
        $id = [string]$sp.id; $seen[$id] = 1
        $t = $p.icons[$id]
        if (-not $t) {
            $ni = New-Object Windows.Forms.NotifyIcon
            $ni.Tag = $p.id
            $ni.Add_MouseClick({
                try { $pp = $Plugins[$this.Tag]; if ($_.Button -eq 'Right') { Show-Menu $pp } else { Toggle-Panel $pp } } catch { Write-Log "click: $_" }
            })
            $t = @{ ni = $ni; spec = $sp; handle = [IntPtr]::Zero }
            $p.icons[$id] = $t
        }
        $t.spec = $sp
        Paint-TrayIcon $t
        $tip = ([string]$sp.tooltip).Trim(); if ($tip.Length -gt 63) { $tip = $tip.Substring(0, 62) + [char]0x2026 }
        $t.ni.Text = $tip
        if (-not $t.ni.Visible) { $t.ni.Visible = $true }
    }
    foreach ($id in @($p.icons.Keys)) {
        if (-not $seen.ContainsKey($id)) { $p.icons[$id].ni.Visible = $false; $p.icons[$id].ni.Dispose(); $p.icons.Remove($id) }
    }
}

# Windows 11 parks new tray icons behind the ^ overflow. This flips the same per-icon switch as
# Settings > Personalization > Taskbar > Other system tray icons, only for SwarlexBattery.exe's own icons.
function Promote-TrayIcons {
    if (-not $ExePath -or -not $Config.alwaysShowInTray) { return }
    $root = 'HKCU:\Control Panel\NotifyIconSettings'
    if (-not (Test-Path -LiteralPath $root)) { return }
    $leaf = '\' + (Split-Path $ExePath -Leaf)
    foreach ($k in Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue) {
        $v = Get-ItemProperty -LiteralPath $k.PSPath -ErrorAction SilentlyContinue
        if ($v.ExecutablePath -and ($v.ExecutablePath -ieq $ExePath -or $v.ExecutablePath.EndsWith($leaf, [StringComparison]::OrdinalIgnoreCase)) -and $v.IsPromoted -ne 1) {
            Set-ItemProperty -LiteralPath $k.PSPath -Name IsPromoted -Value 1 -Type DWord
        }
    }
}

function Remove-TrayIcons($p) { foreach ($t in @($p.icons.Values)) { $t.ni.Visible = $false; $t.ni.Dispose() }; $p.icons.Clear() }

# ---------------------------------------------------------------- notifications
$Notified = @{}
function Show-Toast($p, [string]$title, [string]$body, [string]$icon = 'Info') {
    if (-not $title) { return }
    if ($Config.quietWhileGaming -and [SwarlexBattery.Win]::ForegroundIsFullscreen()) { return }
    $ni = $null
    if ($p) { $ni = @($p.icons.Values | ForEach-Object { $_.ni } | Where-Object { $_.Visible }) | Select-Object -First 1 }
    if (-not $ni) { foreach ($pp in $Plugins.Values) { $ni = @($pp.icons.Values | ForEach-Object { $_.ni } | Where-Object { $_.Visible }) | Select-Object -First 1; if ($ni) { break } } }
    if ($ni) { $ni.ShowBalloonTip(5000, $title, $(if ($body) { $body } else { ' ' }), $icon) }
}

function Apply-Result($p, $res) {
    $p.result = $res
    Sync-TrayIcons $p
    if ($res.toast) { Show-Toast $p (Plugin-Name $p) $res.toast }
    # one-shot notifications: once per key; the key re-arms only after it has been gone for 30 min,
    # so a device that naps and comes back does not repeat the same "battery low" toast
    $now = Get-Date
    foreach ($n in @($res.notify)) {
        if (-not $n) { continue }
        $k = "$($p.id)/$($n.key)"
        if (-not $Notified.ContainsKey($k)) { Show-Toast $p $n.title $n.body 'Warning' }
        $Notified[$k] = $now
    }
    foreach ($k in @($Notified.Keys)) { if ($k.StartsWith("$($p.id)/") -and ($now - $Notified[$k]).TotalMinutes -gt 30) { $Notified.Remove($k) } }
    if ($OpenPanel -and $OpenPanel.id -eq $p.id) { Render-Panel $p }
}

# ---------------------------------------------------------------- flyout window (panel + menu)
function Get-Scale { [double][Windows.Forms.Screen]::PrimaryScreen.Bounds.Width / [Windows.SystemParameters]::PrimaryScreenWidth }

$OpenPanel = $null
$PanelHwnd = [IntPtr]::Zero
$FlyoutAbove = $true
$LastInteraction = [datetime]::MinValue
$Panel = New-Object Windows.Window
$Panel.WindowStyle = 'None'; $Panel.AllowsTransparency = $true; $Panel.Background = [Windows.Media.Brushes]::Transparent
$Panel.ShowInTaskbar = $false; $Panel.Topmost = $true; $Panel.ResizeMode = 'NoResize'; $Panel.SizeToContent = 'Height'
$Panel.Width = $Config.panel.width; $Panel.FontFamily = $TextFont; $Panel.Foreground = $Theme.fg; $Panel.Title = 'SwarlexBattery'
$PanelBorder = New-Object Windows.Controls.Border
$PanelBorder.Background = $Theme.panel; $PanelBorder.CornerRadius = 8; $PanelBorder.Padding = 14
$PanelBorder.BorderBrush = Brush '#26FFFFFF'; $PanelBorder.BorderThickness = 1
# one drawn surface only: a soft shadow in a transparent margin (no window-level blur / DWM frame,
# which drew a second box around this one)
$PanelBorder.Margin = 12
$shadow = New-Object Windows.Media.Effects.DropShadowEffect
$shadow.BlurRadius = 18; $shadow.ShadowDepth = 2; $shadow.Opacity = 0.45; $shadow.Direction = 270; $shadow.Color = [Windows.Media.Colors]::Black
$PanelBorder.Effect = $shadow
$PanelScroll = New-Object Windows.Controls.ScrollViewer
$PanelScroll.VerticalScrollBarVisibility = 'Auto'; $PanelScroll.MaxHeight = $Config.panel.maxHeight
$PanelBorder.Child = $PanelScroll
$Panel.Content = $PanelBorder
$Panel.Add_SourceInitialized({ $script:PanelHwnd = (New-Object Windows.Interop.WindowInteropHelper $Panel).Handle })
$Panel.Add_Deactivated({ if ($script:OpenPanel) { Close-Panel } })
$Panel.Add_KeyDown({ if ($_.Key -eq 'Escape') { Close-Panel } })
$Panel.Add_SizeChanged({ if ($script:OpenPanel -and $script:FlyoutAbove) { $wa = [Windows.SystemParameters]::WorkArea; $Panel.Top = $wa.Bottom - $Panel.ActualHeight } })

$LastClosed = @{ id = $null; at = [datetime]::MinValue }
function Close-Panel {
    if ($script:OpenPanel) { $script:LastClosed = @{ id = $script:OpenPanel.id; at = Get-Date } }
    $script:OpenPanel = $null; $Panel.Hide()
}

# Opens the flyout next to the tray icon that was clicked (the cursor), above or below the taskbar.
function Show-Flyout([double]$width) {
    $Panel.Width = $width
    $scale = Get-Scale; $wa = [Windows.SystemParameters]::WorkArea
    $cur = [Windows.Forms.Cursor]::Position
    $cx = $cur.X / $scale; $cy = $cur.Y / $scale
    # the window is the panel plus a 12 px shadow margin on every side
    $Panel.Left = [Math]::Max($wa.Left, [Math]::Min($wa.Right - $width, $cx - $width / 2))
    $script:FlyoutAbove = $cy -gt ($wa.Top + $wa.Bottom) / 2
    if (-not $Panel.IsVisible) { $Panel.Top = $wa.Top - 5000; $Panel.Show() }
    $Panel.UpdateLayout()
    $Panel.Top = if ($FlyoutAbove) { $wa.Bottom - $Panel.ActualHeight } else { $wa.Top }
    [SwarlexBattery.Win]::ForceForeground($PanelHwnd)
    $null = $Panel.Activate()
}

function Toggle-Panel($p) {
    if ($OpenPanel -and $OpenPanel.id -eq $p.id) { Close-Panel; return }
    # the click that deactivated (and closed) this flyout must not reopen it
    if ($LastClosed.id -eq $p.id -and ((Get-Date) - $LastClosed.at).TotalMilliseconds -lt 400) { return }
    $script:OpenPanel = @{ id = $p.id }
    $script:LastInteraction = [datetime]::MinValue
    Render-Panel $p
    Show-Flyout ($Config.panel.width + 24)
    Start-PluginJob $p 'poll'   # fresh data while open
}

function New-Text([string]$t, $brush = $Theme.fg, [double]$size = 13, [string]$weight = 'Normal') {
    $tb = New-Object Windows.Controls.TextBlock
    $tb.Text = $t; $tb.Foreground = $brush; $tb.FontSize = $size; $tb.FontWeight = $weight; $tb.TextWrapping = 'Wrap'
    $tb.TextTrimming = 'CharacterEllipsis'; $tb
}

function New-Button($p, $item) {
    $b = New-Object Windows.Controls.Border
    $b.Padding = '10,5'; $b.Margin = '0,0,6,6'; $b.CornerRadius = 5; $b.Cursor = 'Hand'
    $b.Background = if ($item.primary) { $Theme.accent } else { $Theme.track }
    $sp = New-Object Windows.Controls.StackPanel; $sp.Orientation = 'Horizontal'
    $fg = if ($item.primary) { Brush '#1a1b26' } else { $Theme.fg }
    if ($item.icon) { $ic = New-Text (IconChar $item.icon) $fg 12; $ic.FontFamily = $IconFont; $ic.Margin = '0,0,6,0'; $ic.VerticalAlignment = 'Center'; $null = $sp.Children.Add($ic) }
    $null = $sp.Children.Add((New-Text $item.text $fg 12))
    $b.Child = $sp
    if ($item.tooltip) { $b.ToolTip = $item.tooltip }
    $b.Tag = @{ p = $p; action = $item.action; arg = $item.arg }
    $b.Add_MouseEnter({ $this.Opacity = 0.82 }); $b.Add_MouseLeave({ $this.Opacity = 1 })
    $b.Add_MouseLeftButtonUp({ $t = $this.Tag; $script:LastInteraction = Get-Date; $this.Opacity = 0.5; Start-PluginJob $t.p $t.action $t.arg })
    $b
}

function New-Row([string]$label, [string]$value, $state, [string]$sub, [string]$icon) {
    $g = New-Object Windows.Controls.Grid; $g.Margin = '0,3'
    foreach ($w in 'Auto', '*', 'Auto') { $c = New-Object Windows.Controls.ColumnDefinition; $c.Width = $w; $g.ColumnDefinitions.Add($c) }
    if ($icon) { $ic = New-Text (IconChar $icon) (StateBrush $state) 14; $ic.FontFamily = $IconFont; $ic.Margin = '0,1,9,0'; $null = $g.Children.Add($ic) }
    $sp = New-Object Windows.Controls.StackPanel; [Windows.Controls.Grid]::SetColumn($sp, 1)
    $null = $sp.Children.Add((New-Text $label))
    if ($sub) { $null = $sp.Children.Add((New-Text $sub $Theme.muted 11)) }
    $null = $g.Children.Add($sp)
    if ($value) { $v = New-Text $value (StateBrush $state) 12; $v.TextWrapping = 'NoWrap'; $v.Margin = '10,1,0,0'; [Windows.Controls.Grid]::SetColumn($v, 2); $null = $g.Children.Add($v) }
    $g
}

function Render-Item($p, $it, $container) {
    switch ($it.t) {
        'row' { $null = $container.Children.Add((New-Row $it.label $it.value $it.state $it.sub $it.icon)) }
        'text' { $tb = New-Text $it.text (StateBrush $(if ($it.state) { $it.state } else { 'off' })) 12; $tb.Margin = '0,2'; $null = $container.Children.Add($tb) }
        'sep' { $s = New-Object Windows.Controls.Border; $s.Height = 1; $s.Background = $Theme.track; $s.Margin = '0,8'; $null = $container.Children.Add($s) }
        'bar' {
            $null = $container.Children.Add((New-Row $it.label $it.value $it.state $it.sub $it.icon))
            $pb = New-Object Windows.Controls.ProgressBar
            $pb.Minimum = 0; $pb.Maximum = 1; $pb.Value = [double]$it.pct; $pb.Height = 4; $pb.Margin = '0,0,0,6'
            $pb.Foreground = if ($it.state) { StateBrush $it.state } else { $Theme.fg }; $pb.Background = $Theme.track; $pb.BorderThickness = 0
            $null = $container.Children.Add($pb)
        }
        'buttons' {
            $wp = New-Object Windows.Controls.WrapPanel; $wp.Margin = '0,6,0,0'
            foreach ($b in @($it.items)) { if ($b) { $null = $wp.Children.Add((New-Button $p $b)) } }
            $null = $container.Children.Add($wp)
        }
        'toggle' {
            $cb = New-Object Windows.Controls.CheckBox
            $cb.Content = New-Text $it.label; $cb.IsChecked = [bool]$it.value; $cb.Margin = '0,4'; $cb.Foreground = $Theme.fg
            $cb.Tag = @{ p = $p; action = $it.action }
            $cb.Add_Click({ $t = $this.Tag; $script:LastInteraction = Get-Date; Start-PluginJob $t.p $t.action ([string][bool]$this.IsChecked) })
            $null = $container.Children.Add($cb)
        }
        'slider' {
            $fmt = if ($it.fmt) { $it.fmt } else { '{0}' }
            $row = New-Row $it.label ($fmt -f $it.value) 'busy' $it.sub $it.icon
            $null = $container.Children.Add($row)
            $sl = New-Object Windows.Controls.Slider
            $sl.Minimum = [double]$it.min; $sl.Maximum = [double]$it.max; $sl.Value = [double]$it.value
            $sl.SmallChange = [double]$it.step; $sl.LargeChange = [double]$it.step * 5
            $sl.TickFrequency = [double]$it.step; $sl.IsSnapToTickEnabled = $true; $sl.Margin = '0,0,0,6'
            $sl.Tag = @{ p = $p; action = $it.action; fmt = $fmt; label = $row.Children | Where-Object { $_ -is [Windows.Controls.TextBlock] } | Select-Object -Last 1 }
            $sl.Add_ValueChanged({ $t = $this.Tag; $script:LastInteraction = Get-Date; if ($t.label) { $t.label.Text = $t.fmt -f [Math]::Round($this.Value, 2) } })
            # commit on release / key up, not on every pixel of a drag
            $sl.Add_PreviewMouseLeftButtonUp({ $t = $this.Tag; Start-PluginJob $t.p $t.action ([string][Math]::Round($this.Value, 2)) })
            $sl.Add_KeyUp({ $t = $this.Tag; Start-PluginJob $t.p $t.action ([string][Math]::Round($this.Value, 2)) })
            $null = $container.Children.Add($sl)
        }
        'choice' {
            $null = $container.Children.Add((New-Text $it.label $Theme.muted 11))
            $wp = New-Object Windows.Controls.WrapPanel; $wp.Margin = '0,4,0,0'
            foreach ($o in @($it.options)) { $null = $wp.Children.Add((New-Button $p @{ text = $o.text; action = $it.action; arg = $o.value; primary = ($o.value -eq $it.value) })) }
            $null = $container.Children.Add($wp)
        }
    }
}

function Render-Panel($p) {
    # don't rebuild while the user is dragging a slider or just clicked something
    if (((Get-Date) - $LastInteraction).TotalSeconds -lt 1.2 -and $PanelScroll.Content) { return }
    $res = $p.result
    $root = New-Object Windows.Controls.StackPanel
    $title = New-Text $(if ($res.title) { $res.title } else { Plugin-Name $p }) $Theme.fg 15 'SemiBold'
    $title.Margin = '0,0,0,6'; $null = $root.Children.Add($title)
    if (-not $res) { $null = $root.Children.Add((New-Text (T 'loading') $Theme.muted 12)) }
    elseif ($res.error) { $null = $root.Children.Add((New-Text $res.error $Theme.error 12)) }
    foreach ($sec in @($res.sections)) {
        if (-not $sec) { continue }
        if ($sec.title) { $h = New-Text ([string]$sec.title).ToUpper() $Theme.muted 11 'SemiBold'; $h.Margin = '0,10,0,4'; $null = $root.Children.Add($h) }
        foreach ($it in @($sec.items)) { if ($it) { Render-Item $p $it $root } }
    }
    $PanelScroll.Content = $root
}

# ---------------------------------------------------------------- right-click menu (same flyout, Windows 11 look)
$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
# "Start with Windows" chosen in the old WinBar version moves over to this exe
if ($ExePath -and (Get-ItemProperty -Path $RunKey -Name WinBar -ErrorAction SilentlyContinue)) {
    Remove-ItemProperty -Path $RunKey -Name WinBar -ErrorAction SilentlyContinue
    Set-ItemProperty -Path $RunKey -Name SwarlexBattery -Value "`"$ExePath`""
}
function Test-Autostart { $ExePath -and ((Get-ItemProperty -Path $RunKey -Name SwarlexBattery -ErrorAction SilentlyContinue).SwarlexBattery -eq "`"$ExePath`"") }

function New-MenuRow([string]$icon, [string]$text, $tag, [bool]$checked = $false) {
    $b = New-Object Windows.Controls.Border
    $b.Padding = '10,7'; $b.CornerRadius = 4; $b.Cursor = 'Hand'; $b.Background = [Windows.Media.Brushes]::Transparent
    $g = New-Object Windows.Controls.Grid
    foreach ($w in '26', '*', 'Auto') { $c = New-Object Windows.Controls.ColumnDefinition; $c.Width = $w; $g.ColumnDefinitions.Add($c) }
    $ic = New-Text (IconChar $icon) $Theme.fg 13; $ic.FontFamily = $IconFont; $ic.VerticalAlignment = 'Center'; $null = $g.Children.Add($ic)
    $tx = New-Text $text $Theme.fg 13; [Windows.Controls.Grid]::SetColumn($tx, 1); $null = $g.Children.Add($tx)
    if ($checked) { $ck = New-Text (IconChar 'E73E') $Theme.accent 12; $ck.FontFamily = $IconFont; $ck.VerticalAlignment = 'Center'; [Windows.Controls.Grid]::SetColumn($ck, 2); $null = $g.Children.Add($ck) }
    $b.Child = $g; $b.Tag = $tag
    $b.Add_MouseEnter({ $this.Background = $Theme.hover }); $b.Add_MouseLeave({ $this.Background = [Windows.Media.Brushes]::Transparent })
    $b.Add_MouseLeftButtonUp({ Invoke-MenuAction $this.Tag })
    $b
}

function Show-Menu($p) {
    $id = "menu:$($p.id)"
    if ($OpenPanel -and $OpenPanel.id -eq $id) { Close-Panel; return }
    if ($LastClosed.id -eq $id -and ((Get-Date) - $LastClosed.at).TotalMilliseconds -lt 400) { return }
    $root = New-Object Windows.Controls.StackPanel
    $h = New-Text (Plugin-Name $p) $Theme.muted 11 'SemiBold'; $h.Margin = '10,0,0,4'; $null = $root.Children.Add($h)
    if ($UpdateInfo) {
        $label = if ($UpdateJob -and $UpdateJob.kind -eq 'install') { (T 'downloading' $UpdateInfo.version) } else { (T 'update' $UpdateInfo.version) }
        $up = New-MenuRow 'E896' $label @{ a = 'update' }
        $up.Background = $Theme.hover; $up.Add_MouseLeave({ $this.Background = $Theme.hover })   # stays highlighted
        $null = $root.Children.Add($up)
    }
    $null = $root.Children.Add((New-MenuRow 'E72C' (T 'refresh') @{ a = 'refresh'; p = $p.id }))
    if ($p.manifest.rightClick) { $null = $root.Children.Add((New-MenuRow 'E768' (T 'toggle') @{ a = 'plugin'; p = $p.id; action = $p.manifest.rightClick })) }
    $sep = New-Object Windows.Controls.Border; $sep.Height = 1; $sep.Background = $Theme.track; $sep.Margin = '4,5'; $null = $root.Children.Add($sep)
    if (-not $Config.simpleMenu) {
        $null = $root.Children.Add((New-MenuRow 'E713' (T 'settings') @{ a = 'settings' }))
        $null = $root.Children.Add((New-MenuRow 'E777' (T 'reload') @{ a = 'reload' }))
        $null = $root.Children.Add((New-MenuRow 'E9F9' (T 'log') @{ a = 'log' }))
    }
    if ($ExePath) { $null = $root.Children.Add((New-MenuRow 'E7E8' (T 'startWithWindows') @{ a = 'autostart' } (Test-Autostart))) }
    $null = $root.Children.Add((New-MenuRow 'E774' (T 'language') @{ a = 'language' }))
    if ($Config.update.repo) { $null = $root.Children.Add((New-MenuRow 'E895' (T 'checkUpdates' $AppVersion) @{ a = 'checkupdate' })) }
    $null = $root.Children.Add((New-MenuRow 'E8BB' (T 'exit') @{ a = 'exit' }))
    $PanelScroll.Content = $root
    $script:OpenPanel = @{ id = $id }
    Show-Flyout 254
}

function Invoke-MenuAction($t) {
    $p = if ($t.p) { $Plugins[$t.p] } else { $null }
    Close-Panel
    switch ($t.a) {
        'open'      { $script:LastClosed = @{ id = $null; at = [datetime]::MinValue }; Toggle-Panel $p }
        'refresh'   { Start-PluginJob $p 'poll' }
        'plugin'    { Start-PluginJob $p $t.action }
        'settings'  { Start-Process notepad.exe -ArgumentList "`"$ConfigFile`"" }
        'reload'    { $script:ReloadRequested = $true }
        'log'       { Start-Process notepad.exe -ArgumentList "`"$LogFile`"" }
        'autostart' {
            if (Test-Autostart) { Remove-ItemProperty -Path $RunKey -Name SwarlexBattery -ErrorAction SilentlyContinue }
            else { Set-ItemProperty -Path $RunKey -Name SwarlexBattery -Value "`"$ExePath`"" }
        }
        'update'    { Start-UpdateInstall }
        'checkupdate' { Start-UpdateCheck $true }
        'language'  { Switch-Language }
        'exit'      { Stop-App }
    }
}

# ---------------------------------------------------------------- updates (GitHub Releases)
# The latest release of github.com/<update.repo> is checked at start and every few hours, in the
# background. A newer tag (v1.2.3) adds "Update" to the menu. Updating downloads the release's
# SwarlexBattery.exe, checks it against SwarlexBattery.exe.sha256 from the same release, swaps it in
# place of the running exe (a running exe can be renamed, not overwritten) and restarts.
function Norm-Version($v) { try { $x = [version]("$v".Trim().TrimStart('v', 'V')); [version]"$($x.Major).$($x.Minor).$([Math]::Max(0, $x.Build))" } catch { $null } }
$AppVersion = Norm-Version $AppVersion
$UpdateInfo = $null; $UpdateJob = $null; $UpdateManual = $false; $UpdateNotified = ''
$NextUpdateCheck = (Get-Date).AddSeconds(20)

$UpdateCheckScript = {
    param($repo)
    $ErrorActionPreference = 'Stop'
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $r = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" -TimeoutSec 20 -UseBasicParsing `
        -Headers @{ 'User-Agent' = 'SwarlexBattery-updater'; Accept = 'application/vnd.github+json' }
    $exe = @($r.assets | Where-Object { $_.name -eq 'SwarlexBattery.exe' })[0]
    $sha = @($r.assets | Where-Object { $_.name -eq 'SwarlexBattery.exe.sha256' })[0]
    @{ tag = [string]$r.tag_name; url = [string]$exe.browser_download_url; sha = [string]$sha.browser_download_url; page = [string]$r.html_url } | ConvertTo-Json -Compress
}

$UpdateDownloadScript = {
    param($url, $shaUrl, $dest, $repo)
    $ErrorActionPreference = 'Stop'
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    # only files of this repository's releases: https://github.com/<owner>/<repo>/releases/download/...
    foreach ($u in $url, $shaUrl) {
        $x = [uri]$u
        if ($x.Scheme -ne 'https' -or $x.Host -ne 'github.com' -or -not $x.AbsolutePath.StartsWith("/$repo/releases/download/", [StringComparison]::OrdinalIgnoreCase)) { throw "unexpected download address: $u" }
    }
    $wc = New-Object Net.WebClient; $wc.Headers['User-Agent'] = 'SwarlexBattery-updater'
    $expected = ($wc.DownloadString($shaUrl).Trim() -split '\s+')[0].ToLowerInvariant()
    if ($expected -notmatch '^[0-9a-f]{64}$') { throw 'invalid SHA-256 file' }
    $wc.Headers['User-Agent'] = 'SwarlexBattery-updater'
    $wc.DownloadFile($url, $dest)
    $bytes = [IO.File]::ReadAllBytes($dest)
    if ($bytes.Length -lt 50KB -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) { Remove-Item -LiteralPath $dest -Force; throw 'the download is not an exe' }
    $actual = (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) { Remove-Item -LiteralPath $dest -Force; throw 'SHA-256 mismatch: the download is damaged or was altered' }
    'ok'
}

function Start-UpdateCheck([bool]$manual = $false) {
    if ($UpdateJob -or -not $Config.update.repo) { return }
    $script:UpdateManual = $manual
    $ps = [PowerShell]::Create(); $ps.RunspacePool = $Pool
    $null = $ps.AddScript($UpdateCheckScript).AddArgument([string]$Config.update.repo)
    $script:UpdateJob = @{ ps = $ps; handle = $ps.BeginInvoke(); kind = 'check'; started = Get-Date }
}

function Start-UpdateInstall {
    if ($UpdateJob -or -not $UpdateInfo) { return }
    if (-not $ExePath) { Show-Toast $null 'SwarlexBattery' (T 'updExeOnly'); return }
    if (-not $UpdateInfo.url -or -not $UpdateInfo.sha) { Show-Toast $null 'SwarlexBattery' (T 'updNoAssets'); Start-Process $UpdateInfo.page; return }
    $ps = [PowerShell]::Create(); $ps.RunspacePool = $Pool
    $null = $ps.AddScript($UpdateDownloadScript).AddArgument($UpdateInfo.url).AddArgument($UpdateInfo.sha).AddArgument("$ExePath.new").AddArgument([string]$Config.update.repo)
    $script:UpdateJob = @{ ps = $ps; handle = $ps.BeginInvoke(); kind = 'install'; started = Get-Date }
    Show-Toast $null 'SwarlexBattery' (T 'updDownloading' $UpdateInfo.version)
}

function Complete-UpdateJob {
    $j = $UpdateJob; $script:UpdateJob = $null
    $out = $null; $err = $null
    try { $out = $j.ps.EndInvoke($j.handle) | Select-Object -Last 1; if ($j.ps.Streams.Error.Count) { $err = "$($j.ps.Streams.Error[0])" } }
    catch { $err = "$($_.Exception.InnerException.Message)"; if (-not $err) { $err = "$_" } }
    finally { $j.ps.Dispose() }
    if ($j.kind -eq 'check') {
        $script:NextUpdateCheck = (Get-Date).AddHours([Math]::Max(1, [double]$Config.update.intervalHours))
        if ($err -or -not $out) {
            Write-Log "update check: $err"
            if ($UpdateManual) { Show-Toast $null 'SwarlexBattery' (T 'updCheckFailed' $err) }
            return
        }
        $info = $out | ConvertFrom-Json
        $latest = Norm-Version $info.tag
        if ($latest -and $AppVersion -and $latest -gt $AppVersion) {
            $script:UpdateInfo = @{ version = "$latest"; url = $info.url; sha = $info.sha; page = $info.page }
            if ($UpdateNotified -ne "$latest") { $script:UpdateNotified = "$latest"; Show-Toast $null 'SwarlexBattery' (T 'updAvailable' $latest) }
        } else {
            $script:UpdateInfo = $null
            if ($UpdateManual) { Show-Toast $null 'SwarlexBattery' (T 'updUpToDate' $AppVersion) }
        }
    } else {
        if ($err -or $out -ne 'ok') { Write-Log "update: $err"; Show-Toast $null 'SwarlexBattery' (T 'updFailed' $err); return }
        try {
            $old = "$ExePath.old"
            Remove-Item -LiteralPath $old -Force -ErrorAction SilentlyContinue
            Rename-Item -LiteralPath $ExePath -NewName (Split-Path $old -Leaf)        # the running exe can be renamed
            Move-Item -LiteralPath "$ExePath.new" -Destination $ExePath
            Write-Log "updated: v$AppVersion -> v$($UpdateInfo.version)"
            Start-Process -FilePath $ExePath                                          # waits for this instance to exit
            Stop-App
        } catch {
            Write-Log "update swap: $_"
            if (-not (Test-Path -LiteralPath $ExePath) -and (Test-Path -LiteralPath "$ExePath.old")) { Rename-Item -LiteralPath "$ExePath.old" -NewName (Split-Path $ExePath -Leaf) }
            Show-Toast $null 'SwarlexBattery' (T 'updApplyFailed' $_)
        }
    }
}

# ---------------------------------------------------------------- lifecycle
$ReloadRequested = $false
function Init-Plugins {
    foreach ($p in $Plugins.Values) {
        if ($p.job) { try { $null = $p.job.ps.BeginStop($null, $null) } catch {} }
        Remove-TrayIcons $p
    }
    Close-Panel
    $script:Config = Read-Config
    Load-Plugins
    foreach ($p in $Plugins.Values) { Sync-TrayIcons $p }   # placeholder icons until the first poll
}

$Stopping = $false
function Stop-App {
    if ($script:Stopping) { return }; $script:Stopping = $true
    $Timer.Stop()
    foreach ($p in $Plugins.Values) { if ($p.job) { try { $null = $p.job.ps.BeginStop($null, $null) } catch {} }; Remove-TrayIcons $p }
    try { $Panel.Close() } catch {}
    try { $Pool.Close() } catch {}
    [Windows.Threading.Dispatcher]::CurrentDispatcher.InvokeShutdown()
}

$Timer = New-Object Windows.Threading.DispatcherTimer
$Timer.Interval = [TimeSpan]::FromMilliseconds(250)
$Timer.Add_Tick({
    try {
        if ($script:ReloadRequested) { $script:ReloadRequested = $false; Init-Plugins; $script:PromoteAt = (Get-Date).AddSeconds(3); $script:PromoteCount = 0 }
        $now = Get-Date
        # Explorer registers a new icon a few seconds after it appears (and again for an exe in a new folder):
        # keep the icons pinned next to the clock - every 5 s for the first 90 s, then every 10 min
        if ($script:PromoteAt -and $now -ge $script:PromoteAt) {
            $script:PromoteCount++
            $script:PromoteAt = if ($script:PromoteCount -lt 18) { $now.AddSeconds(5) } else { $now.AddMinutes(10) }
            try { Promote-TrayIcons } catch { Write-Log "promote: $_" }
        }
        # a device was plugged in or out (receiver, charging cable): refresh after it settles, and once more later
        $dc = [SwarlexBattery.DeviceWatch]::Changes
        if ($dc -ne $script:SeenDeviceChanges) {
            $script:SeenDeviceChanges = $dc
            $script:DevicePollAt = @($now.AddSeconds(1.5), $now.AddSeconds(6))
        }
        if ($script:DevicePollAt -and $now -ge $script:DevicePollAt[0]) {
            $script:DevicePollAt = @($script:DevicePollAt | Select-Object -Skip 1)
            if (-not $script:DevicePollAt) { $script:DevicePollAt = $null }
            foreach ($p in $Plugins.Values) { Start-PluginJob $p 'poll' }
        }
        # updates: background check at start and every few hours; finished jobs are applied here (UI thread)
        if ($script:UpdateJob) {
            if ($script:UpdateJob.handle.IsCompleted) { Complete-UpdateJob }
            elseif (($now - $script:UpdateJob.started).TotalMinutes -gt 5) { $null = $script:UpdateJob.ps.BeginStop($null, $null); $script:UpdateJob = $null }
        } elseif ($Config.update.check -and $now -ge $script:NextUpdateCheck) { Start-UpdateCheck }
        # taskbar theme switch -> repaint (checked every 5 s)
        if ($now.Second % 5 -eq 0 -and $now.Millisecond -lt 250) {
            $light = Test-LightTaskbar
            if ($light -ne $script:LightTaskbar) { $script:LightTaskbar = $light; foreach ($p in $Plugins.Values) { Sync-TrayIcons $p }; Clear-IconCache }
        }
        foreach ($p in @($Plugins.Values)) {
            if ($p.job) {
                if ($p.job.handle.IsCompleted) { Complete-PluginJob $p }
                elseif (($now - $p.job.started).TotalSeconds -gt 60) { Write-Log "[$($p.id)] timed out"; $null = $p.job.ps.BeginStop($null, $null); $p.job = $null; $p.next = $now.AddSeconds($p.interval) }
            } elseif ($now -ge $p.next) { Start-PluginJob $p 'poll' }
        }
    } catch { Write-Log "tick: $_" }
})

$dispatcher = [Windows.Threading.Dispatcher]::CurrentDispatcher
$dispatcher.Add_UnhandledException({ Write-Log "UI: $($_.Exception)"; $_.Handled = $true })

Init-Plugins
$PromoteAt = (Get-Date).AddSeconds(3); $PromoteCount = 0
[SwarlexBattery.DeviceWatch]::Start(); $SeenDeviceChanges = 0; $DevicePollAt = $null
$Timer.Start()
Write-Log "SwarlexBattery v$AppVersion started (PID $PID, icon $IconSize px, $GlyphFont, language $Lang)"
try {
    [Windows.Threading.Dispatcher]::Run()
} finally {
    if (-not $Stopping) { foreach ($p in $Plugins.Values) { Remove-TrayIcons $p } }
    $mutex.ReleaseMutex()
}

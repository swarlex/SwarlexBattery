# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS  Builds dist\SwarlexBattery.exe: one file, scripts + plugins embedded, no install needed.
           Uses the C# compiler that ships with Windows (.NET Framework 4.x) - nothing to download.
#>
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$sma = [PSObject].Assembly.Location
$dist = Join-Path $here 'dist'
$null = New-Item -ItemType Directory -Force -Path $dist

# ---- icon (generated, so the repo stays text-only)
$ico = Join-Path $here 'core\swarlexbattery.ico'
if (-not (Test-Path -LiteralPath $ico)) {
    Add-Type -AssemblyName System.Drawing
    # Same language as the tray icon: a split ring (left = mouse, right = headset) around a
    # charging bolt, on a dark rounded tile. Drawn once at 512 px, scaled down per size.
    $D = 512
    $master = New-Object Drawing.Bitmap $D, $D
    $g = [Drawing.Graphics]::FromImage($master); $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $pad = $D * 0.04; $side = $D - 2 * $pad; $r = $side * 0.23
    $tile = New-Object Drawing.Drawing2D.GraphicsPath
    $tile.AddArc($pad, $pad, $r * 2, $r * 2, 180, 90); $tile.AddArc($pad + $side - $r * 2, $pad, $r * 2, $r * 2, 270, 90)
    $tile.AddArc($pad + $side - $r * 2, $pad + $side - $r * 2, $r * 2, $r * 2, 0, 90); $tile.AddArc($pad, $pad + $side - $r * 2, $r * 2, $r * 2, 90, 90)
    $tile.CloseFigure()
    $bg = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.PointF 0, $pad), (New-Object Drawing.PointF 0, ($D - $pad)), ([Drawing.Color]::FromArgb(255, 54, 56, 62)), ([Drawing.Color]::FromArgb(255, 22, 23, 26))
    $g.FillPath($bg, $tile)
    $g.DrawPath((New-Object Drawing.Pen ([Drawing.Color]::FromArgb(40, 255, 255, 255)), ([float]($D * 0.006))), $tile)

    $w = [float]($D * 0.085); $inset = $D * 0.2
    $rect = New-Object Drawing.RectangleF $inset, $inset, ($D - 2 * $inset), ($D - 2 * $inset)
    $gap = 16.0; $span = 180.0 - 2 * $gap
    $track = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(36, 255, 255, 255)), $w
    $white = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(255, 245, 245, 245)), $w
    $green = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(255, 63, 209, 106)), $w
    foreach ($p in $track, $white, $green) { $p.StartCap = 'Round'; $p.EndCap = 'Round' }
    $g.DrawArc($track, $rect, [float](90 + $gap), [float]$span)          # left track
    $g.DrawArc($track, $rect, [float](90 - $gap), [float](-$span))       # right track
    $g.DrawArc($white, $rect, [float](90 + $gap), [float]($span * 0.72)) # left: mouse 72 %
    $g.DrawArc($green, $rect, [float](90 - $gap), [float](-$span))       # right: headset full, charging

    $bs = $D * 0.32; $o = ($D - $bs) / 2
    $bolt = @(@(0.60, 0.02), @(0.16, 0.57), @(0.46, 0.57), @(0.37, 0.98), @(0.84, 0.41), @(0.54, 0.41), @(0.65, 0.02))
    $g.FillPolygon((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 245, 245, 245))),
        [Drawing.PointF[]]@($bolt | ForEach-Object { New-Object Drawing.PointF ([float]($o + $_[0] * $bs)), ([float]($o + $_[1] * $bs)) }))
    $g.Dispose()

    $pngs = foreach ($size in 256, 64, 48, 32, 24, 16) {
        $bmp = New-Object Drawing.Bitmap $size, $size
        $g = [Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'; $g.CompositingQuality = 'HighQuality'
        $g.DrawImage($master, 0, 0, $size, $size); $g.Dispose()
        $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
        , @($size, $ms.ToArray())
    }
    $master.Save((Join-Path $here 'core\swarlexbattery-icon.png'))
    $master.Dispose()
    $out = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter $out
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$pngs.Count)
    $offset = 6 + 16 * $pngs.Count
    foreach ($p in $pngs) {
        $s = if ($p[0] -ge 256) { 0 } else { $p[0] }
        $bw.Write([byte]$s); $bw.Write([byte]$s); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$p[1].Length); $bw.Write([uint32]$offset)
        $offset += $p[1].Length
    }
    foreach ($p in $pngs) { $bw.Write([byte[]]$p[1]) }
    [IO.File]::WriteAllBytes($ico, $out.ToArray())
}

# ---- embed every runtime file as app/<relative path>
$include = @('SwarlexBattery.ps1', 'config.default.json', 'VERSION', 'lang', 'core\Native.cs', 'core\Hid.cs', 'core\Devices.cs', 'plugins', 'tools')
$resArgs = foreach ($item in $include) {
    Get-ChildItem -LiteralPath (Join-Path $here $item) -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($here.Length + 1).Replace('\', '/')
        "/resource:`"$($_.FullName)`",app/$rel"
    }
}

# csc's temp files break on very long paths: build in a short temp dir, then copy.
$work = Join-Path ([IO.Path]::GetTempPath()) 'swarlexbattery-build'
$null = New-Item -ItemType Directory -Force -Path $work
Copy-Item -LiteralPath $ico -Destination "$work\swarlexbattery.ico" -Force
Copy-Item -LiteralPath (Join-Path $here 'core\app.manifest') -Destination "$work\app.manifest" -Force
# version from the VERSION file, stamped into the exe (the updater compares it with the latest GitHub release)
$version = (Get-Content -LiteralPath (Join-Path $here 'VERSION') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "invalid VERSION: $version" }
$asmInfo = Join-Path $work 'AssemblyInfo.cs'
Set-Content -LiteralPath $asmInfo -Encoding UTF8 -Value @(
    "[assembly: System.Reflection.AssemblyVersion(`"$version.0`")]",
    "[assembly: System.Reflection.AssemblyFileVersion(`"$version.0`")]",
    "[assembly: System.Reflection.AssemblyInformationalVersion(`"$version`")]")
$tmpExe = Join-Path $work 'SwarlexBattery.exe'
$exe = Join-Path $dist 'SwarlexBattery.exe'
$cscArgs = @('/nologo', '/target:winexe', '/optimize+', '/platform:anycpu', "/out:$tmpExe", "/win32icon:$work\swarlexbattery.ico", "/win32manifest:$work\app.manifest",
    "/reference:$sma", '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.Core.dll') +
    @($resArgs) + @((Join-Path $here 'core\Launcher.cs'), (Join-Path $here 'core\Native.cs'), (Join-Path $here 'core\Hid.cs'), (Join-Path $here 'core\Devices.cs'), $asmInfo)
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }
# a copy started from dist\ keeps the exe locked: a running exe can be renamed, so move it aside
if (Test-Path -LiteralPath $exe) {
    try { [IO.File]::Open($exe, 'Open', 'ReadWrite', 'None').Dispose() }
    catch { Remove-Item -LiteralPath "$exe.old" -Force -ErrorAction SilentlyContinue; Rename-Item -LiteralPath $exe -NewName 'SwarlexBattery.exe.old' }
}
Copy-Item -LiteralPath $tmpExe -Destination $exe -Force

# ---- the install wizard: SwarlexBattery-Setup.exe carries the app exe and LICENSE
Copy-Item -LiteralPath (Join-Path $here 'core\setup.manifest') -Destination "$work\setup.manifest" -Force
Copy-Item -LiteralPath (Join-Path $here 'LICENSE') -Destination "$work\LICENSE" -Force
$tmpSetup = Join-Path $work 'SwarlexBattery-Setup.exe'
$setup = Join-Path $dist 'SwarlexBattery-Setup.exe'
$setupArgs = @('/nologo', '/target:winexe', '/optimize+', '/platform:anycpu', '/codepage:65001', "/out:$tmpSetup",
    "/win32icon:$work\swarlexbattery.ico", "/win32manifest:$work\setup.manifest",
    '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.Core.dll', '/reference:System.Web.Extensions.dll',
    "/resource:$tmpExe,app.exe", "/resource:$work\LICENSE,LICENSE",
    (Join-Path $here 'core\Setup.cs'), $asmInfo)
& $csc @setupArgs
if ($LASTEXITCODE -ne 0) { throw "csc (setup) failed ($LASTEXITCODE)" }
Copy-Item -LiteralPath $tmpSetup -Destination $setup -Force
Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
# checksum published next to the exe in each GitHub release; the updater refuses a download that does not match
$hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$exe.sha256" -Value "$hash  SwarlexBattery.exe" -Encoding ASCII
$fi = Get-Item -LiteralPath $exe
"OK: v$version  $($fi.FullName)  ($([math]::Round($fi.Length / 1KB)) KB)"

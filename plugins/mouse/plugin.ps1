<#
  Mousekit for Windows (port of io.github.enoret.mousekit).
  libratbag (hardware DPI / polling rate / button remap stored on the mouse)
  has no Windows equivalent: those live in vendor apps (G HUB, Synapse, ...).
  This exposes everything Windows itself controls, through SystemParametersInfo,
  saved to the user profile like the Settings app does.
#>
param([string]$Action = 'poll', [string]$Arg = '', $Config, [string]$PluginDir, [string]$StateDir)

function Num([string]$v) { [int][Math]::Round([double]::Parse($v.Replace(',', '.'), [Globalization.CultureInfo]::InvariantCulture)) }
$M = [SwarlexBattery.Mouse]
switch ($Action) {
    'speed'     { $M::SetSpeed((Num $Arg)) }
    'precision' { $M::SetPrecision($Arg -eq 'True') }
    'wheel'     { $M::SetWheelLines((Num $Arg)) }
    'dblclick'  { $M::SetDoubleClick((Num $Arg)) }
    'swap'      { $M::SetSwapped($Arg -eq 'True') }
    'sonar'     { $M::SetSonar($Arg -eq 'True') }
    'gaming'    { $M::SetPrecision($false); $M::SetSpeed(10) }   # 1:1 raw-ish movement
    'cpl'       { Start-Process control.exe -ArgumentList 'main.cpl' }
    'settings'  { Start-Process 'ms-settings:mousetouchpad' }
}

$speed = $M::GetSpeed(); $prec = $M::GetPrecision()
$mice = @()
try { $mice = @(Get-CimInstance Win32_PointingDevice -ErrorAction Stop | Where-Object { $_.Status -eq 'OK' } | Select-Object -ExpandProperty Name -Unique) } catch {}

@{
    title = 'Mousekit'
    pill = @{ text = "$speed"; state = 'ok'; tooltip = "Imlec hizi $speed/20 - Hassasiyet $(if ($prec) { 'acik' } else { 'kapali' })" }
    sections = @(
        @{ title = 'Imlec'; items = @(
            @{ t = 'slider'; label = 'Imlec hizi'; min = 1; max = 20; step = 1; value = $speed; action = 'speed'; sub = '10 = 1:1 (varsayilan)' },
            @{ t = 'toggle'; label = 'Isaretci hassasiyetini artir (ivme)'; value = $prec; action = 'precision' },
            @{ t = 'toggle'; label = 'Ctrl ile imleci goster'; value = $M::GetSonar(); action = 'sonar' }
        ) },
        @{ title = 'Tuslar ve kaydirma'; items = @(
            @{ t = 'slider'; label = 'Kaydirma satiri'; min = 1; max = 20; step = 1; value = $M::GetWheelLines(); action = 'wheel' },
            @{ t = 'slider'; label = 'Cift tik suresi'; min = 200; max = 900; step = 50; value = $M::GetDoubleClick(); action = 'dblclick'; fmt = '{0} ms' },
            @{ t = 'toggle'; label = 'Sol/sag tuslari degistir'; value = $M::GetSwapped(); action = 'swap' }
        ) },
        @{ title = 'Cihazlar'; items = @(
            @($mice | ForEach-Object { @{ t = 'row'; icon = 'E962'; label = $_ } }) +
            @(@{ t = 'text'; text = 'DPI, polling rate ve tus atamalari faredeki donanima yazilir: Windows bunu ureticinin yazilimi (G HUB, Synapse, iCUE...) ile yapar.' },
              @{ t = 'buttons'; items = @(
                @{ text = 'Oyun modu (1:1)'; icon = 'E7FC'; action = 'gaming'; tooltip = 'Ivme kapali, hiz 10' },
                @{ text = 'Fare ayarlari'; icon = 'E713'; action = 'settings' },
                @{ text = 'Denetim Masasi'; icon = 'E8A7'; action = 'cpl' }) })
        ) }
    )
}

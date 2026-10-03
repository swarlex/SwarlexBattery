<#
  Glass & Blur for Windows (port of lutfi.glass).
  Hyprland's active/inactive opacity -> per-window alpha via SwarlexBattery.Glass
  (layered windows, restored on exit). Bar blur -> acrylic/blur backdrop of the
  bar itself. Plus Windows' own "Transparency effects" switch (HKCU).
  Fullscreen windows, tool windows and excluded processes are never touched.
#>
param([string]$Action = 'poll', [string]$Arg = '', $Config, [string]$PluginDir, [string]$StateDir)

$StateFile = Join-Path $StateDir 'glass.json'
$PersonalizeKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'

$defaults = @{ enabled = $false; active = 0.95; inactive = 0.85; backdrop = 'acrylic'; barOpacity = 0.6 }
$s = $defaults.Clone()
if (Test-Path -LiteralPath $StateFile) {
    try { $j = Get-Content -LiteralPath $StateFile -Raw | ConvertFrom-Json; foreach ($k in @($defaults.Keys)) { if ($null -ne $j.$k) { $s[$k] = $j.$k } } } catch {}
}

function Clamp([double]$v, [double]$lo, [double]$hi) { [Math]::Round([Math]::Max($lo, [Math]::Min($hi, $v)), 2) }
function Parse-Num([string]$v) { [double]::Parse($v.Replace(',', '.'), [Globalization.CultureInfo]::InvariantCulture) }

$changed = $true
switch ($Action) {
    'toggle'     { $s.enabled = -not $s.enabled }
    'enabled'    { $s.enabled = $Arg -eq 'True' }
    'active'     { $s.active = Clamp (Parse-Num $Arg) 0.3 1 }
    'inactive'   { $s.inactive = Clamp (Parse-Num $Arg) 0.3 1 }
    'barOpacity' { $s.barOpacity = Clamp (Parse-Num $Arg) 0 1 }
    'backdrop'   { if ($Arg -in 'acrylic', 'blur', 'none') { $s.backdrop = $Arg } }
    'systemTransparency' { Set-ItemProperty -Path $PersonalizeKey -Name EnableTransparency -Value ([int]($Arg -eq 'True')) -Type DWord }
    'preset' {
        switch ($Arg) {
            'subtle' { $s.active = 0.97; $s.inactive = 0.88; $s.barOpacity = 0.6 }
            'glass'  { $s.active = 0.90; $s.inactive = 0.75; $s.barOpacity = 0.35 }
            'solid'  { $s.active = 1.0;  $s.inactive = 1.0;  $s.barOpacity = 0.95 }
        }
        $s.enabled = $Arg -ne 'solid'
    }
    'reset' { $s = $defaults.Clone() }
    default { $changed = $false }
}
if ($changed) {
    $tmp = "$StateFile.tmp"
    $s | ConvertTo-Json | Set-Content -LiteralPath $tmp -Encoding UTF8
    Move-Item -LiteralPath $tmp -Destination $StateFile -Force   # atomic replace
}

# Idempotent: the native engine only touches windows whose alpha actually changes.
[SwarlexBattery.Glass]::Configure([bool]$s.enabled, [double]$s.active, [double]$s.inactive, [string[]]@($Config.exclude))

$sysTransparency = $true
try { $sysTransparency = [bool](Get-ItemPropertyValue -Path $PersonalizeKey -Name EnableTransparency -ErrorAction Stop) } catch {}

@{
    title = 'Glass & Blur'
    host = @{ backdrop = $s.backdrop; barOpacity = $s.barOpacity }
    pill = @{ state = $(if ($s.enabled) { 'busy' } else { 'off' }); tooltip = "Glass: $(if ($s.enabled) { 'acik' } else { 'kapali' }) - aktif $([int]($s.active*100))% / pasif $([int]($s.inactive*100))%" }
    sections = @(
        @{ title = 'Pencereler'; items = @(
            @{ t = 'toggle'; label = 'Pencere saydamligi'; value = [bool]$s.enabled; action = 'enabled' },
            @{ t = 'slider'; label = 'Aktif pencere'; min = 0.3; max = 1; step = 0.01; value = $s.active; action = 'active'; fmt = '{0:P0}' },
            @{ t = 'slider'; label = 'Pasif pencereler'; min = 0.3; max = 1; step = 0.01; value = $s.inactive; action = 'inactive'; fmt = '{0:P0}' },
            @{ t = 'text'; text = 'Tam ekran pencereler (oyun/video) ve haric tutulan uygulamalar etkilenmez. Cikista tum pencereler eski haline doner.' }
        ) },
        @{ title = 'Panel'; items = @(
            @{ t = 'choice'; label = 'Arka plan'; value = $s.backdrop; action = 'backdrop'; options = @(
                @{ text = 'Acrylic'; value = 'acrylic' }, @{ text = 'Blur'; value = 'blur' }, @{ text = 'Duz'; value = 'none' }) },
            @{ t = 'slider'; label = 'Panel opakligi'; min = 0; max = 1; step = 0.05; value = $s.barOpacity; action = 'barOpacity'; fmt = '{0:P0}' }
        ) },
        @{ title = 'Sistem'; items = @(
            @{ t = 'toggle'; label = 'Windows saydamlik efektleri (Baslat, gorev cubugu)'; value = $sysTransparency; action = 'systemTransparency' },
            @{ t = 'choice'; label = 'Hazir ayar'; value = ''; action = 'preset'; options = @(
                @{ text = 'Hafif'; value = 'subtle' }, @{ text = 'Cam'; value = 'glass' }, @{ text = 'Opak'; value = 'solid' }) },
            @{ t = 'buttons'; items = @(@{ text = 'Sifirla'; icon = 'E777'; action = 'reset' }) }
        ) }
    )
}

# grab-puck.ps1 - capture the running Puck window so the agent can look at it.
#
# Two sources, tried in order:
#   1. The live window. PrintWindow with PW_RENDERFULLCONTENT works for most apps but
#      commonly returns an all-black frame for a D3D swapchain, so the result is checked
#      and falls back to BitBlt off the screen DC. BitBlt needs the window actually
#      visible and not in exclusive fullscreen; borderless windowed is the reliable mode.
#   2. -Steam: the newest file Steam's own F12 screenshot key wrote for appid 2994020.
#      Slower to trigger but immune to both problems above, since the game renders it.

param(
    [string]$Out,
    [switch]$Steam
)

$ErrorActionPreference = 'Stop'
if (-not $Out) {
    $Out = Join-Path $PSScriptRoot ("puck-{0}.png" -f (Get-Random))
}

if ($Steam) {
    $dir = "C:\Program Files (x86)\Steam\userdata\106780006\760\remote\2994020\screenshots"
    $shot = Get-ChildItem -Path $dir -Filter *.jpg -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $shot) { Write-Output "NO-STEAM-SCREENSHOTS in $dir"; exit 1 }
    Write-Output ("STEAM {0}  ({1})" -f $shot.FullName, $shot.LastWriteTime)
    exit 0
}

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class PuckCap {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}
'@ -ReferencedAssemblies System.Drawing, System.Runtime.InteropServices

$p = Get-Process -Name Puck -ErrorAction SilentlyContinue |
     Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Output "PUCK-NOT-RUNNING"; exit 1 }

$h = $p.MainWindowHandle
[void][PuckCap]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 400   # let the window come forward before the grab

$r = New-Object PuckCap+RECT
[void][PuckCap]::GetWindowRect($h, [ref]$r)
$w = $r.R - $r.L; $ht = $r.B - $r.T
if ($w -le 0 -or $ht -le 0) { Write-Output "BAD-RECT ${w}x${ht}"; exit 1 }

function Save-Bitmap($bmp, $path) {
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
}

# Sample a grid of pixels; an all-black frame means PrintWindow did not see the D3D surface.
function Test-Blank($bmp) {
    for ($x = 4; $x -lt $bmp.Width; $x += [Math]::Max(1, [int]($bmp.Width / 24))) {
        for ($y = 4; $y -lt $bmp.Height; $y += [Math]::Max(1, [int]($bmp.Height / 24))) {
            $c = $bmp.GetPixel($x, $y)
            if ($c.R -gt 8 -or $c.G -gt 8 -or $c.B -gt 8) { return $false }
        }
    }
    return $true
}

$bmp = New-Object System.Drawing.Bitmap($w, $ht)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$dc = $g.GetHdc()
$ok = [PuckCap]::PrintWindow($h, $dc, 2)
$g.ReleaseHdc($dc); $g.Dispose()

$method = 'PrintWindow'
if (-not $ok -or (Test-Blank $bmp)) {
    $bmp.Dispose()
    $bmp = New-Object System.Drawing.Bitmap($w, $ht)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size($w, $ht)))
    $g.Dispose()
    $method = 'BitBlt'
    if (Test-Blank $bmp) { $method = 'BitBlt(STILL-BLANK: exclusive fullscreen? use -Steam)' }
}

Save-Bitmap $bmp $Out
$bmp.Dispose()
Write-Output ("{0} {1}  {2}x{3}" -f $method, $Out, $w, $ht)

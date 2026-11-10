# Screenshot of a process's main window, taken from the screen (works for elevated windows too).
param([string]$Process = 'RigControl', [int]$Id = 0, [string]$Out = "$env:TEMP\rigcontrol-shot.png")
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class W {
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Ri, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out R r, int s);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
}
'@
[W]::SetProcessDPIAware() | Out-Null
$p = if ($Id) { Get-Process -Id $Id } else { Get-Process $Process -ErrorAction Stop | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1 }
if (-not $p) { throw "no window for $Process" }
$r = New-Object W+R
# Extended frame bounds = the visible window without the invisible resize border.
[W]::DwmGetWindowAttribute($p.MainWindowHandle, 9, [ref]$r, 16) | Out-Null
$w = $r.Ri - $r.L; $h = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
# PrintWindow renders the window itself even when covered (same integrity only); fall back to the screen.
$wr = New-Object W+R; [W]::GetWindowRect($p.MainWindowHandle, [ref]$wr) | Out-Null
$full = New-Object System.Drawing.Bitmap ($wr.Ri - $wr.L), ($wr.B - $wr.T)
$fg = [System.Drawing.Graphics]::FromImage($full); $hdc = $fg.GetHdc()
$ok = [W]::PrintWindow($p.MainWindowHandle, $hdc, 2); $fg.ReleaseHdc($hdc); $fg.Dispose()
if ($ok) { $g.DrawImage($full, ($wr.L - $r.L), ($wr.T - $r.T)) } else { $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size) }
$full.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
$Out

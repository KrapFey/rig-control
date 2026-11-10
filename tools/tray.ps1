# Simulates tray-icon mouse messages to a RigControl process (H.NotifyIcon callback id 0x400; lParam = mouse message).
#   tray.ps1 -Id 1234 -Click Single|Double
param([Parameter(Mandatory)][int]$Id, [ValidateSet('Single', 'Double')][string]$Click = 'Double')
Add-Type @'
using System; using System.Text; using System.Runtime.InteropServices;
public static class TrayIcon {
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  public delegate bool P(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(P f, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  public static IntPtr Sink(uint pid) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); var c = new StringBuilder(256); GetClassName(h, c, 256);
      if (p == pid && c.ToString().StartsWith("H.NotifyIcon")) found = h; return true; }, IntPtr.Zero);
    return found;
  }
}
'@
$sink = [TrayIcon]::Sink([uint32]$Id)
if ($sink -eq [IntPtr]::Zero) { throw "no tray window for process $Id" }
$msgs = if ($Click -eq 'Single') { 0x201, 0x202 } else { 0x201, 0x202, 0x203, 0x202 }
foreach ($m in $msgs) { [TrayIcon]::PostMessage($sink, 0x400, [IntPtr]::Zero, [IntPtr]$m) | Out-Null; Start-Sleep -Milliseconds 70 }

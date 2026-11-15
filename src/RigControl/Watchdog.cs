using System.Diagnostics;
using System.Runtime.InteropServices;
using RigControl.Core;

namespace RigControl;

/// <summary>
/// A second copy of RigControl, started by the first, that waits for it to end. If it ended without exiting (see
/// <see cref="CrashGuard"/>), fans are still at the last speed it set: start RigControl again so the curves take over,
/// or, when it keeps crashing, hand every fan back to the hardware. It has no window and does nothing else.
/// </summary>
static class Watchdog
{
    /// <summary>Starts the watchdog for this process (it inherits the admin rights it needs to reach the fans).</summary>
    public static void Start()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--watchdog {Environment.ProcessId}") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
        }
        catch (Exception e) { App.Log($"watchdog: couldn't start ({e.Message})"); }
    }

    public static int Run(int pid)
    {
        App.UseBuildFolder();
        AppConfig.Log = App.Log;
        try
        {
            using var p = Process.GetProcessById(pid);
            p.WaitForExit();
        }
        catch (ArgumentException) { /* already gone */ }

        var now = DateTime.Now;
        var action = CrashGuard.Decide(pid, CrashGuard.MarkedPid(), CrashGuard.RecentRestarts(now), GetSystemMetrics(SM_SHUTTINGDOWN) != 0);
        switch (action)
        {
            case CrashGuard.Action.Restart:
                App.Log($"watchdog: RigControl (pid {pid}) ended without exiting; starting it again");
                CrashGuard.NoteRestart(now);
                try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--tray --crashed") { UseShellExecute = false })?.Dispose(); }
                catch (Exception e) { App.Log($"watchdog: couldn't start RigControl ({e.Message})"); ReleaseFans(); }
                break;
            case CrashGuard.Action.ReleaseFans:
                App.Log($"watchdog: RigControl (pid {pid}) ended without exiting {CrashGuard.MaxRestarts} times in {CrashGuard.RestartWindow.TotalMinutes:0} minutes; handing the fans back to the hardware instead");
                ReleaseFans();
                break;
        }
        return 0;
    }

    /// <summary>Opens the fan drivers the way RigControl does and gives every fan back to the hardware's own control.</summary>
    static void ReleaseFans()
    {
        try
        {
            var cfg = AppConfig.Load();
            using var hub = new HardwareHub();
            hub.Open(cfg.Drivers);
            foreach (var fan in hub.Fans)
            {
                try { fan.Release(); } catch (Exception e) { App.Log($"watchdog: {fan.Name}: {e.Message}"); }
            }
            hub.Flush();
            App.Log($"watchdog: {hub.Fans.Count} fans handed back to the hardware");
        }
        catch (Exception e) { App.Log($"watchdog: couldn't hand the fans back ({e.Message})"); }
    }

    const int SM_SHUTTINGDOWN = 0x2000; // Windows is ending the session: RigControl was told to go, nothing to bring back
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
}

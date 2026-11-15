using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System.Runtime.InteropServices;

namespace RigControl;

public static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // The uninstaller runs this: remove the start-with-Windows task (it needs admin, like everything here).
        if (args.Contains("--uninstall")) { try { Startup.Disable(); } catch { } return 0; }

        // "--watchdog <pid>": started by RigControl itself, to bring it back if it dies without exiting.
        var wd = Array.IndexOf(args, "--watchdog");
        if (wd >= 0 && wd + 1 < args.Length && int.TryParse(args[wd + 1], out var watched))
        {
            using var watchdogMutex = RunningMutex("RigControlWatchdog"); // setup waits for this one too
            return Watchdog.Run(watched);
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();

        // "--after <pid>": a restart. Wait for the old instance to let go of the hardware first. Shutting down can
        // take a while (each device gets a few seconds to let go), and an instance started while the old one is
        // still registered would hand itself over to it below instead of starting.
        var after = Array.IndexOf(args, "--after");
        if (after >= 0 && after + 1 < args.Length && int.TryParse(args[after + 1], out var pid))
        {
            try { System.Diagnostics.Process.GetProcessById(pid).WaitForExit(60_000); } catch { /* already gone */ }
        }

        // One instance owns the hardware. A second launch just brings the first one's window up.
        #if UITEST
        var main = AppInstance.FindOrRegisterForKey("RigControl.UiTest");
#else
        var main = AppInstance.FindOrRegisterForKey("RigControl.Main");
#endif
        if (!main.IsCurrent)
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            // This launch may bring a window to the front (the user just started it); the running copy may not, by
            // itself: Windows lets only the foreground process do that. Hand the permission over.
            AllowSetForegroundWindow(main.ProcessId);
            // Bounded: if the other instance is on its way out it never answers, and waiting forever would leave
            // this one stuck in the background holding the program files.
            Task.Run(() => main.RedirectActivationToAsync(activation).AsTask()).Wait(TimeSpan.FromSeconds(10));
            return 0;
        }
        main.Activated += (_, e) =>
        {
            // "RigControl.exe --mini-dashboard" while it runs (from a shortcut, say) opens the tray's mini dashboard.
            if (e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch && launch.Arguments.Contains("--mini-dashboard"))
                App.Instance?.ToggleMiniDashboard();
            else App.Instance?.ShowWindow();
        };
#if !UITEST
        // Setup checks this mutex to know RigControl is running. RigControl runs elevated and setup doesn't,
        // so the mutex gets a DACL that lets setup see it (synchronize only).
        using var running = RunningMutex("RigControlRunning");
#endif

        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }

    /// <summary>A named mutex setup checks for, openable (SYNCHRONIZE) by everyone in the session; null if it can't be made.</summary>
    internal static SafeHandle? RunningMutex(string name)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("D:(A;;0x100000;;;WD)", 1, out var sd, IntPtr.Zero)) return null;
        try
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = sd };
            var h = CreateMutexW(ref sa, false, name);
            return h.IsInvalid ? null : h;
        }
        finally { LocalFree(sd); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern Microsoft.Win32.SafeHandles.SafeWaitHandle CreateMutexW(ref SECURITY_ATTRIBUTES sa, bool initialOwner, string name);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, int revision, out IntPtr sd, IntPtr size);
    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr mem);
    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(uint processId);
}

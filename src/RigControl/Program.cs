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

        WinRT.ComWrappersSupport.InitializeComWrappers();

        // "--after <pid>": a restart. Wait for the old instance to let go of the hardware first.
        var after = Array.IndexOf(args, "--after");
        if (after >= 0 && after + 1 < args.Length && int.TryParse(args[after + 1], out var pid))
        {
            try { System.Diagnostics.Process.GetProcessById(pid).WaitForExit(15_000); } catch { /* already gone */ }
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
            Task.Run(() => main.RedirectActivationToAsync(activation).AsTask()).Wait();
            return 0;
        }
        main.Activated += (_, _) => App.Instance?.ShowWindow();
#if !UITEST
        // Setup checks this mutex to know RigControl is running. RigControl runs elevated and setup doesn't,
        // so the mutex gets a DACL that lets setup see it (synchronize only).
        using var running = RunningMutex();
#endif

        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }

    /// <summary>Named "RigControlRunning", openable (SYNCHRONIZE) by everyone in the session; null if it can't be made.</summary>
    static SafeHandle? RunningMutex()
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("D:(A;;0x100000;;;WD)", 1, out var sd, IntPtr.Zero)) return null;
        try
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = sd };
            var h = CreateMutexW(ref sa, false, "RigControlRunning");
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
}

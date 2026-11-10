using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace RigControl;

public static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
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

        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }
}

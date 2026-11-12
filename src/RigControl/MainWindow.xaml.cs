using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using RigControl.Pages;

namespace RigControl;

public sealed partial class MainWindow : Window
{
    static readonly Dictionary<string, Type> Pages = new()
    {
        ["Dashboard"] = typeof(DashboardPage),
        ["Curves"] = typeof(CurvesPage),
        ["Lighting"] = typeof(LightingPage),
        ["Devices"] = typeof(DevicesPage),
        ["Sensors"] = typeof(SensorsPage),
        ["Supported"] = typeof(SupportedPage),
        ["Settings"] = typeof(SettingsPage),
    };

    int _lastIndex;
    readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "RigControl.ico"));
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        var scale = (Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new((int)(1280 * scale), (int)(820 * scale)));

        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
        Closed += (_, _) => _statusTimer.Stop();
        Navigate("Dashboard", null);
        UpdateStatus();
    }

    void UpdateStatus()
    {
        var app = App.Instance!;
        Status.Text = app.Engine.Paused ? "Paused · FanControl is running" : "Live";
    }

    void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = args.IsSettingsSelected ? "Settings" : (args.SelectedItemContainer?.Tag as string ?? "Dashboard");
        Navigate(tag, args.RecommendedNavigationTransitionInfo);
    }

    /// <summary>
    /// Clicking the item that's already selected (e.g. Lighting while a lighting device's page is open) still goes to
    /// its page: selection doesn't change, so SelectionChanged doesn't fire.
    /// </summary>
    void Nav_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        var tag = args.IsSettingsInvoked ? "Settings" : (args.InvokedItemContainer?.Tag as string);
        if (tag is not null && Pages.ContainsKey(tag)) Navigate(tag, null);
    }

    void Navigate(string tag, NavigationTransitionInfo? _)
    {
        if (ContentFrame.CurrentSourcePageType == Pages[tag]) return; // already there (both menu events can ask)
        var index = Pages.Keys.ToList().IndexOf(tag);
        // Going down the menu slides up from below; going back up drills in.
        NavigationTransitionInfo info = index >= _lastIndex
            ? new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromBottom }
            : new DrillInNavigationTransitionInfo();
        _lastIndex = index;
        ContentFrame.Navigate(Pages[tag], null, info);
    }

    /// <summary>Builds the open page again (after a rescan replaced what it shows). A fan's page goes to the dashboard.</summary>
    public void ReloadPage()
    {
        var page = ContentFrame.CurrentSourcePageType;
        if (page is null) return;
        if (page == typeof(FanPage)) page = typeof(DashboardPage);
        if (page == typeof(LightingDevicePage)) page = typeof(LightingPage);
        ContentFrame.Navigate(page, null, new SuppressNavigationTransitionInfo());
        if (ContentFrame.BackStackDepth > 0) ContentFrame.BackStack.RemoveAt(ContentFrame.BackStackDepth - 1); // not a step to go back to
    }

    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized) p.Restore();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hWnd);
}

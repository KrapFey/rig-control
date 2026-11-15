using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using RigControl.Core;
using RigControl.Pages;
using Windows.Graphics;

namespace RigControl;

public sealed partial class MainWindow : Window
{
    static readonly Dictionary<string, Type> Pages = new()
    {
        ["Overview"] = typeof(OverviewPage),
        ["Cooling"] = typeof(CoolingPage),
        ["Lighting"] = typeof(LightingPage),
        ["Devices"] = typeof(DevicesPage),
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
        PlaceWindow();

        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
        // Per-frame drawing only while the window can be seen (see Frames).
        Frames.Visible = true;
        VisibilityChanged += (_, e) => Frames.Visible = e.Visible && !Minimized;
        AppWindow.Changed += (_, _) => Frames.Visible = AppWindow.IsVisible && !Minimized;
        Closed += (_, _) =>
        {
            Frames.Visible = false;
            _statusTimer.Stop();
            RememberPlacement();
        };
        Navigate("Overview", null);
        UpdateStatus();
    }

    bool Minimized => AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };

    RectInt32 _restored; // the window's bounds when last neither maximized nor minimized

    /// <summary>Where the window was last time, if that spot is still on a connected screen; otherwise 1280×820.</summary>
    /// <summary>Smallest the window gets (DIPs): Cooling's curves, editor and fan panel side by side still fit.</summary>
    const int MinWidth = 1000, MinHeight = 640;

    void PlaceWindow()
    {
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        int minW = (int)(MinWidth * scale), minH = (int)(MinHeight * scale);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = minW;
            presenter.PreferredMinimumHeight = minH;
        }
        var saved = App.Instance!.Config.Window;
        var rect = saved is { Width: >= 400, Height: >= 300 }
            ? new RectInt32(saved.X, saved.Y, Math.Max(saved.Width, minW), Math.Max(saved.Height, minH)) // saved smaller, before there was a minimum
            : default;
        if (rect.Width > 0 && DisplayArea.GetFromRect(rect, DisplayAreaFallback.None) is not null) AppWindow.MoveAndResize(rect);
        else AppWindow.Resize(new((int)(1280 * scale), (int)(820 * scale)));
        _restored = new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        AppWindow.Changed += (_, e) =>
        {
            if ((e.DidPositionChange || e.DidSizeChange) && AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
                _restored = new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        };
        if (saved is { Maximized: true } && AppWindow.Presenter is OverlappedPresenter p) p.Maximize();
    }

    void RememberPlacement()
    {
        var max = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        var app = App.Instance!;
        var now = new WindowPlacement(_restored.X, _restored.Y, _restored.Width, _restored.Height, max);
        if (now == app.Config.Window) return;
        app.Config.Window = now;
        app.ConfigChanged();
    }

    void UpdateStatus()
    {
        var app = App.Instance!;
        StatusPill.Visibility = app.Engine.Paused ? Visibility.Visible : Visibility.Collapsed;
        Status.Text = "Paused · FanControl is running";
    }

    void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = args.IsSettingsSelected ? "Settings" : (args.SelectedItemContainer?.Tag as string ?? "Overview");
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

    object? _parameter; // for the next navigation (see Open)

    /// <summary>Goes to a menu page with something to open there, e.g. Cooling with one fan's settings.</summary>
    public void Open(string tag, object? parameter)
    {
        var item = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag as string == tag);
        if (item is null) return;
        _parameter = parameter;
        if (ReferenceEquals(Nav.SelectedItem, item)) Navigate(tag, null, force: true);
        else Nav.SelectedItem = item; // navigates through SelectionChanged
    }

    void Navigate(string tag, NavigationTransitionInfo? _, bool force = false)
    {
        if (!force && ContentFrame.CurrentSourcePageType == Pages[tag]) return; // already there (both menu events can ask)
        var index = Pages.Keys.ToList().IndexOf(tag);
        // Going down the menu slides up from below; going back up drills in.
        NavigationTransitionInfo info = index >= _lastIndex
            ? new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromBottom }
            : new DrillInNavigationTransitionInfo();
        _lastIndex = index;
        var parameter = _parameter;
        _parameter = null;
        ContentFrame.Navigate(Pages[tag], parameter, info);
    }

    /// <summary>Builds the open page again (after a rescan replaced what it shows).</summary>
    public void ReloadPage()
    {
        var page = ContentFrame.CurrentSourcePageType;
        if (page is null) return;
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

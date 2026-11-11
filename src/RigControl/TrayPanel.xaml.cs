using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace RigControl;

/// <summary>
/// The mini dashboard that opens from the tray icon: CPU and GPU temperature, airflow, the fans, a lighting switch
/// and a way into the full app. A small borderless window above the taskbar that hides when it loses focus.
/// </summary>
public sealed partial class TrayPanel : Window
{
    const int PanelWidth = 360, PanelHeight = 470; // in DIPs
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    bool _loading;
    long _shownAt;

    public TrayPanel()
    {
        InitializeComponent();
        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = presenter.IsMaximizable = presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false; // not in Alt+Tab or the taskbar
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));

        Activated += (_, e) =>
        {
            // Clicking anywhere else closes it, like the system's own flyouts (but not in the instant it opens).
            if (e.WindowActivationState == WindowActivationState.Deactivated && Environment.TickCount64 - _shownAt > 300) Hide();
        };
        _timer.Tick += (_, _) => Refresh();
        AppWindow.Closing += (_, e) => { e.Cancel = true; Hide(); }; // Alt+F4 hides; the app owns its lifetime
    }

    /// <summary>Opens above the tray, on the screen the pointer is on (or closes it if it was open).</summary>
    public void Toggle()
    {
        if (AppWindow.IsVisible) { Hide(); return; }
        GetCursorPos(out var p);
        var area = DisplayArea.GetFromPoint(new PointInt32(p.X, p.Y), DisplayAreaFallback.Primary).WorkArea;
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        int w = (int)(PanelWidth * scale), h = (int)(PanelHeight * scale), margin = (int)(12 * scale);
        // Bottom-right of the work area, which sits just above (or beside) the taskbar wherever it is.
        int x = Math.Clamp(p.X - w / 2, area.X + margin, area.X + area.Width - w - margin);
        int y = area.Y + area.Height - h - margin;
        if (p.Y < area.Y) y = area.Y + margin; // taskbar on top
        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
        _loading = true;
        LightingSwitch.IsOn = App.Instance!.Config.Lighting.Enabled;
        _loading = false;
        Refresh();
        _shownAt = Environment.TickCount64;
        AppWindow.Show();
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _timer.Start();
    }

    void Hide()
    {
        _timer.Stop();
        AppWindow.Hide();
    }

    void Refresh()
    {
        var app = App.Instance!;
        Status.Text = app.Engine.Paused ? "Paused" : "Live";
        CpuText.Text = Live.CpuSensor(app.Hub.Temps)?.Value is float c ? $"{c:0}°" : "–";
        GpuText.Text = Live.GpuSensor(app.Hub.Temps)?.Value is float g ? $"{g:0}°" : "–";
        AirText.Text = Live.Airflow(app.Hub.Fans) is double a ? $"{a:0}%" : "–";

        // The spinning fans, busiest first.
        var fans = app.Hub.Fans.Where(f => f.Connected ?? f.Rpm > 0).OrderByDescending(f => f.Rpm ?? 0).Take(8).ToList();
        FansTitle.Text = fans.Count == 0 ? "NO FANS SPINNING" : $"FANS · {fans.Count} SPINNING";
        while (Fans.Children.Count > fans.Count) Fans.Children.RemoveAt(Fans.Children.Count - 1);
        while (Fans.Children.Count < fans.Count) Fans.Children.Add(FanRow());
        for (int i = 0; i < fans.Count; i++)
        {
            var row = (Grid)Fans.Children[i];
            var f = fans[i];
            ((TextBlock)row.Children[0]).Text = app.Config.Fans.TryGetValue(f.Id, out var s) && !string.IsNullOrWhiteSpace(s.Name) ? s.Name : f.Name;
            ((TextBlock)row.Children[1]).Text = f.Rpm is float rpm ? $"{rpm:0} rpm" : "";
            var bar = (Border)((Grid)row.Children[2]).Children[0];
            ((ScaleTransform)bar.RenderTransform).ScaleX = Math.Clamp((f.Duty ?? 0) / 100.0, 0, 1);
        }
    }

    static Grid FanRow()
    {
        var row = new Grid { ColumnSpacing = 8, RowSpacing = 4 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition());
        row.RowDefinitions.Add(new RowDefinition());
        row.Children.Add(new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis });
        var rpm = new TextBlock { Opacity = 0.7 };
        Grid.SetColumn(rpm, 1);
        row.Children.Add(rpm);
        var track = new Grid { Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, 255, 255, 255)) };
        track.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(2),
            Background = (Brush)Application.Current.Resources["AccentGradient"],
            RenderTransform = new ScaleTransform(),
        });
        Grid.SetRow(track, 1);
        Grid.SetColumnSpan(track, 2);
        row.Children.Add(track);
        return row;
    }

    void LightingSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Instance!.Config.Lighting.Enabled = LightingSwitch.IsOn;
        App.Instance.ConfigChanged();
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        App.Instance!.ShowWindow();
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}

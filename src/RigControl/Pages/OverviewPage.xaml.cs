using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RigControl.Controls;
using RigControl.Core;

namespace RigControl.Pages;

/// <summary>
/// Everything at a glance: alerts, the main temperatures and airflow with their last 10 minutes, cooling curve by
/// curve, lighting, and device batteries. Each part opens its own page.
/// </summary>
public sealed partial class OverviewPage : Page
{
    readonly Live _live = Live.Instance;
    readonly List<(CurveDef def, TextBlock detail, TextBlock value, ScaleTransform bar)> _curveRows = [];
    readonly List<(string key, TextBlock battery, FontIcon glyph)> _deviceRows = [];
    HashSet<string> _stalled = [];
    bool _loading;

    static App AppI => App.Instance!;

    public OverviewPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        _live.Attach();
        LayoutStats();
        BuildCurveRows();
        BuildNotices();
        AppI.UpdateFound += BuildNotices;
        AppI.LightingStateChanged += OnRefreshed;
        _live.Refreshed += OnRefreshed;
        OnRefreshed();
    }

    void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _live.Refreshed -= OnRefreshed;
        AppI.UpdateFound -= BuildNotices;
        AppI.LightingStateChanged -= OnRefreshed;
        _live.Detach();
    }

    /// <summary>One tile per reading this PC has: the liquid tile only with an AIO or loop that reports its coolant.</summary>
    void LayoutStats()
    {
        var tiles = new List<FrameworkElement> { CpuTile, GpuTile };
        if (Live.LiquidSensor(AppI.Hub.Temps) is not null) tiles.Add(LiquidTile); else LiquidTile.Visibility = Visibility.Collapsed;
        tiles.Add(AirTile);
        Stats.ColumnDefinitions.Clear();
        for (int i = 0; i < tiles.Count; i++)
        {
            Stats.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(tiles[i], i);
        }
    }

    void OnRefreshed()
    {
        var app = AppI;
        var hub = app.Hub;
        Summary.Text = app.Engine.Paused
            ? "FanControl is running, so RigControl is only watching. Exit FanControl to take over."
            : $"{hub.Temps.Count} sensors · {_live.Fans.Count} fan channels · updated every {app.Config.IntervalSeconds:0.#} s";

        Stat(Live.CpuSensor(hub.Temps), CpuValue, CpuName, CpuLine, "cpu");
        Stat(Live.GpuSensor(hub.Temps), GpuValue, GpuName, GpuLine, "gpu");
        Stat(Live.LiquidSensor(hub.Temps), LiquidValue, LiquidName, LiquidLine, "liquid");
        var air = Live.Airflow(hub.Fans);
        AirValue.Text = air is double a ? $"{a:0}%" : "–";
        AirText.Text = $"{_live.Fans.Count(f => f.Present)} fans · {_live.Fans.Count(f => f.Active)} driven";
        AirLine.SetValues(app.History.Of("air"));

        UpdateCurveRows();
        UpdateLighting();
        UpdateDevices();

        var stalled = app.Engine.Status.Where(s => s.Value.Stalled).Select(s => s.Key).ToHashSet();
        if (!stalled.SetEquals(_stalled)) { _stalled = stalled; BuildNotices(); }
    }

    void Stat(TempSensor? sensor, TextBlock value, TextBlock name, Sparkline line, string key)
    {
        value.Text = sensor?.Value is float v ? $"{v:0}°" : "–";
        name.Text = sensor is null ? "no sensor found" : $"{AppI.SensorLabel(sensor)} · {sensor.Device}";
        line.SetValues(AppI.History.Of(key));
    }

    // ---- cooling

    /// <summary>A row per curve: its sensor and temperature, how many fans follow it, and its output now.</summary>
    void BuildCurveRows()
    {
        CurveRows.Children.Clear();
        _curveRows.Clear();
        foreach (var def in AppI.Config.Curves)
        {
            var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(6, 6, 6, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            grid.Children.Add(new FontIcon { Glyph = new CurveGroup(def).Glyph, FontSize = 16, Foreground = (Brush)Application.Current.Resources["AccentGradient"] });
            var detail = new TextBlock { Style = (Style)Application.Current.Resources["Caption"], TextTrimming = TextTrimming.CharacterEllipsis };
            var names = new StackPanel { Children = { new TextBlock { Text = def.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis }, detail } };
            Grid.SetColumn(names, 1);
            grid.Children.Add(names);
            var bar = new ScaleTransform();
            var track = new Grid { Height = 4, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, 255, 255, 255)) };
            track.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = (Brush)Application.Current.Resources["AccentGradient"], RenderTransform = bar });
            Grid.SetColumn(track, 2);
            grid.Children.Add(track);
            var value = new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(value, 3);
            grid.Children.Add(value);
            CurveRows.Children.Add(Row(grid, def.Name, () => AppI.OpenPage("Cooling", "curve:" + def.Name)));
            _curveRows.Add((def, detail, value, bar));
        }
    }

    void UpdateCurveRows()
    {
        var app = AppI;
        var fans = app.Config.Fans;
        foreach (var (def, detail, value, bar) in _curveRows)
        {
            int n = fans.Values.Count(f => f is { Enabled: true, Manual: null } && f.Curve == def.Name);
            var input = def.Kind switch
            {
                CurveKind.Mix => $"{def.Mix} of {def.Inputs.Count}",
                CurveKind.Flat => "Fixed",
                _ => (app.Hub.Temps.FirstOrDefault(t => t.Id == def.Source) is { } t ? app.SensorLabel(t) : "No sensor")
                     + (app.Engine.CurveInputs.TryGetValue(def.Name, out var i) ? $" {i:0}°" : ""),
            };
            detail.Text = $"{input} · {(n == 0 ? "no fans" : n == 1 ? "1 fan" : $"{n} fans")}";
            var output = app.Engine.CurveOutputs.TryGetValue(def.Name, out var o) ? o : (double?)null;
            value.Text = output is double v ? $"{v:0}%" : "–";
            bar.ScaleX = Math.Clamp((output ?? 0) / 100, 0, 1);
        }

        // Fans on no curve, by name, so nothing is out of sight.
        var loose = _live.Fans
            .Where(f => f.Present || fans.TryGetValue(f.Id, out var s) && s.Enabled)
            .Where(f => !(fans.TryGetValue(f.Id, out var s) && s is { Enabled: true, Manual: null, Curve: not null }))
            .Select(f => fans.TryGetValue(f.Id, out var s) && s is { Enabled: true, Manual: double m } ? $"{f.Name} (fixed {m:0}%)" : f.Name)
            .ToList();
        CoolingNote.Text = _curveRows.Count == 0 ? "No curves yet. Open Cooling to make one, or import your FanControl setup."
            : loose.Count == 0 ? "" : $"Not on a curve: {string.Join(", ", loose)}";
        CoolingNote.Visibility = CoolingNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- lighting

    void UpdateLighting()
    {
        var app = AppI;
        var cfg = app.Config.Lighting;
        _loading = true;
        LightingSwitch.IsOn = cfg.Enabled;
        _loading = false;
        var targets = app.Lighting.Targets;
        LightingText.Text = app.LightingNotice is { } notice ? notice
            : !cfg.Enabled ? "Off"
            : targets.Count == 0 ? "No lighting devices found."
            : $"{EffectEditor.LabelOf(cfg.Current.Sync.Kind)} · {targets.Count} device{(targets.Count == 1 ? "" : "s")}"
              + (cfg.Profiles.Count > 1 ? $" · profile \"{cfg.Current.Name}\"" : "");

        // A swatch per device showing its LEDs now (a few of them, left to right), so a rainbow looks like one.
        var shown = targets.Take(16).ToList();
        if (Swatches.Children.Count != shown.Count)
        {
            Swatches.Children.Clear();
            Swatches.ColumnDefinitions.Clear();
            for (int i = 0; i < shown.Count; i++)
            {
                Swatches.ColumnDefinitions.Add(new ColumnDefinition());
                var b = new Border { CornerRadius = new CornerRadius(6) };
                Grid.SetColumn(b, i);
                Swatches.Children.Add(b);
            }
        }
        var frames = app.Lighting.Frames;
        var off = Windows.UI.Color.FromArgb(0x26, 255, 255, 255);
        for (int i = 0; i < shown.Count; i++)
        {
            var leds = cfg.Enabled && frames.TryGetValue(shown[i].Key, out var f) && f.Length > 0 ? f : null;
            // A new brush each time: changing a gradient's stops in place doesn't redraw it reliably.
            var brush = new LinearGradientBrush { StartPoint = new(0, 0.5), EndPoint = new(1, 0.5) };
            for (int k = 0; k < SwatchStops; k++)
            {
                var c = leds?[k * (leds.Length - 1) / (SwatchStops - 1)];
                brush.GradientStops.Add(new GradientStop { Offset = k / (double)(SwatchStops - 1), Color = c is { } led ? Windows.UI.Color.FromArgb(255, led.R, led.G, led.B) : off });
            }
            ((Border)Swatches.Children[i]).Background = brush;
            ToolTipService.SetToolTip(Swatches.Children[i], shown[i].Name);
        }
    }

    const int SwatchStops = 8;

    void LightingSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppI.Config.Lighting.Enabled = LightingSwitch.IsOn;
        AppI.ConfigChanged();
        UpdateLighting();
    }

    // ---- devices

    void UpdateDevices()
    {
        var devices = AppI.Peripherals.Devices;
        var states = AppI.Peripherals.States;
        if (!devices.Select(d => d.Key).SequenceEqual(_deviceRows.Select(r => r.key)))
        {
            DeviceRows.Children.Clear();
            _deviceRows.Clear();
            foreach (var d in devices)
            {
                var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(6) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.Children.Add(new FontIcon { Glyph = PeripheralCard.GlyphFor(d.Kind), FontSize = 16 });
                var name = new TextBlock { Text = d.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(name, 1);
                grid.Children.Add(name);
                var glyph = new FontIcon { FontSize = 14 };
                var battery = new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { glyph, battery } };
                Grid.SetColumn(right, 2);
                grid.Children.Add(right);
                DeviceRows.Children.Add(Row(grid, d.Name, () => AppI.OpenPage("Devices", d.Key)));
                _deviceRows.Add((d.Key, battery, glyph));
            }
            if (devices.Count == 0)
                DeviceRows.Children.Add(new TextBlock { Text = "No mice, keyboards or headsets found.", Style = (Style)Application.Current.Resources["Caption"], Margin = new Thickness(2, 0, 0, 4) });
        }
        foreach (var (key, battery, glyph) in _deviceRows)
        {
            var s = states.GetValueOrDefault(key);
            bool hasBattery = s?.BatteryPercent is not null;
            battery.Text = hasBattery ? $"{s!.BatteryPercent}%" : "";
            glyph.Glyph = hasBattery ? PeripheralCard.BatteryGlyphFor(s!) : "";
            glyph.Visibility = hasBattery ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ---- rows and links

    /// <summary>A clickable row (a real button: Tab, Enter, screen readers) with a soft highlight on hover.</summary>
    static Button Row(Grid content, string name, Action open)
    {
        content.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        content.CornerRadius = new CornerRadius(6);
        var b = new Button { Style = (Style)Application.Current.Resources["TileButton"], Content = content };
        AutomationProperties.SetName(b, name);
        b.PointerEntered += (_, _) => content.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x10, 255, 255, 255));
        b.PointerExited += (_, _) => content.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        b.Click += (_, _) => open();
        return b;
    }

    void OpenCooling_Click(object sender, RoutedEventArgs e) => AppI.OpenPage("Cooling", null);
    void OpenLighting_Click(object sender, RoutedEventArgs e) => AppI.OpenPage("Lighting", null);
    void OpenDevices_Click(object sender, RoutedEventArgs e) => AppI.OpenPage("Devices", null);

    // ---- notices

    void BuildNotices()
    {
        Notices.Children.Clear();
        var app = AppI;

        // A fan that stopped comes first: it's the one thing here that can't wait.
        foreach (var id in _stalled)
        {
            if (_live.Fans.FirstOrDefault(f => f.Id == id) is not { } fan) continue;
            var target = app.Engine.Status.TryGetValue(id, out var st) ? st.Target : null;
            var bar = new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Error,
                Title = $"{fan.Name} isn't spinning",
                Message = $"It reads 0 rpm at {target:0}%. Check the fan and its cable.",
            };
            var open = new Button { Content = "Open" };
            open.Click += (_, _) => app.OpenPage("Cooling", id);
            bar.ActionButton = open;
            Notices.Children.Add(bar);
        }

        foreach (var note in app.StartupNotes.Concat(app.Hub.Problems))
            Notices.Children.Add(new InfoBar { IsOpen = true, Severity = InfoBarSeverity.Warning, Message = note });

        if (app.Update is { } update)
        {
            var bar = new InfoBar { IsOpen = true, Severity = InfoBarSeverity.Success, Title = $"RigControl {update.Version} is available", Message = $"You have {Updates.Current}." };
            var button = new Button { Content = update.SetupUrl is null ? "Open release page" : "Update", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            button.Click += (_, _) => _ = SettingsPage.InstallUpdateAsync(XamlRoot);
            bar.ActionButton = button;
            Notices.Children.Add(bar);
        }

        if (app.Config.Curves.Count == 0 && !app.Config.Fans.Values.Any(f => f.Enabled) && File.Exists(FanControlImport.DefaultPath))
        {
            var bar = new InfoBar
            {
                IsOpen = true,
                Severity = InfoBarSeverity.Informational,
                Title = "Coming from FanControl?",
                Message = "Bring over your curves, fan names and settings in one go.",
            };
            var button = new Button { Content = "Import FanControl setup", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            button.Click += async (_, _) =>
            {
                if (await SettingsPage.ImportFanControl(XamlRoot)) { bar.IsOpen = false; _live.Refresh(); BuildCurveRows(); }
            };
            bar.ActionButton = button;
            Notices.Children.Add(bar);
        }
    }
}

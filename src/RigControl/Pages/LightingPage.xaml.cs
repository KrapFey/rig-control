using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using RigControl.Core;
using Windows.UI;

namespace RigControl.Pages;

public sealed record EffectChoice(EffectKind Kind, string Label, string Glyph)
{
    public override string ToString() => Label; // what screen readers announce
}

/// <summary>
/// One lighting device in the current profile: what it shows, its live LED preview, and (on its own page) every setting:
/// built-in effect settings, layout, and zones down to single LEDs.
/// </summary>
public sealed class DeviceCard : Observable
{
    readonly IRgbTarget _target;
    readonly List<(LightingSource source, string? mode)> _choices = [];

    public DeviceCard(IRgbTarget target, bool showTag = false)
    {
        _target = target;
        Name = showTag && target.Tag.Length > 0 ? $"{target.Name} · {target.Tag}" : target.Name;
        _choices.Add((LightingSource.Sync, null));
        _choices.Add((LightingSource.Effect, null));
        foreach (var m in target.Modes.Where(m => !m.Name.Equals("Direct", StringComparison.OrdinalIgnoreCase)))
            _choices.Add((LightingSource.Hardware, m.Name));
        _choices.Add((LightingSource.Off, null));
        Options = [.. _choices.Select(c => c.source switch
        {
            LightingSource.Sync => "Synced effect",
            LightingSource.Effect => "Own effect",
            LightingSource.Off => "Off",
            _ => $"Built-in effect: {c.mode}",
        })];
        Preview = new LinearGradientBrush { StartPoint = new(0, 0.5), EndPoint = new(1, 0.5) };
    }

    public string Key => _target.Key;
    public string Name { get; }
    public string Detail => $"{Label(_target.Type)} · {_target.LedCount} LED{(_target.LedCount == 1 ? "" : "s")}{LayoutNote}";
    bool HasFans => _target.Zones.Any(z => z.Segments.Count > 1);
    /// <summary>Says so on the card when the effect repeats on each fan or zone.</summary>
    string LayoutNote => Settings.Layout switch
    {
        EffectLayout.Fan when HasFans => " · per fan",
        EffectLayout.Auto when HasFans => " · per fan",
        EffectLayout.Zone => " · per zone",
        _ => "",
    };
    public string Glyph => _target.Type switch
    {
        RgbDeviceType.Keyboard => "",
        RgbDeviceType.Mouse => "",
        RgbDeviceType.Microphone => "",
        RgbDeviceType.Headset => "",
        RgbDeviceType.Gpu => "",
        RgbDeviceType.Dram => "",
        RgbDeviceType.Motherboard => "",
        RgbDeviceType.Cooler => "",
        _ => "",
    };
    public List<string> Options { get; }
    public LinearGradientBrush Preview { get; }

    static string Label(RgbDeviceType t) => t switch
    {
        RgbDeviceType.Dram => "Memory",
        RgbDeviceType.Gpu => "Graphics card",
        RgbDeviceType.LedStrip => "LED controller",
        RgbDeviceType.Mousemat => "Mousepad",
        RgbDeviceType.Motherboard => "Motherboard",
        _ => t.ToString(),
    };

    static LightingProfile Profile => App.Instance!.Config.Lighting.Current;
    DeviceLighting Settings => Profile.Devices.TryGetValue(Key, out var d) ? d : new DeviceLighting();

    DeviceLighting Edit()
    {
        if (!Profile.Devices.TryGetValue(Key, out var d)) Profile.Devices[Key] = d = new DeviceLighting();
        return d;
    }

    RgbMode? HardwareMode => Settings is { Source: LightingSource.Hardware, Mode: string n } ? _target.Modes.FirstOrDefault(m => m.Name == n) : null;

    public int Selected
    {
        get
        {
            var s = Settings;
            var i = _choices.FindIndex(c => c.source == s.Source && (s.Source != LightingSource.Hardware || c.mode == s.Mode));
            return i < 0 ? 0 : i;
        }
        set
        {
            if (value < 0 || value >= _choices.Count || value == Selected) return;
            var (source, mode) = _choices[value];
            var d = Edit();
            d.Source = source;
            d.Mode = mode;
            if (source == LightingSource.Effect && d.Effect is null)
                d.Effect = System.Text.Json.JsonSerializer.Deserialize<EffectSettings>(System.Text.Json.JsonSerializer.Serialize(Profile.Sync)); // start from the synced one
            if (mode is not null && _target.Modes.FirstOrDefault(m => m.Name == mode) is { } m)
            {
                d.Colors = [.. m.Colors.Select(c => c.ToString())];
                d.Speed = d.Brightness = d.Direction = null;
            }
            Changed();
            Raise(string.Empty);
            SourceChanged?.Invoke();
        }
    }

    /// <summary>One line for the card: what the device shows, and whether any zone does something else.</summary>
    public string Shows
    {
        get
        {
            var s = Settings;
            var what = s.Source switch
            {
                LightingSource.Effect => $"Own effect · {EffectEditor.LabelOf(s.Effect?.Kind ?? EffectKind.Static)}",
                LightingSource.Hardware => $"Built-in effect · {s.Mode}",
                LightingSource.Off => "Off",
                _ => "Synced effect",
            };
            int custom = s.Source is LightingSource.Sync or LightingSource.Effect ? CustomZones : 0;
            return custom == 0 ? what : $"{what} · {custom} zone{(custom == 1 ? "" : "s")} changed";
        }
    }

    /// <summary>This device's own effect (made from the synced one the first time it's chosen).</summary>
    public EffectSettings? OwnEffect => Settings.Source == LightingSource.Effect ? Settings.Effect : null;
    public Visibility OwnEffectVisibility => OwnEffect is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>A card switched between the synced effect, a device effect and off (the page recounts who follows the effect).</summary>
    public static event Action? SourceChanged;

    public Visibility HardwareVisibility => HardwareMode is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ColorVisibility => HardwareMode?.Flags.HasFlag(ModeFlags.HasModeSpecificColor) == true ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SpeedVisibility => HardwareMode?.Flags.HasFlag(ModeFlags.HasSpeed) == true ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BrightnessVisibility => HardwareMode?.Flags.HasFlag(ModeFlags.HasBrightness) == true ? Visibility.Visible : Visibility.Collapsed;

    Rgb CurrentColor => Settings.Colors.Count > 0 ? Rgb.Parse(Settings.Colors[0]) : HardwareMode?.Colors.FirstOrDefault() ?? new(255, 255, 255);
    public SolidColorBrush ColorBrush => new(Color.FromArgb(255, CurrentColor.R, CurrentColor.G, CurrentColor.B));
    public string ColorText => CurrentColor.ToString();

    // Device ranges vary (and some run backwards); the sliders are always 0..100.
    public double Speed
    {
        get => HardwareMode is { } m ? ToPercent(Settings.Speed ?? m.Speed, m.SpeedMin, m.SpeedMax) : 0;
        set { if (HardwareMode is { } m) { Edit().Speed = FromPercent(value, m.SpeedMin, m.SpeedMax); Changed(); } }
    }

    public double Brightness
    {
        get => HardwareMode is { } m ? ToPercent(Settings.Brightness ?? m.Brightness, m.BrightnessMin, m.BrightnessMax) : 0;
        set { if (HardwareMode is { } m) { Edit().Brightness = FromPercent(value, m.BrightnessMin, m.BrightnessMax); Changed(); } }
    }

    static double ToPercent(uint v, uint min, uint max) => min == max ? 100 : Math.Clamp((v - (double)min) / ((double)max - min) * 100, 0, 100);
    static uint FromPercent(double p, uint min, uint max) => (uint)Math.Round(min + ((double)max - min) * p / 100);

    public void PickColor(object sender, RoutedEventArgs e) =>
        EffectEditor.ShowColorPicker((FrameworkElement)sender, CurrentColor, c =>
        {
            Edit().Colors = [c.ToString()];
            Changed();
            Raise(nameof(ColorBrush));
            Raise(nameof(ColorText));
        });

    public void Identify() => App.Instance!.Lighting.Identify(Key);

    // ---------------- Layout, zones and single LEDs (devices showing an effect)
    public Visibility ZonesVisibility => (_target.LedCount > 1 || _target.Zones.Any(z => z.Resizable)) && Settings.Source is LightingSource.Sync or LightingSource.Effect ? Visibility.Visible : Visibility.Collapsed;
    int CustomZones => _target.Zones.Count(z => Settings.Zones.TryGetValue(z.Name, out var zl) && zl.Source != ZoneSource.Sync);

    static readonly (string label, ZoneSource source)[] ZoneOptions =
        [("Effect", ZoneSource.Sync), ("Colour", ZoneSource.Color), ("Each LED", ZoneSource.Leds), ("Off", ZoneSource.Off)];

    static string ZoneName(RgbZone z) => string.IsNullOrWhiteSpace(z.DisplayName) ? z.Name : z.DisplayName;

    void ZonesChanged()
    {
        Changed();
        Raise(nameof(Shows));
    }

    /// <summary>Shows the layout and the zone list in <paramref name="host"/> (painting single LEDs opens in it too).</summary>
    public void ShowZones(ContentControl host) => host.Content = ZoneList(host);

    FrameworkElement ZoneList(ContentControl host)
    {
        var panel = new StackPanel { Spacing = 12, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
        // How the effect is laid out: once across everything, or starting over per zone or per fan.
        bool fans = _target.Zones.Any(z => z.Segments.Count > 1);
        var layouts = new List<(string label, EffectLayout layout)> { ("Automatic" + (fans ? " (each fan)" : " (whole device)"), EffectLayout.Auto), ("Across the whole device", EffectLayout.Device) };
        if (_target.Zones.Count > 1) layouts.Add(("Each zone separately", EffectLayout.Zone));
        if (fans) layouts.Add(("Each fan separately", EffectLayout.Fan));
        if (layouts.Count > 2)
        {
            var layoutBox = new ComboBox { Header = "Effect layout", ItemsSource = layouts.Select(l => l.label).ToList(), MinWidth = 300,
                SelectedIndex = Math.Max(0, layouts.FindIndex(l => l.layout == Settings.Layout)) };
            ToolTipService.SetToolTip(layoutBox, "Whether a wave or gradient runs once over the whole device, or plays on each zone or fan by itself");
            layoutBox.SelectionChanged += (_, _) => { if (layoutBox.SelectedIndex >= 0) { Edit().Layout = layouts[layoutBox.SelectedIndex].layout; Changed(); Raise(nameof(Detail)); } };
            panel.Children.Add(layoutBox);
        }
        panel.Children.Add(new TextBlock { Text = "Each zone can follow the effect, show one colour, get a colour per LED, or stay off."
            + (_target.Zones.Any(z => z.Resizable) ? " For addressable (ARGB) headers, set how many LEDs are connected." : ""), TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 });
        foreach (var zone in _target.Zones)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            if (zone.Resizable)
            {
                // An addressable header: the board can't tell how long the strip or fan chain is, so you say.
                var cell = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                cell.Children.Add(new TextBlock { Text = ZoneName(zone), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 });
                var leds = new NumberBox { Value = zone.LedsCount, Minimum = zone.LedsMin, Maximum = zone.LedsMax, SmallChange = 1, LargeChange = 10, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 110 };
                ToolTipService.SetToolTip(leds, $"How many LEDs are connected to this header ({zone.LedsMin}–{zone.LedsMax})");
                leds.ValueChanged += (_, ev) =>
                {
                    if (double.IsNaN(ev.NewValue) || (int)ev.NewValue == zone.LedsCount) return;
                    // the device is rebuilt with its new LED count, and its page with it
                    App.Instance!.SetZoneSize(Key, zone.Name, (int)Math.Clamp(ev.NewValue, zone.LedsMin, zone.LedsMax));
                };
                cell.Children.Add(leds);
                cell.Children.Add(new TextBlock { Text = "LEDs", Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(cell);
            }
            else
                row.Children.Add(new TextBlock { Text = $"{ZoneName(zone)} · {(zone.Segments.Count > 1 ? $"{zone.Segments.Count} fans, " : "")}{zone.LedsCount} LED{(zone.LedsCount == 1 ? "" : "s")}", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var current = Settings.Zones.GetValueOrDefault(zone.Name)?.Source ?? ZoneSource.Sync;
            var box = new ComboBox { ItemsSource = ZoneOptions.Select(o => o.label).ToList(), SelectedIndex = Array.FindIndex(ZoneOptions, o => o.source == current), MinWidth = 150 };
            Grid.SetColumn(box, 1);
            var swatch = new Button { Width = 36, Height = 30, Padding = new Thickness(0), CornerRadius = new CornerRadius(8) };
            var edit = new Button { Width = 36, Height = 30, Padding = new Thickness(0), Content = new FontIcon { Glyph = "\uE70F", FontSize = 14 } };
            ToolTipService.SetToolTip(edit, "Colour each LED");
            Grid.SetColumn(swatch, 2);
            Grid.SetColumn(edit, 2);
            void Show()
            {
                var zl = Settings.Zones.GetValueOrDefault(zone.Name);
                swatch.Visibility = zl?.Source == ZoneSource.Color ? Visibility.Visible : Visibility.Collapsed;
                edit.Visibility = zl?.Source == ZoneSource.Leds ? Visibility.Visible : Visibility.Collapsed;
                var c = Rgb.Parse(zl?.Color ?? "#FFFFFF");
                swatch.Background = new SolidColorBrush(Color.FromArgb(255, c.R, c.G, c.B));
            }
            box.SelectionChanged += (_, _) =>
            {
                if (box.SelectedIndex < 0) return;
                var d = Edit();
                var source = ZoneOptions[box.SelectedIndex].source;
                var old = d.Zones.GetValueOrDefault(zone.Name);
                if (source == ZoneSource.Sync) d.Zones.Remove(zone.Name);
                else d.Zones[zone.Name] = new ZoneLighting { Source = source, Color = old?.Color ?? "#FFFFFF", Leds = old?.Leds ?? [] };
                Show();
                ZonesChanged();
                if (source == ZoneSource.Leds) host.Content = LedEditor(host, zone); // straight to painting
            };
            swatch.Click += (_, _) => EffectEditor.ShowColorPicker(swatch, Rgb.Parse(Settings.Zones.GetValueOrDefault(zone.Name)?.Color ?? "#FFFFFF"), c =>
            {
                if (Edit().Zones.GetValueOrDefault(zone.Name) is { } zl) zl.Color = c.ToString();
                Show();
                Changed();
            });
            edit.Click += (_, _) => host.Content = LedEditor(host, zone);
            Show();
            row.Children.Add(box);
            row.Children.Add(swatch);
            row.Children.Add(edit);
            panel.Children.Add(row);
        }
        return panel;
    }

    /// <summary>One dot per LED: pick a brush colour, then click or drag across the dots to paint them.</summary>
    FrameworkElement LedEditor(ContentControl host, RgbZone zone)
    {
        var zl = Edit().Zones.GetValueOrDefault(zone.Name) ?? new ZoneLighting { Source = ZoneSource.Leds };
        Edit().Zones[zone.Name] = zl;
        int count = (int)zone.LedsCount;
        while (zl.Leds.Count < count) zl.Leds.Add(zl.Color);
        var brush = Rgb.Parse(zl.Color);

        var panel = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Left };
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        var back = new Button { Content = new FontIcon { Glyph = "\uE72B", FontSize = 12 }, Padding = new Thickness(8, 6, 8, 6) };
        ToolTipService.SetToolTip(back, "Back to the zones");
        back.Click += (_, _) => host.Content = ZoneList(host);
        var title = new TextBlock { Text = $"{(_target.Zones.Count > 1 ? ZoneName(zone) : _target.Name)} · {count} LEDs", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 1);
        header.Children.Add(back);
        header.Children.Add(title);
        panel.Children.Add(header);
        panel.Children.Add(new TextBlock { Text = "Pick a colour, then click or drag over the LEDs.", Opacity = 0.7, FontSize = 12 });

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var brushButton = new Button { Width = 44, Height = 30, Padding = new Thickness(0), CornerRadius = new CornerRadius(8) };
        ToolTipService.SetToolTip(brushButton, "Brush colour");
        var fill = new Button { Content = "Fill all" };
        tools.Children.Add(brushButton);
        tools.Children.Add(fill);
        panel.Children.Add(tools);

        static SolidColorBrush Solid(Rgb c) => new(Color.FromArgb(255, c.R, c.G, c.B));
        void ShowBrush() => brushButton.Background = Solid(brush);
        ShowBrush();
        brushButton.Click += (_, _) => EffectEditor.ShowColorPicker(brushButton, brush, c => { brush = c; zl.Color = c.ToString(); ShowBrush(); Changed(); });

        int columns = Math.Min(16, count);
        var grid = new Grid { RowSpacing = 4, ColumnSpacing = 4 };
        for (int c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        for (int r = 0; r < (count + columns - 1) / columns; r++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
        var dots = new Border[count];
        void Paint(int i)
        {
            zl.Leds[i] = brush.ToString();
            dots[i].Background = Solid(brush);
            Changed();
        }
        for (int i = 0; i < count; i++)
        {
            int index = i;
            var dot = new Border { CornerRadius = new CornerRadius(11), BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)), BorderThickness = new Thickness(1), Background = Solid(Rgb.Parse(zl.Leds[i])) };
            ToolTipService.SetToolTip(dot, $"LED {i + 1}");
            dot.PointerPressed += (s, ev) => { Paint(index); ev.Handled = true; };
            dot.PointerEntered += (s, ev) => { if (ev.GetCurrentPoint(dot).Properties.IsLeftButtonPressed) Paint(index); };
            Grid.SetRow(dot, i / columns);
            Grid.SetColumn(dot, i % columns);
            dots[i] = dot;
            grid.Children.Add(dot);
        }
        fill.Click += (_, _) => { for (int i = 0; i < count; i++) Paint(i); };
        panel.Children.Add(new ScrollViewer { Content = grid, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        ZonesChanged();
        return panel;
    }

    static void Changed() => App.Instance!.ConfigChanged();

    /// <summary>Refreshes the strip from the engine's latest frame.</summary>
    Rgb[]? _shown;

    public void Render(Rgb[]? frame)
    {
        if (frame is null || frame.Length == 0 || ReferenceEquals(frame, _shown)) return; // the engine makes a new array per frame
        _shown = frame;
        int stops = Math.Min(frame.Length, 24);
        var g = Preview.GradientStops;
        while (g.Count < stops) g.Add(new GradientStop());
        while (g.Count > stops) g.RemoveAt(g.Count - 1);
        for (int i = 0; i < stops; i++)
        {
            var c = frame[stops == 1 ? 0 : i * (frame.Length - 1) / (stops - 1)];
            g[i].Offset = stops == 1 ? 0 : i / (double)(stops - 1);
            g[i].Color = Color.FromArgb(255, c.R, c.G, c.B);
        }
    }
}

public sealed record SensorOption(string Id, string Label);

public sealed partial class LightingPage : Page
{
    readonly List<DeviceCard> _cards = [];
    bool _loading;

    static App AppI => App.Instance!;
    static LightingConfig Cfg => AppI.Config.Lighting;

    public LightingPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            AppI.LightingStateChanged += Rebuild;
            DeviceCard.SourceChanged += UpdateSyncCount;
            Rebuild();
            CompositionTarget.Rendering += RenderFrame; // device strips, once per screen refresh
        };
        Unloaded += (_, _) =>
        {
            AppI.LightingStateChanged -= Rebuild;
            DeviceCard.SourceChanged -= UpdateSyncCount;
            CompositionTarget.Rendering -= RenderFrame;
        };
    }

    void Rebuild()
    {
        _loading = true;
        ProfileBox.ItemsSource = Cfg.Profiles.Select(p => p.Name).ToList();
        ProfileBox.SelectedItem = Cfg.Current.Name;
        DeleteProfileItem.IsEnabled = Cfg.Profiles.Count > 1;
        EnabledSwitch.IsOn = Cfg.Enabled;

        Notice.IsOpen = AppI.LightingNotice is not null || (AppI.Rgb is null && AppI.Lighting.Targets.Count == 0);
        Notice.Message = AppI.LightingNotice ?? "Finding your RGB devices…";
        Notice.Severity = AppI.LightingNotice is null ? InfoBarSeverity.Informational : InfoBarSeverity.Warning;

        _cards.Clear();
        _cards.AddRange(Cards(AppI.Lighting.Targets));
        DeviceGrid.ItemsSource = null;
        DeviceGrid.ItemsSource = _cards;
        Summary.Text = _cards.Count == 0 ? "No RGB devices yet." : $"{_cards.Count} devices · profile \"{Cfg.Current.Name}\"";
        Editor.Effect = Cfg.Current.Sync;
        UpdateSyncCount();
        _loading = false;
    }

    /// <summary>A card per device; identical names get their tag so they can be told apart.</summary>
    public static IEnumerable<DeviceCard> Cards(IReadOnlyList<IRgbTarget> targets)
    {
        var dupes = targets.GroupBy(t => t.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        return targets.Select(t => new DeviceCard(t, dupes.Contains(t.Name)));
    }

    void UpdateSyncCount() => SyncCount.Text = $"{_cards.Count(c => c.Selected == 0)} of {_cards.Count} devices follow it";

    void RenderFrame(object? sender, object e)
    {
        var frames = AppI.Lighting.Frames;
        foreach (var card in _cards) card.Render(frames.GetValueOrDefault(card.Key));
    }

    void Card_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        // A tap on the card's Identify button is the button's, not "open the page".
        for (var el = e.OriginalSource as DependencyObject; el is not null && el != sender; el = VisualTreeHelper.GetParent(el))
            if (el is ButtonBase) return;
        if (((FrameworkElement)sender).DataContext is DeviceCard card)
            Frame.Navigate(typeof(LightingDevicePage), card.Key, new Microsoft.UI.Xaml.Media.Animation.DrillInNavigationTransitionInfo());
    }

    void Identify_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is DeviceCard card) card.Identify();
    }

    /// <summary>Looks for devices again; the button stays busy until detection has settled.</summary>
    async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        RescanButton.IsEnabled = false;
        RescanText.Text = "Scanning…";
        await App.Instance!.RescanLightingAsync(); // the cards rebuild as devices are found
        RescanButton.IsEnabled = true;
        RescanText.Text = "Rescan";
    }

    void ProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfileBox.SelectedItem is not string name || name == Cfg.Active) return;
        Cfg.Active = name;
        AppI.ConfigChanged();
        Rebuild();
    }

    void EnabledSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Cfg.Enabled = EnabledSwitch.IsOn;
        AppI.ConfigChanged();
    }

    async void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = await AskName("New profile", Unique($"{Cfg.Current.Name} copy"));
        if (name is null) return;
        var copy = System.Text.Json.JsonSerializer.Deserialize<LightingProfile>(System.Text.Json.JsonSerializer.Serialize(Cfg.Current))!;
        copy.Name = Unique(name);
        Cfg.Profiles.Add(copy);
        Cfg.Active = copy.Name;
        AppI.ConfigChanged();
        Rebuild();
    }

    async void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = await AskName("Rename profile", Cfg.Current.Name);
        if (name is null || name == Cfg.Current.Name) return;
        Cfg.Current.Name = Unique(name);
        Cfg.Active = Cfg.Current.Name;
        AppI.ConfigChanged();
        Rebuild();
    }

    void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (Cfg.Profiles.Count < 2) return;
        Cfg.Profiles.Remove(Cfg.Current);
        Cfg.Active = Cfg.Profiles[0].Name;
        AppI.ConfigChanged();
        Rebuild();
    }

    async Task<string?> AskName(string title, string initial)
    {
        var box = new TextBox { Text = initial, SelectionStart = 0, SelectionLength = initial.Length };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = title, Content = box,
            PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text) ? box.Text.Trim() : null;
    }

    static string Unique(string name)
    {
        var taken = Cfg.Profiles.Select(p => p.Name).ToHashSet();
        if (!taken.Contains(name)) return name;
        for (int i = 2; ; i++) if (!taken.Contains($"{name} {i}")) return $"{name} {i}";
    }
}

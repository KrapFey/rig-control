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

/// <summary>A device card: what the device does in the current profile, plus its live LED preview.</summary>
public sealed class DeviceCard : Observable
{
    readonly IRgbTarget _target;
    readonly List<(LightingSource source, string? mode)> _choices = [];

    public DeviceCard(IRgbTarget target, bool showTag = false)
    {
        _target = target;
        Name = showTag && target.Tag.Length > 0 ? $"{target.Name} · {target.Tag}" : target.Name;
        _choices.Add((LightingSource.Sync, null));
        foreach (var m in target.Modes.Where(m => !m.Name.Equals("Direct", StringComparison.OrdinalIgnoreCase)))
            _choices.Add((LightingSource.Hardware, m.Name));
        _choices.Add((LightingSource.Off, null));
        Options = [.. _choices.Select(c => c.source switch
        {
            LightingSource.Sync => "Synced effect",
            LightingSource.Off => "Off",
            _ => $"Device effect: {c.mode}",
        })];
        Preview = new LinearGradientBrush { StartPoint = new(0, 0.5), EndPoint = new(1, 0.5) };
    }

    public string Key => _target.Key;
    public string Name { get; }
    public string Detail => $"{Label(_target.Type)} · {_target.LedCount} LED{(_target.LedCount == 1 ? "" : "s")}";
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
        LightingPage.ShowColorPicker((FrameworkElement)sender, CurrentColor, c =>
        {
            Edit().Colors = [c.ToString()];
            Changed();
            Raise(nameof(ColorBrush));
            Raise(nameof(ColorText));
        });

    public void Identify() => App.Instance!.Lighting.Identify(Key);

    // ---------------- Zones and single LEDs (devices following the synced effect)
    /// <summary>With the Zones button in the header too, Identify shows just its icon so the name keeps room.</summary>
    public Visibility IdentifyTextVisibility => ZonesVisibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ZonesVisibility => (_target.LedCount > 1 || _target.Zones.Any(z => z.Resizable)) && Settings.Source == LightingSource.Sync ? Visibility.Visible : Visibility.Collapsed;
    int CustomZones => _target.Zones.Count(z => Settings.Zones.TryGetValue(z.Name, out var zl) && zl.Source != ZoneSource.Sync);
    string ZonesWord => _target.Zones.Count > 1 ? "Zones" : "LEDs";
    public string ZonesLabel => CustomZones == 0 ? ZonesWord : $"{ZonesWord} · {CustomZones}";
    public string ZonesTip => _target.Zones.Count > 1
        ? (CustomZones == 0 ? $"{_target.Zones.Count} zones, all following the synced effect: give one its own colours, or turn it off"
                            : $"{CustomZones} of {_target.Zones.Count} zones have their own colours or are off")
        : "Colour single LEDs, give the whole device one colour, or turn it off";

    static readonly (string label, ZoneSource source)[] ZoneOptions =
        [("Synced effect", ZoneSource.Sync), ("Colour", ZoneSource.Color), ("Each LED", ZoneSource.Leds), ("Off", ZoneSource.Off)];

    static string ZoneName(RgbZone z) => string.IsNullOrWhiteSpace(z.DisplayName) ? z.Name : z.DisplayName;

    void ZonesChanged()
    {
        Changed();
        Raise(nameof(ZonesLabel));
        Raise(nameof(ZonesTip));
    }

    /// <summary>Opens a panel listing the zones: each follows the effect, shows one colour, a colour per LED, or is off.</summary>
    public void ShowZones(object sender, RoutedEventArgs e)
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.Bottom };
        flyout.Content = ZoneList(flyout);
        flyout.ShowAt((FrameworkElement)sender);
    }

    FrameworkElement ZoneList(Flyout flyout)
    {
        var panel = new StackPanel { Spacing = 10, Width = 420 };
        panel.Children.Add(new TextBlock { Text = "Each zone can follow the synced effect, show one colour, get a colour per LED, or stay off."
            + (_target.Zones.Any(z => z.Resizable) ? " For addressable (ARGB) headers, set how many LEDs are connected." : ""), TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 });
        foreach (var zone in _target.Zones)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
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
                    flyout.Hide(); // the device is rebuilt with its new LED count
                    App.Instance!.SetZoneSize(Key, zone.Name, (int)Math.Clamp(ev.NewValue, zone.LedsMin, zone.LedsMax));
                };
                cell.Children.Add(leds);
                cell.Children.Add(new TextBlock { Text = "LEDs", Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(cell);
            }
            else
                row.Children.Add(new TextBlock { Text = $"{ZoneName(zone)} · {zone.LedsCount} LED{(zone.LedsCount == 1 ? "" : "s")}", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
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
                if (source == ZoneSource.Leds) flyout.Content = LedEditor(flyout, zone); // straight to painting
            };
            swatch.Click += (_, _) => LightingPage.ShowColorPicker(swatch, Rgb.Parse(Settings.Zones.GetValueOrDefault(zone.Name)?.Color ?? "#FFFFFF"), c =>
            {
                if (Edit().Zones.GetValueOrDefault(zone.Name) is { } zl) zl.Color = c.ToString();
                Show();
                Changed();
            });
            edit.Click += (_, _) => flyout.Content = LedEditor(flyout, zone);
            Show();
            row.Children.Add(box);
            row.Children.Add(swatch);
            row.Children.Add(edit);
            panel.Children.Add(row);
        }
        return panel;
    }

    /// <summary>One dot per LED: pick a brush colour, then click or drag across the dots to paint them.</summary>
    FrameworkElement LedEditor(Flyout flyout, RgbZone zone)
    {
        var zl = Edit().Zones.GetValueOrDefault(zone.Name) ?? new ZoneLighting { Source = ZoneSource.Leds };
        Edit().Zones[zone.Name] = zl;
        int count = (int)zone.LedsCount;
        while (zl.Leds.Count < count) zl.Leds.Add(zl.Color);
        var brush = Rgb.Parse(zl.Color);

        var panel = new StackPanel { Spacing = 12, Width = 420 };
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        var back = new Button { Content = new FontIcon { Glyph = "\uE72B", FontSize = 12 }, Padding = new Thickness(8, 6, 8, 6) };
        ToolTipService.SetToolTip(back, "Back to the zones");
        back.Click += (_, _) => flyout.Content = ZoneList(flyout);
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
        brushButton.Click += (_, _) => LightingPage.ShowColorPicker(brushButton, brush, c => { brush = c; zl.Color = c.ToString(); ShowBrush(); Changed(); });

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
    public void Render(Rgb[]? frame)
    {
        if (frame is null || frame.Length == 0) return;
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
    static readonly EffectChoice[] Effects =
    [
        new(EffectKind.Rainbow, "Rainbow wave", ""),
        new(EffectKind.ColorCycle, "Colour cycle", ""),
        new(EffectKind.Breathing, "Breathing", ""),
        new(EffectKind.Static, "Static", ""),
        new(EffectKind.Temperature, "Temperature", ""),
        new(EffectKind.AudioLevel, "Audio level", "\uE767"),
        new(EffectKind.AudioSpectrum, "Spectrum", "\uE9D9"),
        new(EffectKind.AudioPulse, "Beat pulse", "\uE8D6"),
        new(EffectKind.Off, "Off", ""),
    ];

    readonly List<DeviceCard> _cards = [];
    readonly LinearGradientBrush _preview = new() { StartPoint = new(0, 0.5), EndPoint = new(1, 0.5) };
    DispatcherQueueTimer? _timer;
    bool _loading;

    static App AppI => App.Instance!;
    static LightingConfig Cfg => AppI.Config.Lighting;
    static EffectSettings Sync => Cfg.Current.Sync;

    public LightingPage()
    {
        InitializeComponent();
        PreviewBar.Background = _preview;
        PreviewGlow.Background = _preview;
        EffectList.ItemsSource = Effects;
        SensorBox.ItemsSource = AppI.Hub.Temps.OrderBy(t => t.Device).ThenBy(t => t.Name).Select(t => new SensorOption(t.Id, $"{AppI.SensorLabel(t)} · {t.Device}")).ToList();
        Loaded += (_, _) =>
        {
            AppI.LightingStateChanged += Rebuild;
            DeviceCard.SourceChanged += UpdateSyncCount;
            Rebuild();
            _timer = DispatcherQueue.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(50); // 20 fps is plenty for previews
            _timer.Tick += (_, _) => RenderFrame();
            _timer.Start();
        };
        Unloaded += (_, _) =>
        {
            AppI.LightingStateChanged -= Rebuild;
            DeviceCard.SourceChanged -= UpdateSyncCount;
            _timer?.Stop();
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
        var targets = AppI.Lighting.Targets;
        var dupes = targets.GroupBy(t => t.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        _cards.AddRange(targets.Select(t => new DeviceCard(t, dupes.Contains(t.Name))));
        DeviceGrid.ItemsSource = null;
        DeviceGrid.ItemsSource = _cards;
        Summary.Text = _cards.Count == 0 ? "No RGB devices yet." : $"{_cards.Count} devices · profile \"{Cfg.Current.Name}\"";
        LoadEffect();
        _loading = false;
    }

    void LoadEffect()
    {
        var s = Sync;
        EffectList.SelectedIndex = Array.FindIndex(Effects, e => e.Kind == s.Kind);
        SpeedSlider.Value = s.Speed;
        BrightnessSlider.Value = s.Brightness;
        bool temp = s.Kind == EffectKind.Temperature;
        TempPanel.Visibility = temp ? Visibility.Visible : Visibility.Collapsed;
        SpeedSlider.IsEnabled = s.Kind is EffectKind.Rainbow or EffectKind.ColorCycle or EffectKind.Breathing;
        ColorsPanel.Visibility = s.IsAudio || s.Kind is EffectKind.Static or EffectKind.Breathing or EffectKind.Temperature ? Visibility.Visible : Visibility.Collapsed;
        ColorsLabel.Text = temp ? "Cold → hot colours"
            : s.Kind == EffectKind.Static ? "Colours (more than one makes a gradient)"
            : s.Kind == EffectKind.AudioPulse ? "Colours (each beat moves to the next)"
            : s.Kind == EffectKind.AudioLevel ? "Colours, quiet to loud (one colour: green to red)"
            : s.Kind == EffectKind.AudioSpectrum ? "Colours, bass to treble (one colour: a rainbow)"
            : "Colours";
        AudioPanel.Visibility = s.IsAudio ? Visibility.Visible : Visibility.Collapsed;
        if (s.IsAudio) LoadAudio(s);
        if (temp)
        {
            var options = (List<SensorOption>)SensorBox.ItemsSource;
            s.Sensor ??= Live.Instance.Cpu?.Id;
            SensorBox.SelectedItem = options.FirstOrDefault(o => o.Id == s.Sensor);
            ColdBox.Value = s.ColdTemp;
            HotBox.Value = s.HotTemp;
        }
        BuildSwatches();
        UpdateSyncCount();
    }

    sealed record AudioDeviceOption(string? Id, string Label);

    void LoadAudio(EffectSettings s)
    {
        AudioSourceBox.SelectedIndex = s.AudioInput ? 1 : 0;
        var devices = new List<AudioDeviceOption> { new(null, s.AudioInput ? "Windows default microphone" : "Windows default output") };
        devices.AddRange(AudioListener.Devices(s.AudioInput).Select(d => new AudioDeviceOption(d.id, d.name)));
        AudioDeviceBox.ItemsSource = devices;
        AudioDeviceBox.SelectedItem = devices.FirstOrDefault(d => d.Id == s.AudioDevice) ?? devices[0];
        SensitivitySlider.Value = s.Sensitivity;
        AudioNote.Text = s.AudioInput
            ? "Only loudness and pitch are measured from the microphone; nothing is recorded, saved or sent. Windows shows its microphone-in-use icon while this effect is on."
            : "Follows whatever your PC plays. Only loudness and pitch are measured; nothing is recorded, saved or sent.";
    }

    void AudioSource_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AudioSourceBox.SelectedIndex < 0) return;
        Sync.AudioInput = AudioSourceBox.SelectedIndex == 1;
        Sync.AudioDevice = null; // the other kind's device doesn't apply
        AppI.ConfigChanged();
        _loading = true;
        LoadAudio(Sync);
        _loading = false;
    }

    void AudioDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AudioDeviceBox.SelectedItem is not AudioDeviceOption o) return;
        Sync.AudioDevice = o.Id;
        EffectChanged();
    }

    void Sensitivity_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        Sync.Sensitivity = e.NewValue;
        EffectChanged();
    }

    void UpdateSyncCount() => SyncCount.Text = $"{_cards.Count(c => c.Selected == 0)} of {_cards.Count} devices follow it";

    void BuildSwatches()
    {
        Swatches.Children.Clear();
        var s = Sync;
        for (int i = 0; i < s.Colors.Count; i++)
        {
            int index = i;
            var c = Rgb.Parse(s.Colors[i]);
            var b = new Button
            {
                Width = 40, Height = 32, Padding = new Thickness(0), CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromArgb(255, c.R, c.G, c.B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            };
            ToolTipService.SetToolTip(b, s.Colors.Count > 1 ? $"{c} · right-click to remove" : c.ToString());
            b.Click += (_, _) => ShowColorPicker(b, c, picked => { Sync.Colors[index] = picked.ToString(); EffectChanged(); BuildSwatches(); });
            b.RightTapped += (_, _) => { if (Sync.Colors.Count > 1) { Sync.Colors.RemoveAt(index); EffectChanged(); BuildSwatches(); } };
            Swatches.Children.Add(b);
        }
        if (s.Colors.Count < 6)
        {
            var add = new Button { Width = 32, Height = 32, Padding = new Thickness(0), CornerRadius = new CornerRadius(10), Content = new FontIcon { Glyph = "", FontSize = 12 } };
            ToolTipService.SetToolTip(add, "Add a colour");
            add.Click += (_, _) => { Sync.Colors.Add(Rgb.Hsv(Random.Shared.Next(360)).ToString()); EffectChanged(); BuildSwatches(); };
            Swatches.Children.Add(add);
        }
    }

    /// <summary>Colour picker in a flyout; calls back on every change so lights follow live.</summary>
    public static void ShowColorPicker(FrameworkElement anchor, Rgb start, Action<Rgb> changed)
    {
        var picker = new ColorPicker
        {
            Color = Color.FromArgb(255, start.R, start.G, start.B),
            IsAlphaEnabled = false,
            IsMoreButtonVisible = false,
            ColorSpectrumShape = ColorSpectrumShape.Ring,
        };
        picker.ColorChanged += (_, e) => changed(new Rgb(e.NewColor.R, e.NewColor.G, e.NewColor.B));
        new Flyout { Content = picker, Placement = FlyoutPlacementMode.Bottom }.ShowAt(anchor);
    }

    void EffectChanged()
    {
        if (_loading) return;
        AppI.ConfigChanged();
    }

    void RenderFrame()
    {
        // Big preview bar: the synced effect itself, on the engine's clock so it moves with the lights.
        var s = Sync;
        var x = new float[40];
        for (int i = 0; i < x.Length; i++) x[i] = i / (float)(x.Length - 1);
        var frame = new Rgb[x.Length];
        double? temp = AppI.Lighting.CurrentTemperature
            ?? (s.Sensor is string id ? AppI.Hub.Temps.FirstOrDefault(t => t.Id == id)?.Value : null);
        bool heat = s.Kind == EffectKind.Temperature;
        if (heat)
        {
            // Show the whole cold → hot scale, and mark where the reading is.
            var stops = s.Colors.Count >= 2 ? s.Colors.Select(Rgb.Parse).ToArray() : [new Rgb(0, 212, 255), new Rgb(255, 40, 70)];
            for (int i = 0; i < frame.Length; i++) frame[i] = EffectMath.Gradient(stops, x[i]).Scale(Math.Clamp(s.Brightness, 0, 100) / 100);
            UpdateMarker(s, temp, stops);
        }
        else EffectMath.Render(s, AppI.Lighting.Now, x, temp, frame, AppI.Lighting.CurrentAudio);
        MarkerLayer.Visibility = TempLabel.Visibility = heat ? Visibility.Visible : Visibility.Collapsed;
        var g = _preview.GradientStops;
        while (g.Count < frame.Length) g.Add(new GradientStop());
        for (int i = 0; i < frame.Length; i++)
        {
            g[i].Offset = x[i];
            g[i].Color = Color.FromArgb(255, frame[i].R, frame[i].G, frame[i].B);
        }

        // Device strips: what each device is showing right now.
        var frames = AppI.Lighting.Frames;
        foreach (var card in _cards) card.Render(frames.GetValueOrDefault(card.Key));
    }

    void UpdateMarker(EffectSettings s, double? temp, Rgb[] stops)
    {
        var sensor = AppI.Hub.Temps.FirstOrDefault(t => t.Id == s.Sensor);
        if (temp is not double tv)
        {
            TempText.Text = sensor is null ? "Pick a sensor" : $"{AppI.SensorLabel(sensor)} · no reading";
            Marker.Visibility = MarkerGlow.Visibility = Visibility.Collapsed;
            return;
        }
        double f = Math.Clamp((tv - s.ColdTemp) / Math.Max(1, s.HotTemp - s.ColdTemp), 0, 1);
        var c = EffectMath.Gradient(stops, f);
        var color = Color.FromArgb(255, c.R, c.G, c.B);
        double w = PreviewBar.ActualWidth, left = 6 + f * Math.Max(0, w - 34);
        Canvas.SetLeft(Marker, left);
        Canvas.SetLeft(MarkerGlow, left - 11);
        Marker.Fill = new SolidColorBrush(color);
        MarkerGlowStop.Color = color;
        Marker.Visibility = MarkerGlow.Visibility = Visibility.Visible;
        TempText.Text = $"{sensor?.Name ?? "Temperature"} · {tv:0} °C";
        TempText.Foreground = new SolidColorBrush(color);
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

    void EffectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || EffectList.SelectedItem is not EffectChoice c) return;
        Sync.Kind = c.Kind;
        if (c.Kind == EffectKind.Temperature && Sync.Colors.Count < 2) Sync.Colors = ["#00D4FF", "#7C5CFF", "#FF963C", "#FF2846"];
        // Audio effects start from colours that suit them (the shared default is purple → cyan).
        bool defaults = Sync.Colors.SequenceEqual(["#7C5CFF", "#00D4FF"], StringComparer.OrdinalIgnoreCase);
        if (c.Kind == EffectKind.AudioLevel && defaults) Sync.Colors = ["#00DC78", "#FFD200", "#FF283C"];
        if (c.Kind == EffectKind.AudioSpectrum && defaults) Sync.Colors = ["#7C5CFF"];
        AppI.ConfigChanged();
        _loading = true;
        LoadEffect();
        _loading = false;
    }

    void Tuning_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        if (sender == SpeedSlider) Sync.Speed = e.NewValue; else Sync.Brightness = e.NewValue;
        EffectChanged();
    }

    void SensorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SensorBox.SelectedItem is not SensorOption o) return;
        Sync.Sensor = o.Id;
        EffectChanged();
    }

    void Temp_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue)) return;
        if (sender == ColdBox) Sync.ColdTemp = args.NewValue; else Sync.HotTemp = args.NewValue;
        EffectChanged();
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

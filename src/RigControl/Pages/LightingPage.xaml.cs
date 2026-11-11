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

public sealed record EffectChoice(EffectKind Kind, string Label, string Glyph);

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

    // ---------------- Zones (devices with several, following the synced effect)
    public Visibility ZonesVisibility => _target.Zones.Count > 1 && Settings.Source == LightingSource.Sync ? Visibility.Visible : Visibility.Collapsed;
    int CustomZones => _target.Zones.Count(z => Settings.Zones.TryGetValue(z.Name, out var zl) && zl.Source != ZoneSource.Sync);
    public string ZonesLabel => CustomZones == 0 ? "Zones" : $"Zones · {CustomZones}";
    public string ZonesTip => CustomZones == 0
        ? $"{_target.Zones.Count} zones, all following the synced effect: give one its own colour, or turn it off"
        : $"{CustomZones} of {_target.Zones.Count} zones have their own colour or are off";

    static readonly string[] ZoneOptions = ["Synced effect", "Colour", "Off"];

    /// <summary>Opens a panel listing the zones: each follows the effect, shows one colour, or is off.</summary>
    public void ShowZones(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 10, MinWidth = 340 };
        panel.Children.Add(new TextBlock { Text = "Each zone can follow the synced effect, show one colour, or stay off.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 });
        foreach (var zone in _target.Zones)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            var name = string.IsNullOrWhiteSpace(zone.DisplayName) ? zone.Name : zone.DisplayName;
            row.Children.Add(new TextBlock { Text = $"{name} · {zone.LedsCount} LED{(zone.LedsCount == 1 ? "" : "s")}", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var current = Settings.Zones.GetValueOrDefault(zone.Name);
            var box = new ComboBox { ItemsSource = ZoneOptions, SelectedIndex = (int)(current?.Source ?? ZoneSource.Sync), MinWidth = 150 };
            Grid.SetColumn(box, 1);
            var swatch = new Button { Width = 32, Height = 28, Padding = new Thickness(0), CornerRadius = new CornerRadius(8) };
            Grid.SetColumn(swatch, 2);
            void Show()
            {
                var zl = Settings.Zones.GetValueOrDefault(zone.Name);
                swatch.Visibility = zl?.Source == ZoneSource.Color ? Visibility.Visible : Visibility.Collapsed;
                var c = Rgb.Parse(zl?.Color ?? "#FFFFFF");
                swatch.Background = new SolidColorBrush(Color.FromArgb(255, c.R, c.G, c.B));
            }
            box.SelectionChanged += (_, _) =>
            {
                if (box.SelectedIndex < 0) return;
                var d = Edit();
                var source = (ZoneSource)box.SelectedIndex;
                if (source == ZoneSource.Sync) d.Zones.Remove(zone.Name);
                else d.Zones[zone.Name] = new ZoneLighting { Source = source, Color = d.Zones.GetValueOrDefault(zone.Name)?.Color ?? "#FFFFFF" };
                Show();
                Changed();
                Raise(nameof(ZonesLabel));
                Raise(nameof(ZonesTip));
            };
            swatch.Click += (_, _) => LightingPage.ShowColorPicker(swatch, Rgb.Parse(Settings.Zones.GetValueOrDefault(zone.Name)?.Color ?? "#FFFFFF"), c =>
            {
                if (Edit().Zones.GetValueOrDefault(zone.Name) is { } zl) zl.Color = c.ToString();
                Show();
                Changed();
            });
            Show();
            row.Children.Add(box);
            row.Children.Add(swatch);
            panel.Children.Add(row);
        }
        new Flyout { Content = panel, Placement = FlyoutPlacementMode.Bottom }.ShowAt((FrameworkElement)sender);
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
        ColorsPanel.Visibility = s.Kind is EffectKind.Static or EffectKind.Breathing or EffectKind.Temperature ? Visibility.Visible : Visibility.Collapsed;
        ColorsLabel.Text = temp ? "Cold → hot colours" : s.Kind == EffectKind.Static ? "Colours (more than one makes a gradient)" : "Colours";
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
        else EffectMath.Render(s, AppI.Lighting.Now, x, temp, frame);
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

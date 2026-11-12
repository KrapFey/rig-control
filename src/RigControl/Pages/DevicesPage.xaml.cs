using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RigControl.Core;

namespace RigControl.Pages;

/// <summary>A one-click DPI value; highlighted when it's the current one.</summary>
public sealed class DpiPreset(PeripheralCard card, int value) : Observable
{
    static readonly Microsoft.UI.Xaml.Media.Brush On = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentGradient"];
    static readonly Microsoft.UI.Xaml.Media.Brush Off = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x14, 255, 255, 255));
    public int Value { get; } = value;
    public string Label { get; } = value.ToString();
    public Microsoft.UI.Xaml.Media.Brush Background => (int)card.Dpi == Value ? On : Off;
    public void Pick() => card.Dpi = Value;
    public void Refresh() => Raise(nameof(Background));
}

public sealed class StageVm(PeripheralCard card, int index, int max) : Observable
{
    public string Group { get; } = $"stages-{card.Key}";
    public bool Selectable => card.StagesSelectable;
    public string Label { get; } = $"{index + 1}";
    public double Max { get; } = max;
    public bool Active
    {
        get => card.ActiveStage == index + 1;
        set { if (value) card.ActiveStage = index + 1; }
    }
    public double Value
    {
        get => card.StageValue(index);
        set { if (!double.IsNaN(value)) card.SetStage(index, (int)value); }
    }
    public void Refresh() { Raise(nameof(Active)); Raise(nameof(Value)); }
}

/// <summary>One device's settings. Shows saved values, or what the device reports when nothing is saved.</summary>
public sealed class PeripheralCard : Observable
{
    readonly IPeripheral _device;
    PeripheralState _state;

    public PeripheralCard(IPeripheral device, PeripheralState? state)
    {
        _device = device;
        _state = state ?? new PeripheralState();
        PollOptions = [.. device.Caps.PollRates.Select(r => $"{r} Hz")];
        Presets = [.. new[] { 400, 800, 1200, 1600, 3200, 6400, 12800, 16000, 20000, 26000, 30000 }
            .Where(v => v >= device.Caps.DpiMin && v <= device.Caps.DpiMax).Select(v => new DpiPreset(this, v))];
        Stages = [.. Enumerable.Range(0, device.Caps.MaxStages).Select(i => new StageVm(this, i, device.Caps.DpiMax))];
    }

    public string Key => _device.Key;
    public string Name => _device.Name;
    public string Source => $"{_device.Kind} · {_device.Source}";
    /// <summary>One line of what matters now, for the overview tile.</summary>
    public string Vital
    {
        get
        {
            if (_device.Kind == "Headset")
            {
                if (_state.Connected == false) return "Off or out of range";
                var parts = new List<string> { "On" };
                if (_state.MicMuted == true) parts.Add("mic muted");
                if (_state.Sidetone is int st) parts.Add($"sidetone {st * 100 / 128}%");
                return string.Join(" · ", parts);
            }
            var bits = new List<string>();
            if (Caps.Lighting is not null) bits.Add(LightEffectValue == 0 && Caps.LightingEffects ? "lighting off" : $"lighting {LightBrightnessValue:0}%");
            if (Caps.ActuationMm.Length > 0 && ActuationKnown) bits.Add($"actuation {ActuationMm:0.0} mm");
            if (Caps.RapidTrigger && (Saved.RapidTrigger ?? _state.RapidTrigger) is bool rt) bits.Add(rt ? "rapid trigger" : "rapid trigger off");
            if (Caps.GameMode && (Saved.GameMode ?? _state.GameMode) == true) bits.Add("game mode");
            if (Caps.Dpi) bits.Add($"{Dpi:0} DPI");
            if (Caps.MaxStages > 0) bits.Add($"stage {ActiveStage} of {CurrentStages.Count}");
            if ((Saved.PollRate ?? _state.PollRate) is int hz) bits.Add($"{hz} Hz");
            return bits.Count > 0 ? string.Join(" · ", bits) : _device.Kind;
        }
    }

    public string Glyph => _device.Kind switch { "Headset" => "", "Keyboard" => "", _ => "" };
    PeripheralCaps Caps => _device.Caps;

    static AppConfig Cfg => App.Instance!.Config;
    PeripheralSettings Saved => Cfg.Peripherals.TryGetValue(Key, out var s) ? s : new PeripheralSettings();
    PeripheralSettings Edit()
    {
        if (!Cfg.Peripherals.TryGetValue(Key, out var s)) Cfg.Peripherals[Key] = s = new PeripheralSettings();
        return s;
    }
    static void Changed() => App.Instance!.PeripheralSettingsChanged();

    public void Update(PeripheralState? state)
    {
        // Refreshed every second while the page is open: only touch the UI when the device reports something new,
        // so a value being typed isn't reset.
        if (state is null || Same(state, _state)) return;
        _state = state;
        Raise(string.Empty);
        foreach (var s in Stages) s.Refresh();
        foreach (var p in Presets) p.Refresh();
    }

    static bool Same(PeripheralState a, PeripheralState b) =>
        a with { Stages = null } == b with { Stages = null } && (a.Stages ?? []).SequenceEqual(b.Stages ?? []);

    // ---------------- Headsets
    public Visibility HeadsetVisibility => _device.Kind == "Headset" ? Visibility.Visible : Visibility.Collapsed;
    public bool HeadsetOff => _state.Connected == false;
    static Visibility Show(bool b) => b ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ChatmixVisibility => Show(Caps.Chatmix && _state.Chatmix is not null);
    public double ChatmixValue => _state.Chatmix ?? 64;
    public string ChatmixText => _state.Chatmix is int c ? (c < 60 ? $"more game · {100 - c * 100 / 128}%" : c > 68 ? $"more chat · {c * 100 / 128}%" : "balanced") : "";
    public Visibility SidetoneVisibility => Show(Caps.Sidetone);
    public double SidetoneValue => Saved.Sidetone ?? _state.Sidetone ?? 0;
    public string SidetoneText => $"Sidetone (hear your own voice) · {SidetoneValue * 100 / 128:0}%";
    public Visibility MicVolumeVisibility => Show(Caps.MicVolume);
    public double MicVolumeValue => Saved.MicVolume ?? 100;
    public string MicVolumeText => $"Microphone volume · {MicVolumeValue * 100 / 128:0}%";
    public Visibility InactiveVisibility => Show(Caps.InactiveMaxMinutes > 0);
    public double InactiveValue => Saved.InactiveMinutes ?? 30;
    public string InactiveText => InactiveValue == 0 ? "Auto-off: never" : $"Auto-off after {InactiveValue:0} min idle";
    public Visibility EqVisibility => Show(Caps.EqPresets.Length > 0);
    public List<string> EqOptions => [.. Caps.EqPresets.Select(p => System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(p))];
    public int EqIndex => Saved.EqPreset ?? -1;
    public Visibility AncVisibility => Show(Caps.Anc);
    public List<string> AncOptions { get; } = ["Off", "Noise cancelling", "Ambient sound"];
    public int AncIndex => Saved.Anc ?? -1;
    public Visibility LightsVisibility => Show(Caps.Lights);
    public bool LightsOn => Saved.Lights ?? true;
    public Visibility VoiceVisibility => Show(Caps.VoicePrompts);
    public bool VoicePromptsOn => Saved.VoicePrompts ?? true;
    public Visibility RotateVisibility => Show(Caps.RotateToMute);
    public bool RotateOn => Saved.RotateToMute ?? true;

    /// <summary>Headset and keyboard controls report here (only from user input); saves and sends.</summary>
    public void Set(string what, double value)
    {
        var s = Edit();
        switch (what)
        {
            case "sidetone": s.Sidetone = (int)Math.Round(value); break;
            case "micvolume": s.MicVolume = (int)Math.Round(value); break;
            case "inactive": s.InactiveMinutes = (int)Math.Round(value); break;
            case "eq": s.EqPreset = (int)value; break;
            case "anc": s.Anc = (int)value; break;
            case "lights": s.Lights = value != 0; break;
            case "voice": s.VoicePrompts = value != 0; break;
            case "rotate": s.RotateToMute = value != 0; break;
            case "actuation": s.ActuationMm = Caps.ActuationMm[Math.Clamp((int)Math.Round(value), 0, Caps.ActuationMm.Length - 1)]; break;
            case "rapid": s.RapidTrigger = value != 0; break;
            case "gamemode": s.GameMode = value != 0; break;
            case "keyswitch": s.KeyswitchGaming = value != 0; break;
            case "fnswap": s.FnSwap = value != 0; break;
            case "backlight": s.Backlight = (int)Math.Round(value); break;
            case "lbright": s.LightBrightness = (int)Math.Round(value * 255 / 100); break;
            case "lspeed": s.LightSpeed = (int)Math.Round(value * 255 / 100); break;
            case "lhue": s.LightHue = (int)Math.Round(value); break;
            case "lsat": s.LightSaturation = (int)Math.Round(value * 255 / 100); break;
            default: return;
        }
        Raise(string.Empty);
        Changed();
    }

    // ---------------- Keyboards
    public Visibility ActuationVisibility => Show(Caps.ActuationMm.Length > 0);
    bool ActuationKnown => (Saved.ActuationMm ?? _state.ActuationMm) is not null;
    double ActuationMm => Saved.ActuationMm ?? _state.ActuationMm ?? 2.0;
    public double ActuationMax => Math.Max(0, Caps.ActuationMm.Length - 1);
    public double ActuationIndex => Caps.ActuationMm.Length == 0 ? 0
        : Array.IndexOf(Caps.ActuationMm, Caps.ActuationMm.MinBy(m => Math.Abs(m - ActuationMm)));
    public string ActuationText => ActuationKnown
        ? $"Actuation (how far a key travels before it counts) · {ActuationMm:0.0} mm"
        : "Actuation (how far a key travels before it counts) · the keyboard's own setting until you move this";
    public string ActuationNote => "Applies to every analog key. Lives in the keyboard's memory: a replug brings back its own profile, and RigControl sets it again.";
    public Visibility RapidVisibility => Show(Caps.RapidTrigger);
    public Visibility GameModeVisibility => Show(Caps.GameMode);
    public bool GameModeOn => Saved.GameMode ?? _state.GameMode ?? false;
    public Visibility KeyswitchVisibility => Show(Caps.KeyswitchOptimization);
    public bool KeyswitchGaming => Saved.KeyswitchGaming ?? _state.KeyswitchGaming ?? true;
    public Visibility FnSwapVisibility => Show(Caps.FnSwap);
    public bool FnSwapOn => Saved.FnSwap ?? _state.FnSwap ?? false;
    public Visibility BacklightVisibility => Show(Caps.Backlight);
    public double BacklightValue => Saved.Backlight ?? _state.Backlight ?? 100;
    public string BacklightText => $"Backlight · {BacklightValue:0}%";
    public Visibility KeyboardSwitchesVisibility => Show(Caps.GameMode || Caps.KeyswitchOptimization || Caps.FnSwap);
    public bool RapidOn => Saved.RapidTrigger ?? _state.RapidTrigger ?? false;

    // ---------------- On-board lighting (VIA keyboards)
    public Visibility LightingVisibility => Show(Caps.Lighting is not null);
    public string LightingTitle => $"LIGHTING · {Caps.Lighting?.ToUpperInvariant()}";
    public double LightBrightnessValue => Math.Round((Saved.LightBrightness ?? _state.LightBrightness ?? 255) * 100 / 255.0);
    public string LightBrightnessText => $"Brightness · {LightBrightnessValue:0}%";
    public Visibility LightEffectVisibility => Show(Caps.LightingEffects);
    // What the keyboard reports wins: it clamps an effect number past its last one, and this shows that.
    int LightEffectValue => _state.LightEffect ?? Saved.LightEffect ?? 1;
    public string LightEffectText => LightEffectValue == 0 ? "Off" : $"Effect {LightEffectValue}";
    public void EffectPrev() => SetEffect(Math.Max(0, LightEffectValue - 1));
    public void EffectNext() => SetEffect(Math.Min(255, LightEffectValue + 1));
    void SetEffect(int v)
    {
        Edit().LightEffect = v;
        _state = _state with { LightEffect = v }; // show it at once; the next read confirms or corrects it
        Raise(nameof(LightEffectText));
        Changed();
    }
    public Visibility LightSpeedVisibility => Show(Caps.LightingSpeed);
    public double LightSpeedValue => Math.Round((Saved.LightSpeed ?? _state.LightSpeed ?? 128) * 100 / 255.0);
    public string LightSpeedText => $"Effect speed · {LightSpeedValue:0}%";
    public Visibility LightColorVisibility => Show(Caps.LightingColor);
    public double LightHueValue => Saved.LightHue ?? _state.LightHue ?? 0;
    public double LightSatValue => Math.Round((Saved.LightSaturation ?? _state.LightSaturation ?? 255) * 100 / 255.0);
    public string LightSatText => $"Saturation · {LightSatValue:0}%";
    public Microsoft.UI.Xaml.Media.SolidColorBrush LightSwatch => new(Hsv(LightHueValue / 255.0 * 360, LightSatValue / 100, 1));
    static Windows.UI.Color Hsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = (h % 360) switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
        return Windows.UI.Color.FromArgb(255, (byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    public List<DpiPreset> Presets { get; }
    public void StepUp() => Dpi = Dpi + 50;
    public void StepDown() => Dpi = Dpi - 50;

    // Battery
    public Visibility BatteryVisibility => _state.BatteryPercent is null ? Visibility.Collapsed : Visibility.Visible;
    public string BatteryText => _state.BatteryPercent is int b ? $"{b}%" : "";
    public string BatteryGlyph => _state.Charging == true ? "" : _state.BatteryPercent switch { >= 80 => "", >= 50 => "", >= 20 => "", _ => "" };

    // DPI
    public Visibility DpiVisibility => Caps.Dpi ? Visibility.Visible : Visibility.Collapsed;
    public double DpiMin => Caps.DpiMin;
    public double DpiMax => Math.Max(Caps.DpiMax, Caps.DpiMin + 50);
    public double Dpi
    {
        get => Saved.Dpi ?? _state.Dpi ?? 800;
        set
        {
            var v = Math.Clamp((int)(Math.Round(value / 50) * 50), Caps.DpiMin, Math.Max(Caps.DpiMax, Caps.DpiMin));
            if (v == (int)Dpi) return;
            var s = Edit();
            s.Dpi = v;
            if (Caps.MaxStages > 0)
            {
                // Like Synapse: the current DPI is the active stage, so the DPI button cycles from where you are.
                var list = new List<int>(CurrentStages);
                int i = Math.Clamp(ActiveStage, 1, list.Count) - 1;
                list[i] = v;
                s.Stages = list;
                s.ActiveStage = i + 1;
                foreach (var st in Stages) st.Refresh();
            }
            Raise(nameof(Dpi));
            Raise(nameof(DpiText));
            foreach (var p in Presets) p.Refresh();
            Changed();
        }
    }
    public string DpiText => $"{Dpi:0}";

    // Stages
    public Visibility StagesVisibility => Caps.MaxStages > 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool StagesSelectable => Caps.StageSelectable;
    public string StagesTitle => Caps.StageSelectable ? "DPI STAGES · the DPI button cycles through these" : "DPI STAGES · the DPI button cycles through these (it also picks the active one)";
    public List<StageVm> Stages { get; }
    List<int> CurrentStages => Saved.Stages ?? _state.Stages ?? [400, 800, 1600, 3200, 6400];
    public int ActiveStage
    {
        get => Saved.ActiveStage ?? _state.ActiveStage ?? 1;
        set
        {
            var s = Edit();
            s.Stages ??= [.. CurrentStages];
            s.ActiveStage = value;
            if (value >= 1 && value <= s.Stages.Count) s.Dpi = s.Stages[value - 1]; // selecting a stage switches to its DPI
            Raise(nameof(Dpi));
            Raise(nameof(DpiText));
            foreach (var p in Presets) p.Refresh();
            Changed();
            foreach (var st in Stages) st.Refresh();
        }
    }
    public double StageValue(int i) => i < CurrentStages.Count ? CurrentStages[i] : 0;
    public void SetStage(int i, int v)
    {
        var list = new List<int>(CurrentStages);
        while (list.Count <= i) list.Add(800);
        v = Math.Clamp((int)(Math.Round(v / 50.0) * 50), Caps.DpiMin, Math.Max(Caps.DpiMax, Caps.DpiMin));
        if (list[i] == v) return;
        list[i] = v;
        var s = Edit();
        s.Stages = list;
        s.ActiveStage ??= ActiveStage;
        if (i + 1 == s.ActiveStage)
        {
            s.Dpi = v; // editing the active stage changes the current DPI too
            Raise(nameof(Dpi));
            Raise(nameof(DpiText));
            foreach (var p in Presets) p.Refresh();
        }
        Changed();
    }

    // Polling
    public Visibility PollVisibility => Caps.PollRates.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public List<string> PollOptions { get; }
    public int PollIndex
    {
        get => Array.IndexOf(Caps.PollRates, Saved.PollRate ?? _state.PollRate ?? 0);
        set
        {
            if (value < 0 || value >= Caps.PollRates.Length || Caps.PollRates[value] == Saved.PollRate) return;
            Edit().PollRate = Caps.PollRates[value];
            Changed();
        }
    }

    // Power
    public Visibility PowerVisibility => Caps.SleepTimeout || Caps.LowBatteryThreshold ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SleepVisibility => Caps.SleepTimeout ? Visibility.Visible : Visibility.Collapsed;
    public double SleepMinutes
    {
        get => (Saved.SleepSeconds ?? _state.SleepSeconds ?? 300) / 60.0;
        set
        {
            var secs = (int)Math.Round(value) * 60;
            if (secs == Saved.SleepSeconds) return;
            Edit().SleepSeconds = secs;
            Raise(nameof(SleepText));
            Changed();
        }
    }
    public string SleepText => $"Sleep after {SleepMinutes:0} min idle";
    public Visibility LowBatteryVisibility => Caps.LowBatteryThreshold ? Visibility.Visible : Visibility.Collapsed;
    public double LowBattery
    {
        get => Saved.LowBatteryPercent ?? _state.LowBatteryPercent ?? 10;
        set
        {
            var v = (int)Math.Round(value);
            if (v == Saved.LowBatteryPercent) return;
            Edit().LowBatteryPercent = v;
            Raise(nameof(LowBatteryText));
            Changed();
        }
    }
    public string LowBatteryText => $"Low-battery warning at {LowBattery:0}%";
}

public sealed partial class DevicesPage : Page
{
    readonly List<PeripheralCard> _cards = [];

    public DevicesPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            App.Instance!.Peripherals.Changed += OnChanged;
            App.Instance.Peripherals.Watch(true);
            Build();
        };
        Unloaded += (_, _) =>
        {
            App.Instance!.Peripherals.Changed -= OnChanged;
            App.Instance.Peripherals.Watch(false);
        };
    }

    void OnChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        var hub = App.Instance!.Peripherals;
        if (hub.Devices.Select(d => d.Key).SequenceEqual(_cards.Select(c => c.Key)))
        {
            var states = hub.States;
            foreach (var c in _cards) c.Update(states.GetValueOrDefault(c.Key));
        }
        else Build();
    });

    void Build()
    {
        var hub = App.Instance!.Peripherals;
        var states = hub.States;
        _cards.Clear();
        _cards.AddRange(hub.Devices.Select(d => new PeripheralCard(d, states.GetValueOrDefault(d.Key))));
        Tiles.ItemsSource = null;
        Tiles.ItemsSource = _cards;
        Empty.Visibility = _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Summary.Text = _cards.Count == 0 ? "Mice, headsets and keyboards with settings show up here." : $"{_cards.Count} device{(_cards.Count == 1 ? "" : "s")} · click one for its settings";
    }

    /// <summary>Looks for devices again; the button says so for a few seconds while detection runs.</summary>
    async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        RescanButton.IsEnabled = false;
        RescanText.Text = "Scanning…";
        App.Instance!.Peripherals.Rescan();
        await Task.Delay(TimeSpan.FromSeconds(2)); // the list refreshes when the scan finishes; this is just feedback
        RescanButton.IsEnabled = true;
        RescanText.Text = "Rescan";
    }

    void Tile_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PeripheralCard card)
            Frame.Navigate(typeof(DevicePage), card.Key, new Microsoft.UI.Xaml.Media.Animation.DrillInNavigationTransitionInfo());
    }

    // Hover lift (composition, like the dashboard fan tiles).
    void Tile_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => Lift((UIElement)sender, 1.03f);
    void Tile_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => Lift((UIElement)sender, 1f);

    static void Lift(UIElement el, float scale)
    {
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(el);
        visual.CenterPoint = new System.Numerics.Vector3(el.ActualSize.X / 2, el.ActualSize.Y / 2, 0);
        var comp = visual.Compositor;
        var anim = comp.CreateVector3KeyFrameAnimation();
        anim.InsertKeyFrame(1, new System.Numerics.Vector3(scale, scale, 1), comp.CreateCubicBezierEasingFunction(new(0.2f, 0.9f), new(0.3f, 1.2f)));
        anim.Duration = TimeSpan.FromMilliseconds(220);
        visual.StartAnimation("Scale", anim);
    }
}

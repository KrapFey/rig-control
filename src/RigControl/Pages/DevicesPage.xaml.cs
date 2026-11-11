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

    // Device settings are only written for changes the user makes. While a card is being set up, a slider can
    // get its value before its range (5000 DPI clamped to the default maximum of 100), and that must never
    // reach the mouse: so ignore value changes unless the control has focus (being dragged, clicked, typed in).
    void Slider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (sender is not Slider { FocusState: not FocusState.Unfocused, DataContext: PeripheralCard card } s) return;
        switch (s.Tag as string)
        {
            case "dpi": card.Dpi = e.NewValue; break;
            case "sleep": card.SleepMinutes = e.NewValue; break;
            case "lowbat": card.LowBattery = e.NewValue; break;
        }
    }

    // A NumberBox keeps focus on the text box inside it, so its own FocusState reads Unfocused even while you type.
    // Instead: once you've focused a box (clicked or tabbed into it), its committed values (Enter or clicking away) count.
    readonly HashSet<NumberBox> _touched = [];
    void Number_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is NumberBox box) _touched.Add(box);
    }

    void Dpi_NumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_touched.Contains(sender) || sender.DataContext is not PeripheralCard card || double.IsNaN(args.NewValue)) return;
        card.Dpi = args.NewValue;
    }

    void Stage_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_touched.Contains(sender) || sender.DataContext is not StageVm stage || double.IsNaN(args.NewValue)) return;
        stage.Value = args.NewValue;
    }

    void Poll_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { FocusState: not FocusState.Unfocused, DataContext: PeripheralCard card } box) card.PollIndex = box.SelectedIndex;
    }

    void Stage_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { FocusState: not FocusState.Unfocused, DataContext: StageVm stage }) stage.Active = true;
    }

    void Build()
    {
        var hub = App.Instance!.Peripherals;
        var states = hub.States;
        _cards.Clear();
        _cards.AddRange(hub.Devices.Select(d => new PeripheralCard(d, states.GetValueOrDefault(d.Key))));
        Cards.ItemsSource = null;
        Cards.ItemsSource = _cards;
        Empty.Visibility = _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Summary.Text = _cards.Count == 0 ? "Mice, headsets and keyboards with settings show up here." : $"{_cards.Count} configurable device{(_cards.Count == 1 ? "" : "s")}";
    }
}

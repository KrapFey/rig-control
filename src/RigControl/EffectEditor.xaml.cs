using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using RigControl.Core;
using RigControl.Pages;
using Windows.UI;

namespace RigControl;

/// <summary>
/// Edits one effect in place: the synced effect, or a device's own. Every change is saved and reaches the lights at once.
/// </summary>
public sealed partial class EffectEditor : UserControl
{
    public static readonly EffectChoice[] Effects =
    [
        new(EffectKind.Rainbow, "Rainbow wave", ""),
        new(EffectKind.ColorCycle, "Colour cycle", ""),
        new(EffectKind.Breathing, "Breathing", ""),
        new(EffectKind.Static, "Static", ""),
        new(EffectKind.Temperature, "Temperature", ""),
        new(EffectKind.AudioLevel, "Audio level", ""),
        new(EffectKind.AudioSpectrum, "Spectrum", ""),
        new(EffectKind.AudioPulse, "Beat pulse", ""),
        new(EffectKind.Off, "Off", ""),
    ];

    /// <summary>The effect's name as the editor shows it.</summary>
    public static string LabelOf(EffectKind kind) => Effects.FirstOrDefault(e => e.Kind == kind)?.Label ?? kind.ToString();

    readonly LinearGradientBrush _preview = new() { StartPoint = new(0, 0.5), EndPoint = new(1, 0.5) };
    static readonly float[] PreviewX = [.. Enumerable.Range(0, 40).Select(i => i / 39f)];
    readonly Rgb[] _previewFrame = new Rgb[PreviewX.Length];
    EffectSettings _effect = new();
    bool _loading;

    static App AppI => App.Instance!;

    public EffectEditor()
    {
        InitializeComponent();
        PreviewBar.Background = _preview;
        PreviewGlow.Background = _preview;
        EffectList.ItemsSource = Effects;
        Loaded += (_, _) =>
        {
            // Read here, not once: a rescan can change the sensors.
            SensorBox.ItemsSource = AppI.Hub.Temps.OrderBy(t => t.Device).ThenBy(t => t.Name).Select(t => new SensorOption(t.Id, $"{AppI.SensorLabel(t)} · {t.Device}")).ToList();
            Load();
            CompositionTarget.Rendering += RenderFrame; // once per screen refresh, so the preview moves smoothly
        };
        Unloaded += (_, _) => CompositionTarget.Rendering -= RenderFrame;
    }

    /// <summary>The effect being edited (changed in place).</summary>
    public EffectSettings Effect
    {
        get => _effect;
        set { _effect = value; if (IsLoaded) Load(); }
    }

    /// <summary>The synced effect: its temperature reading is the engine's smoothed one.</summary>
    public bool Synced { get; set; }

    EffectSettings S => _effect;

    void Load()
    {
        _loading = true;
        var s = S;
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
        if (temp && SensorBox.ItemsSource is List<SensorOption> options)
        {
            s.Sensor ??= Live.Instance.Cpu?.Id;
            SensorBox.SelectedItem = options.FirstOrDefault(o => o.Id == s.Sensor);
            ColdBox.Value = s.ColdTemp;
            HotBox.Value = s.HotTemp;
        }
        BuildSwatches();
        _loading = false;
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

    void Changed()
    {
        if (_loading) return;
        AppI.ConfigChanged();
        EffectChanged?.Invoke();
    }

    /// <summary>Raised after the user changed the effect (and it was saved).</summary>
    public event Action? EffectChanged;

    void AudioSource_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AudioSourceBox.SelectedIndex < 0) return;
        S.AudioInput = AudioSourceBox.SelectedIndex == 1;
        S.AudioDevice = null; // the other kind's device doesn't apply
        Changed();
        _loading = true;
        LoadAudio(S);
        _loading = false;
    }

    void AudioDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AudioDeviceBox.SelectedItem is not AudioDeviceOption o) return;
        S.AudioDevice = o.Id;
        Changed();
    }

    void Sensitivity_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        S.Sensitivity = e.NewValue;
        Changed();
    }

    void BuildSwatches()
    {
        Swatches.Children.Clear();
        var s = S;
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
            b.Click += (_, _) => ShowColorPicker(b, c, picked => { S.Colors[index] = picked.ToString(); Changed(); BuildSwatches(); });
            b.RightTapped += (_, _) => { if (S.Colors.Count > 1) { S.Colors.RemoveAt(index); Changed(); BuildSwatches(); } };
            Swatches.Children.Add(b);
        }
        if (s.Colors.Count < 6)
        {
            var add = new Button { Width = 32, Height = 32, Padding = new Thickness(0), CornerRadius = new CornerRadius(10), Content = new FontIcon { Glyph = "", FontSize = 12 } };
            ToolTipService.SetToolTip(add, "Add a colour");
            add.Click += (_, _) => { S.Colors.Add(Rgb.Hsv(Random.Shared.Next(360)).ToString()); Changed(); BuildSwatches(); };
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

    void RenderFrame(object? sender, object e)
    {
        if (Visibility != Visibility.Visible) return;
        // The effect itself, on the engine's clock so it moves with the lights.
        var s = S;
        var x = PreviewX;
        var frame = _previewFrame;
        double? temp = (Synced ? AppI.Lighting.CurrentTemperature : null)
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

    void EffectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || EffectList.SelectedItem is not EffectChoice c) return;
        S.Kind = c.Kind;
        if (c.Kind == EffectKind.Temperature && S.Colors.Count < 2) S.Colors = ["#00D4FF", "#7C5CFF", "#FF963C", "#FF2846"];
        // Audio effects start from colours that suit them (the shared default is purple → cyan).
        bool defaults = S.Colors.SequenceEqual(["#7C5CFF", "#00D4FF"], StringComparer.OrdinalIgnoreCase);
        if (c.Kind == EffectKind.AudioLevel && defaults) S.Colors = ["#00DC78", "#FFD200", "#FF283C"];
        if (c.Kind == EffectKind.AudioSpectrum && defaults) S.Colors = ["#7C5CFF"];
        Changed();
        Load();
    }

    void Tuning_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        if (ReferenceEquals(sender, SpeedSlider)) S.Speed = e.NewValue; else S.Brightness = e.NewValue;
        Changed();
    }

    void SensorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SensorBox.SelectedItem is not SensorOption o) return;
        S.Sensor = o.Id;
        Changed();
    }

    void Temp_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue)) return;
        if (sender == ColdBox) S.ColdTemp = args.NewValue; else S.HotTemp = args.NewValue;
        Changed();
    }
}

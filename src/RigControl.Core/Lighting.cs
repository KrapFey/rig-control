using System.Diagnostics;

namespace RigControl.Core;

// ---------------------------------------------------------------- settings (saved in config.json)

public enum EffectKind { Static, Breathing, Rainbow, ColorCycle, Temperature, Off, AudioLevel, AudioSpectrum, AudioPulse }

public sealed class EffectSettings
{
    public EffectKind Kind { get; set; } = EffectKind.Rainbow;
    /// <summary>Hex colours. Static: one colour or a gradient; Breathing: cycles through them; Temperature: cold → hot.</summary>
    public List<string> Colors { get; set; } = ["#7C5CFF", "#00D4FF"];
    /// <summary>0..100.</summary>
    public double Speed { get; set; } = 50;
    /// <summary>0..100.</summary>
    public double Brightness { get; set; } = 100;
    /// <summary>Temperature: sensor id and the range mapped onto the colours.</summary>
    public string? Sensor { get; set; }
    public double ColdTemp { get; set; } = 35;
    public double HotTemp { get; set; } = 80;
    /// <summary>Audio effects: listen to a microphone instead of what the speakers play.</summary>
    public bool AudioInput { get; set; }
    /// <summary>Audio effects: the device to listen to (null = the Windows default).</summary>
    public string? AudioDevice { get; set; }
    /// <summary>Audio effects: 0..100, 50 = as measured.</summary>
    public double Sensitivity { get; set; } = 50;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAudio => Kind is EffectKind.AudioLevel or EffectKind.AudioSpectrum or EffectKind.AudioPulse;
}

public enum LightingSource { Sync, Hardware, Off }

/// <summary>What one device does in a profile.</summary>
public sealed class DeviceLighting
{
    public LightingSource Source { get; set; } = LightingSource.Sync;
    /// <summary>Hardware: the device's own mode, by name, and its settings.</summary>
    public string? Mode { get; set; }
    public List<string> Colors { get; set; } = [];
    public uint? Speed { get; set; }
    public uint? Brightness { get; set; }
    public uint? Direction { get; set; }
    /// <summary>Synced effect: zones that do something else, by zone name. Zones not listed follow the effect.</summary>
    public Dictionary<string, ZoneLighting> Zones { get; set; } = [];
}

public enum ZoneSource { Sync, Color, Off, Leds }

/// <summary>One zone of a device that follows the synced effect: keep following it, one fixed colour, a colour per LED, or off.</summary>
public sealed class ZoneLighting
{
    public ZoneSource Source { get; set; } = ZoneSource.Sync;
    public string Color { get; set; } = "#FFFFFF";
    /// <summary>Each LED: colours from the zone's first LED on. LEDs past the end use <see cref="Color"/>.</summary>
    public List<string> Leds { get; set; } = [];
}

public sealed class LightingProfile
{
    public string Name { get; set; } = "Default";
    /// <summary>The effect every device set to <see cref="LightingSource.Sync"/> shows.</summary>
    public EffectSettings Sync { get; set; } = new();
    /// <summary>Per device, keyed by <see cref="IRgbTarget.Key"/>. Devices not listed follow the sync effect.</summary>
    public Dictionary<string, DeviceLighting> Devices { get; set; } = [];
}

public sealed class LightingConfig
{
    public bool Enabled { get; set; } = true;
    public string Active { get; set; } = "Default";
    public List<LightingProfile> Profiles { get; set; } = [new()];
    /// <summary>LED counts of addressable headers, by device key and zone name (hardware, so shared by all profiles).</summary>
    public Dictionary<string, Dictionary<string, int>> ZoneSizes { get; set; } = [];

    public LightingProfile Current => Profiles.FirstOrDefault(p => p.Name == Active) ?? Profiles.FirstOrDefault() ?? new();
}

// ---------------------------------------------------------------- devices

/// <summary>Anything RigControl can light: an OpenRGB device or a native one (Seiren V3).</summary>
public interface IRgbTarget
{
    string Key { get; }
    string Name { get; }
    /// <summary>Short tag that tells identical devices apart (serial tail or bus address).</summary>
    string Tag { get; }
    RgbDeviceType Type { get; }
    IReadOnlyList<RgbMode> Modes { get; }
    IReadOnlyList<RgbZone> Zones { get; }
    int LedCount { get; }
    /// <summary>Horizontal position of every LED, 0..1 (wave effects travel along it).</summary>
    float[] LedX { get; }
    /// <summary>Most frames per second this device should get (NZXT shares its USB link with fan control).</summary>
    int MaxFps { get; }
    void EnterDirect();
    void SetLeds(Rgb[] colors);
    void ApplyMode(int index, RgbMode mode);
}

sealed class OpenRgbTarget(OpenRgbClient client, RgbDevice dev) : IRgbTarget
{
    public RgbDevice Device => dev;
    public string Key => dev.Key;
    public string Name => string.IsNullOrWhiteSpace(dev.DisplayName) ? dev.Name : dev.DisplayName;
    public string Tag => dev.Serial.Trim() is { Length: >= 4 } s ? s[^4..]
        : dev.Location.Split(' ', ',', '#').LastOrDefault(p => p.Length > 0) ?? "";
    public RgbDeviceType Type => dev.Type;
    public IReadOnlyList<RgbMode> Modes => dev.Modes;
    public IReadOnlyList<RgbZone> Zones => dev.Zones;
    public int LedCount => dev.Leds.Count;
    public float[] LedX { get; } = Layout(dev);
    // ponytail: fixed caps; per-device tuning if another controller turns out to choke on its own.
    public int MaxFps =>
        dev.Vendor.Contains("NZXT", StringComparison.OrdinalIgnoreCase) ? 10 // shares its USB link with fan control
        : dev.Location.StartsWith("I2C", StringComparison.OrdinalIgnoreCase) ? 15 // RAM/GPU: slow SMBus/I2C, can't keep up with 30
        : 30;

    public void EnterDirect()
    {
        if (dev.DirectModeIndex is int i and >= 0) client.UpdateMode(dev.Index, i, dev.Modes[i]);
    }

    public void SetLeds(Rgb[] colors) => client.UpdateLeds(dev.Index, colors);
    public void ApplyMode(int index, RgbMode mode) => client.UpdateMode(dev.Index, index, mode);

    static float[] Layout(RgbDevice dev)
    {
        var x = new float[dev.Leds.Count];
        foreach (var z in dev.Zones)
        {
            if (z.Matrix is { } m && z.MatrixWidth > 1)
            {
                for (int row = 0; row < z.MatrixHeight; row++)
                    for (int col = 0; col < z.MatrixWidth; col++)
                    {
                        var led = m[row * z.MatrixWidth + col];
                        if (led != uint.MaxValue && z.StartLed + led < x.Length) x[z.StartLed + led] = col / (float)(z.MatrixWidth - 1);
                    }
            }
            else
            {
                for (int i = 0; i < z.LedsCount && z.StartLed + i < x.Length; i++)
                    x[z.StartLed + i] = (z.StartLed + i) / (float)Math.Max(1, x.Length - 1);
            }
        }
        return x;
    }
}

sealed class SeirenTarget(SeirenV3 mic) : IRgbTarget
{
    static readonly RgbMode[] ModeList =
    [
        new() { Name = "Direct", Flags = ModeFlags.HasPerLedColor, ColorMode = ColorMode.PerLed },
        new() { Name = "Static", Flags = ModeFlags.HasModeSpecificColor | ModeFlags.HasBrightness, ColorsMin = 1, ColorsMax = 1, Colors = [new(124, 92, 255)], BrightnessMax = 100, Brightness = 100, ColorMode = ColorMode.ModeSpecific },
        new() { Name = "Breathing", Flags = ModeFlags.HasModeSpecificColor | ModeFlags.HasRandomColor | ModeFlags.HasBrightness, ColorsMin = 1, ColorsMax = 1, Colors = [new(0, 212, 255)], BrightnessMax = 100, Brightness = 100, ColorMode = ColorMode.ModeSpecific },
        new() { Name = "Spectrum Cycle", Flags = ModeFlags.HasBrightness, BrightnessMax = 100, Brightness = 100 },
        new() { Name = "Wave", Flags = ModeFlags.HasDirectionLR | ModeFlags.HasBrightness, BrightnessMax = 100, Brightness = 100 },
        new() { Name = "Off" },
    ];

    public SeirenV3 Mic => mic;
    public string Key => "native/razer-seiren-v3";
    public string Name => "Razer Seiren V3 Chroma";
    public string Tag => "";
    public RgbDeviceType Type => RgbDeviceType.Microphone;
    public IReadOnlyList<RgbMode> Modes => ModeList;
    public IReadOnlyList<RgbZone> Zones { get; } = [new RgbZone { Name = "Ring", Type = 1, LedsCount = SeirenV3.LedCount, LedsMin = SeirenV3.LedCount, LedsMax = SeirenV3.LedCount }];
    public int LedCount => SeirenV3.LedCount;
    public float[] LedX { get; } = [.. Enumerable.Range(0, SeirenV3.LedCount).Select(i => i / (float)SeirenV3.LedCount)];
    public int MaxFps => 30;

    public void EnterDirect() => mic.SetEffect(SeirenV3.Effect.Custom);
    public void SetLeds(Rgb[] colors) => mic.SetFrame(colors);

    public void ApplyMode(int index, RgbMode mode)
    {
        if (mode.Flags.HasFlag(ModeFlags.HasBrightness)) mic.SetBrightness((byte)(Math.Clamp(mode.Brightness, 0, 100) * 255 / 100));
        var color = mode.ColorMode == ColorMode.ModeSpecific && mode.Colors.Count > 0 ? mode.Colors[0] : (Rgb?)null;
        switch (mode.Name)
        {
            case "Static": mic.SetEffect(SeirenV3.Effect.Static, color ?? new(255, 255, 255)); break;
            case "Breathing": mic.SetEffect(SeirenV3.Effect.Breathing, color); break;
            case "Spectrum Cycle": mic.SetEffect(SeirenV3.Effect.Spectrum); break;
            case "Wave": mic.SetEffect(SeirenV3.Effect.Wave, waveDirection: (byte)(mode.Direction == 1 ? 2 : 1)); break;
            case "Off": mic.SetEffect(SeirenV3.Effect.None); break;
            default: mic.SetEffect(SeirenV3.Effect.Custom); break;
        }
    }
}

// ---------------------------------------------------------------- effects

public static class EffectMath
{
    static readonly Rgb[] HeatDefault = [new(0, 212, 255), new(124, 92, 255), new(255, 150, 60), new(255, 40, 70)];

    /// <summary>Seconds per cycle for a 0..100 speed: 10 s slowest, 1 s fastest.</summary>
    public static double Period(double speed) => 10 - 9 * Math.Clamp(speed, 0, 100) / 100;

    /// <summary>Renders one frame of an effect into <paramref name="output"/>.</summary>
    static readonly Rgb[] MeterDefault = [new(0, 220, 120), new(255, 210, 0), new(255, 40, 60)];

    public static void Render(EffectSettings e, double t, ReadOnlySpan<float> ledX, double? temp, Span<Rgb> output, AudioFrame? audio = null)
    {
        var colors = e.Colors.Count > 0 ? e.Colors.Select(Rgb.Parse).ToArray() : [new Rgb(255, 255, 255)];
        double p = Period(e.Speed), k = Math.Clamp(e.Brightness, 0, 100) / 100;
        if (e.IsAudio)
        {
            RenderAudio(e, colors, ledX, audio ?? AudioFrame.Silent, output, k);
            return;
        }
        for (int i = 0; i < output.Length; i++)
        {
            double x = i < ledX.Length ? ledX[i] : 0;
            Rgb c = e.Kind switch
            {
                EffectKind.Static => Gradient(colors, x),
                EffectKind.Breathing => colors[(int)(t / p) % colors.Length].Scale((1 - Math.Cos(2 * Math.PI * t / p)) / 2),
                EffectKind.Rainbow => Rgb.Hsv(360 * (x - t / p)),
                EffectKind.ColorCycle => Rgb.Hsv(360 * t / p),
                EffectKind.Temperature => Gradient(e.Colors.Count >= 2 ? colors : HeatDefault,
                    temp is double tv ? Math.Clamp((tv - e.ColdTemp) / Math.Max(1, e.HotTemp - e.ColdTemp), 0, 1) : 1), // no reading: show hot
                _ => default,
            };
            output[i] = c.Scale(k);
        }
    }

    /// <summary>Colour at <paramref name="f"/> (0..1) along evenly spaced stops.</summary>
    public static Rgb Gradient(Rgb[] stops, double f)
    {
        if (stops.Length == 1) return stops[0];
        f = Math.Clamp(f, 0, 1) * (stops.Length - 1);
        int i = Math.Min((int)f, stops.Length - 2);
        return Rgb.Lerp(stops[i], stops[i + 1], f - i);
    }

    /// <summary>Paints zones that don't follow the synced effect over its frame (a fixed colour, a colour per LED, or off).</summary>
    public static void ApplyZones(Span<Rgb> frame, IReadOnlyList<RgbZone> zones, IReadOnlyDictionary<string, ZoneLighting>? settings)
    {
        if (settings is null || settings.Count == 0) return;
        foreach (var z in zones)
        {
            if (!settings.TryGetValue(z.Name, out var zl) || zl.Source == ZoneSource.Sync) continue;
            int start = Math.Clamp(z.StartLed, 0, frame.Length), end = Math.Clamp(z.StartLed + (int)z.LedsCount, start, frame.Length);
            if (zl.Source == ZoneSource.Leds)
            {
                var fill = Rgb.Parse(zl.Color);
                for (int i = start; i < end; i++) frame[i] = i - start < zl.Leds.Count ? Rgb.Parse(zl.Leds[i - start]) : fill;
                continue;
            }
            frame[start..end].Fill(zl.Source == ZoneSource.Color ? Rgb.Parse(zl.Color) : default);
        }
    }

    /// <summary>Sensitivity 0..100 as a gain: 50 = 1x, 0 = 0.25x, 100 = 4x.</summary>
    public static double Gain(double sensitivity) => Math.Pow(2, (Math.Clamp(sensitivity, 0, 100) - 50) / 25);

    static void RenderAudio(EffectSettings e, Rgb[] colors, ReadOnlySpan<float> ledX, AudioFrame a, Span<Rgb> output, double k)
    {
        double gain = Gain(e.Sensitivity);
        bool custom = e.Colors.Count >= 2; // one colour: the level meter uses green→red, the spectrum a rainbow
        for (int i = 0; i < output.Length; i++)
        {
            double x = i < ledX.Length ? ledX[i] : 0;
            Rgb c;
            switch (e.Kind)
            {
                case EffectKind.AudioLevel:
                {
                    // A VU meter along the device: lit up to the level, the edge LED partly.
                    double level = Math.Clamp(a.Level * gain, 0, 1);
                    double lit = Math.Clamp((level - x) * 12 + 1, 0, 1);
                    c = Gradient(custom ? colors : MeterDefault, x).Scale(lit);
                    break;
                }
                case EffectKind.AudioSpectrum:
                {
                    // Bass at one end, treble at the other; each LED as bright as its band.
                    double pos = x * (a.Bands.Length - 1);
                    int b = (int)pos;
                    double f = pos - b, v = b + 1 < a.Bands.Length ? a.Bands[b] * (1 - f) + a.Bands[b + 1] * f : a.Bands[^1];
                    c = (custom ? Gradient(colors, x) : Rgb.Hsv(300 * x)).Scale(Math.Clamp(v * gain, 0, 1));
                    break;
                }
                default: // AudioPulse
                {
                    // The whole device flashes on each beat, stepping through the colours; a glow follows the level.
                    var col = colors[a.Beats % colors.Length];
                    c = col.Scale(Math.Clamp(Math.Max(a.Beat, a.Level * gain * 0.3), 0, 1));
                    break;
                }
            }
            output[i] = c.Scale(k);
        }
    }

    /// <summary>Static frames never change; everything else animates.</summary>
    public static bool Animated(EffectSettings e) => e.IsAudio || e.Kind is EffectKind.Breathing or EffectKind.Rainbow or EffectKind.ColorCycle or EffectKind.Temperature;
}

// ---------------------------------------------------------------- engine

/// <summary>
/// Applies a lighting profile to every device and renders synced effects on its own thread.
/// The UI reads <see cref="Frames"/> for live previews.
/// </summary>
public sealed class LightingEngine : IDisposable
{
    readonly Func<string, double?> _temperature;
    readonly object _lock = new();
    List<IRgbTarget> _targets = [];
    LightingProfile _profile = new();
    bool _enabled = true;
    readonly Dictionary<string, string> _applied = [];        // key -> what was last applied (to avoid resending)
    readonly Dictionary<string, (double at, Rgb[] frame)> _sent = [];
    readonly Thread _thread;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly ManualResetEventSlim _wake = new();
    volatile bool _stop;
    double _smoothTemp = double.NaN;
    string? _identify;
    double _identifyUntil;

    /// <summary>Last frame per device (copy-on-write; safe to read from any thread).</summary>
    public IReadOnlyDictionary<string, Rgb[]> Frames { get; private set; } = new Dictionary<string, Rgb[]>();
    public IReadOnlyList<IRgbTarget> Targets { get { lock (_lock) return [.. _targets]; } }
    public event Action? TargetsChanged;
    /// <summary>The effect clock. Previews use it so the app and the lights stay in step.</summary>
    public double Now => _clock.Elapsed.TotalSeconds;
    readonly AudioListener _audio = new();
    /// <summary>What the audio effects see right now (silent when no audio effect is active).</summary>
    public AudioFrame CurrentAudio => _audio.Current;

    /// <summary>The temperature the effect is using now (smoothed), when the effect is Temperature.</summary>
    public double? CurrentTemperature => double.IsNaN(_smoothTemp) ? null : _smoothTemp;

    public LightingEngine(Func<string, double?> temperature)
    {
        _temperature = temperature;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Lighting" };
        _thread.Start();
    }

    /// <summary>Every device the engine and native drivers offer, in a stable order.</summary>
    public static List<IRgbTarget> TargetsFrom(RgbEngine? engine, SeirenV3? seiren)
    {
        var list = new List<IRgbTarget>();
        if (engine?.Client is { } client) list.AddRange(engine.Devices.Select(d => (IRgbTarget)new OpenRgbTarget(client, d)));
        if (seiren is not null) list.Add(new SeirenTarget(seiren));
        return list;
    }

    public void SetTargets(IEnumerable<IRgbTarget> targets)
    {
        lock (_lock)
        {
            _targets = [.. targets];
            _applied.Clear();
            _sent.Clear();
        }
        TargetsChanged?.Invoke();
        _wake.Set();
    }

    /// <summary>Pulses one device bright white for 4 seconds so you can see which it is, then restores it.</summary>
    public void Identify(string key)
    {
        lock (_lock)
        {
            _identify = key;
            _identifyUntil = _clock.Elapsed.TotalSeconds + 4;
            _applied.Remove(key); // forces Direct mode now, and the profile back afterwards
        }
        _wake.Set();
    }

    public void Apply(LightingConfig config)
    {
        lock (_lock)
        {
            _profile = Clone(config.Current);
            _enabled = config.Enabled;
            // Listen only while an audio effect is lit (the microphone only when chosen; Windows shows it in use).
            var s = _profile.Sync;
            _audio.Listen(_enabled && s.IsAudio ? (s.AudioInput, s.AudioDevice) : null);
            _applied.Clear(); // re-apply modes; settings may have changed
            _sent.Clear();
        }
        _wake.Set();
    }

    static LightingProfile Clone(LightingProfile p) =>
        System.Text.Json.JsonSerializer.Deserialize<LightingProfile>(System.Text.Json.JsonSerializer.Serialize(p))!;

    void Loop()
    {
        while (!_stop)
        {
            double animatedFps = 0;
            try { animatedFps = Tick(); } catch (Exception e) { AppConfig.Log?.Invoke($"lighting: {e.Message}"); }
            _wake.Wait(animatedFps > 0 ? TimeSpan.FromSeconds(1 / animatedFps) : TimeSpan.FromSeconds(1));
            _wake.Reset();
        }
    }

    /// <summary>One pass over all devices. Returns the frame rate the fastest animated device needs (0 = none).</summary>
    double Tick()
    {
        // The whole pass holds the lock: Apply/SetTargets reset the bookkeeping it uses.
        lock (_lock) return TickLocked(_targets, _profile, _enabled);
    }

    double TickLocked(List<IRgbTarget> targets, LightingProfile profile, bool enabled)
    {
        if (_identify is not null && _clock.Elapsed.TotalSeconds > _identifyUntil)
        {
            _applied.Remove(_identify); // restore its profile setting (e.g. a hardware mode)
            _identify = null;
        }
        if (!enabled && _identify is null) return 0;

        double t = _clock.Elapsed.TotalSeconds, fps = 0;
        var sync = profile.Sync;
        double? temp = null;
        if (sync.Kind == EffectKind.Temperature && sync.Sensor is not null && _temperature(sync.Sensor) is double raw)
        {
            _smoothTemp = double.IsNaN(_smoothTemp) ? raw : _smoothTemp + (raw - _smoothTemp) * 0.15; // calm, no flicker
            temp = _smoothTemp;
        }

        var audio = sync.IsAudio ? _audio.Current : null;
        var frames = new Dictionary<string, Rgb[]>(Frames);
        foreach (var target in targets)
        {
            profile.Devices.TryGetValue(target.Key, out var dl);
            var source = dl?.Source ?? LightingSource.Sync;
            bool identify = target.Key == _identify;
            if (!enabled && !identify) continue;
            try
            {
                if (!identify && source == LightingSource.Hardware && dl!.Mode is string name)
                {
                    ApplyHardware(target, dl, name);
                    frames[target.Key] = HardwarePreview(target, dl);
                    continue;
                }

                if (_applied.GetValueOrDefault(target.Key) != "direct")
                {
                    target.EnterDirect();
                    _applied[target.Key] = "direct";
                    _sent.Remove(target.Key);
                }

                var frame = new Rgb[target.LedCount];
                if (identify) Array.Fill(frame, new Rgb(255, 255, 255).Scale(0.15 + 0.85 * Math.Abs(Math.Sin(t * Math.PI * 2))));
                else if (source == LightingSource.Sync)
                {
                    EffectMath.Render(sync, t, target.LedX, temp, frame, audio);
                    EffectMath.ApplyZones(frame, target.Zones, dl?.Zones);
                }
                frames[target.Key] = frame;

                bool animated = identify || (source == LightingSource.Sync && EffectMath.Animated(sync));
                if (animated) fps = Math.Max(fps, target.MaxFps);
                var last = _sent.GetValueOrDefault(target.Key);
                bool due = last.frame is null || t - last.at >= 1.0 / target.MaxFps;
                bool changed = last.frame is null || !last.frame.AsSpan().SequenceEqual(frame);
                // Send changes (rate-limited per device), and resend unchanged frames every few seconds in case a device reset.
                if ((changed && due) || t - last.at > 5)
                {
                    target.SetLeds(frame);
                    _sent[target.Key] = (t, frame);
                }
            }
            catch (Exception e) { AppConfig.Log?.Invoke($"lighting {target.Name}: {e.Message}"); }
        }
        Frames = frames;
        return fps;
    }

    void ApplyHardware(IRgbTarget target, DeviceLighting dl, string modeName)
    {
        var signature = $"hw:{modeName}:{string.Join(',', dl.Colors)}:{dl.Speed}:{dl.Brightness}:{dl.Direction}";
        if (_applied.GetValueOrDefault(target.Key) == signature) return;
        int index = target.Modes.ToList().FindIndex(m => m.Name == modeName);
        if (index < 0) return;
        var src = target.Modes[index];
        var mode = new RgbMode
        {
            Name = src.Name, Flags = src.Flags, SpeedMin = src.SpeedMin, SpeedMax = src.SpeedMax,
            BrightnessMin = src.BrightnessMin, BrightnessMax = src.BrightnessMax, ColorsMin = src.ColorsMin, ColorsMax = src.ColorsMax,
            Speed = dl.Speed ?? src.Speed, Brightness = dl.Brightness ?? src.Brightness, Direction = dl.Direction ?? src.Direction,
            ColorMode = src.ColorMode, Colors = [.. src.Colors],
        };
        if (dl.Colors.Count > 0 && src.Flags.HasFlag(ModeFlags.HasModeSpecificColor))
        {
            mode.ColorMode = ColorMode.ModeSpecific;
            mode.Colors = [.. dl.Colors.Take((int)Math.Max(1, src.ColorsMax)).Select(Rgb.Parse)];
        }
        target.ApplyMode(index, mode);
        _applied[target.Key] = signature;
        _sent.Remove(target.Key);
    }

    static Rgb[] HardwarePreview(IRgbTarget target, DeviceLighting dl)
    {
        var colors = dl.Colors.Count > 0 ? dl.Colors.Select(Rgb.Parse).ToArray() : [new Rgb(90, 90, 110)];
        return [.. Enumerable.Range(0, target.LedCount).Select(i => EffectMath.Gradient(colors, target.LedX.ElementAtOrDefault(i)))];
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(2000);
        _audio.Dispose();
    }
}

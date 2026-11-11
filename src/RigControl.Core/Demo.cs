namespace RigControl.Core;

/// <summary>
/// Made-up hardware for screenshots and demos: fans, sensors, a mouse, a headset, a keyboard and RGB devices with
/// generic names and no serials. Only the UI-test build can switch it on (RIGCONTROL_DEMO=1); nothing real is touched.
/// </summary>
public static class Demo
{
    public static bool On { get; set; }

    static readonly DateTime Start = DateTime.UtcNow;
    static double T => (DateTime.UtcNow - Start).TotalSeconds;

    // ---------------- Fans and sensors
    sealed class DemoFan(string id, string name, string device, double rpmAtFull) : FanChannel(id, name, device)
    {
        double _duty = 45;
        public override void Set(float percent) => _duty = percent;
        public override void Release() { }
        public void Tick()
        {
            Duty = (float)_duty;
            Rpm = (float)(_duty / 100 * rpmAtFull + Math.Sin(T * 1.3 + rpmAtFull) * 12);
            Connected = true;
        }
    }

    static readonly List<(TempSensor sensor, double baseTemp, double swing, double period)> Temps =
    [
        (new("/demo/cpu/temperature/0", "CPU Package", "CPU"), 52, 9, 47),
        (new("/demo/cpu/temperature/1", "Core Max", "CPU"), 55, 10, 47),
        (new("/demo/gpu/temperature/0", "GPU Core", "Graphics card"), 47, 7, 61),
        (new("/demo/gpu/temperature/1", "GPU Hot Spot", "Graphics card"), 58, 8, 61),
        (new("/demo/lpc/temperature/0", "System", "Motherboard"), 34, 2, 90),
        (new("/demo/lpc/temperature/1", "VRM", "Motherboard"), 46, 4, 75),
        (new("/demo/liquid/temperature/0", "Liquid", "AIO cooler"), 31, 1.5, 120),
        (new("/demo/nvme/temperature/0", "Composite", "NVMe SSD"), 39, 3, 100),
    ];

    static readonly List<DemoFan> Fans =
    [
        new("/demo/fan/0", "CPU fan", "Motherboard", 1900),
        new("/demo/fan/1", "Pump", "AIO cooler", 2800),
        new("/demo/fan/2", "Radiator 1", "AIO cooler", 1700),
        new("/demo/fan/3", "Radiator 2", "AIO cooler", 1700),
        new("/demo/fan/4", "Front 1", "Fan hub", 1500),
        new("/demo/fan/5", "Front 2", "Fan hub", 1500),
        new("/demo/fan/6", "Front 3", "Fan hub", 1500),
        new("/demo/fan/7", "Rear", "Motherboard", 1400),
    ];

    internal static void Fill(HardwareHub hub)
    {
        hub.Temps.AddRange(Temps.Select(t => t.sensor));
        hub.Fans.AddRange(Fans);
        Tick();
    }

    internal static void Tick()
    {
        foreach (var (s, b, swing, period) in Temps) s.Value = (float)(b + swing * Math.Sin(T * 2 * Math.PI / period));
        foreach (var f in Fans) f.Tick();
    }

    /// <summary>A config that puts the demo fans on curves, so the dashboard looks like a set-up PC.</summary>
    public static AppConfig Config() => new()
    {
        Curves =
        [
            new() { Name = "CPU", Source = "/demo/cpu/temperature/0", Points = [new(35, 25), new(60, 45), new(80, 100)], Deadband = 2, ResponseSeconds = 2 },
            new() { Name = "GPU", Source = "/demo/gpu/temperature/0", Points = [new(40, 30), new(65, 60), new(80, 100)], Deadband = 2, ResponseSeconds = 2 },
            new() { Name = "Liquid", Source = "/demo/liquid/temperature/0", Points = [new(28, 30), new(36, 70), new(40, 100)] },
            new() { Name = "Case", Kind = CurveKind.Mix, Inputs = ["CPU", "GPU"], Mix = MixFunction.Max },
        ],
        Fans = new()
        {
            ["/demo/fan/0"] = new() { Enabled = true, Curve = "CPU" },
            ["/demo/fan/1"] = new() { Enabled = true, Manual = 70 },
            ["/demo/fan/2"] = new() { Enabled = true, Curve = "Liquid" },
            ["/demo/fan/3"] = new() { Enabled = true, Curve = "Liquid" },
            ["/demo/fan/4"] = new() { Enabled = true, Curve = "Case" },
            ["/demo/fan/5"] = new() { Enabled = true, Curve = "Case" },
            ["/demo/fan/6"] = new() { Enabled = true, Curve = "Case" },
            ["/demo/fan/7"] = new() { Enabled = true, Curve = "Case" },
        },
    };

    // ---------------- Devices with settings
    sealed class DemoPeripheral(string key, string name, string kind, PeripheralCaps caps, PeripheralState state) : IPeripheral
    {
        PeripheralState _state = state;
        public string Key => key;
        public string Name => name;
        public string Kind => kind;
        public string Source => "demo device";
        public PeripheralCaps Caps => caps;
        public PeripheralState Read() => _state;
        public void Apply(PeripheralSettings s, PeripheralState now) => _state = _state with
        {
            Dpi = s.Dpi ?? _state.Dpi, Stages = s.Stages ?? _state.Stages, ActiveStage = s.ActiveStage ?? _state.ActiveStage,
            PollRate = s.PollRate ?? _state.PollRate, Sidetone = s.Sidetone ?? _state.Sidetone,
            ActuationMm = s.ActuationMm ?? _state.ActuationMm, RapidTrigger = s.RapidTrigger ?? _state.RapidTrigger,
            LightBrightness = s.LightBrightness ?? _state.LightBrightness, LightEffect = s.LightEffect ?? _state.LightEffect,
            LightSpeed = s.LightSpeed ?? _state.LightSpeed, LightHue = s.LightHue ?? _state.LightHue, LightSaturation = s.LightSaturation ?? _state.LightSaturation,
        };
        public void Dispose() { }
    }

    internal static List<IPeripheral> Peripherals() =>
    [
        new DemoPeripheral("demo/mouse", "Wireless gaming mouse", "Mouse",
            new PeripheralCaps { DpiMin = 100, DpiMax = 26000, MaxStages = 5, PollRates = [125, 500, 1000, 2000, 4000], SleepTimeout = true, LowBatteryThreshold = true, Battery = true, Charging = true },
            new PeripheralState { Dpi = 1600, Stages = [400, 800, 1600, 3200, 6400], ActiveStage = 3, PollRate = 1000, SleepSeconds = 300, LowBatteryPercent = 10, BatteryPercent = 82, Charging = false }),
        new DemoPeripheral("demo/headset", "Wireless headset", "Headset",
            new PeripheralCaps { Battery = true, Sidetone = true, InactiveMaxMinutes = 90, Lights = true, VoicePrompts = true, MicVolume = true, Chatmix = true, EqPresets = ["flat", "bass boost", "focus", "smiley"] },
            new PeripheralState { Connected = true, BatteryPercent = 64, Charging = false, Sidetone = 40, Chatmix = 64 }),
        new DemoPeripheral("demo/keyboard", "Hall-effect keyboard", "Keyboard",
            new PeripheralCaps { ActuationMm = [.. Enumerable.Range(1, 40).Select(t => t / 10.0)], RapidTrigger = true, Lighting = "RGB matrix", LightingEffects = true, LightingSpeed = true, LightingColor = true },
            new PeripheralState { ActuationMm = 1.2, RapidTrigger = true, LightBrightness = 200, LightEffect = 4, LightSpeed = 128, LightHue = 180, LightSaturation = 255 }),
    ];

    // ---------------- RGB devices
    sealed class DemoRgb(string key, string name, RgbDeviceType type, int leds) : IRgbTarget
    {
        static readonly RgbMode[] ModeList = [new() { Name = "Direct", Flags = ModeFlags.HasPerLedColor, ColorMode = ColorMode.PerLed }, new() { Name = "Off" }];
        public string Key => key;
        public string Name => name;
        public string Tag => "";
        public RgbDeviceType Type => type;
        public IReadOnlyList<RgbMode> Modes => ModeList;
        public IReadOnlyList<RgbZone> Zones { get; } = [new RgbZone { Name = "LEDs", Type = 1, LedsCount = (uint)leds, LedsMin = (uint)leds, LedsMax = (uint)leds }];
        public int LedCount => leds;
        public float[] LedX { get; } = [.. Enumerable.Range(0, leds).Select(i => i / (float)Math.Max(1, leds - 1))];
        public int MaxFps => 30;
        public void EnterDirect() { }
        public void SetLeds(Rgb[] colors) { }
        public void ApplyMode(int index, RgbMode mode) { }
    }

    public static IEnumerable<IRgbTarget> RgbDevices() =>
    [
        new DemoRgb("demo/rgb/ram1", "RGB memory", RgbDeviceType.Dram, 8),
        new DemoRgb("demo/rgb/ram2", "RGB memory", RgbDeviceType.Dram, 8),
        new DemoRgb("demo/rgb/gpu", "Graphics card", RgbDeviceType.Gpu, 12),
        new DemoRgb("demo/rgb/board", "Motherboard", RgbDeviceType.Motherboard, 16),
        new DemoRgb("demo/rgb/fans", "Case fans (ARGB)", RgbDeviceType.Cooler, 48),
        new DemoRgb("demo/rgb/kb", "Keyboard", RgbDeviceType.Keyboard, 104),
    ];
}

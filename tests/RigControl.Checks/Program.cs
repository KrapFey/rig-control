using RigControl.Core;

if (args.Contains("--probe")) { Probe(); return 0; }
if (args.Contains("--razer"))
{
    // Read-only: what each Razer mouse reports. Never writes a setting.
    foreach (var p in RazerMouse.FindAll())
    {
        var st = p.Read();
        if (args.Contains("--stages") && p is RazerMouse rm)
            Console.WriteLine(rm.ProbeStages() is { } probe ? $"   stage table (read-only probe, tid {probe.tid}): active {probe.active} of [{string.Join(", ", probe.stages)}]" : "   no stage table answered");
        Console.WriteLine($"{p.Key} {p.Name} | caps dpi<= {p.Caps.DpiMax} stages {p.Caps.MaxStages} poll [{string.Join(",", p.Caps.PollRates)}] sleep {p.Caps.SleepTimeout} battery {p.Caps.Battery}");
        Console.WriteLine($"   now: dpi {st.Dpi} stages [{string.Join(",", st.Stages ?? [])}] active {st.ActiveStage} poll {st.PollRate} sleep {st.SleepSeconds} lowbat {st.LowBatteryPercent} battery {st.BatteryPercent} charging {st.Charging}");
        p.Dispose();
    }
    return 0;
}
if (args.Contains("--logitech"))
{
    // Read-only: every HID++ 2.0 mouse or keyboard found and what it reports. Never writes a setting.
    var all = LogitechDevice.FindAll();
    if (all.Count == 0) Console.WriteLine("no Logitech HID++ 2.0 mouse or keyboard found");
    foreach (var p in all)
    {
        var st = p.Read();
        Console.WriteLine($"{p.Key} {p.Name} ({p.Kind}) | dpi {p.Caps.DpiMin}-{p.Caps.DpiMax} step {p.Caps.DpiStep} rates [{string.Join(",", p.Caps.PollRates)}] battery {p.Caps.Battery}");
        Console.WriteLine($"   now: dpi {st.Dpi} rate {st.PollRate} battery {st.BatteryPercent} charging {st.Charging}");
        p.Dispose();
    }
    return 0;
}
if (args.Contains("--via"))
{
    // Read-only: VIA keyboards found and their lighting. Never writes a setting.
    var all = ViaKeyboard.FindAll();
    if (all.Count == 0) Console.WriteLine("no VIA keyboard (protocol 12+) with lighting found");
    foreach (var p in all)
    {
        var st = p.Read();
        Console.WriteLine($"{p.Key} {p.Name} | {p.Source} | brightness {st.LightBrightness} effect {st.LightEffect} speed {st.LightSpeed} hue {st.LightHue} sat {st.LightSaturation}");
        p.Dispose();
    }
    return 0;
}
if (args.Contains("--rgb"))
{
    // Read-only: list what an OpenRGB server reports (default port 6742).
    var port = args.SkipWhile(a => a != "--port").Skip(1).Select(int.Parse).FirstOrDefault(6742);
    using var orgb = new OpenRgbClient();
    orgb.ConnectAsync("127.0.0.1", port, "RigControl checks").Wait();
    Console.WriteLine($"server protocol {orgb.ServerProtocol}");
    foreach (var d in orgb.GetDevicesAsync().Result)
    {
        Console.WriteLine($"[{d.Index}] {d.Type} | {d.Name} | {d.Vendor} | leds {d.Leds.Count} | active {d.Modes.ElementAtOrDefault(d.ActiveMode)?.Name} | direct #{d.DirectModeIndex} | serial '{d.Serial}' | location '{d.Location}'");
        Console.WriteLine($"     modes: {string.Join(", ", d.Modes.Select(m => m.Name))}");
        Console.WriteLine($"     zones: {string.Join(", ", d.Zones.Select(z => $"{z.Name}({z.LedsCount}{(z.Matrix is null ? "" : $" {z.MatrixWidth}x{z.MatrixHeight}")})"))}");
    }
    return 0;
}
if (args.Contains("--lqprobe"))
{
    // Read-only: would the liquidctl helper start on this PC with no driver overrides?
    var m = typeof(HardwareHub).Assembly.GetType("RigControl.Core.LiquidctlBridge")!.GetMethod("CandidatePresent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    Console.WriteLine($"candidate present: {m.Invoke(null, [Hue2Device.Supported.ToHashSet()])}");
    return 0;
}
if (args.Contains("--hid")) { RawHid(); return 0; }
if (args.Contains("--lhm"))
{
    // Read-only (needs admin): what the motherboard chip itself reports for its PWM controls and fan speeds.
    var pc = new LibreHardwareMonitor.Hardware.Computer { IsMotherboardEnabled = true };
    pc.Open();
    var chips = pc.Hardware.SelectMany(h => h.SubHardware).ToList();
    int seconds = args.Contains("--long") ? 25 : 6;
    for (int t = 0; t < seconds; t++)
    {
        foreach (var chip in chips)
        {
            chip.Update();
            var ctl = chip.Sensors.Where(s => s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Control).Select(s => $"{s.Index}:{s.Value:0}%({s.Control?.ControlMode})");
            var rpm = chip.Sensors.Where(s => s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Fan).Select(s => $"{s.Index}:{s.Value:0}");
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} {chip.Name}  pwm {string.Join(" ", ctl)}  rpm {string.Join(" ", rpm)}");
        }
        Thread.Sleep(1000);
    }
    pc.Close(); // Close() only restores controls this instance changed: none
    return 0;
}
if (args.Contains("--watch"))
{
    // Read-only: duty/rpm of every HUE 2 controller once a second for 8 s.
    var streams = HidSharp.DeviceList.Local.GetHidDevices(0x1E71).Where(d => d.GetMaxInputReportLength() == 64)
        .Select(d => (tag: d.GetSerialNumber()[^4..], hs: d.Open())).ToList();
    for (int t = 0; t < 8; t++)
    {
        var row = new List<string>();
        foreach (var (tag, hs) in streams)
        {
            hs.ReadTimeout = 700;
            string v = "?";
            for (int k = 0; k < 6; k++)
            {
                try { var m = hs.Read(); if (m[0] == 0x67 && m[1] == 0x02) { v = $"{m[40]}/{m[41]}/{m[42]}% {m[24] | m[25] << 8}/{m[26] | m[27] << 8}/{m[28] | m[29] << 8}rpm"; break; } }
                catch (TimeoutException) { break; }
            }
            row.Add($"{tag} {v}");
        }
        Console.WriteLine($"{t}s  " + string.Join("   ", row));
        Thread.Sleep(300);
    }
    foreach (var (_, hs) in streams) hs.Dispose();
    return 0;
}

int failed = 0;
void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); if (!ok) failed++; }
bool Near(double a, double b) => Math.Abs(a - b) < 0.01;

// --- curve math
List<CurvePoint> line = [new(35, 30), new(85, 100)];
Check(Near(CurveMath.Interpolate(line, 20), 30), "below first point -> first percent");
Check(Near(CurveMath.Interpolate(line, 60), 65), "midpoint interpolates");
Check(Near(CurveMath.Interpolate(line, 99), 100), "above last point -> last percent");
Check(Near(CurveMath.Interpolate([], 50), 100), "no points -> 100 (fail safe)");

// --- fan step logic
var s = new FanSettings { StepUp = 8, StepDown = 8, StartPercent = 16, StopPercent = 7, MinPercent = 0 };
Check(Near(CurveMath.NextDuty(100, 30, s), 38), "step up limited to +8");
Check(Near(CurveMath.NextDuty(0, 30, s), 22), "step down limited to -8");
Check(Near(CurveMath.NextDuty(5, null, s), 0), "below stop -> 0");
Check(Near(CurveMath.NextDuty(10, 0, s), 16), "spin-up from 0 kicks to start percent");
Check(Near(CurveMath.NextDuty(10, null, new FanSettings { MinPercent = 25 }), 25), "min percent clamps");

// --- hysteresis: 3 degree deadband, 2 s response
var h = new Hysteresis();
Check(Near(h.Apply(50, 0, 3, 2, 85), 50), "first reading is used");
Check(Near(h.Apply(52, 1, 3, 2, 85), 50), "inside deadband ignored");
Check(Near(h.Apply(54, 2, 3, 2, 85), 50), "outside deadband waits for response time");
Check(Near(h.Apply(54, 4.1, 3, 2, 85), 54), "...then applies");
Check(Near(h.Apply(90, 4.2, 3, 2, 85), 90), "at/above curve top applies at once");

// --- FanControl import
var hw = new HardwareCatalog(
    [("/intelcpu/0/temperature/18", "CPU Package", "Intel Core i9"), ("/gpu-nvidia/0/temperature/0", "GPU Core", "RTX")],
    [
        ("/lpc/it8689e/0/control/0", "Fan #1", "ITE IT8689E"), // LHM 0.9.6 id format; FanControl wrote /lpc/it8689e/control/0
        ("/gpu-nvidia/0/control/1", "GPU Fan 1", "RTX"),
        ("nzxt-hue2/AAAA/fan/1", "Fan 1", "NZXT RGB & Fan Controller (3+6 channels) · AAAA"),
        ("nzxt-hue2/AAAA/fan/2", "Fan 2", "NZXT RGB & Fan Controller (3+6 channels) · AAAA"),
        ("nzxt-hue2/BBBB/fan/1", "Fan 1", "NZXT RGB & Fan Controller (3+6 channels) · BBBB"),
    ]);
var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fancontrol-sample.json"));
var (cfg, notes) = FanControlImport.Import(json, hw);
foreach (var n in notes) Console.WriteLine($"     note: {n}");

var cpu = cfg.Curves.Single(c => c.Name == "CPU");
Check(cpu.Kind == CurveKind.Graph && cpu.Source == "/intelcpu/0/temperature/18", "linear curve -> graph on same LHM sensor");
Check(cpu.Points.SequenceEqual([new(35, 30), new(85, 100)]) && cpu.Deadband == 3 && cpu.ResponseSeconds == 2, "linear curve points, deadband, response");
Check(cfg.Curves.Single(c => c.Name == "GPU").Source == "/gpu-nvidia/0/temperature/0", "NVApiWrapper sensor -> LHM GPU Core");
var mix = cfg.Curves.Single(c => c.Name == "Case");
Check(mix.Kind == CurveKind.Mix && mix.Inputs.SequenceEqual(["CPU", "GPU"]) && mix.Mix == MixFunction.Max, "mix curve");
Check(cfg.Curves.Single(c => c.Name == "Pts").Points.Count == 3, "graph curve points parsed");

Check(cfg.Fans["/lpc/it8689e/0/control/0"] is { Name: "CPU Front", Curve: "CPU", Enabled: true, StartPercent: 16 }, "LHM fan carried over");
Check(cfg.Fans["nzxt-hue2/AAAA/fan/2"] is { Name: "Front Mid", Curve: "Case" }, "liquidctl device #1 -> first NZXT controller");
Check(cfg.Fans["nzxt-hue2/BBBB/fan/1"] is { Name: "Bottom Front", Curve: "GPU" }, "liquidctl device #2 -> second NZXT controller");
Check(cfg.Fans["/gpu-nvidia/0/control/1"] is { Enabled: false }, "disabled control stays disabled");
Check(notes.Any(n => n.Contains("identical")), "ambiguous controllers are flagged");
Check(!cfg.Fans.ContainsKey("liquidctl/Missing#1/Fan1duty") && notes.Any(n => n.Contains("Ghost")), "unknown fan skipped with a note");

// --- FanControl.Liquidctl stable ids (made-up serials/paths, ids computed with the plugin's formula; HidSharp gives the path lower-case)
const string Hue = "NZXT RGB & Fan Controller (3+6 channels)";
Check(Hue2Device.LiquidctlStableId(Hue, "0123456789ABCDEF0123456789ABCDEF",
    @"\\?\hid#vid_1e71&pid_2020#7&1a2b3c4d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}") == 1660399584, "liquidctl id, controller A");
Check(Hue2Device.LiquidctlStableId(Hue, "FEDCBA9876543210FEDCBA98",
    @"\\?\hid#vid_1e71&pid_2011#8&5e6f7a8b&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}") == 1655589662, "liquidctl id, controller B");

// aliases beat order-matching for identical controllers
var exact = FanControlImport.Import(json, hw with
{
    Aliases = new Dictionary<string, string>
    {
        ["liquidctl/NZXTRGB&FanController(3+6channels)#1660399584/Fan2duty"] = "nzxt-hue2/BBBB/fan/1",
        ["liquidctl/NZXTRGB&FanController(3+6channels)#1700000001/Fan1duty"] = "nzxt-hue2/AAAA/fan/2",
    },
});
Check(exact.Config.Fans["nzxt-hue2/BBBB/fan/1"].Name == "Front Mid" && exact.Config.Fans["nzxt-hue2/AAAA/fan/2"].Name == "Bottom Front", "liquidctl alias maps exactly");

// --- switching a HUE 2 controller between drivers keeps its fan settings
var sw = new AppConfig();
sw.Fans["nzxt-hue2/SER/fan/2"] = new FanSettings { Name = "Top Front", Curve = "Case" };
sw.Fans["/lpc/x/control/0"] = new FanSettings { Name = "CPU" };
sw.MoveHue2Settings("SER", toLiquidctl: true);
Check(sw.Fans.ContainsKey("liquidctl/SER/fan2") && !sw.Fans.ContainsKey("nzxt-hue2/SER/fan/2") && sw.Fans["liquidctl/SER/fan2"].Name == "Top Front", "HUE 2 settings move to liquidctl ids");
sw.MoveHue2Settings("SER", toLiquidctl: false);
Check(sw.Fans["nzxt-hue2/SER/fan/2"].Curve == "Case" && sw.Fans.Count == 2, "...and back, other fans untouched");

// --- the liquidctl helper's own self-test, when the runtime has been built (tools\fetch-liquidctl.ps1)
var runtime = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "build", "liquidctl"));
if (File.Exists(Path.Combine(runtime, "bridge.py")))
{
    var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(runtime, "python", "python.exe"), ["-I", "-B", Path.Combine(runtime, "bridge.py"), "--selftest"])
        { RedirectStandardOutput = true, RedirectStandardError = true };
    using var py = System.Diagnostics.Process.Start(psi)!;
    var outText = py.StandardOutput.ReadToEnd() + py.StandardError.ReadToEnd();
    py.WaitForExit();
    Check(py.ExitCode == 0, "liquidctl bridge selftest" + (py.ExitCode == 0 ? "" : $": {outText.Trim()}"));
}
else Console.WriteLine("skip liquidctl bridge selftest (runtime not built)");

// --- lighting effects
Check(EffectMath.Gradient([new(0, 0, 0), new(200, 100, 50)], 0.5) == new Rgb(100, 50, 25), "gradient midpoint");
Check(Math.Abs(EffectMath.Period(0) - 10) < 1e-9 && Math.Abs(EffectMath.Period(100) - 1) < 1e-9, "speed 0..100 -> 10 s..1 s per cycle");
var px = new Rgb[3];
EffectMath.Render(new EffectSettings { Kind = EffectKind.Static, Colors = ["#FF0000", "#0000FF"], Brightness = 50 }, 0, [0f, 0.5f, 1f], null, px);
Check(px[0] == new Rgb(127, 0, 0) && px[2] == new Rgb(0, 0, 127), "static gradient across LEDs, scaled by brightness");
var heat = new EffectSettings { Kind = EffectKind.Temperature, Colors = ["#0000FF", "#FF0000"], ColdTemp = 40, HotTemp = 80 };
EffectMath.Render(heat, 0, [0f], 40, px.AsSpan(0, 1)); var cold = px[0];
EffectMath.Render(heat, 0, [0f], 80, px.AsSpan(0, 1)); var hot = px[0];
EffectMath.Render(heat, 0, [0f], null, px.AsSpan(0, 1));
Check(cold == new Rgb(0, 0, 255) && hot == new Rgb(255, 0, 0) && px[0] == hot, "temperature: cold/hot ends, missing sensor shows hot");
EffectMath.Render(new EffectSettings { Kind = EffectKind.Rainbow }, 0, [0f, 0.5f], null, px.AsSpan(0, 2));
Check(px[0] != px[1], "rainbow wave varies along the LEDs");
Check(EffectMath.Animated(new EffectSettings { Kind = EffectKind.Rainbow }) && !EffectMath.Animated(new EffectSettings { Kind = EffectKind.Static }), "only moving effects stream frames");

// --- Razer device table (generated from openrazer)
var da2 = RazerMouse.Describe(0x0084);
Check(da2 is { name: "Razer DeathAdder V2", dpiMax: 20000 } && da2.Value.features.Contains("dpi_write") && da2.Value.features.Contains("poll_read"), "Razer table: DeathAdder V2 with DPI and polling");
Check(RazerMouse.Describe(0x056F) is null, "Razer table: the Seiren (a mic) isn't treated as a mouse");

// --- headsets, against HeadsetControl's simulated device (when build\headsetcontrol exists)
var hsExe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "build", "headsetcontrol", "headsetcontrol.exe"));
if (File.Exists(hsExe))
{
    HeadsetControlDevice.ExeOverride = hsExe;
    HeadsetControlDevice.UseTestDevice = true;
    var heads = HeadsetControlDevice.FindAll();
    var head = heads.FirstOrDefault();
    Check(heads.Count == 1 && head is { Kind: "Headset" } && head.Caps is { Sidetone: true, Battery: true, InactiveMaxMinutes: 90, EqPresets.Length: 4 }, "headset: test device found with its capabilities");
    var hs = head?.Read();
    Check(hs is { BatteryPercent: 42, Connected: true, Sidetone: 85, Chatmix: 64 }, "headset: battery, sidetone and chat-mix read");
    head?.Apply(new PeripheralSettings { Sidetone = 40, InactiveMinutes = 30, EqPreset = 1, Lights = false }, hs!);
    Check(true, "headset: settings applied without error");
    HeadsetControlDevice.UseTestDevice = false;
}
else Console.WriteLine("skip headset checks (HeadsetControl not fetched)");

// --- Logitech HID++ 2.0, against a simulated mouse
Check(LogitechDevice.ParseDpiList([0x00, 0x64, 0xE0, 0x32, 0x01, 0x2C, 0x00, 0x00]).SequenceEqual([100, 150, 200, 250, 300]), "HID++ DPI list: step ranges expand");
Check(LogitechDevice.ParseDpiList([0x01, 0x90, 0x03, 0x20, 0x06, 0x40, 0x00, 0x00, 0x12, 0x34]).SequenceEqual([400, 800, 1600]), "HID++ DPI list: plain values, stops at 0");
{
    const string simName = "Simulated Wireless Gaming Mouse"; // longer than one 16-byte name chunk
    int simDpi = 800, simMs = 1, deviceType = 3;
    var writes = new List<string>();
    ushort[] simFeatures = [0x0000, 0x0005, 0x1004, 0x2201, 0x8060];
    LogitechDevice.Transport sim = req =>
    {
        if (req[1] != 0xFF) return null; // nothing paired elsewhere
        var rep = new byte[20];
        Array.Copy(req, rep, 4);
        var p = rep.AsSpan(4);
        int fn = req[3] >> 4;
        switch (simFeatures.ElementAtOrDefault(req[2]))
        {
            case 0x0000 when fn == 1: p[0] = 4; p[1] = 2; p[2] = req[6]; break; // protocol 4.2, ping echoed
            case 0x0000: p[0] = (byte)Math.Max(0, Array.IndexOf(simFeatures, (ushort)(req[4] << 8 | req[5]))); break;
            case 0x0005 when fn == 0: p[0] = (byte)simName.Length; break;
            case 0x0005 when fn == 1: System.Text.Encoding.ASCII.GetBytes(simName[req[4]..][..Math.Min(16, simName.Length - req[4])]).CopyTo(p); break;
            case 0x0005: p[0] = (byte)deviceType; break;
            case 0x1004 when fn == 1: p[0] = 63; p[2] = 1; break; // 63 %, charging
            case 0x2201 when fn == 1: new byte[] { 0, 0x00, 0x64, 0xE0, 0x32, 0x64, 0x00, 0, 0 }.CopyTo(p); break; // 100..25600 step 50
            case 0x2201 when fn == 2: p[1] = (byte)(simDpi >> 8); p[2] = (byte)simDpi; break;
            case 0x2201 when fn == 3: simDpi = req[5] << 8 | req[6]; writes.Add($"dpi {simDpi}"); break;
            case 0x8060 when fn == 0: p[0] = 0b1011; break; // 1, 2 and 4 ms
            case 0x8060 when fn == 1: p[0] = (byte)simMs; break;
            case 0x8060 when fn == 2: simMs = req[4]; writes.Add($"rate {simMs}ms"); break;
            default: rep[2] = 0xFF; rep[3] = req[2]; rep[4] = req[3]; rep[5] = 0x02; break; // ERR_INVALID_ARGUMENT
        }
        return rep;
    };
    var lg = LogitechDevice.Open(sim, 0xFF);
    Check(lg is { Name: simName, Kind: "Mouse", Key: "logitech/simulated-wireless-gaming-mouse" }, "Logitech: name over several chunks, kind, stable key");
    Check(lg?.Caps is { DpiMin: 100, DpiMax: 25600, DpiStep: 50, Battery: true } && lg.Caps.PollRates.SequenceEqual([250, 500, 1000]), "Logitech: DPI range, step and report rates from the device");
    var ls = lg?.Read();
    Check(ls is { Dpi: 800, PollRate: 1000, BatteryPercent: 63, Charging: true }, "Logitech: DPI, report rate and battery read");
    lg?.Apply(new PeripheralSettings { Dpi = 1234, PollRate = 500 }, ls!);
    Check(writes.SequenceEqual(["dpi 1250", "rate 2ms"]), "Logitech: DPI snapped to the sensor's list, rate written as ms");
    lg?.Apply(new PeripheralSettings { Dpi = 1250, PollRate = 500 }, lg.Read());
    Check(writes.Count == 2, "Logitech: nothing re-sent when the device already matches");
    Check(LogitechDevice.Open(sim, 1) is null, "Logitech: empty receiver slot -> no device");
    deviceType = 8;
    Check(LogitechDevice.Open(sim, 0xFF) is null, "Logitech: headsets are left to HeadsetControl");
}

// --- SteelSeries Apex Pro keyboards, against simulated boards
{
    // Gen 3: per-key reads answer {hid, …}; 0x31 writes {hid, press, release}. ISO/JIS-only keys report "no magnet".
    var points = new Dictionary<byte, (byte press, byte release)>();
    int writes = 0;
    byte[] notFitted = [0x89, 0x64, 0x32, 0x87, 0x88, 0x8A, 0x8B];
    SteelSeriesKeyboard.Transport gen3 = (req, valid) =>
    {
        int cmd = req[1], n = req[2];
        if (cmd == 0x31) { for (int i = 0; i < n; i++) points[req[3 + i * 3]] = (req[4 + i * 3], req[5 + i * 3]); writes++; return null; }
        int stride = cmd == 0xDA ? 5 : 3;
        var reply = new byte[64];
        for (int i = 0; i < n; i++)
        {
            byte hid = req[3 + i * stride];
            reply[i * stride] = hid;
            if (cmd == 0xDA)
            {
                (int top, int rest) = notFitted.Contains(hid) ? (2900, 928) : (3000, 1200);
                reply[i * stride + 1] = (byte)top; reply[i * stride + 2] = (byte)(top >> 8);
                reply[i * stride + 3] = (byte)rest; reply[i * stride + 4] = (byte)(rest >> 8);
            }
            else { var (press, release) = points.TryGetValue(hid, out var p) ? p : ((byte)50, (byte)46); reply[i * stride + 1] = press; reply[i * stride + 2] = release; }
        }
        return valid is null || valid(reply) ? reply : null;
    };
    var g3 = new SteelSeriesKeyboard(SteelSeriesKeyboard.Family.Gen3, gen3);
    Check(g3.Caps.ActuationMm.Length == 20 && !g3.Caps.RapidTrigger, "Apex Pro Gen 3: 20 firmware actuation levels, no rapid trigger claimed");
    Check(g3.Read().ActuationMm == 1.8, "Apex Pro Gen 3: actuation read back as the nearest firmware level (raw 50 -> 1.8 mm)");
    Check(SteelSeriesKeyboard.Gen3Level(0.1) == (5, 5) && SteelSeriesKeyboard.Gen3Level(4.0) == (215, 210) && SteelSeriesKeyboard.Gen3Level(1.0) == (23, 21),
        "Apex Pro Gen 3: mm -> press/release points match apex-analog's table");
    g3.Apply(new PeripheralSettings { ActuationMm = 1.0 }, g3.Read());
    Check(writes == 3 && points.Count == 60 && points.Values.All(p => p == (23, 21)) && !points.ContainsKey(0xE3) && notFitted.All(h => !points.ContainsKey(h)),
        "Apex Pro Gen 3: every fitted key set (20 per report), LGUI and missing keys untouched");
    Check(g3.Read().ActuationMm == 1.0, "Apex Pro Gen 3: the new depth reads back");
    g3.Apply(new PeripheralSettings { ActuationMm = 1.0 }, g3.Read());
    Check(writes == 3, "Apex Pro Gen 3: nothing re-sent when it already matches");

    // TKL (2023): write-only feature frames.
    var frames = new List<byte[]>();
    var tkl = new SteelSeriesKeyboard(SteelSeriesKeyboard.Family.Tkl2023, (req, _) => { frames.Add(req); return null; });
    tkl.Apply(new PeripheralSettings { ActuationMm = 0.5, RapidTrigger = true }, tkl.Read());
    Check(frames.Count == 2, "Apex Pro TKL 2023: one actuation frame and one rapid-trigger frame");
    var f = frames[0];
    Check(f[0] == 0 && f[1] == 0x38 && f[2] == 0x61 && f[3] == 68 && f[4] == 0 && f[5] == 0x35 && f[6] == 10 && f[7] == 8 && f.Length == 5 + 68 * 3,
        "Apex Pro TKL 2023: 00 38 61 44 00 {hid, 10, 8}×68 for 0.5 mm");
    Check(frames[1][1] == 0x38 && frames[1][2] == 0x62 && frames[1][3] == 68 && frames[1][5] == 2 && frames[1].Length == 4 + 68 * 2, "Apex Pro TKL 2023: rapid trigger as release mode 2 on all 68 keys");
}

// --- VIA keyboards, against a simulated QMK board
{
    int protocol = 12;
    byte[] channels = [3]; // the board has RGB matrix only
    var values = new Dictionary<int, byte[]> { [1] = [200], [2] = [5], [3] = [128], [4] = [10, 255] };
    var sets = new List<string>();
    ViaKeyboard.Transport via = msg =>
    {
        var r = (byte[])msg.Clone();
        switch (msg[0])
        {
            case 0x01: r[1] = (byte)(protocol >> 8); r[2] = (byte)protocol; break;
            case 0x08 when channels.Contains(msg[1]): values[msg[2]].CopyTo(r, 3); break;
            case 0x07 when channels.Contains(msg[1]): values[msg[2]] = msg[3..5]; sets.Add($"{msg[2]}:{msg[3]}"); break;
            default: r[0] = 0xFF; break; // unhandled
        }
        return r;
    };
    var vk = ViaKeyboard.Open(via, "via/AAAA0001", "Test Board");
    Check(vk?.Caps is { Lighting: "RGB matrix", LightingEffects: true, LightingColor: true } && vk.Kind == "Keyboard", "VIA: lighting channel found by asking the board");
    var vs = vk?.Read();
    Check(vs is { LightBrightness: 200, LightEffect: 5, LightSpeed: 128, LightHue: 10, LightSaturation: 255 }, "VIA: brightness, effect, speed and colour read");
    vk?.Apply(new PeripheralSettings { LightBrightness = 100, LightHue = 170 }, vs!);
    Check(sets.SequenceEqual(["1:100", "4:170"]) && values[4][1] == 255, "VIA: only changed values sent; saturation kept when only the hue changes");
    channels = [1];
    Check(ViaKeyboard.Open(via, "k", "n")?.Caps is { Lighting: "Backlight", LightingEffects: false, LightingColor: false }, "VIA: backlight-only board gets brightness only");
    channels = [];
    Check(ViaKeyboard.Open(via, "k", "n") is null, "VIA: board without lighting isn't listed");
    channels = [3]; protocol = 11;
    Check(ViaKeyboard.Open(via, "k", "n") is null, "VIA: protocol before 12 (different lighting commands) left alone");
}

// --- SteelSeries mice through rivalcfg: C# mapping against a stand-in helper
{
    var calls = new List<string[]>();
    SteelSeriesMouse.RunOverride = args =>
    {
        calls.Add(args);
        return args[0] switch
        {
            "list" => """[{"vid": 4152, "pid": 6180, "name": "SteelSeries Rival 3", "stages": 5, "presets": "list", "dpi": [200, 8500, 100], "poll": [125, 250, 500, 1000], "sleep": null, "battery": false}, {"vid": 4152, "pid": 6266, "name": "SteelSeries Aerox 3 Wireless (wired mode)", "stages": 5, "presets": "list", "dpi": [100, 18000, 100], "poll": [125, 1000], "sleep": [0, 20], "battery": true}]""",
            "battery" => """{"level": 77, "charging": true}""",
            _ => """{"ok": true, "errors": {}}""",
        };
    };
    var ss = SteelSeriesMouse.FindAll();
    var rival = ss.FirstOrDefault(); var aerox = ss.LastOrDefault();
    Check(ss.Count == 2 && rival is { Key: "steelseries/1824", Kind: "Mouse" } && rival.Caps is { DpiMin: 200, DpiMax: 8500, MaxStages: 5, Battery: false }, "SteelSeries mouse: caps from rivalcfg's profile");
    Check(aerox?.Caps is { SleepTimeout: true, SleepMaxSeconds: 1200, Battery: true } && aerox.Read() is { BatteryPercent: 77, Charging: true }, "SteelSeries mouse: sleep timer and battery on the wireless model");
    rival?.Apply(new PeripheralSettings { Stages = [400, 9999], ActiveStage = 2, PollRate = 500 }, rival.Read());
    var applied = calls.Last();
    Check(applied[0] == "apply" && applied[2] == "6180" && applied[3] == """{"stages":[400,8500],"active":2,"poll":500}""", "SteelSeries mouse: presets clamped to the sensor, active preset and polling sent");
    SteelSeriesMouse.RunOverride = null;
}
// ... and the real rivalcfg in the bundled Python, in its dry-run mode (fake device, nothing sent)
var rivalPy = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "build", "liquidctl"));
if (File.Exists(Path.Combine(rivalPy, "rival.py")))
{
    string RivalDry(string profile, params string[] a)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(rivalPy, "python", "python.exe")) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        psi.Environment["RIVALCFG_DRY"] = "1";
        psi.Environment["RIVALCFG_PROFILE"] = profile;
        foreach (var x in new[] { "-I", Path.Combine(rivalPy, "rival.py") }.Concat(a)) psi.ArgumentList.Add(x);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd(); p.WaitForExit();
        return o.Trim();
    }
    const string want = """{"stages":[400,800,1600],"active":2,"poll":500,"sleep":10}""";
    Check(RivalDry("1038:1824", "list").Contains("\"presets\": \"list\"") && RivalDry("1038:1824", "apply", "4152", "6180", want) == """{"ok": true, "errors": {}}""", "rivalcfg (dry run): Rival 3 takes five presets and polling");
    Check(RivalDry("1038:1729", "list").Contains("\"presets\": \"pair\"") && RivalDry("1038:1729", "apply", "4152", "5929", want) == """{"ok": true, "errors": {}}""", "rivalcfg (dry run): Rival 110 takes its two presets one by one");
    Check(RivalDry("1038:187a", "list").Contains("\"sleep\": [0, 20]") && RivalDry("1038:187a", "apply", "4152", "6266", want) == """{"ok": true, "errors": {}}""", "rivalcfg (dry run): Aerox 3 Wireless takes the sleep timer");
}
else Console.WriteLine("skip rivalcfg checks (liquidctl runtime not fetched)");

// --- ASUS mice (libratbag's protocol), against a simulated mouse
{
    (AsusMouse mouse, List<string> log, int[] raw) Sim(int pid, int presets, int[] startRaw, int rate, int active)
    {
        var raw = (int[])startRaw.Clone();
        var log = new List<string>();
        AsusMouse.Transport io = req =>
        {
            var r = new byte[64];
            int cmd = req[0] | req[1] << 8;
            r[0] = req[0]; r[1] = req[1];
            switch (cmd)
            {
                case 0x0012: r[11] = (byte)active; break;
                case 0x0412:
                    for (int i = 0; i < presets; i++) { r[4 + 2 * i] = (byte)raw[i]; r[5 + 2 * i] = (byte)(raw[i] >> 8); }
                    r[4 + 2 * presets] = (byte)rate; break;
                case 0x3151:
                    int field = req[2], v = req[4] | req[5] << 8;
                    if (field < presets) raw[field] = v; else rate = v;
                    log.Add($"set {field}={v}"); break;
                case 0x0350: log.Add("save"); break;
                default: r[0] = 0xff; r[1] = 0xaa; break;
            }
            return r;
        };
        return (new AsusMouse(AsusMouse.Lookup(0x0B05, pid)!, pid, io), log, raw);
    }

    // ROG Chakram: 4 presets, DOUBLE_DPI. Raw 7 -> (7*50+50)*2 = 800 DPI.
    var (chakram, clog, craw) = Sim(0x18e3, 4, [7, 15, 31, 63], 3, 2);
    var cs = chakram.Read();
    Check(chakram is { Name: "ASUS ROG Chakram", Key: "asus/18E3" } && chakram.Caps is { MaxStages: 4, DpiMax: 16000, StageSelectable: false }, "ASUS: model from libratbag's data, presets switch only on the mouse");
    Check(cs.Stages!.SequenceEqual([800, 1600, 3200, 6400]) && cs.ActiveStage == 2 && cs.Dpi == 1600 && cs.PollRate == 1000, "ASUS: doubled-DPI presets, active preset and polling rate read");
    chakram.Apply(new PeripheralSettings { Stages = [800, 2000, 3200, 6400], ActiveStage = 2, PollRate = 500 }, cs);
    Check(clog.SequenceEqual(["set 1=19", "set 4=2", "save"]) && craw[1] == 19, "ASUS: only the changed preset and the rate are sent, then saved (2000 DPI doubled -> raw 19)");
    chakram.Apply(new PeripheralSettings { Stages = [800, 2000, 3200, 6400], PollRate = 500 }, chakram.Read());
    Check(clog.Count == 3, "ASUS: nothing sent (and no save) when the mouse already matches");

    // ROG Gladius II: 2 presets, plain DPI. Raw 15 -> 800 DPI.
    var (gladius, glog, _) = Sim(0x1845, 2, [15, 31], 3, 1);
    var gs = gladius.Read();
    Check(gs.Stages!.SequenceEqual([800, 1600]) && gs.Dpi == 800, "ASUS: two-preset model read");
    gladius.Apply(new PeripheralSettings { Dpi = 1200 }, gs);
    Check(glog.SequenceEqual(["set 0=23", "save"]), "ASUS: a DPI change goes to the active preset");
    Check(AsusMouse.Lookup(0x0B05, 0x1ad7) is null, "ASUS: mice behind the Omni receiver are left out");
}

// --- older Logitech mice (HID++ 1.0, libratbag's protocol), against simulated mice
{
    (LogitechHidpp10Mouse mouse, List<string> sent) Sim(int pid, byte startRaw)
    {
        byte raw = startRaw;
        var sent = new List<string>();
        LogitechHidpp10Mouse.Transport io = (req, replyReport) =>
        {
            var r = new byte[replyReport == 0x11 ? 20 : 7];
            r[0] = replyReport; r[1] = req[1]; r[2] = req[2]; r[3] = req[3];
            if (req[3] != 0x63) { r[2] = 0x8F; return r; }
            if (req[2] is 0x81 or 0x83) r[4] = raw;
            else { raw = req[4]; sent.Add($"{req[0]:X2} {req[2]:X2} {req[4]} {req[6]}"); }
            return r;
        };
        return (new LogitechHidpp10Mouse(LogitechHidpp10Mouse.Lookup(pid)!, io), sent);
    }
    // G500: DPI range 0..5700 in 23.53 steps, rounded to 25 (raw 34 = 800 DPI). Long register, X and Y.
    var (g500, g500sent) = Sim(0xc068, 34);
    var g5s = g500.Read();
    Check(g500 is { Name: "Logitech G500", Key: "logitech/C068" } && g5s is { Dpi: 800, Connected: true } && g500.Caps.DpiMax == 5700, "HID++ 1.0: G500 current DPI read through the long register");
    g500.Apply(new PeripheralSettings { Dpi = 1600 }, g5s);
    Check(g500sent.SequenceEqual(["11 82 68 68"]) && g500.Read().Dpi == 1600, "HID++ 1.0: G500 DPI set as X and Y raw values (1600 -> 68)");
    // G9: a DPI list, raws from 0x80, one-byte short register.
    var (g9, g9sent) = Sim(0xc048, 0x83);
    Check(g9.Read().Dpi == 800, "HID++ 1.0: G9 current DPI read from its list (raw 0x83 = 800)");
    g9.Apply(new PeripheralSettings { Dpi = 3150 }, g9.Read());
    Check(g9sent.SequenceEqual(["10 80 143 0"]), "HID++ 1.0: G9 snaps to the nearest DPI it has (3150 -> 3200, raw 0x8F) on the short register");
    Check(LogitechHidpp10Mouse.Lookup(0x1028) is null, "HID++ 1.0: mice without DPI data (M570) aren't listed");
}

// --- supported-devices list and its fuzzy search
Check(SupportedDevices.List.Count > 2000 && SupportedDevices.List.Any(d => d.Kind == "Mouse") && SupportedDevices.List.Any(d => d.Kind == "Headset"), "supported list: loaded, every kind present");
Check(SupportedDevices.Search("deathadder v2").FirstOrDefault()?.Name == "Razer DeathAdder V2", "search: exact words find the device first");
Check(SupportedDevices.Search("dthaddr v2").Any(d => d.Name == "Razer DeathAdder V2"), "search: letters in order still match (typos with dropped letters)");
Check(SupportedDevices.Search("kraken", "Fans & cooling").All(d => d.Kind.Contains("Fans & cooling")) && SupportedDevices.Search("kraken", "Fans & cooling").Count > 0, "search: kind filter");
Check(SupportedDevices.Search("zzqqxx").Count == 0, "search: nonsense finds nothing");
Check(SupportedDevices.Search("kraken x").TakeWhile(d => !d.Name.Contains("Kraken M")).Any(d => d.Name == "NZXT Kraken X2"), "search: a word that starts a later word beats the same letters inside an earlier one");
Check(SupportedDevices.List.Single(d => d.Name == "Razer DeathAdder V2").Kind == "Mouse · Lighting", "supported list: one row per device, settings and lighting merged");

// --- config round trip
var path = Path.Combine(Path.GetTempPath(), $"rigcontrol-{Guid.NewGuid():N}.json");
cfg.Save(path);
var back = AppConfig.Load(path);
File.Delete(path);
Check(back.Curves.Count == cfg.Curves.Count && back.Fans["/lpc/it8689e/0/control/0"].Curve == "CPU" && back.Curves[0].Points.Count == cfg.Curves[0].Points.Count, "config round trip");

Console.WriteLine(failed == 0 ? "\nall checks passed" : $"\n{failed} check(s) FAILED");
return failed == 0 ? 0 : 1;

// Read-only: dumps the first input reports of each NZXT device (no writes).
static void RawHid()
{
    foreach (var d in HidSharp.DeviceList.Local.GetHidDevices(0x1E71))
    {
        Console.WriteLine($"{d.ProductID:X4} in={d.GetMaxInputReportLength()} {d.GetSerialNumber()}");
        if (!d.TryOpen(out HidSharp.HidStream s)) { Console.WriteLine("  open failed"); continue; }
        using (s)
        {
            s.ReadTimeout = 1500;
            var until = DateTime.Now.AddSeconds(3);
            while (DateTime.Now < until)
            {
                try { var b = s.Read(); Console.WriteLine($"  {b.Length}: {Convert.ToHexString(b, 0, Math.Min(48, b.Length))}"); }
                catch (Exception e) { Console.WriteLine($"  {e.GetType().Name}: {e.Message}"); }
            }
        }
    }
}

// Read-only: lists what the hub finds. Never sets a fan, never releases one.
static void Probe()
{
    using var hub = new HardwareHub();
    hub.Open();
    Thread.Sleep(1500); // let Hue2 status reports arrive
    hub.Update();
    foreach (var p in hub.Problems) Console.WriteLine($"problem: {p}");
    Console.WriteLine($"\n{hub.Temps.Count} temperatures");
    foreach (var t in hub.Temps) Console.WriteLine($"  {t.Value,6:0.0} °C  {t.Name,-28} {t.Device,-32} {t.Id}");
    Console.WriteLine($"\n{hub.Fans.Count} fan channels");
    foreach (var f in hub.Fans) Console.WriteLine($"  {f.Rpm,6:0} rpm {f.Duty,5:0}%  {f.Name,-14} {f.Device,-48} {f.Id}");
}

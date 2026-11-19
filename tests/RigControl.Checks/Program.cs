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

// A stopped fan: 0 rpm for 10 s at 30 %+, after it was seen spinning. Empty headers and slow spin-ups never alarm.
{
    var w = new StallWatch();
    Check(!w.Update(0, 80, 0) && !w.Update(0, 80, 60), "stall: a fan never seen spinning (empty header) never alarms");
    w.Update(900, 50, 61);
    Check(!w.Update(0, 50, 62) && !w.Update(0, 50, 71.9), "stall: not yet after under 10 s at 0 rpm");
    Check(w.Update(0, 50, 72), "stall: 0 rpm for 10 s at 50 % is a stopped fan");
    Check(!w.Update(0, 20, 73), "stall: below 30 % a 0 rpm fan may just be stopping on purpose");
    Check(!w.Update(0, 50, 74) && !w.Update(null, 50, 90), "stall: the clock restarts, and no reading (null) isn't 0 rpm");
    Check(!w.Update(800, 50, 95), "stall: spinning again clears it");
}
// Cooling profiles: the one in use is Curves + Fans; switching puts it away and takes another out. Names stay with fans.
{
    var cp = new AppConfig { Curves = [new CurveDef { Name = "Calm" }] };
    cp.Fans["f1"] = new FanSettings { Name = "Front", Enabled = true, Curve = "Calm" };
    Check(cp.CoolingProfileNames().SequenceEqual(["Default"]), "profiles: a fresh config has one, Default");
    cp.AddCooling("Gaming");
    Check(cp.ActiveCooling == "Gaming" && cp.Curves[0].Name == "Calm" && cp.Fans["f1"].Curve == "Calm" && cp.CoolingProfileNames().SequenceEqual(["Default", "Gaming"]),
          "profiles: a new one starts as a copy of the one in use");
    cp.Curves[0].Name = "Loud"; cp.Fans["f1"].Curve = "Loud"; cp.Fans["f1"].MinPercent = 40;
    cp.Fans["f1"].Name = "Front intake";
    cp.SwitchCooling("Default");
    Check(cp.ActiveCooling == "Default" && cp.Curves[0].Name == "Calm" && cp.Fans["f1"] is { Curve: "Calm", MinPercent: 0 },
          "profiles: switching brings back the other's curves and fan settings (the copy was separate)");
    Check(cp.Fans["f1"].Name == "Front intake", "profiles: a fan renamed in one profile keeps the name in the others");
    cp.SwitchCooling("Gaming");
    Check(cp.Curves[0].Name == "Loud" && cp.Fans["f1"] is { Curve: "Loud", MinPercent: 40 }, "profiles: ...and switching back restores those");
    cp.Fans["f9"] = new FanSettings();
    cp.MoveHue2Settings("S", toLiquidctl: false); // nothing to move, just must not throw
    cp.CoolingProfiles[0].Fans["nzxt-hue2/S/fan/1"] = new FanSettings { Curve = "Calm" };
    cp.MoveHue2Settings("S", toLiquidctl: true);
    Check(cp.CoolingProfiles[0].Fans.ContainsKey("liquidctl/S/fan1"), "profiles: a driver switch moves the fan's settings in profiles not in use too");
    var rt = AppConfig.Load(WriteTemp(cp));
    Check(rt.ActiveCooling == "Gaming" && rt.CoolingProfiles.Single().Name == "Default" && rt.CoolingProfiles[0].Curves[0].Name == "Calm", "profiles: saved and loaded");
    cp.DeleteCooling();
    Check(cp.ActiveCooling == "Default" && cp.CoolingProfiles.Count == 0 && cp.Curves[0].Name == "Calm" && cp.Fans["f1"].Name == "Front intake",
          "profiles: deleting the one in use switches to another");
    cp.DeleteCooling();
    Check(cp.ActiveCooling == "Default", "profiles: the last one can't be deleted");
}
static string WriteTemp(AppConfig c) { var p = Path.Combine(Path.GetTempPath(), $"rigcontrol-profiles-{Guid.NewGuid():N}.json"); c.Save(p); return p; }

// Cooling presets: valid shapes that always end at 100 %, the right shape for each sensor, mix and flat untouched.
{
    bool allValid = true;
    foreach (var kind in Enum.GetValues<SensorKind>())
        foreach (var preset in CoolingPresets.All)
        {
            var shape = CoolingPresets.Shape(kind, preset);
            allValid &= shape.Count >= 2 && shape[^1].Percent == 100 && shape.Zip(shape.Skip(1)).All(p => p.First.Temp < p.Second.Temp && p.First.Percent <= p.Second.Percent)
                        && shape.All(p => p.Temp is >= 20 and <= 100 && p.Percent is >= 0 and <= 100);
        }
    Check(allValid, "presets: every shape rises, stays on the graph and ends at 100 %");
    Check(CoolingPresets.All.All(p => CurveMath.Interpolate(CoolingPresets.Shape(SensorKind.Cpu, p), 90) == 100)
          && CoolingPresets.All.All(p => CurveMath.Interpolate(CoolingPresets.Shape(SensorKind.Liquid, p), 45) == 100),
          "presets: full speed by 90 °C on a CPU and 45 °C coolant, even Silent");
    Check(CurveMath.Interpolate(CoolingPresets.Shape(SensorKind.Cpu, CoolingPreset.Silent), 60) < CurveMath.Interpolate(CoolingPresets.Shape(SensorKind.Cpu, CoolingPreset.Balanced), 60)
          && CurveMath.Interpolate(CoolingPresets.Shape(SensorKind.Cpu, CoolingPreset.Balanced), 60) < CurveMath.Interpolate(CoolingPresets.Shape(SensorKind.Cpu, CoolingPreset.Performance), 60),
          "presets: at 60 °C, Silent < Balanced < Performance");
    Check(CoolingPresets.KindOf("/intelcpu/0/temperature/0", "CPU Package") == SensorKind.Cpu
          && CoolingPresets.KindOf("/gpu-nvidia/0/temperature/0", "GPU Core") == SensorKind.Gpu
          && CoolingPresets.KindOf("liquidctl/abc/temp/Liquid temperature", "Liquid temperature") == SensorKind.Liquid
          && CoolingPresets.KindOf("/lpc/it8689e/temperature/2", "VRM MOS") == SensorKind.Other
          && CoolingPresets.KindOf(null, null) == SensorKind.Other,
          "presets: CPU, GPU, coolant and other sensors told apart");
    var g = new CurveDef { Name = "G", Source = "/gpu-nvidia/0/temperature/0", Points = [new(30, 30), new(80, 100)] };
    var mixCurve = new CurveDef { Name = "M", Kind = CurveKind.Mix, Inputs = ["G"] };
    var flat = new CurveDef { Name = "F", Kind = CurveKind.Flat, Percent = 55 };
    int n = CoolingPresets.ApplyAll([g, mixCurve, flat], CoolingPreset.Silent, id => id.Contains("gpu") ? "GPU Core" : null);
    Check(n == 1 && g.Points.SequenceEqual(CoolingPresets.Shape(SensorKind.Gpu, CoolingPreset.Silent)) && g is { Deadband: 3, ResponseSeconds: 4 }
          && mixCurve.Points.Count == 0 && flat.Percent == 55, "presets: a graph curve reshaped for its sensor; mix and flat left alone");
}

// Starter setup: curves for what this PC has, fans guessed from where they're connected, pumps left alone.
{
    StarterFan F(string id, string name, string device) => new(id, name, device);
    var fans = new[]
    {
        F("/lpc/it8689e/control/0", "CPU Fan", "Motherboard"),
        F("liquidctl/k/pump", "Pump", "NZXT Kraken · 1A2B"),
        F("liquidctl/k/fan1", "Fan 1", "NZXT Kraken · 1A2B"),
        F("/gpu-nvidia/0/control/0", "GPU Fan", "RTX 4070"),
        F("/lpc/it8689e/control/1", "System Fan #1", "Motherboard"),
    };
    string? Name(string id) => id.Contains("Liquid") ? "Liquid temperature" : null;
    var plan = StarterSetup.Plan("/intelcpu/0/temperature/0", "/gpu-nvidia/0/temperature/0", ("liquidctl/k/temp/Liquid", "NZXT Kraken · 1A2B"),
        fans, CoolingPreset.Balanced, Name);
    string? Follows(string id) => plan.Fans.Single(c => c.Fan.Id == id).Curve;
    Check(plan.Curves.Select(c => c.Name).SequenceEqual(["CPU", "GPU", "Liquid", "Case"])
          && plan.Curves.Single(c => c.Name == "Case") is { Kind: CurveKind.Mix, Mix: MixFunction.Max } cs && cs.Inputs.SequenceEqual(["CPU", "GPU"]),
          "starter: CPU, GPU and Liquid curves, and Case = the higher of CPU and GPU");
    Check(plan.Curves[0].Points.SequenceEqual(CoolingPresets.Shape(SensorKind.Cpu, CoolingPreset.Balanced))
          && plan.Curves[2].Points.SequenceEqual(CoolingPresets.Shape(SensorKind.Liquid, CoolingPreset.Balanced)), "starter: each curve shaped for its sensor");
    Check(Follows("/lpc/it8689e/control/0") == "CPU" && Follows("liquidctl/k/fan1") == "Liquid" && Follows("/gpu-nvidia/0/control/0") == "GPU"
          && Follows("/lpc/it8689e/control/1") == "Case" && Follows("liquidctl/k/pump") is null,
          "starter: CPU header -> CPU, radiator -> Liquid, graphics card -> GPU, others -> Case, pump left alone");
    var bare = StarterSetup.Plan("/amdcpu/0/temperature/0", null, null, fans, CoolingPreset.Silent, Name);
    Check(bare.Curves.Select(c => c.Name).SequenceEqual(["CPU", "Case"]) && bare.Fans.Single(c => c.Fan.Id == "/gpu-nvidia/0/control/0").Curve == "Case"
          && bare.Fans.Single(c => c.Fan.Id == "liquidctl/k/fan1").Curve == "Case",
          "starter: no GPU or coolant sensor -> no such curves, those fans follow Case");
    var cfgS = new AppConfig();
    cfgS.Fans["liquidctl/k/pump"] = new FanSettings { Manual = 70, Enabled = true };
    StarterSetup.Apply(plan, cfgS);
    Check(cfgS.Curves.Count == 4 && cfgS.Fans["/lpc/it8689e/control/1"] is { Enabled: true, Curve: "Case", Manual: null }
          && cfgS.Fans["liquidctl/k/pump"] is { Manual: 70 }, "starter: applied, the pump's own setting kept");
}

// Overview history lines: averaged into columns, newest at the right edge, and a curve that never overshoots.
{
    var jitter = Enumerable.Range(0, 300).Select(i => 50 + (i % 2 == 0 ? 3.0 : -3.0) + i / 30.0).ToList(); // a slow rise under 2-second noise
    var pts = SparklineMath.Layout(jitter, 300, 200, 34, 10);
    Check(pts.Count is > 40 and <= 51 && Math.Abs(pts[^1].X - 200) < 4 && pts.Zip(pts.Skip(1)).All(q => q.First.X < q.Second.X),
          "history line: 300 samples averaged into ~4 px columns, left to right, newest at the right edge");
    Check(pts.Zip(pts.Skip(1)).Count(q => q.Second.Y > q.First.Y) <= 2, "history line: the 2-second jitter is averaged away (the slow rise is what shows)");
    var wave = Enumerable.Range(0, 121).Select(i => 50 + 5 * Math.Sin(i / 7.0)).ToList();
    var before = SparklineMath.Layout(wave[..120], 180, 80, 34, 10, first: 0);
    var after = SparklineMath.Layout(wave[1..], 180, 80, 34, 10, first: 1);
    Check(before.Count == after.Count && Enumerable.Range(1, before.Count - 2).All(i => Math.Abs(before[i].X - 80.0 / 179 - after[i].X) < 1e-9),
          "history line: a new sample moves every full column exactly one step left (no wobble, so it can glide)");
    var full = SparklineMath.Layout(wave[..117], 180, 80, 34, 10); // 13 full columns of 9
    var filling = SparklineMath.Layout(wave[..118], 180, 80, 34, 10); // ...and one sample of the next
    Check(full.Count == filling.Count && Math.Abs(full[^1].Y - filling[^1].Y) < 1e-9 && Math.Abs(full[^1].X - 80.0 / 179 - filling[^1].X) < 1e-9,
          "history line: the end stays put while the newest column fills (it only glides left)");
    var shortHistory = SparklineMath.Layout([.. Enumerable.Range(40, 20).Select(i => (double)i)], 300, 200, 34, 10);
    Check(shortHistory.Count >= 2 && shortHistory.All(q => q.X > 180), "history line: a short history fills only the right-hand part");
    var bumpy = new List<(double, double)> { (0, 30), (10, 5), (20, 28), (30, 28), (40, 2), (50, 20) };
    var curve = SparklineMath.Curve(bumpy);
    bool inBand = true;
    for (int k = 0; k < curve.Count; k++)
    {
        var (x0, y0) = bumpy[k];
        var (c1, c2, end) = curve[k];
        double lo = Math.Min(y0, end.Y) - 1e-9, hi = Math.Max(y0, end.Y) + 1e-9;
        for (double t = 0; t <= 1; t += 0.05)
        {
            double u = 1 - t, y = u * u * u * y0 + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * end.Y;
            inBand &= y >= lo && y <= hi;
        }
    }
    Check(curve.Count == 5 && inBand, "history line: the curve never swings past a peak or a dip");
}

// The fan loop on the demo hardware: graph curves follow their sensor, fans follow their curve, and a swapped
// config is used from the next pass on (its lookups are kept between passes, so they must notice).
{
    Demo.On = true;
    try
    {
        var hub = new HardwareHub();
        hub.Open();
        var demoCfg = Demo.Config();
        foreach (var c in demoCfg.Curves) { c.Deadband = 0; c.ResponseSeconds = 0; }
        foreach (var f in demoCfg.Fans.Values) { f.StepUp = 0; f.StepDown = 0; f.StartPercent = 0; }
        using var engine = new FanEngine(hub, demoCfg);
        bool curvesRight = true, fansRight = true, sawRenamed = false;
        int passes = 0;
        using var done = new ManualResetEventSlim();
        engine.Updated += () => // on the loop's thread, before the next pass reads the sensors again
        {
            var used = engine.Config;
            foreach (var c in used.Curves.Where(c => c.Kind == CurveKind.Graph))
            {
                var t = hub.Temps.First(s => s.Id == c.Source).Value!.Value;
                // The engine ignores moves under 0.0001° even with no deadband, so the input it used is "about" t.
                curvesRight &= engine.CurveInputs.TryGetValue(c.Name, out var input) && Math.Abs(input - t) < 0.001
                    && engine.CurveOutputs.TryGetValue(c.Name, out var o) && Math.Abs(o - CurveMath.Interpolate(c.Points, input)) < 1e-9;
            }
            foreach (var (id, s) in used.Fans.Where(f => f.Value.Enabled && f.Value.Curve is not null && f.Value.Manual is null))
                if (engine.CurveOutputs.TryGetValue(s.Curve!, out var o))
                    fansRight &= engine.Status[id].Target == CurveMath.NextDuty(o, null, s);
            sawRenamed |= engine.CurveOutputs.ContainsKey("Renamed");
            if (++passes == 2)
            {
                var next = used.Clone();
                var g = next.Curves.First(c => c.Kind == CurveKind.Graph);
                foreach (var f in next.Fans.Values.Where(f => f.Curve == g.Name)) f.Curve = "Renamed";
                foreach (var m in next.Curves) m.Inputs = [.. m.Inputs.Select(i => i == g.Name ? "Renamed" : i)];
                g.Name = "Renamed";
                engine.Apply(next);
            }
            if (passes == 4) done.Set();
        };
        engine.Start();
        Check(done.Wait(TimeSpan.FromSeconds(15)), "fan loop: runs passes on the demo hardware");
        Check(curvesRight, "fan loop: each graph curve gives its points' value at its sensor's temperature");
        Check(fansRight, "fan loop: each fan on a curve runs at that curve's output");
        Check(sawRenamed, "fan loop: a swapped config (a curve renamed) is used from the next pass");
    }
    finally { Demo.On = false; }
}

// The watchdog: bring RigControl back after it died without exiting, up to 3 times in 10 minutes, then give the
// fans to the hardware; never when it exited cleanly (no mark, or another process's) or Windows is ending.
Check(CrashGuard.Decide(10, 10, 0, false) == CrashGuard.Action.Restart, "watchdog: died without exiting -> start it again");
Check(CrashGuard.Decide(10, 10, CrashGuard.MaxRestarts, false) == CrashGuard.Action.ReleaseFans, "watchdog: keeps crashing -> fans back to the hardware");
Check(CrashGuard.Decide(10, null, 0, false) == CrashGuard.Action.None && CrashGuard.Decide(10, 11, 0, false) == CrashGuard.Action.None,
      "watchdog: a clean exit (no mark, or a newer run's mark) -> nothing");
Check(CrashGuard.Decide(10, 10, 0, true) == CrashGuard.Action.None, "watchdog: Windows ending the session -> nothing");
{
    var keepFolder = AppConfig.Folder;
    AppConfig.Folder = Path.Combine(Path.GetTempPath(), $"rigcontrol-check-{Guid.NewGuid():N}");
    try
    {
        var boot = DateTime.Now.AddHours(-1);
        Check(CrashGuard.Begin(100, boot) is null && CrashGuard.MarkedPid() == 100, "session: first start, marked");
        Check(CrashGuard.Begin(200, boot) is not null && CrashGuard.MarkedPid() == 200, "session: a start after a run that didn't exit says so");
        CrashGuard.End(999);
        Check(CrashGuard.MarkedPid() == 200, "session: another process's exit doesn't clear this run's mark");
        CrashGuard.End(200);
        Check(CrashGuard.MarkedPid() is null && CrashGuard.Begin(300, boot) is null, "session: a clean exit, then a quiet start");
        CrashGuard.End(300);
        CrashGuard.Begin(400, boot);
        Check(CrashGuard.Begin(500, DateTime.Now.AddMinutes(1)) is null, "session: a mark from before Windows started isn't reported (power cut, forced shutdown)");
        var now = DateTime.Now;
        CrashGuard.NoteRestart(now.AddMinutes(-20));
        CrashGuard.NoteRestart(now.AddMinutes(-2));
        CrashGuard.NoteRestart(now.AddMinutes(-1));
        Check(CrashGuard.RecentRestarts(now) == 2, "watchdog: restarts counted within the last 10 minutes only");
    }
    finally
    {
        try { Directory.Delete(AppConfig.Folder, true); } catch { }
        AppConfig.Folder = keepFolder;
    }
}
Check(FanEngine.IsPump(new TestFan("AIO Pump"), null) && FanEngine.IsPump(new TestFan("Fan #2"), new FanSettings { Name = "Loop pump" })
      && !FanEngine.IsPump(new TestFan("Fan #2"), new FanSettings { Name = "Rear" }), "identify: pumps found by hardware name or your name");

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

// a HUE 2 that can't report its serial gets one from its path: CRC-32 (standard check value), never a per-run hash
Check(Hue2Device.PathSerial("123456789") == "cbf43926", "HUE 2 stand-in serial is stable across runs");
Check(LiquidctlBridge.IsCandidate(0x1B1C, 0x0C1C, new HashSet<(int, int)>()) && !LiquidctlBridge.IsCandidate(0x1E71, 0x2001, new HashSet<(int, int)>())
      && !LiquidctlBridge.IsCandidate(0x1E71, 0x2020, Hue2Device.Supported.ToHashSet()), "fan controller watch: liquidctl vendors yes, lighting-only and native no");

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
    // the runtime's Python, the source bridge.py (the one the app ships, see RigControl.csproj)
    var bridgeSrc = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "RigControl.Core", "liquidctl", "bridge.py"));
    var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(runtime, "python", "python.exe"), ["-I", "-B", bridgeSrc, "--selftest"])
        { RedirectStandardOutput = true, RedirectStandardError = true };
    using var py = System.Diagnostics.Process.Start(psi)!;
    var outText = py.StandardOutput.ReadToEnd() + py.StandardError.ReadToEnd();
    py.WaitForExit();
    Check(py.ExitCode == 0, "liquidctl bridge selftest" + (py.ExitCode == 0 ? "" : $": {outText.Trim()}"));
}
else Console.WriteLine("skip liquidctl bridge selftest (runtime not built)");

// --- lighting effects
Check(EffectMath.Gradient([new(0, 0, 0), new(200, 100, 50)], 0.5) == new Rgb(100, 50, 25), "gradient midpoint");
// Layouts: per fan, each 8-LED fan runs 0..1 on its own; per zone, each channel does; auto picks per fan when fans are known.
{
    var hub = Demo.RgbDevices().First(d => d.Key == "demo/rgb/hub");
    var fan = EffectMath.LayoutX(hub, EffectLayout.Fan);
    Check(fan[0] == 0 && fan[7] == 1 && fan[8] == 0 && fan[39] == 1 && Math.Abs(fan[3] - 3 / 7f) < 1e-6, "layout: x restarts on every fan");
    var zone = EffectMath.LayoutX(hub, EffectLayout.Zone);
    Check(zone[0] == 0 && zone[23] == 1 && zone[24] == 0 && zone[39] == 1, "layout: x restarts on every zone");
    Check(EffectMath.LayoutX(hub, EffectLayout.Auto).SequenceEqual(fan), "layout: automatic = per fan when the controller lists fans");
    var gpu = Demo.RgbDevices().First(d => d.Key == "demo/rgb/gpu");
    Check(ReferenceEquals(EffectMath.LayoutX(gpu, EffectLayout.Auto), gpu.LedX), "layout: automatic = whole device otherwise");
}
// Frame pacing: an animated effect reaches each device at its own steady rate (30 and 10 fps here), without bunching.
{
    var fast = new PacedTarget("fast", 30); var slow = new PacedTarget("slow", 10);
    using var lights = new LightingEngine(_ => null);
    lights.SetTargets([fast, slow]);
    lights.Apply(new LightingConfig());
    Thread.Sleep(2300);
    foreach (var (tg, want) in new[] { (fast, 30.0), (slow, 10.0) })
    {
        var gaps = tg.Gaps();
        double avg = gaps.Average(), worst = gaps.Max();
        Console.WriteLine($"     {tg.Key}: {1000 / avg:0.0} fps, gaps {gaps.Min():0}..{worst:0} ms");
        Check(1000 / avg > want * 0.93 && worst < 1000 / want * 1.5, $"lighting: {want:0} fps device gets steady frames");
    }
    int modes = fast.Modes;
    lights.Apply(new LightingConfig());
    Thread.Sleep(300);
    Check(fast.Modes == modes, "lighting: a settings change doesn't re-initialise devices");

    // A device that takes 300 ms per send: changing settings (the UI thread) must not wait for it.
    var sluggish = new PacedTarget("sluggish", 30) { SendMs = 300 };
    lights.SetTargets([sluggish]);
    Thread.Sleep(450); // mid-send
    var applyTime = System.Diagnostics.Stopwatch.StartNew();
    lights.Apply(new LightingConfig());
    _ = lights.Targets;
    Check(applyTime.ElapsedMilliseconds < 50, $"lighting: settings change doesn't wait for a slow device ({applyTime.ElapsedMilliseconds} ms)");
}
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
        foreach (var x in new[] { "-I", "-B", Path.Combine(rivalPy, "rival.py") }.Concat(a)) psi.ArgumentList.Add(x);
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

// --- per-zone lighting
{
    var frame = Enumerable.Repeat(new Rgb(10, 20, 30), 10).ToArray();
    RgbZone[] zones = [new() { Name = "Logo", StartLed = 0, LedsCount = 2 }, new() { Name = "Ring", StartLed = 2, LedsCount = 6 }, new() { Name = "Strip", StartLed = 8, LedsCount = 5 }];
    EffectMath.ApplyZones(frame, zones, new Dictionary<string, ZoneLighting> { ["Ring"] = new() { Source = ZoneSource.Color, Color = "#FF0000" }, ["Strip"] = new() { Source = ZoneSource.Off } });
    Check(frame[..2].All(c => c == new Rgb(10, 20, 30)) && frame[2..8].All(c => c == new Rgb(255, 0, 0)) && frame[8..].All(c => c == default(Rgb)),
        "zones: unlisted zones keep the effect, a colour zone and an off zone paint only their LEDs (an overlong zone is clipped)");
    var leds = Enumerable.Repeat(new Rgb(10, 20, 30), 6).ToArray();
    EffectMath.ApplyZones(leds, [new RgbZone { Name = "Ring", StartLed = 1, LedsCount = 4 }],
        new Dictionary<string, ZoneLighting> { ["Ring"] = new() { Source = ZoneSource.Leds, Color = "#0000FF", Leds = ["#FF0000", "#00FF00"] } });
    Check(leds[0] == new Rgb(10, 20, 30) && leds[1] == new Rgb(255, 0, 0) && leds[2] == new Rgb(0, 255, 0) && leds[3] == new Rgb(0, 0, 255) && leds[4] == new Rgb(0, 0, 255) && leds[5] == new Rgb(10, 20, 30),
        "zones: each LED gets its own colour, unpainted ones the zone colour, LEDs outside the zone untouched");
}

// --- Razer keyboards (openrazer data), against simulated keyboards
{
    (RazerKeyboard kb, List<string> sent) Sim(int pid, bool game, byte pollCode, byte keyswitch)
    {
        var sent = new List<string>();
        RazerKeyboard.Transport io = (tid, cls, id, size, args) =>
        {
            var r = new byte[80];
            switch ((cls, id))
            {
                case (0x03, 0x80): r[2] = (byte)(game ? 1 : 0); break;
                case (0x00, 0xC0): r[1] = pollCode; break;
                case (0x02, 0x82): r[1] = keyswitch; break;
                default: sent.Add($"{tid:x2} {cls:x2}{id:x2} " + string.Join(" ", args.Select(a => a.ToString("x2")))); break;
            }
            return r;
        };
        return (new RazerKeyboard(pid, RazerKeyboard.Lookup(pid)!, io), sent);
    }
    var (hv2, hsent) = Sim(0x026C, false, 0x08, 0x14);
    var hs = hv2.Read();
    Check(hv2 is { Name: "Razer Huntsman V2", Kind: "Keyboard" } && hv2.Caps is { GameMode: true, KeyswitchOptimization: true } && hv2.Caps.PollRates.Contains(8000),
        "Razer keyboard: Huntsman V2 offers game mode, switch optimisation, 8000 Hz");
    Check(hs is { GameMode: false, PollRate: 1000, KeyswitchGaming: false }, "Razer keyboard: game mode, polling rate and typing optimisation read");
    hv2.Apply(new PeripheralSettings { GameMode = true, PollRate = 8000, KeyswitchGaming = true }, hs);
    Check(hsent.SequenceEqual(["1f 0300 00 08 01", "1f 0040 00 01", "1f 0202 ", "1f 0215 01"]),
        "Razer keyboard: game mode (no store, tid 0x1f), 8000 Hz (code 0x01), gaming optimisation in two commands");
    var (bw4, bsent) = Sim(0x0287, true, 0x08, 0);
    bw4.Apply(new PeripheralSettings { GameMode = false }, bw4.Read());
    Check(bsent.SequenceEqual(["ff 0300 01 08 00"]) && !bw4.Caps.KeyswitchOptimization, "Razer keyboard: BlackWidow V4 game mode off with variable storage, tid 0xff");
    Check(RazerKeyboard.Lookup(0x0084) is null, "Razer keyboard: mice aren't in the keyboard table");
}
// --- Logitech keyboards (HID++ 2.0, Solaar's feature details), against a simulated keyboard
{
    int fnSwap = 0, disabled = 0x01 /* caps lock already disabled */, bright = 50, on = 1;
    var writes = new List<string>();
    ushort[] feats = [0x0000, 0x0005, 0x40A2, 0x4521, 0x8040];
    LogitechDevice.Transport kbd = req =>
    {
        var rep = new byte[20];
        Array.Copy(req, rep, 4);
        var p = rep.AsSpan(4);
        int fn = req[3] >> 4;
        switch (feats.ElementAtOrDefault(req[2]))
        {
            case 0x0000 when fn == 1: p[0] = 4; p[2] = req[6]; break;
            case 0x0000: p[0] = (byte)Math.Max(0, Array.IndexOf(feats, (ushort)(req[4] << 8 | req[5]))); break;
            case 0x0005 when fn == 0: p[0] = 8; break;
            case 0x0005 when fn == 1: "Keys 915"u8.ToArray().CopyTo(p); break;
            case 0x0005: p[0] = 0; break; // keyboard
            case 0x40A2 when fn == 0: p[0] = (byte)fnSwap; break;
            case 0x40A2: fnSwap = req[4]; writes.Add($"fn {fnSwap}"); break;
            case 0x4521 when fn == 0: p[0] = 0x1F; break; // caps, num, scroll, insert, win
            case 0x4521 when fn == 1: p[0] = (byte)disabled; break;
            case 0x4521: disabled = req[4]; writes.Add($"keys {disabled:x2}"); break;
            case 0x8040 when fn == 0: p[1] = 100; p[3] = 0x04; p[5] = 10; break; // max 100, on/off, min 10
            case 0x8040 when fn == 1: p[1] = (byte)bright; break;
            case 0x8040 when fn == 2: bright = req[5]; writes.Add($"bright {bright}"); break;
            case 0x8040 when fn == 3: p[0] = (byte)on; break;
            case 0x8040 when fn == 4: on = req[4]; writes.Add($"on {on}"); break;
            default: rep[2] = 0xFF; break;
        }
        return rep;
    };
    var lk = LogitechDevice.Open(kbd, 0xFF);
    var ls2 = lk?.Read();
    Check(lk is { Kind: "Keyboard" } && lk.Caps is { FnSwap: true, GameMode: true, Backlight: true } && ls2 is { FnSwap: false, GameMode: false, Backlight: 50 },
        "Logitech keyboard: Fn swap, Windows-key lock and backlight found and read");
    lk!.Apply(new PeripheralSettings { FnSwap = true, GameMode = true, Backlight = 0 }, ls2!);
    Check(writes.SequenceEqual(["fn 1", "keys 11", "on 0"]), "Logitech keyboard: Fn swap on, Win key locked with Caps Lock kept disabled, backlight 0 % switches it off");
    lk.Apply(new PeripheralSettings { Backlight = 80 }, lk.Read());
    Check(writes.Skip(3).SequenceEqual(["on 1", "bright 80"]), "Logitech keyboard: backlight back on at 80 %");
}
// --- audio-reactive lighting: analysis and effects
{
    const int rate = 48000;
    float[] Tone(double hz, double seconds, double amp = 0.5) => [.. Enumerable.Range(0, (int)(rate * seconds)).Select(i => (float)(amp * Math.Sin(2 * Math.PI * hz * i / rate)))];
    int Loudest(AudioFrame f) => Array.IndexOf(f.Bands, f.Bands.Max());

    var a1 = new AudioAnalyzer(); a1.Feed(Tone(1000, 1), rate);
    var a2 = new AudioAnalyzer(); a2.Feed(Tone(60, 1), rate);
    Check(Loudest(a1.Current) == 8 && Loudest(a2.Current) == 1 && a1.Current.Level > 0.8, "audio: a 1 kHz tone lands in band 8, 60 Hz in band 1, loud tone reads near full level");
    a1.Feed(new float[rate * 2], rate);
    Check(a1.Current.Level < 0.05 && a1.Current.Bands.Max() < 0.1, "audio: silence fades the level and bands to zero");

    // A kick drum: 120 ms of 60 Hz every half second, for 6 seconds.
    var kicks = new AudioAnalyzer();
    var song = new float[rate * 6];
    for (int beat = 0; beat < 12; beat++)
        for (int i = 0; i < rate * 0.12; i++) song[beat * rate / 2 + i] = (float)(0.8 * Math.Sin(2 * Math.PI * 60 * i / rate));
    kicks.Feed(song, rate);
    Check(kicks.Current.Beats is >= 9 and <= 12, $"audio: a kick every half second is counted as beats ({kicks.Current.Beats} of 12; the first second learns the average)");

    var x = Enumerable.Range(0, 11).Select(i => i / 10f).ToArray();
    var leds = new Rgb[11];
    EffectMath.Render(new EffectSettings { Kind = EffectKind.AudioLevel }, 0, x, null, leds, new AudioFrame(0.5f, new float[16], 0, 0));
    Check(leds[..5].All(c => c != default) && leds[7..].All(c => c == default), "audio: the level meter lights the LEDs up to the level and leaves the rest dark");
    var bands = new float[16]; bands[15] = 1;
    EffectMath.Render(new EffectSettings { Kind = EffectKind.AudioSpectrum }, 0, x, null, leds, new AudioFrame(0.3f, bands, 0, 0));
    Check(leds[^1] != default && leds[0] == default, "audio: the spectrum puts treble at the far end and bass at the start");
    EffectMath.Render(new EffectSettings { Kind = EffectKind.AudioPulse, Colors = ["#FF0000", "#0000FF"] }, 0, x, null, leds, new AudioFrame(0, new float[16], 1, 1));
    Check(leds.All(c => c == new Rgb(0, 0, 255)), "audio: a beat flashes the whole device in the next colour");
}
// --- supported-devices list and its fuzzy search
Check(SupportedDevices.List.Count > 2000 && SupportedDevices.List.Any(d => d.Kind == "Mouse") && SupportedDevices.List.Any(d => d.Kind == "Headset"), "supported list: loaded, every kind present");
Check(SupportedDevices.Search("deathadder v2").FirstOrDefault()?.Name == "Razer DeathAdder V2", "search: exact words find the device first");
Check(SupportedDevices.Search("dthaddr v2").Any(d => d.Name == "Razer DeathAdder V2"), "search: letters in order still match (typos with dropped letters)");
Check(SupportedDevices.Search("kraken", "Fans & cooling").All(d => d.Kind.Contains("Fans & cooling")) && SupportedDevices.Search("kraken", "Fans & cooling").Count > 0, "search: kind filter");
Check(SupportedDevices.Search("zzqqxx").Count == 0, "search: nonsense finds nothing");
Check(SupportedDevices.Search("kraken x").TakeWhile(d => !d.Name.Contains("Kraken M")).Any(d => d.Name == "NZXT Kraken X2"), "search: a word that starts a later word beats the same letters inside an earlier one");
Check(SupportedDevices.List.Single(d => d.Name == "Razer DeathAdder V2").Kind == "Mouse · Lighting", "supported list: one row per device, settings and lighting merged");

// --- games: known programs and library folders, minus what only runs beside a game
{
    var exes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"D:\Games\Solo\solo.exe" };
    string[] folders = [@"E:\SteamLibrary\steamapps\common\"];
    Check(GameDetector.IsGame(@"d:\games\solo\SOLO.exe", exes, folders), "games: a program Windows lists, whatever the case");
    Check(GameDetector.IsGame(@"E:\SteamLibrary\steamapps\common\Hades\x64\Hades.exe", exes, folders), "games: anything in a library folder");
    Check(!GameDetector.IsGame(@"E:\SteamLibrary\steamapps\common\Hades\UnityCrashHandler64.exe", exes, folders)
          && !GameDetector.IsGame(@"E:\SteamLibrary\steamapps\common\wallpaper_engine\wallpaper32.exe", exes, folders)
          && !GameDetector.IsGame(@"C:\Windows\explorer.exe", exes, folders), "games: not crash handlers, Wallpaper Engine or other programs");
    Check(GameDetector.SteamLibraries("\"0\"\n{\n\t\"path\"\t\t\"E:\\\\SteamLibrary\"\n}").SequenceEqual([@"E:\SteamLibrary"]), "games: Steam's library list");
}

// --- plugins: manifests checked, then the sample plugin (plugins\sample-mouse, Rust) end to end when it's built
{
    var root = Path.Combine(Path.GetTempPath(), $"rigcontrol-plugins-{Guid.NewGuid():N}");
    try
    {
        void Plugin(string dir, string json) { Directory.CreateDirectory(Path.Combine(root, dir)); File.WriteAllText(Path.Combine(root, dir, "plugin.json"), json); }
        Plugin("escape", """{ "protocol": 1, "id": "escape", "name": "Escape", "program": "..\\..\\Windows\\notepad.exe" }""");
        Plugin("bad-id", """{ "protocol": 1, "id": "Bad Id", "name": "Bad", "program": "x.exe" }""");
        Plugin("future", """{ "protocol": 2, "id": "future", "name": "Future", "program": "x.exe" }""");
        Plugin("bad-device", """{ "protocol": 1, "id": "bad-device", "name": "Bad device", "program": "x.exe", "devices": ["1038"] }""");
        File.WriteAllBytes(Path.Combine(root, "bad-device", "x.exe"), []);
        var installed = PluginManager.Installed(root);
        string? Problem(string dir) => installed.Single(p => Path.GetFileName(p.Folder) == dir).Problem;
        Check(Problem("escape")?.Contains("inside its folder") == true, "plugins: a program outside the plugin's folder is refused");
        Check(Problem("bad-id")?.Contains("lower-case") == true && Problem("future")?.Contains("protocol 2") == true, "plugins: a bad id or another protocol is refused");
        Check(Problem("bad-device")?.Contains("isn't a USB device id") == true, "plugins: device ids must be VID:PID");
        Check(PluginManager.ParseDevice("1038:1836") == (0x1038, 0x1836) && PluginManager.ParseDevice("1038-1836") is null, "plugins: VID:PID read as hex");

        var sample = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\plugins\sample-mouse"));
        var exe = Path.Combine(sample, "target", "release", "sample-mouse.exe");
        if (!File.Exists(exe)) Console.WriteLine("skip plugins: the sample isn't built (cargo build --release in plugins\\sample-mouse)");
        else
        {
            var dir = Path.Combine(root, "sample-mouse");
            Directory.CreateDirectory(dir);
            File.Copy(Path.Combine(sample, "plugin.json"), Path.Combine(dir, "plugin.json"));
            File.Copy(exe, Path.Combine(dir, "sample-mouse.exe"));
            List<string> enabled = [];
            using var manager = new PluginManager(root, () => new PluginChoices(enabled, []));
            Check(manager.Discover().Count == 0 && manager.Status["sample-mouse"] == "Off", "plugins: a plugin stays off until it's switched on");
            enabled.Add("sample-mouse");
            var devices = manager.Discover();
            var mouse = devices.SingleOrDefault();
            // 0xC0000142 (a DLL failed to start): with no one logged on (a CI machine), a program with user rights
            // isn't let onto the desktop the checks run on. People run RigControl on their own desktop.
            if (mouse is null && manager.Status["sample-mouse"].Contains("0xC0000142"))
                Console.WriteLine("skip plugins: a program with user rights can't start here (no desktop for it, as on a CI machine)");
            else Check(mouse is { Key: "plugin/sample-mouse/mouse-1", Kind: "Mouse" } && mouse.Caps.DpiMax == 16000 && mouse.Caps.PollRates.SequenceEqual([125, 500, 1000]),
                $"plugins: the sample plugin starts and lists its mouse, with what it can do ({manager.Status["sample-mouse"]})");
            if (mouse is not null)
            {
                mouse.Apply(new PeripheralSettings { Dpi = 3200, PollRate = 500 }, mouse.Read());
                var state = mouse.Read();
                Check(state is { Dpi: 3200, PollRate: 500, BatteryPercent: 77 }, "plugins: settings reach the plugin and its state comes back");
                bool refused = false;
                try { mouse.Apply(new PeripheralSettings { PollRate = 333 }, state); } catch (InvalidOperationException) { refused = true; }
                Check(refused && mouse.Read().PollRate == 500, "plugins: an error the plugin reports reaches RigControl, and the plugin carries on");
                var process = manager.Host("sample-mouse")!.Process!;
                var level = ChildProcess.IntegrityLevel(process);
                Check(level is <= 0x2000, $"plugins: the plugin runs with an ordinary user's rights (integrity 0x{level:X}{(Environment.IsPrivilegedProcess ? ", checks run as administrator" : "; run the checks as administrator to test it from there")})");
                process.Kill();
                process.WaitForExit(3000);
                bool died = false;
                try { mouse.Read(); } catch (InvalidOperationException) { died = true; }
                Check(died && manager.Due && manager.Discover().Count == 0 && manager.Status["sample-mouse"].StartsWith("Failed"),
                    "plugins: a plugin that dies drops its devices and is started again later");
                enabled.Clear();
                var host = manager.Host("sample-mouse");
                Check(manager.Discover().Count == 0 && manager.Host("sample-mouse") is null && manager.Status["sample-mouse"] == "Off", "plugins: switching one off stops it");
                enabled.Add("sample-mouse");
                manager.Discover(); // a new host: no backoff left over from the failure
                var pid = manager.Host("sample-mouse")?.Process?.Id;
                manager.Dispose();
                bool gone;
                try { using var p = System.Diagnostics.Process.GetProcessById(pid ?? -1); gone = pid is not null && p.WaitForExit(3000); }
                catch (ArgumentException) { gone = pid is not null; } // already gone
                Check(gone, "plugins: stopping RigControl stops its plugins");
            }
        }
    }
    finally { try { Directory.Delete(root, true); } catch { } }
}

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

/// <summary>A pretend RGB device that notes when each frame arrives.</summary>
sealed class TestFan(string name) : FanChannel($"test/{name}", name, "test")
{
    public override void Set(float percent) { }
    public override void Release() { }
}

sealed class PacedTarget(string key, int fps) : IRgbTarget
{
    readonly List<double> _at = [];
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    public int Modes;
    public string Key => key;
    public string Name => key;
    public string Tag => "";
    public RgbDeviceType Type => RgbDeviceType.LedStrip;
    public IReadOnlyList<RgbMode> Modes_ => [];
    IReadOnlyList<RgbMode> IRgbTarget.Modes => Modes_;
    public IReadOnlyList<RgbZone> Zones => [];
    public int LedCount => 8;
    public float[] LedX { get; } = [0, 1 / 7f, 2 / 7f, 3 / 7f, 4 / 7f, 5 / 7f, 6 / 7f, 1];
    public int MaxFps => fps;
    public void EnterDirect() => Modes++;
    /// <summary>How long each send takes (a slow USB device).</summary>
    public int SendMs;
    public void SetLeds(Rgb[] colors) { if (SendMs > 0) Thread.Sleep(SendMs); lock (_at) _at.Add(_clock.Elapsed.TotalMilliseconds); }
    public void ApplyMode(int index, RgbMode mode) { }
    /// <summary>Milliseconds between frames, skipping the first few while the engine starts.</summary>
    public double[] Gaps() { lock (_at) return [.. _at.Skip(4).Zip(_at.Skip(5), (a, b) => b - a)]; }
}

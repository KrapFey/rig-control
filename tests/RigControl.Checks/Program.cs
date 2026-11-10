using RigControl.Core;

if (args.Contains("--probe")) { Probe(); return 0; }
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

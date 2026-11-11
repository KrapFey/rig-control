using System.Diagnostics;
using System.Text.Json;
using HidSharp;

namespace RigControl.Core;

/// <summary>
/// SteelSeries mice (Rival, Sensei, Aerox, Prime, Kana, Kinzu… about 80 models) through rivalcfg in the bundled
/// Python (liquidctl\rival.py): DPI presets, polling rate, sleep timer and battery. rivalcfg's commands are
/// write-only, so RigControl shows what it set. Settings are sent live, never saved to the mouse's flash, and sent
/// again whenever the mouse appears. Lighting stays with OpenRGB.
/// </summary>
public sealed class SteelSeriesMouse : IPeripheral
{
    readonly int _vid, _pid;
    readonly bool _pair; // older mice: two presets set one by one, the active one only by the mouse's button
    PeripheralState _cache = new();
    double _cacheAt = double.NegativeInfinity;
    static readonly Stopwatch Clock = Stopwatch.StartNew();

    public string Key { get; }
    public string Name { get; }
    public string Kind => "Mouse";
    public string Source => "SteelSeries protocol · from rivalcfg";
    public PeripheralCaps Caps { get; }

    /// <summary>Checks replace the Python helper with this (arguments → JSON output).</summary>
    internal static Func<string[], string?>? RunOverride { get; set; }
    static string Helper => Path.Combine(LiquidctlBridge.RuntimeDir, "rival.py");
    static string Python => Path.Combine(LiquidctlBridge.RuntimeDir, "python", "python.exe");

    SteelSeriesMouse(JsonElement d)
    {
        _vid = d.GetProperty("vid").GetInt32();
        _pid = d.GetProperty("pid").GetInt32();
        Key = $"steelseries/{_pid:X4}";
        Name = d.GetProperty("name").GetString() ?? $"SteelSeries mouse {_pid:X4}";
        _pair = d.GetProperty("presets").GetString() == "pair";
        int[] dpi = d.GetProperty("dpi") is { ValueKind: JsonValueKind.Array } r ? [.. r.EnumerateArray().Select(x => x.GetInt32())] : [];
        bool sleep = d.GetProperty("sleep").ValueKind == JsonValueKind.Array;
        Caps = new PeripheralCaps
        {
            // The current DPI is the active preset, as with Razer mice.
            DpiMin = dpi.Length == 3 ? dpi[0] : 100,
            DpiMax = dpi.Length == 3 ? dpi[1] : 0,
            DpiStep = dpi.Length == 3 ? dpi[2] : 50,
            MaxStages = d.GetProperty("stages").GetInt32(),
            PollRates = [.. d.GetProperty("poll").EnumerateArray().Select(x => x.GetInt32())],
            SleepTimeout = sleep,
            SleepMinSeconds = 60,
            SleepMaxSeconds = sleep ? d.GetProperty("sleep")[1].GetInt32() * 60 : 900,
            Battery = d.GetProperty("battery").GetBoolean(),
            Charging = d.GetProperty("battery").GetBoolean(),
        };
    }

    public static List<IPeripheral> FindAll()
    {
        if (RunOverride is null && (!File.Exists(Python) || !File.Exists(Helper))) return [];
        // Starting Python takes a moment: only when a SteelSeries device is plugged in at all.
        if (RunOverride is null && !DeviceList.Local.GetHidDevices(0x1038).Any()) return [];
        if (Run(["list"]) is not { } json) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            return [.. doc.RootElement.EnumerateArray().Select(d => (IPeripheral)new SteelSeriesMouse(d.Clone()))];
        }
        catch (Exception e)
        {
            AppConfig.Log?.Invoke($"rivalcfg: {e.Message}");
            return [];
        }
    }

    public PeripheralState Read()
    {
        // Only the battery can be read; each read starts Python, so at most every 30 s.
        if (!Caps.Battery || Clock.Elapsed.TotalSeconds - _cacheAt < 30) return _cache;
        _cacheAt = Clock.Elapsed.TotalSeconds;
        if (Run(["battery", _vid.ToString(), _pid.ToString()]) is { } json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                _cache = new PeripheralState
                {
                    BatteryPercent = r.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : null,
                    Charging = r.TryGetProperty("charging", out var c) && c.ValueKind is JsonValueKind.True or JsonValueKind.False ? c.GetBoolean() : null,
                };
            }
            catch (Exception e) { AppConfig.Log?.Invoke($"rivalcfg {Name}: {e.Message}"); }
        }
        return _cache;
    }

    public void Apply(PeripheralSettings s, PeripheralState now)
    {
        var want = new Dictionary<string, object>();
        if (s.Stages is { Count: > 0 } stages && Caps.MaxStages > 0)
        {
            want["stages"] = stages.Take(Caps.MaxStages).Select(v => Math.Clamp(v, Caps.DpiMin, Caps.DpiMax)).ToList();
            if (!_pair && s.ActiveStage is int a) want["active"] = a;
        }
        else if (s.Dpi is int dpi && Caps.Dpi)
            want["stages"] = new List<int> { Math.Clamp(dpi, Caps.DpiMin, Caps.DpiMax) }; // one preset = that DPI
        if (s.PollRate is int hz && Caps.PollRates.Contains(hz)) want["poll"] = hz;
        if (s.SleepSeconds is int sec && Caps.SleepTimeout) want["sleep"] = Math.Clamp((int)Math.Round(sec / 60.0), 1, Caps.SleepMaxSeconds / 60);
        if (want.Count == 0) return;
        var result = Run(["apply", _vid.ToString(), _pid.ToString(), JsonSerializer.Serialize(want)]);
        if (result is null || !result.Contains("\"ok\": true")) AppConfig.Log?.Invoke($"rivalcfg {Name}: {result ?? "no answer"}");
    }

    static string? Run(string[] args)
    {
        if (RunOverride is { } o) return o(args);
        var psi = new ProcessStartInfo(Python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-I"); // isolated: ignore PYTHON* env vars and user site-packages
        psi.ArgumentList.Add(Helper);
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(15_000)) { p.Kill(); return null; }
            return output.Result.Trim();
        }
        catch (Exception e)
        {
            AppConfig.Log?.Invoke($"rivalcfg: {e.Message}");
            return null;
        }
    }

    public void Dispose() { }
}

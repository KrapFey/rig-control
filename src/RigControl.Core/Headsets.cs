using System.Diagnostics;
using System.Text.Json;

namespace RigControl.Core;

/// <summary>
/// Headsets through the bundled HeadsetControl (github.com/Sapd/HeadsetControl, ~50 headsets from Logitech,
/// SteelSeries, Corsair, HyperX, Razer, Audeze…): battery, sidetone, auto-off, lights, voice prompts,
/// rotate-to-mute, EQ preset, mic volume, ANC. RigControl runs its CLI and reads its JSON output.
/// </summary>
public sealed class HeadsetControlDevice : IPeripheral
{
    readonly string _id; // "vid:pid" for --device
    PeripheralState _cache = new();
    double _cacheAt = double.NegativeInfinity;
    static readonly Stopwatch Clock = Stopwatch.StartNew();

    public string Key { get; }
    public string Name { get; }
    public string Kind => "Headset";
    public string Source => "HeadsetControl";
    public PeripheralCaps Caps { get; }

    /// <summary>Checks point this at build\headsetcontrol; the app uses its own folder.</summary>
    internal static string? ExeOverride { get; set; }
    static string Exe => ExeOverride ?? Path.Combine(AppContext.BaseDirectory, "headsetcontrol", "headsetcontrol.exe");
    public static bool Available => File.Exists(Exe);
    /// <summary>For testing without a headset: HeadsetControl's built-in simulated device.</summary>
    public static bool UseTestDevice { get; set; } = Environment.GetEnvironmentVariable("RIGCONTROL_HEADSET_TEST") == "1";

    HeadsetControlDevice(JsonElement d)
    {
        var vid = Hex(d, "id_vendor");
        var pid = Hex(d, "id_product");
        _id = $"{vid}:{pid}";
        Key = $"headset/{vid}{pid}";
        Name = Str(d, "device") ?? $"Headset {_id}";
        var caps = d.TryGetProperty("capabilities_str", out var c) ? c.EnumerateArray().Select(x => x.GetString()).ToHashSet() : [];
        Caps = new PeripheralCaps
        {
            Battery = caps.Contains("battery"),
            Charging = caps.Contains("battery"),
            Sidetone = caps.Contains("sidetone"),
            InactiveMaxMinutes = caps.Contains("inactive time") ? 90 : 0,
            Lights = caps.Contains("lights"),
            VoicePrompts = caps.Contains("voice prompts"),
            RotateToMute = caps.Contains("rotate to mute"),
            MicVolume = caps.Contains("microphone volume"),
            Anc = caps.Contains("anc"),
            Chatmix = caps.Contains("chatmix"),
            EqPresets = caps.Contains("equalizer preset") && d.TryGetProperty("equalizer_presets", out var p) && p.ValueKind == JsonValueKind.Object
                ? [.. p.EnumerateObject().Select(x => x.Name)] : [],
        };
        _cache = Parse(d);
        _cacheAt = Clock.Elapsed.TotalSeconds;
    }

    /// <summary>Every headset HeadsetControl sees (none, quickly, when the tool isn't bundled).</summary>
    public static List<IPeripheral> FindAll()
    {
        if (!Available) return [];
        var json = Run(["-o", "json"]);
        if (json is null) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            return [.. doc.RootElement.GetProperty("devices").EnumerateArray()
                .Where(d => Str(d, "id_vendor") is not null)
                .Select(d => (IPeripheral)new HeadsetControlDevice(d.Clone()))];
        }
        catch (Exception e)
        {
            AppConfig.Log?.Invoke($"headsetcontrol: {e.Message}");
            return [];
        }
    }

    public PeripheralState Read()
    {
        // Each read starts the tool and talks to the headset: at most every 5 s, even while the page is open.
        if (Clock.Elapsed.TotalSeconds - _cacheAt < 5) return _cache;
        _cacheAt = Clock.Elapsed.TotalSeconds;
        var json = Run(["-d", _id, "-o", "json"]);
        if (json is null) return _cache;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var d = doc.RootElement.GetProperty("devices").EnumerateArray().FirstOrDefault();
            if (d.ValueKind == JsonValueKind.Object) _cache = Parse(d);
        }
        catch (Exception e) { AppConfig.Log?.Invoke($"headsetcontrol {Name}: {e.Message}"); }
        return _cache;
    }

    static PeripheralState Parse(JsonElement d)
    {
        int? battery = null;
        bool? charging = null, connected = null;
        if (d.TryGetProperty("battery", out var b) && b.ValueKind == JsonValueKind.Object)
        {
            var status = Str(b, "status");
            connected = status != "BATTERY_UNAVAILABLE";
            charging = status == "BATTERY_CHARGING";
            if (b.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.Number && l.GetInt32() >= 0) battery = l.GetInt32();
        }
        return new PeripheralState
        {
            BatteryPercent = battery,
            Charging = charging,
            Connected = connected,
            Sidetone = d.TryGetProperty("sidetone", out var s) && s.ValueKind == JsonValueKind.Object && s.TryGetProperty("level", out var sl) ? sl.GetInt32() : null,
            Chatmix = d.TryGetProperty("chatmix", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : null,
            MicMuted = d.TryGetProperty("mic_muted", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() != 0 : null,
        };
    }

    /// <summary>
    /// Headset settings are mostly write-only (there's no way to read most of them back), so everything that is set
    /// is sent; the hub only calls this when settings changed or the headset (re)appeared.
    /// </summary>
    public void Apply(PeripheralSettings s, PeripheralState now)
    {
        var args = new List<string> { "-d", _id };
        if (s.Sidetone is int st && Caps.Sidetone) args.AddRange(["-s", Math.Clamp(st, 0, 128).ToString()]);
        if (s.InactiveMinutes is int im && Caps.InactiveMaxMinutes > 0) args.AddRange(["-i", Math.Clamp(im, 0, Caps.InactiveMaxMinutes).ToString()]);
        if (s.Lights is bool li && Caps.Lights) args.AddRange(["-l", li ? "1" : "0"]);
        if (s.VoicePrompts is bool vp && Caps.VoicePrompts) args.AddRange(["-v", vp ? "1" : "0"]);
        if (s.RotateToMute is bool rm && Caps.RotateToMute) args.AddRange(["-r", rm ? "1" : "0"]);
        if (s.MicVolume is int mv && Caps.MicVolume) args.AddRange(["--microphone-volume", Math.Clamp(mv, 0, 128).ToString()]);
        if (s.EqPreset is int ep && ep >= 0 && ep < Caps.EqPresets.Length) args.AddRange(["-p", ep.ToString()]);
        if (s.Anc is int anc && Caps.Anc) args.AddRange(["--anc", Math.Clamp(anc, 0, 2).ToString()]);
        if (args.Count == 2) return;
        Run(args);
        _cacheAt = double.NegativeInfinity; // read fresh next time
    }

    static string? Run(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(Exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (UseTestDevice) psi.ArgumentList.Add("--test-device");
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(10_000)) { p.Kill(); return null; }
            return output.Result;
        }
        catch (Exception e)
        {
            AppConfig.Log?.Invoke($"headsetcontrol: {e.Message}");
            return null;
        }
    }

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static string Hex(JsonElement e, string name) => (Str(e, name) ?? "0").Replace("0x", "").PadLeft(4, '0').ToLowerInvariant();

    public void Dispose() { }
}

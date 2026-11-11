using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace RigControl.Core;

/// <summary>
/// Runs the bundled liquidctl (embedded Python + bridge.py) as a helper process for fan/pump devices that have
/// no native driver here. Talks one JSON line per request. Not thread-safe: the engine thread owns it.
/// </summary>
sealed class LiquidctlBridge : IDisposable
{
    // Vendors with liquidctl fan/pump drivers: NZXT, Corsair, Asetek, ASUS, Lian Li, MSI, Aquacomputer, EVGA, NZXT PSU (7793).
    static readonly HashSet<string> Vendors = ["1E71", "1B1C", "2433", "0B05", "0CF2", "0DB0", "0C70", "3842", "7793"];
    // Lighting-only devices of those vendors: never worth starting the helper for (NZXT HUE 2, HUE 2 Ambient, 2023 RGB controllers).
    static readonly HashSet<(int vid, int pid)> LightingOnly = [(0x1E71, 0x2001), (0x1E71, 0x2002), (0x1E71, 0x2012), (0x1E71, 0x2021)];

    readonly string _dir;
    readonly List<string> _exclude;
    readonly List<string> _problems;
    Process? _proc;
    double _lastStart = double.NegativeInfinity;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly List<JsonObject> _pendingSet = [], _pendingRelease = [];

    public List<LiquidctlDevice> Devices { get; } = [];

    LiquidctlBridge(string dir, List<string> exclude, List<string> problems)
    {
        _dir = dir;
        _exclude = exclude;
        _problems = problems;
    }

    public static string RuntimeDir => Path.Combine(AppContext.BaseDirectory, "liquidctl");
    static string Python => Path.Combine(RuntimeDir, "python", "python.exe");
    public static bool Available => File.Exists(Python) && File.Exists(Path.Combine(RuntimeDir, "bridge.py"));

    /// <summary>
    /// Starts the helper when a candidate device was ever plugged in (cheap registry check) or a device was
    /// switched to liquidctl. Returns null when there's nothing for it to do.
    /// </summary>
    public static LiquidctlBridge? StartIfUseful(List<string> exclude, ISet<(int vid, int pid)> native, bool forced, List<string> problems)
    {
        if (!Available) { AppConfig.Log?.Invoke("liquidctl: runtime not bundled"); return null; }
        var candidate = !forced && CandidatePresent(native);
        AppConfig.Log?.Invoke($"liquidctl: forced={forced} candidate={candidate}");
        if (!forced && !candidate) return null;
        var bridge = new LiquidctlBridge(RuntimeDir, exclude, problems);
        bridge.Start();
        AppConfig.Log?.Invoke($"liquidctl: helper found {bridge.Devices.Count} device(s): {string.Join(", ", bridge.Devices.Select(d => d.Id[^Math.Min(4, d.Id.Length)..]))}");
        return bridge.Devices.Count > 0 ? bridge : Dispose(bridge);
    }

    static LiquidctlBridge? Dispose(LiquidctlBridge b) { b.Dispose(); return null; }

    static bool CandidatePresent(ISet<(int vid, int pid)> native)
    {
        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            foreach (var name in usb?.GetSubKeyNames() ?? [])
            {
                // "VID_1E71&PID_2020" (sometimes with "&MI_00")
                if (name.Length < 17 || !name.StartsWith("VID_", StringComparison.OrdinalIgnoreCase)) continue;
                var vid = name.Substring(4, 4).ToUpperInvariant();
                if (!Vendors.Contains(vid)) continue;
                if (int.TryParse(name.AsSpan(13, 4), System.Globalization.NumberStyles.HexNumber, null, out var pid)
                    && (native.Contains((Convert.ToInt32(vid, 16), pid)) || LightingOnly.Contains((Convert.ToInt32(vid, 16), pid)))) continue;
                return true;
            }
        }
        catch { return true; } // can't tell: let liquidctl look
        return false;
    }

    void Start()
    {
        _lastStart = _clock.Elapsed.TotalSeconds;
        var psi = new ProcessStartInfo(Python)
        {
            WorkingDirectory = _dir,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-I"); // isolated: ignore PYTHON* env vars and user site-packages
        psi.ArgumentList.Add("-B"); // no __pycache__ beside the app
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add("-B"); // never write .pyc files next to the app
        psi.ArgumentList.Add(Path.Combine(_dir, "bridge.py"));
        try
        {
            _proc = Process.Start(psi)!;
            _proc.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) Trace.WriteLine($"liquidctl: {e.Data}"); };
            _proc.BeginErrorReadLine();
            var reply = Call(new JsonObject { ["op"] = "start", ["exclude"] = new JsonArray([.. _exclude.Select(e => (JsonNode)e)]) }, 30_000);
            var seen = Devices.ToDictionary(d => d.Id);
            foreach (var d in reply?["devices"]?.AsArray() ?? [])
            {
                var id = (string)d!["id"]!;
                if (seen.ContainsKey(id)) continue; // restart: keep the same objects
                Devices.Add(new LiquidctlDevice(this, d.AsObject()));
            }
        }
        catch (Exception e)
        {
            _problems.Add($"liquidctl: {e.Message}");
            Kill();
        }
    }

    JsonNode? Call(JsonObject request, int timeoutMs = 5000)
    {
        if (_proc is null || _proc.HasExited) throw new InvalidOperationException("liquidctl helper isn't running");
        _proc.StandardInput.WriteLine(request.ToJsonString());
        _proc.StandardInput.Flush();
        var read = _proc.StandardOutput.ReadLineAsync();
        if (!read.Wait(timeoutMs)) { Kill(); throw new TimeoutException("liquidctl helper stopped answering"); }
        var node = read.Result is string line ? JsonNode.Parse(line) : null;
        if (node?["error"] is JsonNode err) throw new InvalidOperationException((string?)err);
        return node;
    }

    public void Update()
    {
        if (_proc is null || _proc.HasExited)
        {
            foreach (var d in Devices) d.MarkLost();
            // Restart at most every 30 s.
            if (_clock.Elapsed.TotalSeconds - _lastStart > 30) Start();
            return;
        }
        try
        {
            var status = Call(new JsonObject { ["op"] = "status" })?["status"]?.AsObject();
            foreach (var d in Devices) d.Apply(status?[d.Id]);
        }
        catch (Exception e) { Trace.WriteLine($"liquidctl status: {e.Message}"); }
    }

    internal void QueueSet(string dev, string channel, double duty) =>
        _pendingSet.Add(new JsonObject { ["dev"] = dev, ["channel"] = channel, ["duty"] = duty });

    internal void QueueRelease(string dev, string channel) =>
        _pendingRelease.Add(new JsonObject { ["dev"] = dev, ["channel"] = channel });

    /// <summary>Sends this tick's duties (and releases) in one request each.</summary>
    public void Flush()
    {
        try
        {
            if (_pendingSet.Count > 0) Report(Call(new JsonObject { ["op"] = "set", ["items"] = new JsonArray([.. _pendingSet]) }));
            if (_pendingRelease.Count > 0) Call(new JsonObject { ["op"] = "release", ["items"] = new JsonArray([.. _pendingRelease]) });
        }
        catch (Exception e)
        {
            foreach (var d in Devices) d.Error = e.Message;
        }
        finally
        {
            _pendingSet.Clear();
            _pendingRelease.Clear();
        }
    }

    void Report(JsonNode? reply)
    {
        var errors = reply?["errors"]?.AsObject();
        foreach (var d in Devices) d.Error = errors?[d.Id] is JsonNode e ? (string?)e : null;
    }

    void Kill()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(); } catch { }
    }

    public void Dispose()
    {
        try
        {
            if (_proc is { HasExited: false })
            {
                Call(new JsonObject { ["op"] = "quit" }, 3000);
                _proc.WaitForExit(3000);
            }
        }
        catch { }
        Kill();
        _proc?.Dispose();
    }
}

/// <summary>A device found by the liquidctl helper, with its fan/pump channels and temperature sensors.</summary>
sealed class LiquidctlDevice
{
    public LiquidctlBridge Bridge { get; }
    public string Id { get; }
    public string Description { get; }
    public string? Serial { get; }
    public long FanControlId { get; }
    public List<LiquidctlFan> Fans { get; } = [];
    public List<TempSensor> Temps { get; } = [];
    public string? Error { get; set; }

    public LiquidctlDevice(LiquidctlBridge bridge, JsonObject d)
    {
        Bridge = bridge;
        Id = (string)d["id"]!;
        Description = (string)d["description"]!;
        Serial = (string?)d["serial"];
        FanControlId = (long)d["fancontrol_id"]!;
        var label = $"{Description} · {(Id.Length > 4 ? Id[^4..] : Id)}";
        foreach (var (channel, info) in d["channels"]!.AsObject())
            Fans.Add(new LiquidctlFan(this, channel, (string?)info?["label"] ?? channel, label));
        foreach (var (name, _) in d["temps"]!.AsObject())
            Temps.Add(new TempSensor($"liquidctl/{Id}/temp/{name}", name, label));
        Apply(d);
    }

    public void Apply(JsonNode? status)
    {
        if (status is null || status["error"] is not null) { MarkLost(); return; }
        var channels = status["channels"]?.AsObject();
        foreach (var f in Fans)
        {
            var c = channels?[f.Channel];
            f.Rpm = c?["rpm"] is JsonNode r ? (float)r.GetValue<double>() : null;
            if (c?["duty"] is JsonNode du) f.Duty = (float)du.GetValue<double>();
        }
        var temps = status["temps"]?.AsObject();
        foreach (var t in Temps)
            t.Value = temps?[t.Name] is JsonNode v ? (float)v.GetValue<double>() : null;
    }

    public void MarkLost()
    {
        foreach (var f in Fans) f.Rpm = null;
        foreach (var t in Temps) t.Value = null; // curves on these go to 100%
    }
}

sealed class LiquidctlFan(LiquidctlDevice dev, string channel, string label, string device)
    : FanChannel($"liquidctl/{dev.Id}/{channel}", label, device)
{
    public LiquidctlDevice Owner => dev;
    public string Channel => channel;
    /// <summary>FanControl.Liquidctl's id for this channel: "liquidctl/&lt;description without spaces&gt;#&lt;id&gt;/&lt;status key&gt;duty".</summary>
    public string FanControlAlias => $"liquidctl/{dev.Description.Replace(" ", "")}#{dev.FanControlId}/{Name.Replace(" ", "")}duty";

    public override void Set(float percent)
    {
        // Surface the last write error on this fan (the engine shows exceptions per fan), then retry.
        if (dev.Error is string error) { dev.Error = null; throw new InvalidOperationException(error); }
        dev.Bridge.QueueSet(dev.Id, channel, Math.Clamp(percent, 0, 100));
        Duty = percent;
    }

    public override void Release() => dev.Bridge.QueueRelease(dev.Id, channel);
}

using System.Text.Json;
using HidSharp;

namespace RigControl.Core;

/// <summary>
/// Razer mice: DPI, DPI stages, polling rate, sleep timeout, low-battery warning and battery, over Razer's 90-byte
/// feature-report protocol. Which command variant and transaction id each mouse needs comes from razer-mice.json,
/// generated from openrazer (tools\gen-razer-devices.py); a setting missing there is never sent.
/// </summary>
public sealed class RazerMouse : IPeripheral
{
    const int ReportLength = 90;
    const byte NoStore = 0x00, VarStore = 0x01;
    const byte StatusOk = 0x02, StatusBusy = 0x01, StatusNotSupported = 0x05;

    sealed class Command { public string Cmd { get; set; } = ""; public string Tid { get; set; } = "0xff"; public bool VarStore { get; set; } }
    sealed class Entry
    {
        public string Name { get; set; } = "";
        public int? DpiMax { get; set; }
        public int[]? PollRates { get; set; }
        public Dictionary<string, JsonElement> Extra { get; set; } = [];
        public Command? Get(string feature) =>
            Extra.TryGetValue(feature, out var e) ? e.Deserialize<Command>(JsonOptions) : null;
    }

    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    static readonly Lazy<Dictionary<int, Entry>> Table = new(LoadTable);

    readonly FeatureHandle _stream;
    readonly Entry _entry;
    readonly object _io = new();

    public string Key { get; }
    public string Name => _entry.Name;
    public string Kind => "Mouse";
    public string Source => _stagesDetected
        ? "Razer protocol · openrazer data · DPI stages detected on the device"
        : "Razer protocol · device data from openrazer";
    public PeripheralCaps Caps { get; private set; }

    // DPI stages found by probing (openrazer doesn't list them for this mouse): the transaction id that answered.
    string? _stagesTid;
    bool _stagesDetected;

    RazerMouse(int pid, FeatureHandle stream, Entry entry)
    {
        _stream = stream;
        _entry = entry;
        Key = $"razer/{pid:X4}";
        Caps = new PeripheralCaps
        {
            DpiMax = Has("dpi_write") && Get("dpi_write")!.Cmd == "set_dpi_xy" ? entry.DpiMax ?? 16000 : 0,
            MaxStages = Has("stages_write") ? 5 : 0,
            PollRates = Get("poll_write")?.Cmd == "set_polling_rate" ? entry.PollRates ?? [125, 500, 1000] : [],
            SleepTimeout = Has("idle_write"),
            LowBatteryThreshold = Has("lowbat_write"),
            Battery = Has("battery"),
            Charging = Has("charging"),
        };
    }

    bool Has(string feature) => Get(feature) is not null;
    Command? Get(string feature) => _stagesTid is not null && feature is "stages_read" or "stages_write"
        ? new Command { Cmd = feature == "stages_read" ? "get_dpi_stages" : "set_dpi_stages", Tid = _stagesTid, VarStore = true }
        : _entry.Get(feature);

    /// <summary>
    /// Some mice have DPI stages openrazer doesn't list (e.g. the wired DeathAdder V2, whose siblings have them).
    /// Probe once, read-only; enable stages only when the mouse answers with a sane table. Every listed mouse
    /// reads and writes stages with the same transaction id, so the one that answered is used for both.
    /// </summary>
    void DetectStages()
    {
        if (Has("stages_write") || ProbeStages() is not { } hit) return;
        _stagesTid = hit.tid;
        _stagesDetected = true;
        Caps = new PeripheralCaps
        {
            DpiMax = Caps.DpiMax, MaxStages = 5, PollRates = Caps.PollRates, SleepTimeout = Caps.SleepTimeout,
            LowBatteryThreshold = Caps.LowBatteryThreshold, Battery = Caps.Battery, Charging = Caps.Charging,
        };
        AppConfig.Log?.Invoke($"razer: {Name} answers the DPI stage table (tid {hit.tid}), enabling stages");
    }

    static Dictionary<int, Entry> LoadTable()
    {
        using var s = typeof(RazerMouse).Assembly.GetManifestResourceStream("RigControl.Core.Razer.razer-mice.json")!;
        using var doc = JsonDocument.Parse(s);
        var table = new Dictionary<int, Entry>();
        foreach (var d in doc.RootElement.GetProperty("devices").EnumerateObject())
        {
            var e = new Entry();
            foreach (var p in d.Value.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "name": e.Name = p.Value.GetString() ?? ""; break;
                    case "dpiMax": e.DpiMax = p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetInt32() : null; break;
                    case "pollRates": e.PollRates = p.Value.ValueKind == JsonValueKind.Array ? [.. p.Value.EnumerateArray().Select(x => x.GetInt32())] : null; break;
                    default: e.Extra[p.Name] = p.Value.Clone(); break;
                }
            }
            table[Convert.ToInt32(d.Name, 16)] = e;
        }
        return table;
    }

    /// <summary>For checks: the generated table's entry for a product id (name, DPI max, features).</summary>
    internal static (string name, int? dpiMax, string[] features)? Describe(int pid) =>
        Table.Value.TryGetValue(pid, out var e) ? (e.Name, e.DpiMax, e.Extra.Keys.Order().ToArray()) : null;

    /// <summary>Every supported Razer mouse plugged in, opened on its 90-byte control interface.</summary>
    public static List<IPeripheral> FindAll()
    {
        var found = new List<IPeripheral>();
        var seen = new HashSet<int>();
        foreach (var dev in DeviceList.Local.GetHidDevices(0x1532))
        {
            if (seen.Contains(dev.ProductID) || !Table.Value.TryGetValue(dev.ProductID, out var entry)) continue;
            try
            {
                // Only the control interface takes the 90-byte feature report (+1 byte report id on Windows).
                if (dev.GetMaxFeatureReportLength() < ReportLength + 1) continue;
                // The control interface is also the pointer interface Windows holds, so open it feature-only.
                if (FeatureHandle.Open(dev.DevicePath) is not { } stream) continue;
                var mouse = new RazerMouse(dev.ProductID, stream, entry);
                mouse.DetectStages();
                found.Add(mouse);
                seen.Add(dev.ProductID);
            }
            catch { /* another collection of the same mouse */ }
        }
        return found;
    }

    /// <summary>
    /// Sends one request and returns the response's 80 argument bytes. Retries when the mouse is busy or answers a
    /// different command (another app, e.g. the lighting engine, may be talking to it at the same time).
    /// </summary>
    byte[]? Transact(Command c, byte cls, byte id, byte size, ReadOnlySpan<byte> args)
    {
        var req = new byte[ReportLength + 1]; // [0] = report id 0
        var r = req.AsSpan(1);
        r[1] = Convert.ToByte(c.Tid, 16);
        r[5] = size;
        r[6] = cls;
        r[7] = id;
        args[..Math.Min(args.Length, 80)].CopyTo(r[8..]);
        byte crc = 0;
        for (int i = 2; i < 88; i++) crc ^= r[i];
        r[88] = crc;

        lock (_io)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                _stream.SetFeature(req);
                Thread.Sleep(attempt == 0 ? 5 : 20);
                var resp = new byte[ReportLength + 1];
                _stream.GetFeature(resp);
                var p = resp.AsSpan(1);
                if (p[0] == StatusBusy || p[6] != cls || p[7] != id) continue;
                if (p[0] == StatusOk) return p.Slice(8, 80).ToArray();
                if (p[0] == StatusNotSupported) return null;
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// Read-only probe for DPI stages on mice openrazer doesn't list them for: tries the stage-table read with the
    /// transaction ids its sibling models use and accepts it only if the answer is a sane table. Returns the id that worked.
    /// </summary>
    internal (string tid, int active, List<int> stages)? ProbeStages()
    {
        foreach (var tid in new[] { "0xff", "0x3f", "0x1f" })
        {
            if (Transact(new Command { Cmd = "get_dpi_stages", Tid = tid }, 0x04, 0x86, 0x26, [VarStore]) is not { } s) continue;
            int count = s[2], active = s[1];
            if (count is < 1 or > 5 || active < 1 || active > count) continue;
            var list = Enumerable.Range(0, count).Select(i => s[4 + 7 * i] << 8 | s[5 + 7 * i]).ToList();
            if (list.All(v => v >= 100 && v <= Math.Max(Caps.DpiMax, 100))) return (tid, active, list);
        }
        return null;
    }

    public PeripheralState Read()
    {
        int? dpi = null, poll = null, idle = null, lowbat = null, battery = null, active = null;
        bool? charging = null;
        List<int>? stages = null;

        if (Get("dpi_read") is { Cmd: "get_dpi_xy" } dr && Transact(dr, 0x04, 0x85, 0x07, [dr.VarStore ? VarStore : NoStore]) is { } a)
            dpi = a[1] << 8 | a[2];
        if (Get("stages_read") is { Cmd: "get_dpi_stages" } sr && Transact(sr, 0x04, 0x86, 0x26, [VarStore]) is { } s)
        {
            active = s[1];
            stages = [];
            for (int i = 0; i < Math.Min((int)s[2], 5); i++) stages.Add(s[4 + 7 * i] << 8 | s[5 + 7 * i]);
        }
        if (Get("poll_read") is { } pr)
        {
            if (pr.Cmd == "get_polling_rate" && Transact(pr, 0x00, 0x85, 0x01, []) is { } p)
                poll = p[0] switch { 0x01 => 1000, 0x02 => 500, 0x08 => 125, _ => null };
            else if (pr.Cmd == "get_polling_rate2" && Transact(pr, 0x00, 0xC0, 0x01, []) is { } p2)
                poll = p2[1] switch { 0x01 => 8000, 0x02 => 4000, 0x04 => 2000, 0x08 => 1000, 0x10 => 500, 0x20 => 250, 0x40 => 125, _ => null };
        }
        if (Get("idle_read") is { } ir && Transact(ir, 0x07, 0x83, 0x02, []) is { } t) idle = t[0] << 8 | t[1];
        if (Get("lowbat_read") is { } lr && Transact(lr, 0x07, 0x81, 0x01, []) is { } l) lowbat = (int)Math.Round(l[0] * 100 / 255.0);
        if (Get("battery") is { } br && Transact(br, 0x07, 0x80, 0x02, []) is { } b) battery = (int)Math.Round(b[1] * 100 / 255.0);
        if (Get("charging") is { } cr && Transact(cr, 0x07, 0x84, 0x02, []) is { } ch) charging = ch[1] != 0;

        return new PeripheralState
        {
            Dpi = dpi, Stages = stages, ActiveStage = active, PollRate = poll, SleepSeconds = idle,
            LowBatteryPercent = lowbat, BatteryPercent = battery, Charging = charging,
        };
    }

    public void Apply(PeripheralSettings want, PeripheralState now)
    {
        // Stage table first: writing it can move the DPI to the active stage; the DPI write then settles it.
        if (want.Stages is { Count: > 0 } stages && Caps.MaxStages > 0 && Get("stages_write") is { } sw
            && (now.Stages is null || !stages.SequenceEqual(now.Stages) || want.ActiveStage != now.ActiveStage))
        {
            var list = stages.Take(Caps.MaxStages).Select(v => Math.Clamp(v, Caps.DpiMin, Math.Max(Caps.DpiMax, Caps.DpiMin))).ToList();
            var args = new List<byte> { VarStore, (byte)Math.Clamp(want.ActiveStage ?? 1, 1, list.Count), (byte)list.Count };
            for (int i = 0; i < list.Count; i++)
                args.AddRange([(byte)i, (byte)(list[i] >> 8), (byte)list[i], (byte)(list[i] >> 8), (byte)list[i], 0, 0]);
            Transact(sw, 0x04, 0x06, 0x26, [.. args]);
        }
        if (want.Dpi is int dpi && Caps.Dpi && dpi != now.Dpi && Get("dpi_write") is { Cmd: "set_dpi_xy" } dw)
        {
            dpi = Math.Clamp(dpi, Caps.DpiMin, Caps.DpiMax);
            Transact(dw, 0x04, 0x05, 0x07, [VarStore, (byte)(dpi >> 8), (byte)dpi, (byte)(dpi >> 8), (byte)dpi, 0, 0]);
        }
        if (want.PollRate is int rate && rate != now.PollRate && Caps.PollRates.Contains(rate) && Get("poll_write") is { Cmd: "set_polling_rate" } pw)
            Transact(pw, 0x00, 0x05, 0x01, [rate switch { 1000 => (byte)0x01, 125 => (byte)0x08, _ => (byte)0x02 }]);
        if (want.SleepSeconds is int idle && idle != now.SleepSeconds && Get("idle_write") is { } iw)
        {
            idle = Math.Clamp(idle, Caps.SleepMinSeconds, Caps.SleepMaxSeconds);
            Transact(iw, 0x07, 0x03, 0x02, [(byte)(idle >> 8), (byte)idle]);
        }
        if (want.LowBatteryPercent is int lb && lb != now.LowBatteryPercent && Get("lowbat_write") is { } lw)
            Transact(lw, 0x07, 0x01, 0x01, [(byte)Math.Clamp((int)Math.Round(lb * 255 / 100.0), 0x0C, 0x3F)]);
    }

    public void Dispose()
    {
        try { _stream.Dispose(); } catch { }
    }
}

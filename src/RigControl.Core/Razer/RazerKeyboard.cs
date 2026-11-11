using System.Text.Json;
using HidSharp;

namespace RigControl.Core;

/// <summary>
/// Razer keyboards: game mode (the keyboard's own Windows-key lock, Fn+F10), polling rate (up to 8000 Hz),
/// keyswitch optimisation (gaming/typing) and battery, over the same 90-byte feature reports as the mice. Which
/// command and transaction id each keyboard takes comes from razer-keyboards.json (tools\gen-razer-keyboards.py,
/// from openrazer); a setting missing there is never sent.
/// </summary>
public sealed class RazerKeyboard : IPeripheral
{
    const byte GameLed = 0x08, NoStore = 0x00, VarStore = 0x01;
    const byte StatusOk = 0x02, StatusBusy = 0x01;

    /// <summary>One request (transaction id, class, command, data size, arguments) → the 80 argument bytes of the reply, or null.</summary>
    internal delegate byte[]? Transport(byte tid, byte cls, byte id, byte size, byte[] args);

    internal sealed class Command { public string Tid { get; set; } = "0xff"; public bool VarStore { get; set; } }
    internal sealed class Entry
    {
        public string Name { get; set; } = "";
        public int[]? PollRates { get; set; }
        public Dictionary<string, Command> Features { get; } = [];
    }

    static readonly Lazy<Dictionary<int, Entry>> Table = new(() =>
    {
        using var s = typeof(RazerKeyboard).Assembly.GetManifestResourceStream("RigControl.Core.Razer.razer-keyboards.json")!;
        using var doc = JsonDocument.Parse(s);
        var table = new Dictionary<int, Entry>();
        foreach (var d in doc.RootElement.GetProperty("devices").EnumerateObject())
        {
            var e = new Entry();
            foreach (var p in d.Value.EnumerateObject())
            {
                if (p.Name == "name") e.Name = p.Value.GetString() ?? "";
                else if (p.Name == "pollRates") e.PollRates = p.Value.ValueKind == JsonValueKind.Array ? [.. p.Value.EnumerateArray().Select(x => x.GetInt32())] : null;
                else e.Features[p.Name] = new Command
                {
                    Tid = p.Value.GetProperty("tid").GetString()!,
                    VarStore = p.Value.TryGetProperty("varstore", out var v) && v.GetBoolean(),
                };
            }
            table[Convert.ToInt32(d.Name, 16)] = e;
        }
        return table;
    });

    // polling_rate2: one bit per rate
    static readonly (int hz, byte code)[] Rates = [(8000, 0x01), (4000, 0x02), (2000, 0x04), (1000, 0x08), (500, 0x10), (250, 0x20), (125, 0x40)];

    readonly Entry _entry;
    readonly Transport _io;
    readonly IDisposable? _owner;

    public string Key { get; }
    public string Name => _entry.Name;
    public string Kind => "Keyboard";
    public string Source => "Razer protocol · device data from openrazer";
    public PeripheralCaps Caps { get; }

    internal RazerKeyboard(int pid, Entry entry, Transport io, IDisposable? owner = null)
    {
        _entry = entry;
        _io = io;
        _owner = owner;
        Key = $"razer/{pid:X4}";
        Caps = new PeripheralCaps
        {
            GameMode = Has("game_write"),
            KeyswitchOptimization = Has("keyswitch_write"),
            PollRates = Has("poll_write") ? entry.PollRates ?? [125, 500, 1000] : [],
            Battery = Has("battery"),
            Charging = Has("charging"),
        };
    }

    internal static Entry? Lookup(int pid) => Table.Value.GetValueOrDefault(pid);
    bool Has(string f) => _entry.Features.ContainsKey(f);

    byte[]? Send(string feature, byte cls, byte id, byte size, params byte[] args) =>
        _entry.Features.TryGetValue(feature, out var c) ? _io(Convert.ToByte(c.Tid, 16), cls, id, size, args) : null;

    byte Storage(string feature) => _entry.Features.TryGetValue(feature, out var c) && c.VarStore ? VarStore : NoStore;

    public static List<IPeripheral> FindAll()
    {
        var found = new List<IPeripheral>();
        foreach (var dev in DeviceList.Local.GetHidDevices(0x1532))
        {
            if (found.Any(f => f.Key == $"razer/{dev.ProductID:X4}") || Lookup(dev.ProductID) is not { } entry) continue;
            try
            {
                // The control interface takes the 90-byte feature report; Windows holds it, so open it feature-only.
                if (dev.GetMaxFeatureReportLength() < 91 || FeatureHandle.Open(dev.DevicePath) is not { } h) continue;
                found.Add(new RazerKeyboard(dev.ProductID, entry, HidTransport(h), h));
            }
            catch { /* another collection of the same keyboard */ }
        }
        return found;
    }

    /// <summary>Razer's 90-byte report: status, transaction id, data size, class, command, 80 arguments, XOR checksum.</summary>
    static Transport HidTransport(FeatureHandle stream)
    {
        var gate = new object();
        return (tid, cls, id, size, args) =>
        {
            var req = new byte[91];
            var r = req.AsSpan(1);
            r[1] = tid;
            r[5] = size;
            r[6] = cls;
            r[7] = id;
            args.AsSpan(0, Math.Min(args.Length, 80)).CopyTo(r[8..]);
            byte crc = 0;
            for (int i = 2; i < 88; i++) crc ^= r[i];
            r[88] = crc;
            lock (gate)
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    stream.SetFeature(req);
                    Thread.Sleep(attempt == 0 ? 5 : 20);
                    var resp = new byte[91];
                    stream.GetFeature(resp);
                    var p = resp.AsSpan(1);
                    if (p[0] == StatusBusy || p[6] != cls || p[7] != id) continue;
                    return p[0] == StatusOk ? p.Slice(8, 80).ToArray() : null;
                }
            }
            return null;
        };
    }

    public PeripheralState Read()
    {
        bool? game = null, gaming = null, charging = null;
        int? poll = null, battery = null;
        if (Send("game_read", 0x03, 0x80, 0x03, Storage("game_read"), GameLed) is { } g) game = g[2] != 0;
        if (Send("poll_read", 0x00, 0xC0, 0x01) is { } p) poll = Rates.FirstOrDefault(r => r.code == p[1]).hz is int hz and > 0 ? hz : null;
        if (Send("keyswitch_read", 0x02, 0x82, 0x04) is { } k) gaming = k[1] != 0x14; // 0x14 = typing
        if (Send("battery", 0x07, 0x80, 0x02) is { } b) battery = (int)Math.Round(b[1] * 100 / 255.0);
        if (Send("charging", 0x07, 0x84, 0x02) is { } c) charging = c[1] != 0;
        return new PeripheralState { GameMode = game, PollRate = poll, KeyswitchGaming = gaming, BatteryPercent = battery, Charging = charging };
    }

    public void Apply(PeripheralSettings want, PeripheralState now)
    {
        if (want.GameMode is bool on && Caps.GameMode && on != now.GameMode)
            Send("game_write", 0x03, 0x00, 0x03, Storage("game_write"), GameLed, (byte)(on ? 1 : 0));
        if (want.PollRate is int hz && hz != now.PollRate && Caps.PollRates.Contains(hz) && Rates.FirstOrDefault(r => r.hz == hz).code is byte code and > 0)
            Send("poll_write", 0x00, 0x40, 0x02, 0x00, code);
        if (want.KeyswitchGaming is bool gaming && Caps.KeyswitchOptimization && gaming != now.KeyswitchGaming)
        {
            // Two commands, as Synapse sends them: the live state, then the stored one.
            if (gaming) { Send("keyswitch_write", 0x02, 0x02, 0x04); Send("keyswitch_write", 0x02, 0x15, 0x05, 0x01); }
            else { Send("keyswitch_write", 0x02, 0x02, 0x04, 0x00, 0x14, 0x00, 0x28, 0x00); Send("keyswitch_write", 0x02, 0x15, 0x05, 0x01, 0x00, 0x14, 0x00, 0x28, 0x00); }
        }
    }

    public void Dispose() => _owner?.Dispose();
}

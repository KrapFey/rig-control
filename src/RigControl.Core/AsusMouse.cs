using System.Diagnostics;
using System.Text.Json;
using HidSharp;

namespace RigControl.Core;

/// <summary>
/// ASUS ROG and TUF mice: DPI presets (2 or 4), the active preset and polling rate, over ASUS's 64-byte vendor
/// reports. Protocol and per-model data (DPI range, preset count, quirks) from libratbag's ASUS driver
/// (libratbag-mice.json, tools\gen-libratbag-devices.py). Like libratbag, changes are followed by the mouse's own
/// save command; they're only sent when they differ from what the mouse reports.
/// </summary>
public sealed class AsusMouse : IPeripheral
{
    const ushort GetProfileData = 0x0012, GetSettings = 0x0412, SetSetting = 0x3151, Save = 0x0350;
    const ushort StatusError = 0xaaff; // invalid request, or the mouse is asleep / out of range
    static readonly int[] Rates = [125, 250, 500, 1000];

    /// <summary>Sends one 64-byte report (command in the first two bytes) and returns the 64-byte reply, or null.</summary>
    internal delegate byte[]? Transport(byte[] request);

    internal sealed record Entry(string Name, int[][] Ids, int Presets, int[] Dpi, string[] Quirks);
    static readonly Lazy<List<Entry>> Table = new(() =>
    {
        using var s = typeof(AsusMouse).Assembly.GetManifestResourceStream("RigControl.Core.libratbag-mice.json")!;
        using var doc = JsonDocument.Parse(s);
        return [.. doc.RootElement.GetProperty("mice").EnumerateArray()
            .Where(m => m.GetProperty("driver").GetString() == "asus")
            .Select(m => new Entry(
                m.GetProperty("name").GetString()!,
                [.. m.GetProperty("ids").EnumerateArray().Select(i => new[] { i[0].GetInt32(), i[1].GetInt32() })],
                m.GetProperty("presets").GetInt32(),
                [.. m.GetProperty("dpi").EnumerateArray().Select(x => x.GetInt32())],
                [.. m.GetProperty("quirks").EnumerateArray().Select(x => x.GetString()!)]))];
    });

    readonly Transport _io;
    readonly Entry _entry;
    readonly bool _doubleDpi;
    readonly IDisposable? _owner;

    public string Key { get; }
    public string Name => _entry.Name;
    public string Kind => "Mouse";
    public string Source => "ASUS protocol · from libratbag";
    public PeripheralCaps Caps { get; }

    internal AsusMouse(Entry entry, int pid, Transport io, IDisposable? owner = null)
    {
        _entry = entry;
        _io = io;
        _owner = owner;
        _doubleDpi = entry.Quirks.Contains("DOUBLE_DPI");
        Key = $"asus/{pid:X4}";
        Caps = new PeripheralCaps
        {
            DpiMin = entry.Dpi[0], DpiMax = entry.Dpi[1], DpiStep = entry.Dpi[2],
            MaxStages = entry.Presets,
            StageSelectable = false, // only the mouse's DPI button switches presets
            PollRates = Rates,
        };
    }

    internal static Entry? Lookup(int vid, int pid) => Table.Value.FirstOrDefault(e => e.Ids.Any(i => i[0] == vid && i[1] == pid));

    public static List<IPeripheral> FindAll()
    {
        var found = new List<IPeripheral>();
        foreach (var dev in DeviceList.Local.GetHidDevices(0x0B05))
        {
            try
            {
                if (Lookup(dev.VendorID, dev.ProductID) is not { } entry || found.Any(f => f.Key == $"asus/{dev.ProductID:X4}")) continue;
                // The command interface takes 64-byte reports (the report id is the command's first byte).
                if (dev.GetMaxOutputReportLength() != 64 || dev.GetMaxInputReportLength() != 64) continue;
                if (!dev.TryOpen(out HidStream s)) continue;
                var io = HidTransport(s);
                // Only the right interface answers the profile query.
                if (Query(io, GetProfileData) is null) { s.Dispose(); continue; }
                found.Add(new AsusMouse(entry, dev.ProductID, io, s));
            }
            catch (Exception e) { AppConfig.Log?.Invoke($"asus: {e.Message}"); }
        }
        return found;
    }

    static Transport HidTransport(HidStream s)
    {
        var gate = new object();
        s.ReadTimeout = 300;
        return req =>
        {
            lock (gate)
            {
                s.Write(req);
                var r = new byte[64];
                try { return s.Read(r, 0, r.Length) == 64 ? r : null; } catch (TimeoutException) { return null; }
            }
        };
    }

    /// <summary>One request; null when nothing came back or the mouse answered "error" (asleep, unsupported).</summary>
    static byte[]? Query(Transport io, ushort cmd, params byte[] parameters)
    {
        var req = new byte[64];
        req[0] = (byte)cmd;
        req[1] = (byte)(cmd >> 8);
        parameters.AsSpan(0, Math.Min(parameters.Length, 62)).CopyTo(req.AsSpan(2));
        var r = io(req);
        return r is null || (r[0] | r[1] << 8) == StatusError ? null : r;
    }

    /// <summary>Raw preset value → DPI (50-DPI units starting at 50; doubled on some models).</summary>
    internal int ToDpi(int raw) => (raw * 50 + 50) * (_doubleDpi ? 2 : 1);
    internal int FromDpi(int dpi) => ((_doubleDpi ? dpi / 2 : dpi) - 50) / 50;

    public PeripheralState Read()
    {
        if (Query(_io, GetSettings, 0) is not { } s) return new PeripheralState { Connected = false };
        // Presets are little-endian 16-bit from byte 4, followed by the polling-rate index.
        var stages = Enumerable.Range(0, _entry.Presets).Select(i => ToDpi(s[4 + 2 * i] | s[5 + 2 * i] << 8)).ToList();
        int rate = s[4 + 2 * _entry.Presets] & 0x07;
        int? active = Query(_io, GetProfileData) is { } p && p[11] is > 0 and var a && a <= stages.Count ? a : null;
        return new PeripheralState
        {
            Connected = true,
            Stages = stages,
            ActiveStage = active,
            Dpi = active is int i ? stages[i - 1] : null,
            PollRate = rate < Rates.Length ? Rates[rate] : null,
        };
    }

    public void Apply(PeripheralSettings want, PeripheralState now)
    {
        if (now.Connected == false) return;
        bool changed = false;
        var target = new List<int>(now.Stages ?? []);
        if (want.Stages is { Count: > 0 } ws)
            for (int i = 0; i < Math.Min(ws.Count, target.Count); i++) target[i] = ws[i];
        else if (want.Dpi is int dpi && now.ActiveStage is int a && a <= target.Count)
            target[a - 1] = dpi;
        for (int i = 0; i < target.Count; i++)
        {
            int dpi = Math.Clamp(target[i], Caps.DpiMin, Caps.DpiMax);
            if (now.Stages is { } cur && i < cur.Count && cur[i] == ToDpi(FromDpi(dpi))) continue;
            int raw = FromDpi(dpi);
            Query(_io, SetSetting, (byte)i, 0, (byte)raw, (byte)(raw >> 8));
            changed = true;
        }
        if (want.PollRate is int hz && hz != now.PollRate && Array.IndexOf(Rates, hz) is >= 0 and var idx)
        {
            Query(_io, SetSetting, (byte)(_entry.Presets + 0), 0, (byte)idx); // field after the presets: polling rate
            changed = true;
        }
        if (changed) Query(_io, Save);
    }

    public void Dispose() => _owner?.Dispose();
}

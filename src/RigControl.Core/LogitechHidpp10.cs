using System.Diagnostics;
using System.Text.Json;
using HidSharp;

namespace RigControl.Core;

/// <summary>
/// Older Logitech G mice on HID++ 1.0 (G5, G500/G500s, G700/G700s, G9/G9x): the current DPI, read and set through
/// the "current resolution" register (0x63), which takes effect at once and isn't stored in the mouse's profiles.
/// Protocol and per-model DPI tables from libratbag's hidpp10 driver (libratbag-mice.json). The onboard profiles
/// (DPI stages, buttons) live in the mouse's flash and are left alone.
/// </summary>
public sealed class LogitechHidpp10Mouse : IPeripheral
{
    const byte Short = 0x10, Long = 0x11;
    const byte SetRegister = 0x80, GetRegister = 0x81, SetLongRegister = 0x82, GetLongRegister = 0x83, Error = 0x8F;
    const byte CurrentResolution = 0x63;

    /// <summary>Sends a report (short 7 or long 20 bytes) and returns the reply of the given report id, or null.</summary>
    internal delegate byte[]? Transport(byte[] request, byte replyReport);

    internal sealed record Entry(string Name, int Pid, List<(int dpi, byte raw)> Table, bool G9, byte Index);

    static readonly Lazy<List<Entry>> Entries = new(() =>
    {
        using var s = typeof(LogitechHidpp10Mouse).Assembly.GetManifestResourceStream("RigControl.Core.libratbag-mice.json")!;
        using var doc = JsonDocument.Parse(s);
        var list = new List<Entry>();
        foreach (var m in doc.RootElement.GetProperty("mice").EnumerateArray().Where(m => m.GetProperty("driver").GetString() == "hidpp10"))
        {
            // libratbag's DPI tables: a list maps to raws 0x80.., a range to raws 1.. (rounded to 25 DPI).
            var table = new List<(int, byte)>();
            if (m.TryGetProperty("dpiList", out var dl))
                table.AddRange(dl.EnumerateArray().Select((d, i) => (d.GetInt32(), (byte)(0x80 + i))));
            else
            {
                var r = m.GetProperty("dpiRange");
                double lo = r[0].GetDouble(), hi = r[1].GetDouble(), step = r[2].GetDouble();
                int rawMax = (int)((hi - lo) / step);
                for (int i = 1; i <= Math.Min(rawMax, 255); i++) table.Add(((int)(Math.Round((lo + step * i) / 25.0) * 25), (byte)i));
            }
            var name = m.GetProperty("name").GetString()!;
            if (!name.StartsWith("Logitech", StringComparison.OrdinalIgnoreCase)) name = "Logitech " + name;
            foreach (var id in m.GetProperty("ids").EnumerateArray())
                list.Add(new Entry(name, id[1].GetInt32(), table, m.GetProperty("profileType").GetString() == "G9", (byte)m.GetProperty("deviceIndex").GetInt32()));
        }
        return list;
    });

    readonly Transport _io;
    readonly Entry _entry;
    readonly IDisposable? _owner;

    public string Key { get; }
    public string Name => _entry.Name;
    public string Kind => "Mouse";
    public string Source => "Logitech HID++ 1.0 · from libratbag";
    public PeripheralCaps Caps { get; }

    internal LogitechHidpp10Mouse(Entry entry, Transport io, IDisposable? owner = null)
    {
        _entry = entry;
        _io = io;
        _owner = owner;
        Key = $"logitech/{entry.Pid:X4}";
        var dpis = entry.Table.Select(t => t.dpi).Where(d => d > 0).ToList();
        Caps = new PeripheralCaps
        {
            DpiMin = dpis.Min(), DpiMax = dpis.Max(),
            DpiStep = dpis.Count > 1 ? Math.Max(1, dpis.Order().Skip(1).Zip(dpis.Order(), (a, b) => a - b).Min()) : 50,
        };
    }

    internal static Entry? Lookup(int pid) => Entries.Value.FirstOrDefault(e => e.Pid == pid);

    public static List<IPeripheral> FindAll()
    {
        var found = new List<IPeripheral>();
        foreach (var group in DeviceList.Local.GetHidDevices(0x046D).GroupBy(d => d.ProductID))
        {
            if (Lookup(group.Key) is not { } entry) continue;
            try
            {
                // HID++ short (7-byte) and long (20-byte) reports sit on separate collections on Windows.
                var shortDev = group.FirstOrDefault(d => d.GetMaxOutputReportLength() == 7);
                var longDev = group.FirstOrDefault(d => d.GetMaxOutputReportLength() == 20);
                if (shortDev is null || longDev is null || !shortDev.TryOpen(out HidStream ss)) continue;
                if (!longDev.TryOpen(out HidStream ls)) { ss.Dispose(); continue; }
                var both = new Both(ss, ls);
                var mouse = new LogitechHidpp10Mouse(entry, HidTransport(ss, ls), both);
                if (mouse.ReadRaw() is null) { both.Dispose(); continue; } // not answering: wrong interface, or asleep
                found.Add(mouse);
            }
            catch (Exception e) { AppConfig.Log?.Invoke($"logitech hid++1.0: {e.Message}"); }
        }
        return found;
    }

    sealed class Both(IDisposable a, IDisposable b) : IDisposable
    {
        public void Dispose() { a.Dispose(); b.Dispose(); }
    }

    static Transport HidTransport(HidStream shortStream, HidStream longStream)
    {
        var gate = new object();
        shortStream.ReadTimeout = longStream.ReadTimeout = 300;
        return (req, replyReport) =>
        {
            lock (gate)
            {
                (req[0] == Long ? longStream : shortStream).Write(req);
                var from = replyReport == Long ? longStream : shortStream;
                var buf = new byte[replyReport == Long ? 20 : 7];
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 400)
                {
                    try { if (from.Read(buf, 0, buf.Length) < buf.Length) continue; } catch (TimeoutException) { return null; }
                    // Same device, sub-id and register: the reply (errors come back on the short collection as 0x8F).
                    if (buf[1] == req[1] && buf[2] == req[2] && buf[3] == req[3]) return [.. buf];
                }
                return null;
            }
        };
    }

    byte[] Message(byte report, byte sub, params byte[] parameters)
    {
        var m = new byte[report == Long ? 20 : 7];
        m[0] = report;
        m[1] = _entry.Index;
        m[2] = sub;
        m[3] = CurrentResolution;
        parameters.CopyTo(m, 4);
        return m;
    }

    /// <summary>The raw current-resolution value, or null when the mouse doesn't answer.</summary>
    byte? ReadRaw()
    {
        // G9: short register, one byte. Others: a long register read with a short request (X then Y, 16-bit).
        var reply = _entry.G9 ? _io(Message(Short, GetRegister), Short) : _io(Message(Short, GetLongRegister), Long);
        return reply?[4];
    }

    public PeripheralState Read()
    {
        if (ReadRaw() is not byte raw) return new PeripheralState { Connected = false };
        var hit = _entry.Table.FirstOrDefault(t => t.raw == raw);
        return new PeripheralState { Connected = true, Dpi = hit.dpi > 0 ? hit.dpi : null };
    }

    public void Apply(PeripheralSettings want, PeripheralState now)
    {
        if (want.Dpi is not int dpi || dpi == now.Dpi || now.Connected == false) return;
        // The nearest DPI the mouse has, like libratbag's mapping.
        var (_, raw) = _entry.Table.Where(t => t.dpi > 0).MinBy(t => Math.Abs(t.dpi - dpi));
        if (_entry.G9) _io(Message(Short, SetRegister, raw), Short);
        else _io(Message(Long, SetLongRegister, raw, 0, raw, 0), Short);
    }

    public void Dispose() => _owner?.Dispose();
}

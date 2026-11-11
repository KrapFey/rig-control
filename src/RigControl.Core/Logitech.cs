using System.Diagnostics;
using HidSharp;

namespace RigControl.Core;

/// <summary>
/// Logitech mice and keyboards over HID++ 2.0 (wired, Lightspeed, Unifying and Bolt receivers): DPI, report rate and
/// battery. Protocol details follow Solaar and libratbag. Only features the device lists are used, and nothing switches
/// the device out of its onboard-profile mode, so its own DPI button keeps working.
/// </summary>
public sealed class LogitechDevice : IPeripheral
{
    // HID++ 2.0 features RigControl uses.
    const ushort Root = 0x0000, DeviceName = 0x0005, BatteryStatus = 0x1000, UnifiedBattery = 0x1004,
        AdjustableDpi = 0x2201, ReportRate = 0x8060, ExtendedReportRate = 0x8061;
    const byte LongReport = 0x11, SoftwareId = 0x0B;
    static readonly int[] ExtendedRates = [125, 250, 500, 1000, 2000, 4000, 8000]; // 0x8061 bit/index → Hz

    /// <summary>Sends one 20-byte long report and returns the matching 20-byte reply, or null (timeout).</summary>
    internal delegate byte[]? Transport(byte[] request);

    readonly Transport _io;
    readonly byte _index;
    readonly Dictionary<ushort, byte> _features = [];
    readonly int[] _dpiList = [];
    readonly IDisposable? _owner;

    public string Key { get; }
    public string Name { get; }
    public string Kind { get; }
    public string Source => "Logitech HID++ 2.0";
    public PeripheralCaps Caps { get; }

    LogitechDevice(Transport io, byte index, string name, string kind, IDisposable? owner)
    {
        _io = io;
        _index = index;
        _owner = owner;
        Name = name;
        Kind = kind;
        Key = "logitech/" + new string([.. name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')]).Trim('-');
        foreach (var f in new[] { AdjustableDpi, ReportRate, ExtendedReportRate, BatteryStatus, UnifiedBattery })
            if (FeatureIndex(f) is byte i) _features[f] = i;
        if (_features.ContainsKey(AdjustableDpi)) _dpiList = ReadDpiList();
        Caps = new PeripheralCaps
        {
            DpiMin = _dpiList.Length > 0 ? _dpiList[0] : 100,
            DpiMax = _dpiList.Length > 0 ? _dpiList[^1] : 0,
            DpiStep = _dpiList.Length > 1 ? Math.Max(1, _dpiList[1] - _dpiList[0]) : 50,
            PollRates = ReadRates(),
            Battery = _features.ContainsKey(BatteryStatus) || _features.ContainsKey(UnifiedBattery),
            Charging = _features.ContainsKey(BatteryStatus) || _features.ContainsKey(UnifiedBattery),
        };
    }

    /// <summary>
    /// Opens a device behind <paramref name="io"/> at <paramref name="index"/> (0xFF wired, 1–6 behind a receiver).
    /// Null when nothing HID++ 2.0 answers there or it isn't a mouse or keyboard.
    /// </summary>
    internal static LogitechDevice? Open(Transport io, byte index, IDisposable? owner = null)
    {
        var dev = Probe(io, index, owner);
        if (dev is null) owner?.Dispose();
        return dev;
    }

    static LogitechDevice? Probe(Transport io, byte index, IDisposable? owner)
    {
        // Root.getProtocolVersion with a ping byte: HID++ 2.0 devices echo it.
        if (Call(io, index, 0, 1, [0, 0, 0x5A]) is not { } ver || ver[0] < 2 || ver[2] != 0x5A) return null;
        if (Call(io, index, 0, 0, [DeviceName >> 8, DeviceName & 0xFF]) is not { } nf || nf[0] == 0) return null;
        byte ni = nf[0];
        string kind = Call(io, index, ni, 2, []) is { } t ? t[0] switch { 0 => "Keyboard", 3 or 4 or 5 => "Mouse", _ => "" } : "Mouse";
        if (kind == "") return null; // headsets (HeadsetControl has them), receivers, webcams…
        if (Call(io, index, ni, 0, []) is not { } len) return null;
        var name = new List<byte>();
        while (name.Count < len[0] && Call(io, index, ni, 1, [(byte)name.Count]) is { } chunk)
            name.AddRange(chunk.Take(Math.Min(16, len[0] - name.Count)));
        var text = System.Text.Encoding.UTF8.GetString([.. name]).TrimEnd('\0').Trim();
        return text.Length == 0 ? null : new LogitechDevice(io, index, text, kind, owner);
    }

    /// <summary>Every HID++ 2.0 mouse or keyboard reachable on Logitech long-report collections.</summary>
    public static List<IPeripheral> FindAll()
    {
        var found = new List<IPeripheral>();
        foreach (var dev in DeviceList.Local.GetHidDevices(0x046D))
        {
            try
            {
                // HID++ long reports: report id 0x11 + 19 bytes, on their own vendor collection.
                if (dev.GetMaxOutputReportLength() != 20 || dev.GetMaxInputReportLength() != 20) continue;
                if (!dev.TryOpen(out HidStream stream)) continue;
                var io = HidTransport(stream);
                bool receiver = (Safe(dev.GetProductName) ?? "").Contains("receiver", StringComparison.OrdinalIgnoreCase);
                // ponytail: an empty receiver slot only answers on the short collection, so each costs one timeout at scan.
                byte[] slots = receiver ? [1, 2, 3, 4, 5, 6] : [0xFF];
                // Devices behind one receiver share its stream; it closes when the last of them is disposed.
                var shared = new Shared(stream);
                foreach (var d in slots.Select(s => Open(io, s, shared.Ref())).OfType<LogitechDevice>())
                    if (found.Any(f => f.Key == d.Key)) d.Dispose(); // same model wired and wireless at once
                    else found.Add(d);
                shared.Dispose(); // the scan's own reference
            }
            catch (Exception e) { AppConfig.Log?.Invoke($"logitech: {e.Message}"); }
        }
        return found;
    }

    sealed class Shared(IDisposable inner) : IDisposable
    {
        int _refs = 1;
        public Shared Ref() { Interlocked.Increment(ref _refs); return this; }
        public void Dispose() { if (Interlocked.Decrement(ref _refs) == 0) inner.Dispose(); }
    }

    static string? Safe(Func<string> f) { try { return f(); } catch { return null; } }

    static Transport HidTransport(HidStream stream)
    {
        var gate = new object();
        stream.ReadTimeout = 250;
        return req =>
        {
            lock (gate)
            {
                stream.Write(req);
                var clock = Stopwatch.StartNew();
                var buf = new byte[20];
                while (clock.ElapsedMilliseconds < 400)
                {
                    int n;
                    try { n = stream.Read(buf, 0, buf.Length); } catch (TimeoutException) { return null; }
                    // Same device, and either our feature/function/software id or an error about it.
                    if (n < 7 || buf[0] != LongReport || buf[1] != req[1]) continue;
                    if (buf[2] == req[2] && buf[3] == req[3]) return [.. buf];
                    if (buf[2] == 0xFF && buf[3] == req[2] && buf[4] == req[3]) return [.. buf];
                }
                return null;
            }
        };
    }

    /// <summary>One HID++ 2.0 call; returns the 16 parameter bytes of the reply, or null on error or timeout.</summary>
    static byte[]? Call(Transport io, byte index, byte feature, int function, byte[] args)
    {
        var req = new byte[20];
        req[0] = LongReport;
        req[1] = index;
        req[2] = feature;
        req[3] = (byte)(function << 4 | SoftwareId);
        args.AsSpan(0, Math.Min(args.Length, 16)).CopyTo(req.AsSpan(4));
        var reply = io(req);
        if (reply is null || reply.Length < 20 || reply[2] == 0xFF) return null;
        return reply[4..20];
    }

    byte[]? Call(ushort feature, int function, params byte[] args) =>
        _features.TryGetValue(feature, out var i) ? Call(_io, _index, i, function, args) : null;

    byte? FeatureIndex(ushort feature) =>
        Call(_io, _index, 0, 0, [(byte)(feature >> 8), (byte)feature]) is { } r && r[0] != 0 ? r[0] : null;

    /// <summary>
    /// 0x2201 getSensorDpiList: big-endian values, 0 ends the list; a value with the top three bits set is a step
    /// (low 13 bits) up to the value after it.
    /// </summary>
    int[] ReadDpiList()
    {
        var bytes = new List<byte>();
        for (byte page = 0; page < 8; page++)
        {
            if (Call(AdjustableDpi, 1, 0, 0, page) is not { } r) break;
            bytes.AddRange(r.Skip(1)); // first byte is the sensor index
            if (bytes.Chunk(2).Any(p => p.Length == 2 && p[0] == 0 && p[1] == 0)) break; // the list ended
        }
        return ParseDpiList([.. bytes]);
    }

    internal static int[] ParseDpiList(byte[] b)
    {
        var list = new List<int>();
        for (int i = 0; i + 1 < b.Length; )
        {
            int v = b[i] << 8 | b[i + 1];
            if (v == 0) break;
            if (v >> 13 == 0b111 && list.Count > 0 && i + 3 < b.Length)
            {
                int step = v & 0x1FFF, last = b[i + 2] << 8 | b[i + 3];
                for (int d = list[^1] + step; step > 0 && d <= last; d += step) list.Add(d);
                i += 4;
            }
            else { list.Add(v); i += 2; }
        }
        return [.. list.Distinct().Order()];
    }

    int[] ReadRates()
    {
        if (Call(ExtendedReportRate, 1) is { } x)
        {
            int flags = x[0] << 8 | x[1];
            return [.. ExtendedRates.Where((_, i) => (flags >> i & 1) != 0)];
        }
        if (Call(ReportRate, 0) is { } r)
            return [.. Enumerable.Range(0, 8).Where(i => (r[0] >> i & 1) != 0).Select(i => 1000 / (i + 1)).Order()];
        return [];
    }

    public PeripheralState Read()
    {
        int? dpi = null, poll = null, battery = null;
        bool? charging = null;
        if (Call(UnifiedBattery, 1) is { } u)
        {
            battery = u[0];
            charging = u[2] is 1 or 2 or 3;
        }
        else if (Call(BatteryStatus, 0) is { } b)
        {
            battery = b[0] == 0 ? null : b[0];
            charging = b[2] is 1 or 2 or 3 or 4;
        }
        if (Call(AdjustableDpi, 2, 0) is { } d)
        {
            dpi = d[1] << 8 | d[2];
            if (dpi == 0) dpi = d[3] << 8 | d[4]; // 0 = the sensor's default
        }
        if (Call(ExtendedReportRate, 2) is { } x) poll = x[0] < ExtendedRates.Length ? ExtendedRates[x[0]] : null;
        else if (Call(ReportRate, 1) is { } r && r[0] > 0) poll = 1000 / r[0];
        return new PeripheralState { Dpi = dpi, PollRate = poll, BatteryPercent = battery, Charging = charging };
    }

    public void Apply(PeripheralSettings want, PeripheralState now)
    {
        if (want.Dpi is int dpi && Caps.Dpi && dpi != now.Dpi)
        {
            // Only values the sensor lists are valid: take the nearest.
            dpi = _dpiList.MinBy(v => Math.Abs(v - dpi));
            Call(AdjustableDpi, 3, 0, (byte)(dpi >> 8), (byte)dpi);
        }
        if (want.PollRate is int hz && hz != now.PollRate && Caps.PollRates.Contains(hz))
        {
            // ponytail: in onboard-profile mode some mice refuse this (the profile owns the rate); it then just doesn't stick.
            if (_features.ContainsKey(ExtendedReportRate)) Call(ExtendedReportRate, 3, (byte)Array.IndexOf(ExtendedRates, hz));
            else Call(ReportRate, 2, (byte)(1000 / hz));
        }
    }

    public void Dispose() => _owner?.Dispose();
}

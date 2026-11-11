using System.Diagnostics;
using HidSharp;

namespace RigControl.Core;

/// <summary>
/// SteelSeries Apex Pro keyboards with adjustable switches: one actuation depth for every analog key, and rapid
/// trigger where the protocol for it is documented. Two families with published, community-documented protocols:
/// <list type="bullet">
/// <item>Apex Pro TKL (2023), wired: frames from SteelSeries GG's own device descriptions as documented by the
/// OpenMouse keyboard-protocol project; a live actuation write was confirmed on hardware by steelseriesgg-rs.</item>
/// <item>Apex Pro Gen 3 (full size), firmware 4.16.8 only: commands from apex-analog (MIT), read on that firmware.
/// Other firmware is left alone, as apex-analog does.</item>
/// </list>
/// Only live settings are written, never the keyboard's flash: a replug restores the keyboard's own profile, and
/// RigControl applies its settings again when the keyboard appears.
/// </summary>
public sealed class SteelSeriesKeyboard : IPeripheral
{
    internal enum Family { Tkl2023, Gen3 }

    /// <summary>Sends one report (report id 0 first). For Gen 3 reads, returns the 64-byte reply that <paramref name="valid"/> accepts.</summary>
    internal delegate byte[]? Transport(byte[] report, Func<byte[], bool>? valid);

    const int Tkl2023 = 0x1628, Gen3 = 0x1640;
    const ushort Gen3Firmware = 0x0416; // bcdDevice of 4.16.8

    // ---- Apex Pro TKL (2023): 645-byte feature reports
    /// <summary>The 68 analog keys by HID usage, in GG's table order.</summary>
    static readonly byte[] TklKeys =
    [
        0x35, 0x1e, 0x1f, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x2d, 0x2e, 0x89,
        0x2b, 0x14, 0x1a, 0x08, 0x15, 0x17, 0x1c, 0x18, 0x0c, 0x12, 0x13, 0x2f, 0x30, 0x31,
        0x39, 0x04, 0x16, 0x07, 0x09, 0x0a, 0x0b, 0x0d, 0x0e, 0x0f, 0x33, 0x34, 0x32, 0x28,
        0xe1, 0x64, 0x1d, 0x1b, 0x06, 0x19, 0x05, 0x11, 0x10, 0x36, 0x37, 0x38, 0x87, 0xe5,
        0xe0, 0xe3, 0xe2, 0x8b, 0x2c, 0x8a, 0x88, 0xe6, 0xe7, 0xf0, 0xe4, 0x2a,
    ];
    /// <summary>GG's (actuation, reset) raw pair for 0.1 … 4.0 mm in 0.1 mm steps.</summary>
    static readonly (byte press, byte release)[] TklLevels =
    [
        (5, 5), (5, 4), (6, 5), (8, 6), (10, 8), (12, 10), (14, 12), (16, 14), (18, 16), (21, 18),
        (24, 21), (27, 24), (30, 27), (33, 29), (36, 33), (39, 36), (43, 40), (47, 42), (52, 47), (57, 52),
        (62, 57), (68, 62), (74, 68), (81, 74), (88, 81), (95, 87), (104, 95), (114, 104), (124, 114), (134, 123),
        (147, 134), (161, 147), (175, 161), (189, 174), (197, 189), (206, 197), (215, 206), (224, 215), (224, 217), (224, 219),
    ];
    const byte TklRapidTrigger = 2, TklNormal = 0;

    // ---- Apex Pro Gen 3: 65-byte output reports, 64-byte replies with no id
    /// <summary>Keys with a sensor, by HID usage (LGUI can't be changed this way, so it isn't here).</summary>
    static readonly byte[] Gen3Keys =
    [
        0x35, 0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x2D, 0x2E, 0x89, 0x2B, 0x14, 0x1A, 0x08, 0x15,
        0x17, 0x1C, 0x18, 0x0C, 0x12, 0x13, 0x2F, 0x30, 0x31, 0x39, 0x04, 0x16, 0x07, 0x09, 0x0A, 0x0B, 0x0D, 0x0E, 0x0F,
        0x33, 0x34, 0x32, 0x28, 0xE1, 0x64, 0x1D, 0x1B, 0x06, 0x19, 0x05, 0x11, 0x10, 0x36, 0x37, 0x38, 0x87, 0xE5, 0xE0,
        0xE2, 0x8B, 0x2C, 0x8A, 0x88, 0xE6, 0xE7, 0xF0, 0xE4, 0x2A,
    ];
    /// <summary>The firmware's 20 actuation levels (tenths of a mm) and the thresholds its press/release points come from.</summary>
    static readonly int[] Gen3Tenths = [1, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30, 32, 34, 37, 40];
    static readonly byte[] Gen3Thresholds =
    [
        5, 5, 4, 5, 6, 8, 10, 12, 14, 16, 18, 21, 23, 26, 28, 31, 34, 38, 42, 46, 50, 55, 60, 65, 71, 77, 84, 91, 99, 108,
        118, 128, 139, 151, 165, 180, 201, 206, 210, 215,
    ];
    const byte Gen3SetPoints = 0x31, Gen3ReadPoints = 0xB1, Gen3ReadCalibration = 0xDA;

    readonly Family _family;
    readonly Transport _io;
    readonly IDisposable? _owner;
    readonly byte[] _keys; // the keys this keyboard really has

    public string Key { get; }
    public string Name { get; }
    public string Kind => "Keyboard";
    public string Source => _family == Family.Gen3
        ? "SteelSeries protocol · from apex-analog"
        : "SteelSeries protocol · from GG's device descriptions (OpenMouse)";
    public PeripheralCaps Caps { get; }

    internal SteelSeriesKeyboard(Family family, Transport io, IDisposable? owner = null)
    {
        _family = family;
        _io = io;
        _owner = owner;
        if (family == Family.Gen3)
        {
            Key = $"steelseries/{Gen3:X4}";
            Name = "SteelSeries Apex Pro Gen 3";
            _keys = FittedGen3Keys();
            Caps = new PeripheralCaps { ActuationMm = [.. Gen3Tenths.Select(t => t / 10.0)] };
        }
        else
        {
            Key = $"steelseries/{Tkl2023:X4}";
            Name = "SteelSeries Apex Pro TKL (2023)";
            _keys = TklKeys;
            Caps = new PeripheralCaps { ActuationMm = [.. Enumerable.Range(1, 40).Select(t => t / 10.0)], RapidTrigger = true };
        }
    }

    public static List<IPeripheral> FindAll()
    {
        var found = new List<IPeripheral>();
        foreach (var dev in DeviceList.Local.GetHidDevices(0x1038))
        {
            try
            {
                if (dev.ProductID == Tkl2023 && dev.GetMaxFeatureReportLength() >= 645 && IsVendorCollection(dev)
                    && dev.TryOpen(out HidStream ts) && found.All(f => f.Key != $"steelseries/{Tkl2023:X4}"))
                {
                    found.Add(new SteelSeriesKeyboard(Family.Tkl2023, FeatureTransport(ts), ts));
                }
                else if (dev.ProductID == Gen3 && dev.GetMaxOutputReportLength() == 65 && IsVendorCollection(dev))
                {
                    if (dev.ReleaseNumberBcd != Gen3Firmware)
                    {
                        AppConfig.Log?.Invoke($"steelseries: Apex Pro Gen 3 firmware {dev.ReleaseNumberBcd:X4} isn't the one its commands were checked on (0416); left alone");
                        continue;
                    }
                    if (dev.TryOpen(out HidStream gs) && found.All(f => f.Key != $"steelseries/{Gen3:X4}"))
                        found.Add(new SteelSeriesKeyboard(Family.Gen3, OutputTransport(gs), gs));
                }
            }
            catch (Exception e) { AppConfig.Log?.Invoke($"steelseries: {e.Message}"); }
        }
        return found;
    }

    /// <summary>The command interface: vendor usage page 0xFFC0.</summary>
    static bool IsVendorCollection(HidDevice dev)
    {
        var d = dev.GetRawReportDescriptor();
        return d.Length >= 3 && d[0] == 0x06 && d[1] == 0xC0 && d[2] == 0xFF;
    }

    static Transport FeatureTransport(HidStream s) => (report, _) =>
    {
        var buf = new byte[645];
        report.AsSpan(0, Math.Min(report.Length, buf.Length)).CopyTo(buf);
        s.SetFeature(buf);
        return null;
    };

    static Transport OutputTransport(HidStream s)
    {
        var gate = new object();
        s.ReadTimeout = 30;
        return (report, valid) =>
        {
            lock (gate)
            {
                var buf = new byte[65];
                report.AsSpan(0, Math.Min(report.Length, buf.Length)).CopyTo(buf);
                // Replies carry no id: retry a few times, as a reply right after a write can get lost.
                for (int attempt = 0; attempt < (valid is null ? 1 : 10); attempt++)
                {
                    s.Write(buf);
                    if (valid is null) { Thread.Sleep(5); return null; } // a write that arrives straight after another replaces it
                    var clock = Stopwatch.StartNew();
                    var r = new byte[65];
                    while (clock.ElapsedMilliseconds < 30)
                    {
                        int n;
                        try { n = s.Read(r, 0, r.Length); } catch (TimeoutException) { break; }
                        var data = r.AsSpan(1, Math.Max(0, n - 1)).ToArray(); // drop the report id Windows puts first
                        if (valid(data)) return data;
                    }
                }
                return null;
            }
        };
    }

    /// <summary>Gen 3: ask a per-key read command about keys, 12 at a time; yields (hid, reply bytes after the hid).</summary>
    IEnumerable<(byte hid, byte[] value)> PerKey(byte command, int stride, byte[] keys)
    {
        foreach (var part in keys.Chunk(12))
        {
            var req = new byte[65];
            req[1] = command;
            req[2] = (byte)part.Length;
            for (int i = 0; i < part.Length; i++) req[3 + i * stride] = part[i];
            var reply = _io(req, r => r.Length >= part.Length * stride && part.Select((h, i) => r[i * stride] == h).All(x => x));
            if (reply is null) yield break;
            for (int i = 0; i < part.Length; i++) yield return (part[i], reply[(i * stride + 1)..((i + 1) * stride)]);
        }
    }

    /// <summary>Gen 3: keys whose calibration shows a switch is fitted (ISO and JIS keys only exist on those boards).</summary>
    byte[] FittedGen3Keys()
    {
        var fitted = PerKey(Gen3ReadCalibration, 5, Gen3Keys)
            .Where(k => (k.value[0] | k.value[1] << 8, k.value[2] | k.value[3] << 8) is var (top, rest) && (top, rest) != (2900, 928) && top > rest && rest > 0)
            .Select(k => k.hid).ToArray();
        return fitted.Length > 0 ? fitted : Gen3Keys;
    }

    internal static (byte press, byte release) Gen3Level(double mm)
    {
        int level = Enumerable.Range(0, Gen3Tenths.Length).MinBy(i => Math.Abs(Gen3Tenths[i] / 10.0 - mm));
        if (level == 0) return (Gen3Thresholds[1], Gen3Thresholds[1]);
        if (level == Gen3Tenths.Length - 1) return (Gen3Thresholds[^1], Gen3Thresholds[^2]);
        return (Gen3Thresholds[2 * level + 2], Gen3Thresholds[2 * level + 1]);
    }

    public PeripheralState Read()
    {
        // The TKL (2023) keeps its settings in an on-board profile RigControl doesn't read; only what was set is known.
        if (_family != Family.Gen3) return new PeripheralState();
        // The depth most keys use, as the nearest firmware level.
        var press = PerKey(Gen3ReadPoints, 3, _keys).Select(k => k.value[0]).ToList();
        if (press.Count == 0) return new PeripheralState();
        int common = press.GroupBy(p => p).MaxBy(g => g.Count())!.Key;
        double mm = Caps.ActuationMm.MinBy(m => Math.Abs(Gen3Level(m).press - common));
        return new PeripheralState { ActuationMm = mm };
    }

    public void Apply(PeripheralSettings want, PeripheralState now)
    {
        if (want.ActuationMm is double mm && Caps.ActuationMm.Length > 0 && mm != now.ActuationMm)
        {
            if (_family == Family.Gen3)
            {
                var (press, release) = Gen3Level(mm);
                foreach (var part in _keys.Chunk(20))
                {
                    var req = new byte[65];
                    req[1] = Gen3SetPoints;
                    req[2] = (byte)part.Length;
                    for (int i = 0; i < part.Length; i++) { req[3 + i * 3] = part[i]; req[4 + i * 3] = press; req[5 + i * 3] = release; }
                    _io(req, null);
                }
            }
            else
            {
                int level = Math.Clamp((int)Math.Round(mm * 10) - 1, 0, TklLevels.Length - 1);
                var (h, l) = TklLevels[level];
                // 00 38 61 <count> <layer 0> {hid, actuation, reset} × count
                var req = new List<byte> { 0, 0x38, 0x61, (byte)_keys.Length, 0x00 };
                foreach (var k in _keys) req.AddRange([k, h, l]);
                _io([.. req], null);
            }
        }
        if (want.RapidTrigger is bool rt && Caps.RapidTrigger && rt != now.RapidTrigger && _family == Family.Tkl2023)
        {
            // 00 38 62 <count> {hid, release mode} × count; mode 2 = rapid trigger, 0 = normal
            var req = new List<byte> { 0, 0x38, 0x62, (byte)_keys.Length };
            foreach (var k in _keys) req.AddRange([k, rt ? TklRapidTrigger : TklNormal]);
            _io([.. req], null);
        }
    }

    public void Dispose() => _owner?.Dispose();
}

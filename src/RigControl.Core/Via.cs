using System.Diagnostics;
using HidSharp;

namespace RigControl.Core;

/// <summary>
/// Keyboards running QMK firmware with VIA enabled (Keychron and hundreds of others): their lighting (brightness,
/// effect, speed, colour) over VIA's open raw-HID protocol (qmk_firmware quantum/via.h, protocol 12 and newer).
/// Which lighting the board has is asked, not assumed: a channel the firmware lacks answers "unhandled".
/// Settings are applied live (VIA's "noeeprom" setters); RigControl applies them again when the keyboard appears,
/// so the keyboard's saved lighting is never overwritten.
/// </summary>
public sealed class ViaKeyboard : IPeripheral
{
    const byte GetProtocolVersion = 0x01, CustomSetValue = 0x07, CustomGetValue = 0x08, Unhandled = 0xFF;
    const byte Brightness = 1, Effect = 2, Speed = 3, Color = 4;
    const int MinProtocol = 12; // custom-value channels arrived with VIA protocol 12

    /// <summary>Lighting channels in the order RigControl prefers them, with what each one has.</summary>
    static readonly (byte id, string name, bool effect, bool speed, bool color)[] Channels =
    [
        (3, "RGB matrix", true, true, true),
        (2, "RGB light", true, true, true),
        (5, "LED matrix", true, true, false),
        (1, "Backlight", false, false, false),
    ];

    /// <summary>Sends one 32-byte VIA message and returns the 32-byte reply (null on timeout).</summary>
    internal delegate byte[]? Transport(byte[] message);

    readonly Transport _io;
    readonly IDisposable? _owner;
    readonly byte _channel;

    public string Key { get; }
    public string Name { get; }
    public string Kind => "Keyboard";
    public string Source { get; }
    public PeripheralCaps Caps { get; }

    ViaKeyboard(Transport io, string key, string name, int protocol, int channelIndex, IDisposable? owner)
    {
        _io = io;
        _owner = owner;
        Key = key;
        Name = name;
        var ch = Channels[channelIndex];
        _channel = ch.id;
        Source = $"VIA protocol {protocol} · {ch.name}";
        Caps = new PeripheralCaps { Lighting = ch.name, LightingEffects = ch.effect, LightingSpeed = ch.speed, LightingColor = ch.color };
    }

    /// <summary>A VIA keyboard behind <paramref name="io"/>, or null when it isn't one or has no lighting RigControl can set.</summary>
    internal static ViaKeyboard? Open(Transport io, string key, string name, IDisposable? owner = null)
    {
        if (Send(io, [GetProtocolVersion]) is not { } v || v[0] != GetProtocolVersion) return null;
        int protocol = v[1] << 8 | v[2];
        if (protocol < MinProtocol) return null;
        for (int i = 0; i < Channels.Length; i++)
            if (Send(io, [CustomGetValue, Channels[i].id, Brightness]) is { } r && r[0] != Unhandled)
                return new ViaKeyboard(io, key, name, protocol, i, owner);
        return null;
    }

    public static List<IPeripheral> FindAll()
    {
        var found = new List<IPeripheral>();
        foreach (var dev in DeviceList.Local.GetHidDevices())
        {
            try
            {
                // Raw HID: usage page 0xFF60, usage 0x61, 32-byte reports (+ report id 0 on Windows).
                if (dev.GetMaxOutputReportLength() != 33 || dev.GetMaxInputReportLength() != 33) continue;
                var d = dev.GetRawReportDescriptor();
                if (d.Length < 5 || d[0] != 0x06 || d[1] != 0x60 || d[2] != 0xFF || d[3] != 0x09 || d[4] != 0x61) continue;
                string key = $"via/{dev.VendorID:X4}{dev.ProductID:X4}";
                if (found.Any(f => f.Key == key) || !dev.TryOpen(out HidStream s)) continue;
                var name = string.Join(" ", new[] { Safe(dev.GetManufacturer), Safe(dev.GetProductName) }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
                if (Open(HidTransport(s), key, name.Length > 0 ? name : $"Keyboard {dev.VendorID:X4}:{dev.ProductID:X4}", s) is { } kb) found.Add(kb);
                else s.Dispose();
            }
            catch (Exception e) { AppConfig.Log?.Invoke($"via: {e.Message}"); }
        }
        return found;
    }

    static string? Safe(Func<string> f) { try { return f().Trim(); } catch { return null; } }

    static Transport HidTransport(HidStream s)
    {
        var gate = new object();
        s.ReadTimeout = 200;
        return msg =>
        {
            lock (gate)
            {
                var buf = new byte[33];
                msg.AsSpan(0, Math.Min(32, msg.Length)).CopyTo(buf.AsSpan(1));
                s.Write(buf);
                // The reply echoes the command (or says unhandled); anything else is a stray report.
                var clock = Stopwatch.StartNew();
                var r = new byte[33];
                while (clock.ElapsedMilliseconds < 300)
                {
                    try { if (s.Read(r, 0, r.Length) < 33) continue; } catch (TimeoutException) { return null; }
                    if (r[1] == msg[0] || r[1] == Unhandled) return r[1..33];
                }
                return null;
            }
        };
    }

    static byte[]? Send(Transport io, byte[] head)
    {
        var msg = new byte[32];
        head.CopyTo(msg, 0);
        return io(msg);
    }

    byte[]? Get(byte value) => Send(_io, [CustomGetValue, _channel, value]) is { } r && r[0] == CustomGetValue ? r : null;
    void Set(byte value, params byte[] data) => Send(_io, [CustomSetValue, _channel, value, .. data]);

    public PeripheralState Read()
    {
        // Reply: [command, channel, value id, data…]
        var b = Get(Brightness);
        var e = Caps.LightingEffects ? Get(Effect) : null;
        var sp = Caps.LightingSpeed ? Get(Speed) : null;
        var c = Caps.LightingColor ? Get(Color) : null;
        return new PeripheralState
        {
            LightBrightness = b?[3],
            LightEffect = e?[3],
            LightSpeed = sp?[3],
            LightHue = c?[3],
            LightSaturation = c?[4],
        };
    }

    public void Apply(PeripheralSettings want, PeripheralState now)
    {
        if (want.LightEffect is int ef && Caps.LightingEffects && ef != now.LightEffect) Set(Effect, (byte)Math.Clamp(ef, 0, 255));
        if (want.LightBrightness is int br && br != now.LightBrightness) Set(Brightness, (byte)Math.Clamp(br, 0, 255));
        if (want.LightSpeed is int sp && Caps.LightingSpeed && sp != now.LightSpeed) Set(Speed, (byte)Math.Clamp(sp, 0, 255));
        if (Caps.LightingColor && (want.LightHue is not null || want.LightSaturation is not null)
            && (want.LightHue != now.LightHue || want.LightSaturation != now.LightSaturation))
            Set(Color, (byte)Math.Clamp(want.LightHue ?? now.LightHue ?? 0, 0, 255), (byte)Math.Clamp(want.LightSaturation ?? now.LightSaturation ?? 255, 0, 255));
    }

    public void Dispose() => _owner?.Dispose();
}

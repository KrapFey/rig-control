using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace RigControl.Core;

/// <summary>An RGB colour; OpenRGB packs it as 0x00BBGGRR.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public uint Packed => (uint)(R | G << 8 | B << 16);
    public static Rgb FromPacked(uint v) => new((byte)v, (byte)(v >> 8), (byte)(v >> 16));
    public static Rgb Lerp(Rgb a, Rgb b, double t) =>
        new((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    public Rgb Scale(double k) => new((byte)(R * k), (byte)(G * k), (byte)(B * k));
    public static Rgb Hsv(double h, double s = 1, double v = 1)
    {
        h = ((h % 360) + 360) % 360 / 60;
        int i = (int)h;
        double f = h - i, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        var (r, g, b) = i switch { 0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q) };
        return new((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
    public static Rgb Parse(string hex)
    {
        var s = hex.TrimStart('#');
        return new(Convert.ToByte(s[..2], 16), Convert.ToByte(s[2..4], 16), Convert.ToByte(s[4..6], 16));
    }
}

public enum RgbDeviceType
{
    Motherboard, Dram, Gpu, Cooler, LedStrip, Keyboard, Mouse, Mousemat, Headset, HeadsetStand, Gamepad, Light,
    Speaker, Virtual, Storage, Case, Microphone, Accessory, Keypad, Laptop, Monitor, Unknown,
}

[Flags]
public enum ModeFlags : uint
{
    HasSpeed = 1 << 0, HasDirectionLR = 1 << 1, HasDirectionUD = 1 << 2, HasDirectionHV = 1 << 3,
    HasBrightness = 1 << 4, HasPerLedColor = 1 << 5, HasModeSpecificColor = 1 << 6, HasRandomColor = 1 << 7,
    ManualSave = 1 << 8, AutomaticSave = 1 << 9,
}

public enum ColorMode : uint { None, PerLed, ModeSpecific, Random }

public sealed class RgbMode
{
    public string Name { get; set; } = "";
    public ModeFlags Flags { get; set; }
    public uint SpeedMin { get; set; }
    public uint SpeedMax { get; set; }
    public uint BrightnessMin { get; set; }
    public uint BrightnessMax { get; set; }
    public uint ColorsMin { get; set; }
    public uint ColorsMax { get; set; }
    public uint Speed { get; set; }
    public uint Brightness { get; set; }
    public uint Direction { get; set; }
    public ColorMode ColorMode { get; set; }
    public List<Rgb> Colors { get; set; } = [];

    /// <summary>"Direct"/"Custom" style modes take per-LED colours we stream ourselves.</summary>
    public bool IsDirect => Flags.HasFlag(ModeFlags.HasPerLedColor) &&
        (Name.Equals("Direct", StringComparison.OrdinalIgnoreCase) || Name.Equals("Custom", StringComparison.OrdinalIgnoreCase) || Name.Equals("Static", StringComparison.OrdinalIgnoreCase));
}

public sealed class RgbSegment
{
    public string Name { get; set; } = "";
    public int Type { get; set; }
    public uint StartIndex { get; set; }
    public uint LedsCount { get; set; }
}

public sealed class RgbZone
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public uint Flags { get; set; }
    public List<RgbSegment> Segments { get; } = [];
    /// <summary>Zone-level hardware modes (protocol 6); empty when the zone only follows the device mode.</summary>
    public List<RgbMode> Modes { get; } = [];
    /// <summary>-1 = the zone follows the device mode.</summary>
    public int ActiveMode { get; set; } = -1;
    public int Type { get; set; } // 0 single, 1 linear, 2 matrix
    public uint LedsMin { get; set; }
    public uint LedsMax { get; set; }
    public uint LedsCount { get; set; }
    public uint MatrixHeight { get; set; }
    public uint MatrixWidth { get; set; }
    public uint[]? Matrix { get; set; }
    /// <summary>Index of this zone's first LED in the device's LED array.</summary>
    public int StartLed { get; set; }
}

/// <summary>One OpenRGB controller as described by the SDK (protocol 6, OpenRGB 1.1).</summary>
public sealed class RgbDevice
{
    public int Index { get; set; }
    public RgbDeviceType Type { get; set; }
    public string Name { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "";
    public string Serial { get; set; } = "";
    public string Location { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public uint Flags { get; set; }
    public string Configuration { get; set; } = "";
    public int ActiveMode { get; set; }
    public List<RgbMode> Modes { get; } = [];
    public List<RgbZone> Zones { get; } = [];
    public List<string> Leds { get; } = [];
    public Rgb[] Colors { get; set; } = [];

    /// <summary>Stable across runs and reboots: name + location (serials are often empty).</summary>
    public string Key => $"openrgb/{Name}@{Location}";

    public int DirectModeIndex => Modes.FindIndex(m => m.Name.Equals("Direct", StringComparison.OrdinalIgnoreCase)) is int i and >= 0 ? i
        : Modes.FindIndex(m => m.IsDirect);

    internal static RgbDevice Parse(int index, ReadOnlySpan<byte> d)
    {
        var r = new Reader(d);
        r.U32(); // data size
        var dev = new RgbDevice
        {
            Index = index,
            Type = (RgbDeviceType)Math.Min(r.I32(), (int)RgbDeviceType.Unknown),
            Name = r.Str(),
            Vendor = r.Str(),
            Description = r.Str(),
            Version = r.Str(),
            Serial = r.Str(),
            Location = r.Str(),
        };
        int modes = r.U16();
        dev.ActiveMode = r.I32();
        for (int i = 0; i < modes; i++) dev.Modes.Add(ReadMode(ref r));
        int zones = r.U16();
        int led = 0;
        for (int i = 0; i < zones; i++)
        {
            var z = new RgbZone { Name = r.Str(), Type = r.I32(), LedsMin = r.U32(), LedsMax = r.U32(), LedsCount = r.U32(), StartLed = led };
            (z.MatrixHeight, z.MatrixWidth, z.Matrix) = ReadMatrix(ref r);
            int segments = r.U16();
            for (int s = 0; s < segments; s++)
            {
                z.Segments.Add(new RgbSegment { Name = r.Str(), Type = r.I32(), StartIndex = r.U32(), LedsCount = r.U32() });
                ReadMatrix(ref r);
                r.U32(); // segment flags
            }
            z.Flags = r.U32();
            z.ActiveMode = r.I32();
            int zoneModes = r.U16();
            for (int m = 0; m < zoneModes; m++) z.Modes.Add(ReadMode(ref r));
            z.DisplayName = r.Str();
            led += (int)z.LedsCount;
            dev.Zones.Add(z);
        }
        int leds = r.U16();
        for (int i = 0; i < leds; i++) dev.Leds.Add(r.Str());
        int colors = r.U16();
        dev.Colors = new Rgb[colors];
        for (int i = 0; i < colors; i++) dev.Colors[i] = Rgb.FromPacked(r.U32());
        int displayNames = r.U16();
        for (int i = 0; i < displayNames; i++) r.Str(); // per-LED display names: not used yet
        dev.Flags = r.U32();
        dev.DisplayName = r.Str();
        dev.Configuration = r.LongStr();
        return dev;
    }

    static (uint h, uint w, uint[]? map) ReadMatrix(ref Reader r)
    {
        if (r.U16() == 0) return (0, 0, null);
        uint h = r.U32(), w = r.U32();
        var map = new uint[h * w];
        for (int k = 0; k < map.Length; k++) map[k] = r.U32();
        return (h, w, map);
    }

    static RgbMode ReadMode(ref Reader r)
    {
        var m = new RgbMode
        {
            Name = r.Str(), Flags = (ModeFlags)r.U32(),
            SpeedMin = r.U32(), SpeedMax = r.U32(), BrightnessMin = r.U32(), BrightnessMax = r.U32(),
            ColorsMin = r.U32(), ColorsMax = r.U32(), Speed = r.U32(), Brightness = r.U32(),
            Direction = r.U32(), ColorMode = (ColorMode)r.U32(),
        };
        int n = r.U16();
        for (int i = 0; i < n; i++) m.Colors.Add(Rgb.FromPacked(r.U32()));
        return m;
    }

    internal static void WriteMode(List<byte> b, RgbMode m)
    {
        Writer.Str(b, m.Name);
        Writer.U32(b, (uint)m.Flags);
        foreach (var v in new[] { m.SpeedMin, m.SpeedMax, m.BrightnessMin, m.BrightnessMax, m.ColorsMin, m.ColorsMax, m.Speed, m.Brightness, m.Direction, (uint)m.ColorMode })
            Writer.U32(b, v);
        Writer.U16(b, (ushort)m.Colors.Count);
        foreach (var c in m.Colors) Writer.U32(b, c.Packed);
    }

    ref struct Reader(ReadOnlySpan<byte> d)
    {
        readonly ReadOnlySpan<byte> _d = d;
        int _o;
        public uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(_d[_o..]); _o += 4; return v; }
        public int I32() { var v = BinaryPrimitives.ReadInt32LittleEndian(_d[_o..]); _o += 4; return v; }
        public ushort U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(_d[_o..]); _o += 2; return v; }
        public string LongStr()
        {
            int n = (int)U32();
            var s = n > 0 ? Encoding.UTF8.GetString(_d.Slice(_o, n - 1)) : "";
            _o += n;
            return s;
        }
        public string Str()
        {
            int n = U16();
            var s = n > 0 ? Encoding.UTF8.GetString(_d.Slice(_o, n - 1)) : ""; // length includes the trailing NUL
            _o += n;
            return s;
        }
    }
}

static class Writer
{
    public static void U32(List<byte> b, uint v) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(s, v); b.AddRange(s); }
    public static void I32(List<byte> b, int v) => U32(b, (uint)v);
    public static void U16(List<byte> b, ushort v) { Span<byte> s = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(s, v); b.AddRange(s); }
    public static void Str(List<byte> b, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        U16(b, (ushort)(bytes.Length + 1));
        b.AddRange(bytes);
        b.Add(0);
    }
}

/// <summary>
/// Minimal OpenRGB SDK client (protocol 6, the version OpenRGB 1.1 speaks). One reader thread; requests are serialized.
/// Fire-and-forget commands (colours, modes) don't wait for a reply.
/// </summary>
public sealed class OpenRgbClient : IDisposable
{
    const uint Protocol = 6;
    const uint IdControllerCount = 0, IdControllerData = 1, IdProtocolVersion = 40, IdClientName = 50, IdDeviceListUpdated = 100;
    const uint IdUpdateLeds = 1050, IdUpdateZoneLeds = 1051, IdSetCustomMode = 1100, IdUpdateMode = 1101, IdSaveMode = 1102, IdUpdateZoneMode = 1103;

    readonly TcpClient _tcp = new() { NoDelay = true };
    NetworkStream _stream = null!;
    readonly object _send = new();
    readonly SemaphoreSlim _request = new(1, 1);
    TaskCompletionSource<(uint id, byte[] data)>? _pending;
    uint _pendingId;
    Thread? _reader;
    volatile bool _closed;

    public uint ServerProtocol { get; private set; }
    /// <summary>Raised (on the reader thread) when OpenRGB's device list changed, e.g. after detection.</summary>
    public event Action? DeviceListChanged;
    public bool Connected => !_closed && _tcp.Connected;

    public async Task ConnectAsync(string host, int port, string clientName, CancellationToken ct = default)
    {
        await _tcp.ConnectAsync(host, port, ct);
        _stream = _tcp.GetStream();
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "OpenRGB SDK" };
        _reader.Start();
        var reply = await Request(IdProtocolVersion, 0, BitConverter.GetBytes(Protocol), ct);
        ServerProtocol = reply.Length >= 4 ? BitConverter.ToUInt32(reply) : 0;
        Send(IdClientName, 0, [.. Encoding.UTF8.GetBytes(clientName), 0]);
    }

    public async Task<List<RgbDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        var count = BitConverter.ToUInt32(await Request(IdControllerCount, 0, [], ct));
        var list = new List<RgbDevice>();
        for (int i = 0; i < count; i++)
            list.Add(RgbDevice.Parse(i, await Request(IdControllerData, (uint)i, BitConverter.GetBytes(Protocol), ct)));
        return list;
    }

    /// <summary>Sets every LED of a device (device must be in a per-LED mode for this to show).</summary>
    public void UpdateLeds(int device, ReadOnlySpan<Rgb> colors)
    {
        var b = new List<byte>(6 + colors.Length * 4);
        Writer.U32(b, (uint)(4 + 2 + colors.Length * 4));
        Writer.U16(b, (ushort)colors.Length);
        foreach (var c in colors) Writer.U32(b, c.Packed);
        Send(IdUpdateLeds, (uint)device, [.. b]);
    }

    public void SetCustomMode(int device) => Send(IdSetCustomMode, (uint)device, []);

    public void UpdateMode(int device, int modeIndex, RgbMode mode, bool save = false)
    {
        var body = new List<byte>();
        Writer.I32(body, modeIndex);
        RgbDevice.WriteMode(body, mode);
        var b = new List<byte>(body.Count + 4);
        Writer.U32(b, (uint)(body.Count + 4));
        b.AddRange(body);
        Send(save ? IdSaveMode : IdUpdateMode, (uint)device, [.. b]);
    }

    /// <summary>Sets a zone's own hardware mode; <paramref name="modeIndex"/> -1 hands the zone back to the device mode.</summary>
    public void UpdateZoneMode(int device, int zone, int modeIndex, RgbMode? mode)
    {
        var body = new List<byte>();
        Writer.I32(body, zone);
        Writer.I32(body, modeIndex);
        if (modeIndex >= 0 && mode is not null) RgbDevice.WriteMode(body, mode);
        var b = new List<byte>(body.Count + 4);
        Writer.U32(b, (uint)(body.Count + 4));
        b.AddRange(body);
        Send(IdUpdateZoneMode, (uint)device, [.. b]);
    }

    async Task<byte[]> Request(uint id, uint device, byte[] data, CancellationToken ct)
    {
        await _request.WaitAsync(ct);
        try
        {
            var tcs = new TaskCompletionSource<(uint, byte[])>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingId = id;
            _pending = tcs;
            Send(id, device, data);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await using var _ = timeout.Token.Register(() => tcs.TrySetCanceled());
            return (await tcs.Task).Item2;
        }
        finally
        {
            _pending = null;
            _request.Release();
        }
    }

    void Send(uint id, uint device, byte[] data)
    {
        var header = new byte[16];
        "ORGB"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), device);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), id);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)data.Length);
        lock (_send)
        {
            _stream.Write(header);
            if (data.Length > 0) _stream.Write(data);
        }
    }

    void ReadLoop()
    {
        var header = new byte[16];
        try
        {
            while (!_closed)
            {
                _stream.ReadExactly(header);
                if (!header.AsSpan(0, 4).SequenceEqual("ORGB"u8)) throw new InvalidDataException("bad OpenRGB packet");
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
                var data = new byte[BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12))];
                _stream.ReadExactly(data);
                if (id == IdDeviceListUpdated) { DeviceListChanged?.Invoke(); continue; }
                if (_pending is { } p && id == _pendingId) p.TrySetResult((id, data));
                // anything else (detection progress, ACKs on newer servers) is ignored
            }
        }
        catch when (_closed) { }
        catch { _closed = true; _pending?.TrySetException(new IOException("OpenRGB connection lost")); }
    }

    public void Dispose()
    {
        _closed = true;
        try { _tcp.Close(); } catch { }
    }
}

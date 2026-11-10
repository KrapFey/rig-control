using HidSharp;

namespace RigControl.Core;

/// <summary>
/// NZXT HUE 2 family fan control (Smart Device V2, RGB &amp; Fan Controller, 3+6 channel variants).
/// Protocol ported from liquidctl's SmartDevice2 driver (GPL-3.0-or-later, Jonas Malaco, CaseySJ and contributors).
/// Lighting is left alone, so OpenRGB can keep driving it over its own handle.
/// </summary>
sealed class Hue2Device : IDisposable
{
    // PID -> product name; every entry has 3 fan channels. H1 V2 (0x2015) uses another status layout: not included.
    static readonly Dictionary<int, string> Products = new()
    {
        [0x2006] = "NZXT Smart Device V2",
        [0x200d] = "NZXT Smart Device V2",
        [0x200f] = "NZXT Smart Device V2",
        [0x2009] = "NZXT RGB & Fan Controller",
        [0x200e] = "NZXT RGB & Fan Controller",
        [0x2010] = "NZXT RGB & Fan Controller",
        [0x2011] = "NZXT RGB & Fan Controller (3+6 channels)",
        [0x2019] = "NZXT RGB & Fan Controller (3+6 channels)",
        [0x201f] = "NZXT RGB & Fan Controller (3+6 channels)",
        [0x2020] = "NZXT RGB & Fan Controller (3+6 channels)",
    };

    // ponytail: fixed duty on release; these controllers hold the last duty with no host, so leave them somewhere safe.
    // Make it a setting if anyone needs a different value.
    const byte ReleaseDuty = 60;

    // Two handles: with HidSharp on Windows a write on the stream a reader thread is blocked on stalls that read for good.
    readonly HidStream _stream, _writer;
    readonly int _reportLength;
    readonly object _writeLock = new();
    readonly Thread _reader;
    volatile bool _stop;
    readonly int[] _rpm = new int[3];
    readonly int[] _duty = new int[3];
    readonly int[] _mode = new int[3]; // 0 = nothing connected, 1 = DC, 2 = PWM
    volatile bool _haveStatus;

    public string Product { get; }
    public string Serial { get; }
    /// <summary>The id FanControl's liquidctl plugin gives this device, so imports map exactly.</summary>
    public long LiquidctlId { get; }
    public Hue2Fan[] Channels { get; }

    Hue2Device(HidDevice dev, HidStream stream, HidStream writer, string product)
    {
        _stream = stream;
        _writer = writer;
        _reportLength = Math.Max(64, dev.GetMaxOutputReportLength());
        Product = product;
        Serial = SafeSerial(dev);
        LiquidctlId = LiquidctlStableId(product, Serial, dev.DevicePath);
        Channels = [.. Enumerable.Range(0, 3).Select(i => new Hue2Fan(this, i))];

        _stream.ReadTimeout = 1000;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = $"Hue2 {Serial}" };
        _reader.Start();
    }

    /// <summary>Key for this device in <see cref="AppConfig.Drivers"/>; same prefix as its fan ids.</summary>
    public static string DriverKey(string serial) => $"nzxt-hue2/{serial}";

    /// <summary>USB ids this driver handles (so the liquidctl helper can skip them).</summary>
    public static IEnumerable<(int vid, int pid)> Supported => Products.Keys.Select(pid => (0x1E71, pid));

    /// <summary>Every supported device present, without opening anything: (serial, product, hid path).</summary>
    public static List<(string serial, string product, string path)> Present()
    {
        var list = new List<(string, string, string)>();
        foreach (var dev in DeviceList.Local.GetHidDevices(0x1E71))
            if (Products.TryGetValue(dev.ProductID, out var product)) list.Add((SafeSerial(dev), product, dev.DevicePath));
        return list;
    }

    /// <summary>Opens every supported device except the serials in <paramref name="skip"/>.</summary>
    public static List<Hue2Device> FindAll(List<string> problems, ISet<string>? skip = null)
    {
        var found = new List<Hue2Device>();
        foreach (var dev in DeviceList.Local.GetHidDevices(0x1E71))
        {
            if (!Products.TryGetValue(dev.ProductID, out var product)) continue;
            if (skip?.Contains(SafeSerial(dev)) == true) continue;
            try
            {
                if (dev.TryOpen(out HidStream stream) && dev.TryOpen(out HidStream writer)) found.Add(new Hue2Device(dev, stream, writer, product));
                else problems.Add($"{product}: could not open the device");
            }
            catch (Exception e) { problems.Add($"{product}: {e.Message}"); }
        }
        return found;
    }

    /// <summary>
    /// FanControl.Liquidctl's bridge: crc32("{description}|{serial}|{hidapi path}") &amp; 0x7FFFFFFF (0 -> 1).
    /// hidapi reports the path with the hardware-id part upper-cased: \\?\HID#VID_1E71&amp;PID_2020#7&amp;1a2b3c4d&amp;0&amp;0000#{guid}.
    /// </summary>
    internal static long LiquidctlStableId(string description, string serial, string devicePath)
    {
        var parts = devicePath.Split('#');
        if (parts.Length > 2) { parts[0] = parts[0].ToUpperInvariant(); parts[1] = parts[1].ToUpperInvariant(); }
        var key = $"{description}|{serial}|{string.Join('#', parts)}";
        var id = Crc32(System.Text.Encoding.UTF8.GetBytes(key)) & 0x7FFFFFFF;
        return id == 0 ? 1 : id;
    }

    static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc >> 1) ^ (0xEDB88320 & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }

    static string SafeSerial(HidDevice dev)
    {
        try { return dev.GetSerialNumber(); } catch { return dev.DevicePath.GetHashCode().ToString("x8"); }
    }

    // liquidctl initialize() sends both of these at start. 60 03 rescans the fans and silences the device for
    // seconds (also for any other app reading it), so only nudge a device that isn't reporting on its own.
    void Wake(int silentSeconds)
    {
        if (_haveStatus) return;
        const byte interval = 1; // status every 0.5 s: 1 + round((0.5 - 0.5) / 0.25)
        if (silentSeconds == 2) Write([0x60, 0x02, 0x01, 0xe8, interval, 0x01, 0xe8, interval]);
        if (silentSeconds == 5) Write([0x60, 0x03]);
    }

    void ReadLoop()
    {
        int silent = 0;
        while (!_stop)
        {
            byte[] msg;
            try { msg = _stream.Read(); }
            catch (TimeoutException) { Wake(++silent); continue; }
            catch { if (_stop) return; Thread.Sleep(500); continue; }

            // Fan status: 0x67 0x02, mode @16, rpm (LE u16) @24, duty @40.
            if (msg.Length < 43 || msg[0] != 0x67 || msg[1] != 0x02) continue;
            for (int i = 0; i < 3; i++)
            {
                _mode[i] = msg[16 + i];
                _rpm[i] = msg[24 + 2 * i] | msg[25 + 2 * i] << 8;
                _duty[i] = msg[40 + i];
            }
            _haveStatus = true;
        }
    }

    internal void Refresh()
    {
        if (!_haveStatus) return;
        for (int i = 0; i < 3; i++)
        {
            Channels[i].Connected = _mode[i] != 0;
            Channels[i].Rpm = _rpm[i];
            Channels[i].Duty = _duty[i];
        }
    }

    // The controller takes about one duty command per status cycle: three back-to-back single-channel writes
    // get dropped, so channel 3 lagged behind. Collect the tick's duties and send them as one masked command.
    readonly int[] _pending = [-1, -1, -1];

    internal void SetDuty(int channel, byte duty)
    {
        lock (_writeLock) _pending[channel] = duty;
    }

    internal void Flush()
    {
        var msg = new byte[] { 0x62, 0x01, 0, 0, 0, 0 };
        lock (_writeLock)
        {
            for (int i = 0; i < 3; i++)
            {
                if (_pending[i] < 0) continue;
                msg[2] |= (byte)(1 << i); // channel bitmask
                msg[3 + i] = (byte)_pending[i];
                _pending[i] = -1;
            }
        }
        if (msg[2] != 0) Write(msg);
    }

    internal void Release(int channel) => SetDuty(channel, ReleaseDuty);

    void Write(byte[] data)
    {
        var buf = new byte[_reportLength]; // byte 0 is the report ID (0x60 / 0x62), rest zero padded
        data.CopyTo(buf, 0);
        lock (_writeLock) _writer.Write(buf);
    }

    public void Dispose()
    {
        _stop = true;
        try { _stream.Dispose(); } catch { }
        try { _writer.Dispose(); } catch { }
        _reader.Join(1500);
    }
}

sealed class Hue2Fan(Hue2Device dev, int channel)
    : FanChannel($"nzxt-hue2/{dev.Serial}/fan/{channel + 1}", $"Fan {channel + 1}", $"{dev.Product} · {dev.Serial[^4..]}")
{
    internal Hue2Device Owner => dev;
    internal int Channel => channel;

    public override void Set(float percent) => dev.SetDuty(channel, (byte)Math.Round(Math.Clamp(percent, 0, 100)));
    public override void Release() => dev.Release(channel);
}

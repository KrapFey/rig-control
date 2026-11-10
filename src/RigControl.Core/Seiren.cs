using HidSharp;

namespace RigControl.Core;

/// <summary>
/// Razer Seiren V3 Chroma (1532:056F), 10-LED ring. OpenRGB has no driver for it, so RigControl drives it directly.
/// Lighting goes through the vendor HID collection as 64-byte feature reports, report ID 0x07: bytes 1-62 hold a
/// Razer report, byte 63 is the XOR of bytes 1-62. Protocol research: openrazer PR #2896.
/// </summary>
public sealed class SeirenV3 : IDisposable
{
    public const int LedCount = 10;
    const int ReportLength = 64;
    const byte ReportId = 0x07, Transaction = 0x1F, VarStore = 0x01, NoStore = 0x00;

    public enum Effect : byte { None = 0, Static = 1, Breathing = 2, Spectrum = 3, Wave = 4, Custom = 8 }

    readonly HidStream _stream;
    readonly object _lock = new();

    SeirenV3(HidStream stream) => _stream = stream;

    /// <summary>Opens the mic's lighting interface, or returns null when there's no Seiren V3.</summary>
    public static SeirenV3? Open()
    {
        foreach (var dev in DeviceList.Local.GetHidDevices(0x1532, 0x056F))
        {
            // Only the vendor collection (usage page 0xFF53) has 64-byte feature reports; the other one is media keys.
            try
            {
                if (dev.GetMaxFeatureReportLength() < ReportLength) continue;
                if (dev.GetReportDescriptor().DeviceItems.All(i => i.Usages.GetAllValues().All(u => (u >> 16) != 0xFF53))) continue;
                if (dev.TryOpen(out HidStream stream)) return new SeirenV3(stream);
            }
            catch { /* not this collection */ }
        }
        return null;
    }

    bool Send(byte cmdClass, byte cmdId, byte dataSize, ReadOnlySpan<byte> args)
    {
        var buf = new byte[ReportLength];
        buf[0] = ReportId;
        buf[2] = Transaction;
        buf[6] = dataSize;
        buf[7] = cmdClass;
        buf[8] = cmdId;
        args[..Math.Min(args.Length, ReportLength - 10)].CopyTo(buf.AsSpan(9));
        byte crc = 0;
        for (int i = 1; i < 63; i++) crc ^= buf[i];
        buf[63] = crc;
        lock (_lock)
        {
            try { _stream.SetFeature(buf); return true; }
            catch { return false; }
        }
    }

    /// <summary>Per-LED colours (Direct mode). Call <see cref="SetEffect"/>(Custom) once first.</summary>
    public void SetFrame(ReadOnlySpan<Rgb> leds)
    {
        Span<byte> args = stackalloc byte[5 + LedCount * 3];
        args[4] = LedCount - 1; // row 0, first LED 0, last LED 9
        for (int i = 0; i < LedCount; i++)
        {
            var c = i < leds.Length ? leds[i] : default;
            args[5 + i * 3] = c.R;
            args[6 + i * 3] = c.G;
            args[7 + i * 3] = c.B;
        }
        Send(0x0F, 0x03, 0x22, args);
    }

    /// <summary>Hardware effects (same layouts as openrazer's extended matrix helpers). Effects other than Custom persist on the mic.</summary>
    public void SetEffect(Effect effect, Rgb? color = null, byte waveDirection = 1)
    {
        Span<byte> args = stackalloc byte[9];
        args[0] = effect == Effect.Custom ? NoStore : VarStore;
        args[2] = (byte)effect;
        byte size = 0x06;
        switch (effect)
        {
            case Effect.Static when color is Rgb c:
                args[5] = 0x01;
                (args[6], args[7], args[8]) = (c.R, c.G, c.B);
                size = 0x09;
                break;
            case Effect.Breathing when color is Rgb c: // without a colour the mic picks random colours
                args[3] = 0x01;
                args[5] = 0x01;
                (args[6], args[7], args[8]) = (c.R, c.G, c.B);
                size = 0x09;
                break;
            case Effect.Wave:
                args[3] = waveDirection;
                args[4] = 0x28; // speed, lower is faster
                break;
            case Effect.Custom:
                size = 0x0C;
                break;
        }
        Send(0x0F, 0x02, size, args);
    }

    public void SetBrightness(byte brightness) => Send(0x0F, 0x04, 0x03, [VarStore, 0x00, brightness]);

    public void Dispose()
    {
        try { _stream.Dispose(); } catch { }
    }
}

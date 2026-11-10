using LibreHardwareMonitor.Hardware;

namespace RigControl.Core;

/// <summary>A temperature reading. Id is stable across runs (LHM identifier or our own).</summary>
public sealed class TempSensor(string id, string name, string device)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Device { get; } = device;
    public float? Value { get; internal set; }
}

/// <summary>A controllable fan channel, with its paired speed reading when the hardware has one.</summary>
public abstract class FanChannel(string id, string name, string device)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Device { get; } = device;
    public float? Rpm { get; protected internal set; }
    /// <summary>Duty the hardware reports (or the last one we set when it reports none).</summary>
    public float? Duty { get; protected internal set; }
    /// <summary>Whether something is plugged in, when the hardware can tell (null = unknown).</summary>
    public bool? Connected { get; protected internal set; }
    public abstract void Set(float percent);
    /// <summary>Give the channel back to the hardware's own control (BIOS / firmware / safe duty).</summary>
    public abstract void Release();
}

/// <summary>
/// Owns every hardware backend. Not thread-safe: call Update/Set from one thread (the engine's).
/// </summary>
public sealed class HardwareHub : IDisposable
{
    readonly Computer _lhm = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMotherboardEnabled = true,
        IsControllerEnabled = true,
        IsStorageEnabled = true,
    };
    readonly List<IHardware> _lhmHardware = [];
    readonly List<(ISensor sensor, TempSensor temp)> _lhmTemps = [];
    readonly List<LhmFan> _lhmFans = [];
    readonly List<Hue2Device> _hue2 = [];

    public List<TempSensor> Temps { get; } = [];
    public List<FanChannel> Fans { get; } = [];
    public List<string> Problems { get; } = [];
    internal IReadOnlyList<Hue2Device> Hue2 => _hue2;

    public void Open()
    {
        try
        {
            _lhm.Open();
            foreach (var hw in _lhm.Hardware) Collect(hw);
        }
        catch (Exception e) { Problems.Add($"LibreHardwareMonitor: {e.Message}"); }

        foreach (var dev in Hue2Device.FindAll(Problems))
        {
            _hue2.Add(dev);
            Fans.AddRange(dev.Channels);
        }
        Update();
    }

    void Collect(IHardware hw)
    {
        _lhmHardware.Add(hw);
        hw.Update();
        var speeds = hw.Sensors.Where(s => s.SensorType == SensorType.Fan).ToList();
        foreach (var s in hw.Sensors)
        {
            if (s.SensorType == SensorType.Temperature)
            {
                var t = new TempSensor(s.Identifier.ToString(), s.Name, hw.Name);
                _lhmTemps.Add((s, t));
                Temps.Add(t);
            }
            else if (s.SensorType == SensorType.Control && s.Control != null)
            {
                // LHM numbers a chip's controls and speed readings the same way: control/N <-> fan/N.
                var rpm = speeds.FirstOrDefault(f => f.Index == s.Index);
                var fan = new LhmFan(s, rpm, hw.Name);
                _lhmFans.Add(fan);
                Fans.Add(fan);
            }
        }
        foreach (var sub in hw.SubHardware) Collect(sub);
    }

    public void Update()
    {
        foreach (var hw in _lhmHardware)
        {
            try { hw.Update(); } catch { /* one flaky chip must not stop the loop */ }
        }
        foreach (var (s, t) in _lhmTemps) t.Value = s.Value;
        foreach (var f in _lhmFans) f.Refresh();
        foreach (var d in _hue2) d.Refresh();
    }

    /// <summary>Sends the duties set since the last flush (devices that batch their commands).</summary>
    public void Flush()
    {
        foreach (var d in _hue2)
        {
            try { d.Flush(); } catch { }
        }
    }

    /// <summary>Closes devices without touching fans; whoever drove them releases them first.</summary>
    public void Dispose()
    {
        foreach (var d in _hue2) d.Dispose();
        try { _lhm.Close(); } catch { }
    }
}

sealed class LhmFan(ISensor control, ISensor? rpm, string device)
    : FanChannel(control.Identifier.ToString(), control.Name, device)
{
    public override void Set(float percent) => control.Control!.SetSoftware(Math.Clamp(percent, 0, 100));
    public override void Release() => control.Control!.SetDefault();

    internal void Refresh()
    {
        Rpm = rpm?.Value;
        Duty = control.Value;
    }
}

using System.Diagnostics;
using HidSharp;

namespace RigControl.Core;

/// <summary>What a device can do. Anything null/false isn't offered for that device.</summary>
public sealed class PeripheralCaps
{
    public int DpiMin { get; init; } = 100;
    public int DpiMax { get; init; }
    public int DpiStep { get; init; } = 50;
    public bool Dpi => DpiMax > 0;
    public int MaxStages { get; init; }
    public int[] PollRates { get; init; } = [];
    public bool SleepTimeout { get; init; }
    public int SleepMinSeconds { get; init; } = 60;
    public int SleepMaxSeconds { get; init; } = 900;
    public bool LowBatteryThreshold { get; init; }
    public bool Battery { get; init; }
    public bool Charging { get; init; }

    // Headsets
    public bool Sidetone { get; init; }
    public int InactiveMaxMinutes { get; init; }
    public bool Lights { get; init; }
    public bool VoicePrompts { get; init; }
    public bool RotateToMute { get; init; }
    public bool MicVolume { get; init; }
    public bool Anc { get; init; }
    public bool Chatmix { get; init; }
    /// <summary>Names of the built-in EQ presets (empty = none).</summary>
    public string[] EqPresets { get; init; } = [];
}

/// <summary>What the device reports right now (null = unknown or not supported).</summary>
public sealed record PeripheralState
{
    public int? Dpi { get; init; }
    public List<int>? Stages { get; init; }
    public int? ActiveStage { get; init; }
    public int? PollRate { get; init; }
    public int? SleepSeconds { get; init; }
    public int? LowBatteryPercent { get; init; }
    public int? BatteryPercent { get; init; }
    public bool? Charging { get; init; }
    /// <summary>Headsets: false when the headset is off or out of range (the dongle is still there).</summary>
    public bool? Connected { get; init; }
    public int? Sidetone { get; init; }
    public int? Chatmix { get; init; }
    public bool? MicMuted { get; init; }
}

/// <summary>Settings saved per device; only the ones set are applied.</summary>
public sealed class PeripheralSettings
{
    public int? Dpi { get; set; }
    public List<int>? Stages { get; set; }
    public int? ActiveStage { get; set; }
    public int? PollRate { get; set; }
    public int? SleepSeconds { get; set; }
    public int? LowBatteryPercent { get; set; }

    // Headsets
    public int? Sidetone { get; set; }
    public int? InactiveMinutes { get; set; }
    public bool? Lights { get; set; }
    public bool? VoicePrompts { get; set; }
    public bool? RotateToMute { get; set; }
    public int? MicVolume { get; set; }
    public int? EqPreset { get; set; }
    /// <summary>0 off, 1 noise cancelling, 2 ambient.</summary>
    public int? Anc { get; set; }
}

/// <summary>A mouse, headset or other device whose settings RigControl can change.</summary>
public interface IPeripheral : IDisposable
{
    /// <summary>Stable id for saved settings, e.g. "razer/0084".</summary>
    string Key { get; }
    string Name { get; }
    /// <summary>Mouse, Headset, Keyboard…</summary>
    string Kind { get; }
    /// <summary>Where the device support comes from, shown to the user, e.g. "Razer protocol · device data from openrazer".</summary>
    string Source { get; }
    PeripheralCaps Caps { get; }
    PeripheralState Read();
    /// <summary>Applies the settings that differ from <paramref name="current"/>.</summary>
    void Apply(PeripheralSettings settings, PeripheralState current);
}

/// <summary>
/// Finds supported peripherals, keeps their state fresh (battery every 30 s), applies saved settings when a device
/// appears and whenever settings change. One background thread owns all device I/O.
/// </summary>
public sealed class PeripheralHub : IDisposable
{
    readonly Func<Dictionary<string, PeripheralSettings>> _settings;
    readonly object _lock = new();
    readonly Thread _thread;
    readonly AutoResetEvent _wake = new(false);
    readonly Stopwatch _clock = Stopwatch.StartNew();
    volatile bool _stop, _rescan = true, _reapply;
    List<IPeripheral> _devices = [];
    Dictionary<string, PeripheralState> _states = [];

    public IReadOnlyList<IPeripheral> Devices { get { lock (_lock) return [.. _devices]; } }
    public IReadOnlyDictionary<string, PeripheralState> States { get { lock (_lock) return new Dictionary<string, PeripheralState>(_states); } }
    public event Action? Changed;
    /// <summary>
    /// The DPI or active stage changed on the device itself (its DPI button), not from RigControl: the owner should
    /// take it into the saved settings, or the next re-apply would undo the button press. Raised on the hub thread.
    /// </summary>
    public event Action<string, PeripheralState>? ChangedOnDevice;
    int _watchers;
    readonly Dictionary<string, PeripheralState> _lastRead = [];

    /// <summary>While someone watches (the Devices page is open), read every second so changes made on the device itself
    /// (pressing the DPI button) show up almost at once. Otherwise every 30 s.</summary>
    public void Watch(bool on)
    {
        Interlocked.Add(ref _watchers, on ? 1 : -1);
        _wake.Set();
    }

    public PeripheralHub(Func<Dictionary<string, PeripheralSettings>> settings)
    {
        _settings = settings;
        DeviceList.Local.Changed += (_, _) => { _rescan = true; _wake.Set(); }; // plugged in / out
        _thread = new Thread(Loop) { IsBackground = true, Name = "Peripherals" };
        _thread.Start();
    }

    /// <summary>Call after the user changed a device's settings.</summary>
    public void SettingsChanged()
    {
        _reapply = true;
        _wake.Set();
    }

    void Loop()
    {
        double lastPoll = -1e9;
        while (!_stop)
        {
            try
            {
                if (_rescan)
                {
                    _rescan = false;
                    Thread.Sleep(500); // let a newly plugged device settle
                    Rescan();
                }
                bool poll = _clock.Elapsed.TotalSeconds - lastPoll > (Volatile.Read(ref _watchers) > 0 ? 1 : 30);
                if (_reapply || poll)
                {
                    bool apply = _reapply;
                    _reapply = false;
                    lastPoll = _clock.Elapsed.TotalSeconds;
                    Refresh(apply);
                }
            }
            catch (Exception e) { AppConfig.Log?.Invoke($"peripherals: {e.Message}"); }
            _wake.WaitOne(TimeSpan.FromSeconds(Volatile.Read(ref _watchers) > 0 ? 1 : 5));
        }
    }

    void Rescan()
    {
        var found = new List<IPeripheral>();
        found.AddRange(RazerMouse.FindAll());
        found.AddRange(HeadsetControlDevice.FindAll());
        found.AddRange(LogitechDevice.FindAll());

        List<IPeripheral> old;
        lock (_lock)
        {
            old = _devices;
            // Keep open devices that are still there; their keys are stable.
            var keep = old.Where(o => found.Any(f => f.Key == o.Key)).ToList();
            foreach (var f in found.Where(f => keep.Any(k => k.Key == f.Key))) f.Dispose();
            var added = found.Where(f => keep.All(k => k.Key != f.Key)).ToList();
            foreach (var o in old.Except(keep)) o.Dispose();
            _devices = [.. keep, .. added];
            if (added.Count > 0 || keep.Count != old.Count)
                AppConfig.Log?.Invoke($"peripherals: {string.Join(", ", _devices.Select(d => d.Name))}");
            if (added.Count > 0) _reapply = true; // new device: apply its saved settings
        }
        Refresh(_reapply);
        _reapply = false;
    }

    void Refresh(bool apply)
    {
        var saved = _settings();
        var states = new Dictionary<string, PeripheralState>();
        foreach (var d in Devices)
        {
            try
            {
                var state = d.Read();
                if (apply && saved.TryGetValue(d.Key, out var s))
                {
                    d.Apply(s, state);
                    state = d.Read();
                }
                // Between two plain reads only the user, on the device, can have changed the DPI or stage.
                else if (_lastRead.TryGetValue(d.Key, out var last) && (state.Dpi != last.Dpi || state.ActiveStage != last.ActiveStage)
                         && state.Dpi is not null)
                {
                    ChangedOnDevice?.Invoke(d.Key, state);
                }
                _lastRead[d.Key] = state;
                states[d.Key] = state;
            }
            catch (Exception e) { AppConfig.Log?.Invoke($"peripheral {d.Name}: {e.Message}"); }
        }
        lock (_lock) _states = states;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(3000);
        foreach (var d in Devices) d.Dispose();
    }
}

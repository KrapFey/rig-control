using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;
using RigControl.Core;

namespace RigControl;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>
/// A number that glides to its target (critically damped, frame-rate independent).
/// Only ticks while moving: the frame callback detaches once everything has settled.
/// </summary>
public sealed class Smooth : Observable
{
    static readonly HashSet<Smooth> Moving = [];
    static DateTime _lastFrame;
    static bool _hooked;

    double _value, _target;
    readonly double _speed;

    public Smooth(double speed = 7) => _speed = speed;

    public double Value { get => _value; private set => Set(ref _value, value); }
    public double Target
    {
        get => _target;
        set
        {
            if (double.IsNaN(value)) value = 0;
            _target = value;
            if (Math.Abs(_value - value) < 0.01) return;
            Moving.Add(this);
            if (!_hooked)
            {
                _hooked = true;
                _lastFrame = DateTime.Now;
                CompositionTarget.Rendering += Frame;
            }
        }
    }

    /// <summary>Jump without animating (first value).</summary>
    public void Snap(double v) { _target = v; Value = v; Moving.Remove(this); }

    static void Frame(object? sender, object e)
    {
        var now = DateTime.Now;
        var dt = Math.Min((now - _lastFrame).TotalSeconds, 0.1);
        _lastFrame = now;
        foreach (var s in Moving.ToList())
        {
            var k = 1 - Math.Exp(-s._speed * dt);
            var v = s._value + (s._target - s._value) * k;
            if (Math.Abs(s._target - v) < 0.05) { v = s._target; Moving.Remove(s); }
            s.Value = v;
        }
        if (Moving.Count == 0)
        {
            CompositionTarget.Rendering -= Frame;
            _hooked = false;
        }
    }
}

public sealed class FanVm(FanChannel channel) : Observable
{
    public FanChannel Channel { get; } = channel;
    public string Id => Channel.Id;
    public string Device => Channel.Device;
    /// <summary>Compact device label for tiles: "NZXT · 1A2B", "GeForce RTX 4070 SUPER", "ITE IT8689E".</summary>
    public string Short { get; } = ShortName(channel.Device);

    static string ShortName(string device)
    {
        // "Vendor Long Product Name · tag" -> "Vendor · tag" (tells identical controllers apart); otherwise drop the vendor prefix.
        var dot = device.LastIndexOf(" · ", StringComparison.Ordinal);
        if (dot > 0) return $"{device.Split(' ')[0]} · {device[(dot + 3)..]}";
        return System.Text.RegularExpressions.Regex.Replace(device, @"^(NVIDIA|AMD|Intel)\s+", "");
    }
    public string Caption => $"{Short} · {Mode}";
    public Smooth Rpm { get; } = new(5);
    public Smooth Duty { get; } = new(6);

    string _name = channel.Name;
    public string Name { get => _name; set => Set(ref _name, value); }

    string _mode = "";
    /// <summary>"Auto", "CPU curve", "Manual 40%", ...</summary>
    public string Mode { get => _mode; set { if (Set(ref _mode, value)) Raise(nameof(Caption)); } }

    bool _dutyKnown;
    /// <summary>False when the hardware doesn't report a duty and nothing is driving the fan.</summary>
    public bool DutyKnown { get => _dutyKnown; set => Set(ref _dutyKnown, value); }

    bool _active;
    public bool Active { get => _active; set => Set(ref _active, value); }

    bool _present = true;
    public bool Present { get => _present; set => Set(ref _present, value); }

    string? _error;
    public string? Error { get => _error; set => Set(ref _error, value); }
}

public sealed class TempVm(TempSensor sensor) : Observable
{
    public TempSensor Sensor { get; } = sensor;
    public string Id => Sensor.Id;
    /// <summary>Shown name: yours if you renamed it.</summary>
    public string Name => App.Instance!.SensorLabel(Sensor);
    /// <summary>The hardware's own name (also what CPU/GPU detection looks for).</summary>
    public string OriginalName => Sensor.Name;
    /// <summary>Your name for it; empty clears it.</summary>
    public string CustomName
    {
        get => App.Instance!.Config.SensorNames.GetValueOrDefault(Id) ?? "";
        set
        {
            var v = value.Trim();
            var names = App.Instance!.Config.SensorNames;
            if (v == CustomName) return;
            if (v.Length == 0 || v == Sensor.Name) names.Remove(Id); else names[Id] = v;
            App.Instance.ConfigChanged();
            Raise(nameof(Name));
            Raise(nameof(CustomName));
        }
    }
    public string Device => Sensor.Device;
    public Smooth Value { get; } = new(5);
}

public sealed class TempGroup(string device, IEnumerable<TempVm> items) : List<TempVm>(items)
{
    public string Device { get; } = device;
}

/// <summary>Everything the pages show, refreshed from the engine each tick while a window is open.</summary>
public sealed class Live
{
    public static Live Instance { get; } = new();

    public ObservableCollection<FanVm> Fans { get; } = [];
    public List<TempVm> Temps { get; } = [];
    public event Action? Refreshed;

    bool _first = true;
    int _listeners;

    Live()
    {
        var app = App.Instance!;
        foreach (var f in app.Hub.Fans) Fans.Add(new FanVm(f));
        foreach (var t in app.Hub.Temps) Temps.Add(new TempVm(t));
    }

    public void Attach()
    {
        if (_listeners++ == 0) App.Instance!.Engine.Updated += OnEngine;
        Refresh();
    }

    public void Detach()
    {
        if (--_listeners == 0) App.Instance!.Engine.Updated -= OnEngine;
    }

    void OnEngine() => App.Instance!.Ui.TryEnqueue(Refresh);

    public void Refresh()
    {
        var app = App.Instance!;
        var cfg = app.Config;
        foreach (var vm in Fans)
        {
            var ch = vm.Channel;
            cfg.Fans.TryGetValue(vm.Id, out var s);
            app.Engine.Status.TryGetValue(vm.Id, out var st);
            vm.Name = string.IsNullOrWhiteSpace(s?.Name) ? ch.Name : s!.Name!;
            vm.Active = st?.Target is not null;
            vm.Error = st?.Error;
            vm.Mode = app.Engine.Paused ? "Paused"
                : s is not { Enabled: true } ? "Hardware auto"
                : s.Manual is double m ? $"Manual · {m:0}%"
                : s.Curve ?? "No curve";
            vm.Present = ch.Connected ?? (ch.Rpm > 0 || vm.Active);
            Push(vm.Rpm, ch.Rpm ?? 0);
            var duty = st?.Target ?? ch.Duty;
            vm.DutyKnown = duty is not null;
            Push(vm.Duty, duty ?? 0);
        }
        foreach (var t in Temps) Push(t.Value, t.Sensor.Value ?? 0);
        _first = false;
        Refreshed?.Invoke();
    }

    void Push(Smooth s, double v)
    {
        if (_first) s.Snap(v); else s.Target = v;
    }

    /// <summary>Best guess at "the CPU temperature" and "the GPU temperature" for any machine.</summary>
    public TempVm? Cpu => Temps.FirstOrDefault(t => t.Sensor == CpuSensor(App.Instance!.Hub.Temps));
    public TempVm? Gpu => Temps.FirstOrDefault(t => t.Sensor == GpuSensor(App.Instance!.Hub.Temps));

    public static TempSensor? CpuSensor(IEnumerable<TempSensor> temps) => Pick(temps, ["CPU Package", "Core (Tctl/Tdie)", "Core Max", "CPU"], "cpu");
    public static TempSensor? GpuSensor(IEnumerable<TempSensor> temps) => Pick(temps, ["GPU Core", "GPU Hot Spot"], "gpu");

    static TempSensor? Pick(IEnumerable<TempSensor> temps, string[] names, string idHint)
    {
        var list = temps.ToList();
        foreach (var n in names)
        {
            var hit = list.FirstOrDefault(t => t.Name == n && t.Id.Contains(idHint, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return list.FirstOrDefault(t => t.Id.Contains(idHint, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Average duty of the fans that are spinning (the dashboard's "airflow"), or null with none.</summary>
    public static double? Airflow(IEnumerable<FanChannel> fans)
    {
        var spinning = fans.Where(f => f.Connected ?? f.Rpm > 0).Select(f => (double?)f.Duty).Where(d => d is not null).ToList();
        return spinning.Count == 0 ? null : spinning.Average();
    }
}

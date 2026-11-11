using System.Diagnostics;

namespace RigControl.Core;

public static class CurveMath
{
    /// <summary>Piecewise linear; flat beyond the first and last point.</summary>
    public static double Interpolate(IReadOnlyList<CurvePoint> pts, double t)
    {
        if (pts.Count == 0) return 100;
        if (t <= pts[0].Temp) return pts[0].Percent;
        for (int i = 1; i < pts.Count; i++)
        {
            var (a, b) = (pts[i - 1], pts[i]);
            if (t <= b.Temp)
                return b.Temp == a.Temp ? b.Percent : a.Percent + (b.Percent - a.Percent) * (t - a.Temp) / (b.Temp - a.Temp);
        }
        return pts[^1].Percent;
    }

    /// <summary>
    /// One fan step: min clamp, stop threshold, step limits, then a start kick when spinning up from 0.
    /// </summary>
    public static double NextDuty(double target, double? last, FanSettings s)
    {
        target = Math.Clamp(Math.Max(target, s.MinPercent), 0, 100);
        if (s.StopPercent > 0 && target < s.StopPercent) target = 0;
        if (last is double l)
        {
            if (s.StepUp > 0) target = Math.Min(target, l + s.StepUp);
            if (s.StepDown > 0) target = Math.Max(target, l - s.StepDown);
            if (l <= 0 && target > 0) target = Math.Max(target, s.StartPercent);
        }
        return Math.Round(target, 1);
    }
}

/// <summary>Deadband + response time on a curve's input temperature.</summary>
public sealed class Hysteresis
{
    double? _used;
    double? _pendingSince;

    /// <param name="hot">At or above this the new temperature is used at once (top of the curve).</param>
    public double Apply(double t, double now, double deadband, double response, double hot)
    {
        if (_used is not double used || t >= hot || Math.Abs(t - used) >= Math.Max(deadband, 0.0001))
        {
            if (_used is null || t >= hot || now - (_pendingSince ??= now) >= response)
            {
                _used = t;
                _pendingSince = null;
            }
        }
        else _pendingSince = null;
        return _used!.Value;
    }
}

public sealed class FanStatus
{
    public required FanChannel Channel { get; init; }
    public double? Target { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Runs the control loop on its own thread. Swap the config with <see cref="Apply"/>; read results from
/// <see cref="CurveOutputs"/> / <see cref="Status"/> after <see cref="Updated"/> fires.
/// </summary>
public sealed class FanEngine : IDisposable
{
    readonly HardwareHub _hub;
    volatile AppConfig _config;
    readonly Dictionary<string, Hysteresis> _hyst = [];
    readonly Dictionary<string, double> _last = [];
    readonly HashSet<string> _driven = [];
    readonly Thread _thread;
    readonly ManualResetEventSlim _wake = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    volatile bool _stop;
    volatile string? _identify;
    double _identifyUntil;

    public HardwareHub Hub => _hub;
    public AppConfig Config => _config;
    public IReadOnlyDictionary<string, double> CurveOutputs { get; private set; } = new Dictionary<string, double>();
    public IReadOnlyDictionary<string, double> CurveInputs { get; private set; } = new Dictionary<string, double>();
    public Dictionary<string, FanStatus> Status { get; }
    public event Action? Updated;
    /// <summary>While set (e.g. another fan app is running) curves still compute but no fan is touched.</summary>
    public volatile bool Paused;

    public FanEngine(HardwareHub hub, AppConfig config)
    {
        _hub = hub;
        _config = config;
        Status = hub.Fans.ToDictionary(f => f.Id, f => new FanStatus { Channel = f });
        _thread = new Thread(Loop) { IsBackground = true, Name = "FanEngine", Priority = ThreadPriority.AboveNormal };
    }

    public void Start() => _thread.Start();

    /// <summary>Use a new config from the next tick on.</summary>
    public void Apply(AppConfig config)
    {
        _config = config.Clone();
        _wake.Set();
    }

    /// <summary>Stops one fan for a few seconds so you can see which one it is (a stopping fan is easier to spot than a faster one).</summary>
    public void Identify(string fanId)
    {
        _identify = fanId;
        _identifyUntil = _clock.Elapsed.TotalSeconds + 8; // big fans coast down slowly; 4 s barely shows
        _wake.Set();
    }

    void Loop()
    {
        while (!_stop)
        {
            var cfg = _config;
            try { Tick(cfg); } catch (Exception e) { Trace.WriteLine($"FanEngine tick: {e}"); }
            Updated?.Invoke();
            _wake.Wait(TimeSpan.FromSeconds(Math.Clamp(cfg.IntervalSeconds, 0.25, 5)));
            _wake.Reset();
        }
    }

    void Tick(AppConfig cfg)
    {
        _hub.Update();
        double now = _clock.Elapsed.TotalSeconds;
        var temps = _hub.Temps.ToDictionary(t => t.Id, t => t.Value);
        var curves = cfg.Curves.ToDictionary(c => c.Name);
        var outputs = new Dictionary<string, double>();
        var inputs = new Dictionary<string, double>();

        double? Eval(string name, int depth)
        {
            if (outputs.TryGetValue(name, out var done)) return done;
            if (depth > 8 || !curves.TryGetValue(name, out var c)) return null;
            double? r;
            switch (c.Kind)
            {
                case CurveKind.Flat:
                    r = c.Percent;
                    break;
                case CurveKind.Mix:
                    var vals = c.Inputs.Select(i => Eval(i, depth + 1)).ToList();
                    // A missing input counts as unknown -> fail safe to 100 rather than silently ignoring it.
                    r = vals.Count == 0 || vals.Any(v => v is null) ? null : c.Mix switch
                    {
                        MixFunction.Min => vals.Min(),
                        MixFunction.Average => vals.Average(),
                        _ => vals.Max(),
                    };
                    break;
                default:
                    if (c.Source is null || !temps.TryGetValue(c.Source, out var t) || t is not float tv || float.IsNaN(tv)) { r = null; break; }
                    var h = _hyst.TryGetValue(name, out var hh) ? hh : _hyst[name] = new Hysteresis();
                    var hot = c.Points.Count > 0 ? c.Points[^1].Temp : double.MaxValue;
                    var used = h.Apply(tv, now, c.Deadband, c.ResponseSeconds, hot);
                    inputs[name] = used;
                    r = CurveMath.Interpolate(c.Points, used);
                    break;
            }
            if (r is double v) outputs[name] = v;
            return r;
        }

        foreach (var c in cfg.Curves) Eval(c.Name, 0);

        CurveOutputs = outputs;
        CurveInputs = inputs;
        if (_identify is not null && now > _identifyUntil) _identify = null;
        if (Paused)
        {
            ReleaseDriven();
            foreach (var st in Status.Values) st.Target = null;
            _last.Clear();
            return;
        }

        foreach (var (id, st) in Status)
        {
            var fan = st.Channel;
            cfg.Fans.TryGetValue(id, out var s);
            bool identify = _identify == id;
            if (!identify && (s is null || !s.Enabled || (s.Manual is null && s.Curve is null)))
            {
                if (_driven.Remove(id)) Safe(st, fan.Release);
                st.Target = null;
                _last.Remove(id);
                continue;
            }

            double target;
            if (identify) target = 0;
            else if (s!.Manual is double m) target = m;
            else target = (s.Curve is not null ? Eval(s.Curve, 0) : null) ?? 100; // sensor lost -> full speed

            double duty = identify ? 0 : CurveMath.NextDuty(target, _last.TryGetValue(id, out var l) ? l : null, s!);
            _last[id] = duty;
            st.Target = duty;
            _driven.Add(id);
            Safe(st, () => fan.Set((float)duty));
        }
        _hub.Flush();
    }

    static void Safe(FanStatus st, Action a)
    {
        try { a(); st.Error = null; } catch (Exception e) { st.Error = e.Message; }
    }

    /// <summary>Hands every fan this engine drove back to the hardware. Safe to call from a crash handler.</summary>
    public void ReleaseDriven()
    {
        foreach (var id in _driven.ToList())
            if (Status.TryGetValue(id, out var st)) Safe(st, st.Channel.Release);
        _driven.Clear();
        _hub.Flush();
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        if (_thread.IsAlive) _thread.Join(3000);
        ReleaseDriven();
    }
}

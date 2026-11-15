namespace RigControl.Core;

/// <summary>A fan as the starter setup sees it.</summary>
public sealed record StarterFan(string Id, string Name, string Device);

/// <summary>One fan's suggestion: the curve it would follow (null: left as it is), and why, in words.</summary>
public sealed record StarterChoice(StarterFan Fan, string? Curve, string Why);

/// <summary>What the starter setup would make: the curves, and which fan follows which.</summary>
public sealed record StarterPlan(List<CurveDef> Curves, List<StarterChoice> Fans);

/// <summary>
/// Cooling in one step for a fresh install: a curve for the CPU, the GPU and the coolant (when there's a sensor
/// for each) shaped like a preset, a Case curve following the higher of CPU and GPU, and a guess at which fan
/// follows which from where it's connected. Only a suggestion: the app shows it for confirming before anything
/// changes, and every choice can be moved on the Cooling page afterwards.
/// </summary>
public static class StarterSetup
{
    public const string Cpu = "CPU", Gpu = "GPU", Liquid = "Liquid", Case = "Case";

    /// <param name="liquid">The coolant sensor and the device it's on (a cooler: its fans are radiator fans).</param>
    /// <param name="sensorName">The hardware's name for a sensor id (shapes the curve for that kind of sensor).</param>
    public static StarterPlan Plan(string? cpuSensor, string? gpuSensor, (string Id, string Device)? liquid,
        IEnumerable<StarterFan> fans, CoolingPreset preset, Func<string, string?> sensorName)
    {
        var curves = new List<CurveDef>();
        CurveDef? Graph(string name, string? sensor)
        {
            if (sensor is null) return null;
            var c = new CurveDef { Name = name, Kind = CurveKind.Graph, Source = sensor };
            CoolingPresets.Apply(c, preset, sensorName);
            curves.Add(c);
            return c;
        }
        var cpu = Graph(Cpu, cpuSensor);
        var gpu = Graph(Gpu, gpuSensor);
        var coolant = Graph(Liquid, liquid?.Id);
        // Case air warms with whichever works harder.
        var inputs = new[] { cpu, gpu }.Where(c => c is not null).Select(c => c!.Name).ToList();
        if (inputs.Count > 0) curves.Add(new CurveDef { Name = Case, Kind = CurveKind.Mix, Mix = MixFunction.Max, Inputs = inputs });
        string? caseCurve = inputs.Count > 0 ? Case : coolant?.Name;

        var choices = fans.Select(f =>
        {
            if (f.Name.Contains("pump", StringComparison.OrdinalIgnoreCase))
                return new StarterChoice(f, null, "a pump: left to the cooler");
            if (gpu is not null && f.Id.Contains("gpu", StringComparison.OrdinalIgnoreCase))
                return new StarterChoice(f, Gpu, "on the graphics card");
            if (coolant is not null && liquid is { } l && f.Device == l.Device)
                return new StarterChoice(f, Liquid, "on the cooler: a radiator fan");
            if (cpu is not null && f.Name.Contains("cpu", StringComparison.OrdinalIgnoreCase))
                return new StarterChoice(f, Cpu, "on the CPU fan header");
            return caseCurve is not null ? new StarterChoice(f, caseCurve, "a case fan") : new StarterChoice(f, null, "no curve to follow");
        }).ToList();
        return new StarterPlan(curves, choices);
    }

    /// <summary>Puts the plan in the config: its curves, and every fan with a curve set to follow it.</summary>
    public static void Apply(StarterPlan plan, AppConfig cfg)
    {
        cfg.Curves = plan.Curves;
        foreach (var c in plan.Fans.Where(c => c.Curve is not null))
        {
            if (!cfg.Fans.TryGetValue(c.Fan.Id, out var s)) cfg.Fans[c.Fan.Id] = s = new FanSettings();
            s.Enabled = true;
            s.Manual = null;
            s.Curve = c.Curve;
        }
    }
}

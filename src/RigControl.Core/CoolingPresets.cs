namespace RigControl.Core;

public enum CoolingPreset { Silent, Balanced, Performance }

/// <summary>What a curve's sensor measures, which decides the temperatures a preset uses.</summary>
public enum SensorKind { Cpu, Gpu, Liquid, Other }

/// <summary>
/// Ready-made curve shapes (Silent, Balanced, Performance), one per kind of sensor: a CPU runs hotter than coolant,
/// so the same "quiet" means different temperatures. A preset only reshapes curves; which fan follows which curve,
/// fan limits and names stay as they are. Every shape reaches 100% at a safe temperature.
/// </summary>
public static class CoolingPresets
{
    public static IReadOnlyList<CoolingPreset> All { get; } = [CoolingPreset.Silent, CoolingPreset.Balanced, CoolingPreset.Performance];

    // ponytail: general-purpose numbers, not tuned to any one PC; the points list is there to adjust them
    static readonly Dictionary<(SensorKind, CoolingPreset), CurvePoint[]> Shapes = new()
    {
        [(SensorKind.Cpu, CoolingPreset.Silent)] = [new(40, 20), new(60, 30), new(75, 55), new(85, 100)],
        [(SensorKind.Cpu, CoolingPreset.Balanced)] = [new(40, 25), new(60, 40), new(75, 70), new(85, 100)],
        [(SensorKind.Cpu, CoolingPreset.Performance)] = [new(35, 35), new(55, 55), new(70, 85), new(80, 100)],
        [(SensorKind.Gpu, CoolingPreset.Silent)] = [new(45, 20), new(65, 35), new(78, 60), new(85, 100)],
        [(SensorKind.Gpu, CoolingPreset.Balanced)] = [new(40, 25), new(60, 45), new(75, 75), new(83, 100)],
        [(SensorKind.Gpu, CoolingPreset.Performance)] = [new(35, 35), new(55, 60), new(70, 90), new(78, 100)],
        [(SensorKind.Liquid, CoolingPreset.Silent)] = [new(28, 20), new(34, 40), new(40, 75), new(45, 100)],
        [(SensorKind.Liquid, CoolingPreset.Balanced)] = [new(28, 25), new(33, 50), new(38, 85), new(42, 100)],
        [(SensorKind.Liquid, CoolingPreset.Performance)] = [new(26, 40), new(31, 70), new(36, 100)],
        [(SensorKind.Other, CoolingPreset.Silent)] = [new(35, 20), new(55, 40), new(70, 100)],
        [(SensorKind.Other, CoolingPreset.Balanced)] = [new(35, 25), new(50, 50), new(65, 100)],
        [(SensorKind.Other, CoolingPreset.Performance)] = [new(30, 35), new(45, 65), new(60, 100)],
    };

    /// <summary>The shape for a kind of sensor (a fresh copy each time: callers keep it).</summary>
    public static List<CurvePoint> Shape(SensorKind kind, CoolingPreset preset) => [.. Shapes[(kind, preset)]];

    /// <summary>Deadband and response time: Silent waits out short spikes, Performance answers at once.</summary>
    public static (double deadband, double response) Tuning(CoolingPreset preset) => preset switch
    {
        CoolingPreset.Silent => (3, 4),
        CoolingPreset.Performance => (1, 1),
        _ => (2, 2),
    };

    /// <summary>
    /// Coolant by its name ("Liquid temperature", "Coolant"); otherwise CPU or GPU by the sensor's id
    /// ("/intelcpu/0/…", "/gpu-nvidia/0/…"); anything else (VRM, drives, the board) is Other.
    /// </summary>
    public static SensorKind KindOf(string? sensorId, string? sensorName)
    {
        if (sensorName is { } n && (n.Contains("liquid", StringComparison.OrdinalIgnoreCase) || n.Contains("coolant", StringComparison.OrdinalIgnoreCase)))
            return SensorKind.Liquid;
        if (sensorId is null) return SensorKind.Other;
        if (sensorId.Contains("gpu", StringComparison.OrdinalIgnoreCase)) return SensorKind.Gpu;
        if (sensorId.Contains("cpu", StringComparison.OrdinalIgnoreCase)) return SensorKind.Cpu;
        return SensorKind.Other;
    }

    /// <summary>Reshapes a graph curve for its sensor. Mix curves follow their inputs and flat ones keep their value: untouched.</summary>
    /// <param name="sensorName">The hardware's name for a sensor id (null when it isn't there).</param>
    public static bool Apply(CurveDef curve, CoolingPreset preset, Func<string, string?> sensorName)
    {
        if (curve.Kind != CurveKind.Graph) return false;
        curve.Points = Shape(KindOf(curve.Source, curve.Source is null ? null : sensorName(curve.Source)), preset);
        (curve.Deadband, curve.ResponseSeconds) = Tuning(preset);
        return true;
    }

    /// <summary>Reshapes every graph curve; returns how many.</summary>
    public static int ApplyAll(IEnumerable<CurveDef> curves, CoolingPreset preset, Func<string, string?> sensorName) =>
        curves.Count(c => Apply(c, preset, sensorName));
}

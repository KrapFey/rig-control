using System.Text.Json;
using System.Text.Json.Serialization;

namespace RigControl.Core;

public enum CurveKind { Graph, Mix, Flat }
public enum MixFunction { Max, Min, Average }

public sealed record CurvePoint(double Temp, double Percent);

public sealed class CurveDef
{
    public string Name { get; set; } = "Curve";
    public CurveKind Kind { get; set; } = CurveKind.Graph;

    /// <summary>Graph: temperature sensor id.</summary>
    public string? Source { get; set; }
    /// <summary>Graph: points sorted by temperature.</summary>
    public List<CurvePoint> Points { get; set; } = [];
    /// <summary>Only react once the temperature moved this many degrees from the one in use.</summary>
    public double Deadband { get; set; }
    /// <summary>...and stayed there this long.</summary>
    public double ResponseSeconds { get; set; }

    /// <summary>Mix: names of the curves to combine.</summary>
    public List<string> Inputs { get; set; } = [];
    public MixFunction Mix { get; set; } = MixFunction.Max;

    /// <summary>Flat: fixed percent.</summary>
    public double Percent { get; set; }
}

/// <summary>Per fan channel settings, keyed by the channel's device-qualified id.</summary>
public sealed class FanSettings
{
    public string? Name { get; set; }
    public bool Enabled { get; set; }
    public string? Curve { get; set; }
    /// <summary>When set, overrides the curve.</summary>
    public double? Manual { get; set; }
    public double MinPercent { get; set; }
    /// <summary>Kick to at least this when spinning up from 0.</summary>
    public double StartPercent { get; set; }
    /// <summary>Below this the fan is stopped (0 disables).</summary>
    public double StopPercent { get; set; }
    public double StepUp { get; set; } = 8;
    public double StepDown { get; set; } = 8;
}

public sealed class AppConfig
{
    public List<CurveDef> Curves { get; set; } = [];
    public Dictionary<string, FanSettings> Fans { get; set; } = [];
    /// <summary>Friendly names for temperature sensors.</summary>
    public Dictionary<string, string> SensorNames { get; set; } = [];
    public double IntervalSeconds { get; set; } = 1;

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RigControl");
    public static string FilePath => Path.Combine(Folder, "config.json");

    public static AppConfig Load(string? path = null)
    {
        path ??= FilePath;
        if (!File.Exists(path)) return new AppConfig();
        return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Json) ?? new AppConfig();
    }

    public void Save(string? path = null)
    {
        path ??= FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true); // no half-written config if we die mid-save
    }

    public AppConfig Clone() => JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(this, Json), Json)!;
}

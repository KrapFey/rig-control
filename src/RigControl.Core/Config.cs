using System.Text.Json;
using System.Text.Json.Serialization;

namespace RigControl.Core;

public enum CurveKind { Graph, Mix, Flat }
public enum MixFunction { Max, Min, Average }

/// <summary>What the tray icon shows.</summary>
public enum TrayShow { Logo, CpuTemp, GpuTemp, Airflow }

public sealed record CurvePoint(double Temp, double Percent);

/// <summary>A named set of curves and fan settings, kept while another is in use.</summary>
public sealed class CoolingProfile
{
    public string Name { get; set; } = "Default";
    public List<CurveDef> Curves { get; set; } = [];
    public Dictionary<string, FanSettings> Fans { get; set; } = [];
}

/// <summary>While <see cref="Process"/> runs, these profiles are in use (null = leave that one alone).</summary>
public sealed class AppRule
{
    /// <summary>The program's file name, e.g. "game.exe" (".exe" is optional).</summary>
    public string Process { get; set; } = "";
    public string? Cooling { get; set; }
    public string? Lighting { get; set; }
}

/// <summary>The main window's restored bounds in screen pixels, and whether it was maximized.</summary>
public sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized);

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
    /// <summary>Ask GitHub for a newer release at start and once a day.</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>The tray icon shows this value live (or the logo).</summary>
    public TrayShow TrayShows { get; set; } = TrayShow.CpuTemp;
    /// <summary>A notification when a mouse's, keyboard's or headset's battery falls to this percent (0 = never).</summary>
    public int LowBatteryAlert { get; set; } = 15;
    /// <summary>A notification when the CPU or GPU stays at or above this many °C (0 = never).</summary>
    public int HotAlert { get; set; } = 95;
    /// <summary>Apps that bring their own cooling and lighting profiles while they run; the first one running wins.</summary>
    public List<AppRule> AppRules { get; set; } = [];
    /// <summary>The profiles in use while any game runs (see <see cref="GameDetector"/>); null = leave that one alone.</summary>
    public string? GameCooling { get; set; }
    public string? GameLighting { get; set; }
    /// <summary>Where the main window was when it last closed; null = the default size.</summary>
    public WindowPlacement? Window { get; set; }
    /// <summary>Per-device driver: device key (e.g. "nzxt-hue2/&lt;serial&gt;") -> "liquidctl". Missing = built-in.</summary>
    public Dictionary<string, string> Drivers { get; set; } = [];
    public LightingConfig Lighting { get; set; } = new();
    /// <summary>Plugins switched on, by id (see <see cref="PluginManager"/>). Off until you switch one on.</summary>
    public List<string> Plugins { get; set; } = [];
    /// <summary>Plugins you allowed to run as administrator (only those whose plugin.json asks for it).</summary>
    public List<string> AdminPlugins { get; set; } = [];
    /// <summary>Device settings (DPI, polling rate…) per peripheral key, e.g. "razer/0084".</summary>
    public Dictionary<string, PeripheralSettings> Peripherals { get; set; } = [];

    /// <summary>
    /// Moves a HUE 2 device's fan settings between its built-in ids ("nzxt-hue2/S/fan/N") and its liquidctl ids
    /// ("liquidctl/S/fanN"), so switching drivers keeps names, curves and limits, in every cooling profile.
    /// </summary>
    public void MoveHue2Settings(string serial, bool toLiquidctl)
    {
        foreach (var fans in CoolingProfiles.Select(p => p.Fans).Prepend(Fans))
            for (int n = 1; n <= 3; n++)
            {
                string native = $"nzxt-hue2/{serial}/fan/{n}", lq = $"liquidctl/{serial}/fan{n}";
                var (from, to) = toLiquidctl ? (native, lq) : (lq, native);
                if (fans.Remove(from, out var s)) fans[to] = s;
            }
    }

    // ---- cooling profiles: named sets of curves and fan settings (Quiet, Gaming…)

    /// <summary>The cooling profile in use. Its curves and fan settings are <see cref="Curves"/> and <see cref="Fans"/>.</summary>
    public string ActiveCooling { get; set; } = "Default";
    /// <summary>The other cooling profiles, kept as they were when last in use.</summary>
    public List<CoolingProfile> CoolingProfiles { get; set; } = [];

    /// <summary>Every cooling profile's name, in alphabetical order (the one in use among them).</summary>
    public List<string> CoolingProfileNames() =>
        [.. CoolingProfiles.Select(p => p.Name).Append(ActiveCooling).Distinct().Order(StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>Puts the profile in use away and takes out <paramref name="name"/>.</summary>
    public void SwitchCooling(string name)
    {
        if (name == ActiveCooling || CoolingProfiles.FirstOrDefault(p => p.Name == name) is not { } next) return;
        CoolingProfiles.Remove(next);
        Use(next);
    }

    /// <summary>A new profile, a copy of the one in use, which it becomes (change it from there).</summary>
    public void AddCooling(string name)
    {
        var copy = JsonSerializer.Deserialize<CoolingProfile>(JsonSerializer.Serialize(new CoolingProfile { Name = name, Curves = Curves, Fans = Fans }, Json), Json)!;
        Use(copy);
    }

    /// <summary>Removes the profile in use and switches to another; the last one stays.</summary>
    public void DeleteCooling()
    {
        if (CoolingProfiles.Count == 0) return;
        var next = CoolingProfiles[0];
        CoolingProfiles.RemoveAt(0);
        var names = FanNames();
        (Curves, Fans, ActiveCooling) = (next.Curves, next.Fans, next.Name);
        KeepNames(names);
    }

    void Use(CoolingProfile next)
    {
        var names = FanNames();
        CoolingProfiles.Add(new CoolingProfile { Name = ActiveCooling, Curves = Curves, Fans = Fans });
        (Curves, Fans, ActiveCooling) = (next.Curves, next.Fans, next.Name);
        KeepNames(names);
    }

    // A fan's name is the fan's, not a profile's: it stays when the profile changes.
    Dictionary<string, string> FanNames() =>
        Fans.Where(f => !string.IsNullOrWhiteSpace(f.Value.Name)).ToDictionary(f => f.Key, f => f.Value.Name!);

    void KeepNames(Dictionary<string, string> names)
    {
        foreach (var (id, s) in Fans) s.Name = null;
        foreach (var (id, name) in names)
        {
            if (!Fans.TryGetValue(id, out var s)) Fans[id] = s = new FanSettings();
            s.Name = name;
        }
    }

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Settings folder; the UI-test build points this elsewhere so it never touches real settings.</summary>
    public static string Folder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RigControl");

    /// <summary>Optional diagnostics sink (the app writes it to rigcontrol.log).</summary>
    public static Action<string>? Log { get; set; }
    public static string FilePath => Path.Combine(Folder, "config.json");

    public static AppConfig Load(string? path = null)
    {
        path ??= FilePath;
        if (!File.Exists(path)) { Log?.Invoke($"config: {path} not found, starting empty"); return new AppConfig(); }
        var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Json) ?? new AppConfig();
        Log?.Invoke($"config: loaded {path} ({cfg.Curves.Count} curves, {cfg.Fans.Count} fans, {cfg.Drivers.Count} driver overrides)");
        return cfg;
    }

    public void Save(string? path = null)
    {
        path ??= FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true); // no half-written config if we die mid-save
        Log?.Invoke($"config: saved {path} ({Fans.Count} fans, {Drivers.Count} driver overrides)");
    }

    public AppConfig Clone() => JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(this, Json), Json)!;
}

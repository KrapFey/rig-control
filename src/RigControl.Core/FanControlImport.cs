using System.Text.Json;
using System.Text.RegularExpressions;

namespace RigControl.Core;

/// <summary>What the importer can map onto: the ids this PC actually has.</summary>
public sealed record HardwareCatalog(
    IReadOnlyList<(string Id, string Name, string Device)> Temps,
    IReadOnlyList<(string Id, string Name, string Device)> Fans,
    IReadOnlyDictionary<string, string>? Aliases = null)
{
    /// <summary>Aliases: other apps' ids for our fans, e.g. "liquidctl/NZXTRGB&amp;FanController(3+6channels)#1660399584/Fan2duty".</summary>
    public static HardwareCatalog From(HardwareHub hub) => new(
        [.. hub.Temps.Select(t => (t.Id, t.Name, t.Device))],
        [.. hub.Fans.Select(f => (f.Id, f.Name, f.Device))],
        hub.Fans.OfType<Hue2Fan>().ToDictionary(
            f => $"liquidctl/{f.Owner.Product.Replace(" ", "")}#{f.Owner.LiquidctlId}/Fan{f.Channel + 1}duty",
            f => f.Id));
}

public sealed record ImportResult(AppConfig Config, List<string> Notes);

/// <summary>
/// Imports a FanControl (Rem0o) userConfig.json: curves, fan names, curve assignment and fan settings.
/// FanControl's own sensors come from LibreHardwareMonitor, so "/..." ids carry over unchanged.
/// </summary>
public static partial class FanControlImport
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "FanControl", "Configurations", "userConfig.json");

    public static ImportResult Import(string json, HardwareCatalog hw)
    {
        var notes = new List<string>();
        var cfg = new AppConfig();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement.TryGetProperty("FanControl", out var fc) ? fc : doc.RootElement;
        var map = new IdMapper(hw, notes);

        foreach (var c in Array(root, "FanCurves"))
        {
            var name = Str(c, "Name") ?? "Curve";
            var curve = new CurveDef { Name = name };
            if (c.TryGetProperty("SelectedFanCurves", out var mix))
            {
                curve.Kind = CurveKind.Mix;
                curve.Inputs = [.. mix.EnumerateArray().Select(m => Str(m, "Name")).OfType<string>()];
                curve.Mix = Num(c, "SelectedMixFunction") switch { 1 => MixFunction.Min, 2 => MixFunction.Average, _ => MixFunction.Max };
            }
            else if (c.TryGetProperty("SelectedTempSource", out var src))
            {
                curve.Kind = CurveKind.Graph;
                curve.Source = map.Temp(Str(src, "Identifier"), name);
                curve.Deadband = Num(c, "Deadband") ?? 0;
                curve.ResponseSeconds = Num(c, "SelectedResponseTime") ?? 0;
                if (c.TryGetProperty("Points", out var pts)) // graph curve: ["30,20", "50,40", ...]
                {
                    foreach (var p in pts.EnumerateArray())
                    {
                        var parts = (p.ValueKind == JsonValueKind.String ? p.GetString()! : "").Split(',', ';');
                        if (parts.Length == 2 && double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var x)
                            && double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var y))
                            curve.Points.Add(new(x, y));
                    }
                }
                else // linear curve: idle temp -> min speed, load temp -> max speed
                {
                    curve.Points.Add(new(Num(c, "IdleTemperature") ?? 40, Num(c, "MinFanSpeed") ?? 30));
                    curve.Points.Add(new(Num(c, "LoadTemperature") ?? 80, Num(c, "MaxFanSpeed") ?? 100));
                }
                curve.Points.Sort((a, b) => a.Temp.CompareTo(b.Temp));
            }
            else
            {
                curve.Kind = CurveKind.Flat;
                curve.Percent = Num(c, "Percent") ?? 50;
            }
            cfg.Curves.Add(curve);
        }

        foreach (var c in Array(root, "Controls"))
        {
            var nick = Str(c, "NickName");
            var id = map.Fan(Str(c, "Identifier"), nick);
            if (id is null) continue;
            var curveName = c.TryGetProperty("SelectedFanCurve", out var sc) ? Str(sc, "Name") : null;
            bool manual = c.TryGetProperty("ManualControl", out var mc) && mc.ValueKind == JsonValueKind.True;
            cfg.Fans[id] = new FanSettings
            {
                Name = nick,
                Enabled = c.TryGetProperty("Enable", out var en) && en.ValueKind == JsonValueKind.True && (curveName is not null || manual),
                Curve = curveName,
                Manual = manual ? Num(c, "ManualControlValue") : null,
                MinPercent = Num(c, "MinimumPercent") ?? 0,
                StartPercent = Num(c, "SelectedStart") ?? 0,
                StopPercent = Num(c, "SelectedStop") ?? 0,
                StepUp = Num(c, "SelectedCommandStepUp") ?? 8,
                StepDown = Num(c, "SelectedCommandStepDown") ?? 8,
            };
        }

        notes.AddRange(map.Ambiguous());
        return new(cfg, notes);
    }

    /// <summary>Maps FanControl identifiers onto ids of this PC's hardware.</summary>
    sealed partial class IdMapper(HardwareCatalog hw, List<string> notes)
    {
        // liquidctl key ("<description>#<id>") -> our device label, assigned in first-seen order per product
        readonly Dictionary<string, string?> _liquid = [];
        readonly Dictionary<string, int> _ambiguous = [];

        public string? Temp(string? id, string what)
        {
            if (id is null) return null;
            if (Resolve(id, hw.Temps) is string same) return same;
            var nv = NvApi().Match(id);
            if (nv.Success)
            {
                // FanControl's NVAPI sensor 0 is the GPU core; LHM calls it "GPU Core".
                var gpu = hw.Temps.FirstOrDefault(t => t.Id.StartsWith($"/gpu-nvidia/{nv.Groups[1].Value}/") && t.Name == "GPU Core");
                if (gpu.Id is not null) return gpu.Id;
            }
            notes.Add($"Curve \"{what}\": temperature sensor {id} isn't on this PC. Pick one in Curves.");
            return id;
        }

        public string? Fan(string? id, string? what)
        {
            if (id is null) return null;
            if (Resolve(id, hw.Fans) is string same) return same;
            if (hw.Aliases?.TryGetValue(id, out var alias) == true) return alias;

            var nv = NvApi().Match(id);
            if (nv.Success && id.Contains("/control/"))
            {
                var gpuFans = hw.Fans.Where(f => f.Id.StartsWith($"/gpu-nvidia/{nv.Groups[1].Value}/control/")).ToList();
                if (int.TryParse(id[(id.LastIndexOf('/') + 1)..], out var n) && n < gpuFans.Count) return gpuFans[n].Id;
            }

            var lq = Liquid().Match(id);
            if (lq.Success)
            {
                var (key, product, channel) = (lq.Groups[1].Value + "#" + lq.Groups[2].Value, Squash(lq.Groups[1].Value), lq.Groups[3].Value);
                if (!_liquid.TryGetValue(key, out var device))
                {
                    var taken = _liquid.Values.ToHashSet();
                    var candidates = hw.Fans.Select(f => f.Device).Distinct()
                        .Where(d => Squash(d).StartsWith(product)).Order().ToList();
                    device = candidates.FirstOrDefault(d => !taken.Contains(d));
                    _liquid[key] = device;
                    if (candidates.Count > 1) _ambiguous[lq.Groups[1].Value] = candidates.Count;
                }
                var match = device is null ? default : hw.Fans.FirstOrDefault(f => f.Device == device && f.Id.EndsWith($"/fan/{channel}"));
                if (match.Id is not null) return match.Id;
            }

            notes.Add($"Fan \"{what ?? id}\": {id} isn't on this PC, skipped.");
            return null;
        }

        public IEnumerable<string> Ambiguous() => _ambiguous.Keys.Select(p =>
            $"You have {_ambiguous[p]} identical {p} controllers; FanControl's ids don't say which is which, so they were matched in order. " +
            "Use Identify on each fan to check, and swap names if needed.");

        // Same LHM sensor, other LHM version: FanControl has "/lpc/it8689e/control/0", LHM 0.9.6 "/lpc/it8689e/0/control/0".
        // Compare with the numeric instance segments dropped, and only take a unique match.
        static string? Resolve(string id, IReadOnlyList<(string Id, string Name, string Device)> list)
        {
            if (list.Any(x => x.Id == id)) return id;
            var key = Normalize(id);
            var hits = list.Where(x => Normalize(x.Id) == key).Take(2).ToList();
            return hits.Count == 1 ? hits[0].Id : null;
        }

        static string Normalize(string id)
        {
            var parts = id.Split('/');
            return string.Join('/', parts.Where((p, i) => i == parts.Length - 1 || !p.All(char.IsAsciiDigit) || p.Length == 0));
        }

        static string Squash(string s) => NonWord().Replace(s, "").ToLowerInvariant();

        [GeneratedRegex(@"^NVApiWrapper/(\d+)-")] private static partial Regex NvApi();
        [GeneratedRegex(@"^liquidctl/(.+)#(\d+)/Fan(\d+)duty$", RegexOptions.IgnoreCase)] private static partial Regex Liquid();
        [GeneratedRegex(@"[^A-Za-z0-9]")] private static partial Regex NonWord();
    }

    static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];
    // FanControl writes null for unset objects ("SelectedFanCurve": null), so never assume an object.
    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}

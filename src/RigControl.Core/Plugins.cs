using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RigControl.Core;

/// <summary>A plugin's plugin.json: who made it, what to start, and which USB devices it handles. See docs/plugins.md.</summary>
public sealed class PluginManifest
{
    /// <summary>The protocol version the plugin speaks (<see cref="PluginManager.Protocol"/>).</summary>
    public int Protocol { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Author { get; set; }
    public string? Description { get; set; }
    /// <summary>The program to start, inside the plugin's folder.</summary>
    public string Program { get; set; } = "";
    public List<string> Args { get; set; } = [];
    /// <summary>USB devices it handles, "VID:PID" in hex ("1038:1836"). Empty: started at every scan, to look for itself.</summary>
    public List<string> Devices { get; set; } = [];
    /// <summary>Needs administrator rights (most devices don't). Runs that way only once you've agreed to it.</summary>
    public bool Admin { get; set; }
}

/// <summary>A folder in the plugins folder: its manifest, or why it can't be used.</summary>
public sealed record PluginInfo(string Folder, PluginManifest? Manifest, string? Problem)
{
    public string Id => Manifest?.Id ?? Path.GetFileName(Folder);
    public string Name => Manifest?.Name is { Length: > 0 } n ? n : Path.GetFileName(Folder);
}

/// <summary>What the user chose: plugins switched on, and those allowed to run as administrator.</summary>
public sealed record PluginChoices(IReadOnlyCollection<string> Enabled, IReadOnlyCollection<string> Admin);

/// <summary>
/// Device support in separate programs ("plugins"), each in its own folder under <see cref="DefaultFolder"/> with a
/// plugin.json. RigControl starts an enabled plugin when one of its devices is plugged in, with an ordinary user's
/// rights, and talks to it one JSON line at a time (docs/plugins.md). Its devices appear on the Devices page like the
/// built-in ones. A plugin that crashes, hangs or answers nonsense is stopped and started again later, so it can't
/// take RigControl down with it. Called from the peripherals thread only (<see cref="PeripheralHub"/>).
/// </summary>
public sealed class PluginManager(string folder, Func<PluginChoices> choices) : IDisposable
{
    public const int Protocol = 1;
    public static string DefaultFolder => Path.Combine(AppContext.BaseDirectory, "plugins");

    readonly ChildJob _job = new();
    readonly Dictionary<string, PluginHost> _hosts = [];
    IReadOnlyDictionary<string, string> _status = new Dictionary<string, string>();

    public string Folder => folder;

    /// <summary>A line per plugin id saying what it's doing (running, waiting for its device, failed…), for Settings.</summary>
    public IReadOnlyDictionary<string, string> Status => Volatile.Read(ref _status);

    /// <summary>Something changed that a device scan should pick up: a plugin failed, or it's time to start one again.</summary>
    public bool Due => _hosts.Values.Any(h => h.Due);

    /// <summary>Every folder in the plugins folder, checked: usable plugins first, by name.</summary>
    public static List<PluginInfo> Installed(string folder)
    {
        var list = new List<PluginInfo>();
        if (!Directory.Exists(folder)) return list;
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.EnumerateDirectories(folder).Order(StringComparer.OrdinalIgnoreCase))
        {
            var info = Read(dir);
            if (info.Manifest is { } m && !ids.Add(m.Id)) info = info with { Problem = $"Another plugin already has the id \"{m.Id}\"." };
            list.Add(info);
        }
        return [.. list.OrderBy(p => p.Problem is not null).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9._-]{0,63}$");

    /// <summary>A plugin folder's manifest, checked: a usable id, a program inside the folder, readable device ids.</summary>
    public static PluginInfo Read(string dir)
    {
        var path = Path.Combine(dir, "plugin.json");
        if (!File.Exists(path)) return new(dir, null, "It has no plugin.json.");
        PluginManifest? m;
        try { m = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path), Json); }
        catch (Exception e) { return new(dir, null, $"Its plugin.json can't be read: {e.Message}"); }
        if (m is null) return new(dir, null, "Its plugin.json is empty.");
        string? problem =
            m.Protocol != Protocol ? $"It's made for plugin protocol {m.Protocol}; this RigControl speaks {Protocol}." :
            !IdPattern.IsMatch(m.Id) ? "Its id must be lower-case letters, digits, '.', '-' or '_'." :
            string.IsNullOrWhiteSpace(m.Name) ? "It has no name." :
            ProgramPath(dir, m) is null ? $"Its program \"{m.Program}\" isn't an .exe inside its folder." :
            m.Devices.FirstOrDefault(d => ParseDevice(d) is null) is { } bad ? $"\"{bad}\" isn't a USB device id (VID:PID in hex, e.g. 1038:1836)." :
            null;
        return new(dir, m, problem);
    }

    /// <summary>The program's full path, if it's an existing .exe inside the plugin's folder (never outside it).</summary>
    internal static string? ProgramPath(string dir, PluginManifest m)
    {
        if (string.IsNullOrWhiteSpace(m.Program) || Path.IsPathRooted(m.Program)) return null;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)) + Path.DirectorySeparatorChar;
        var exe = Path.GetFullPath(Path.Combine(root, m.Program));
        return exe.StartsWith(root, StringComparison.OrdinalIgnoreCase) && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(exe) ? exe : null;
    }

    /// <summary>"1038:1836" → (0x1038, 0x1836).</summary>
    internal static (int vid, int pid)? ParseDevice(string id)
    {
        var parts = id.Split(':');
        const System.Globalization.NumberStyles Hex = System.Globalization.NumberStyles.AllowHexSpecifier;
        return parts.Length == 2 && parts[0].Length == 4 && parts[1].Length == 4
               && int.TryParse(parts[0], Hex, null, out var v) && int.TryParse(parts[1], Hex, null, out var p) ? (v, p) : null;
    }

    /// <summary>
    /// Administrator plugins only from a folder that only administrators can change (RigControl installed under
    /// Program Files). From anywhere else, any program could swap in its own and get administrator rights.
    /// </summary>
    // ponytail: judged by where RigControl is, not by the folder's ACL; read the ACL if portable installs need admin plugins
    static bool AdminFolder(string dir) =>
        Path.GetFullPath(dir).StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Starts the enabled plugins whose devices are plugged in (and stops the rest), then asks each for its devices.
    /// </summary>
    internal List<IPeripheral> Discover()
    {
        var chosen = choices();
        var enabled = new HashSet<string>(chosen.Enabled, StringComparer.OrdinalIgnoreCase);
        var admin = new HashSet<string>(chosen.Admin, StringComparer.OrdinalIgnoreCase);
        var installed = Installed(folder);
        HashSet<(int, int)>? present = null;
        var status = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<IPeripheral>();
        foreach (var info in installed)
        {
            string? why =
                info.Problem is { } p ? $"Can't be used: {p}" :
                !enabled.Contains(info.Id) ? "Off" :
                info.Manifest!.Admin && !AdminFolder(info.Folder) ? "Off: it asks for administrator rights, which plugins only get when RigControl is installed in Program Files." :
                info.Manifest.Admin && !admin.Contains(info.Id) ? "Off: it asks for administrator rights. Switch it on again to allow that." :
                null;
            if (why is null && info.Manifest!.Devices.Count > 0)
            {
                present ??= PresentDevices();
                if (!info.Manifest.Devices.Any(d => present.Contains(ParseDevice(d)!.Value))) why = "Waiting for its device";
            }
            if (why is not null)
            {
                Stop(info.Id);
                status[info.Id] = why;
                continue;
            }
            if (_hosts.TryGetValue(info.Id, out var host) && host.Info.Manifest!.Version != info.Manifest!.Version) { Stop(info.Id); host = null; } // updated
            if (host is null) _hosts[info.Id] = host = new PluginHost(info, _job, this);
            found.AddRange(host.Discover());
            status[info.Id] = host.Status;
        }
        foreach (var gone in _hosts.Keys.Where(id => !installed.Any(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase))).ToList()) Stop(gone);
        Volatile.Write(ref _status, status);
        return found;
    }

    /// <summary>The plugin's current host (a restart or an update replaces it), or null while it isn't running.</summary>
    internal PluginHost? Host(string id) => _hosts.GetValueOrDefault(id);

    void Stop(string id)
    {
        if (!_hosts.Remove(id, out var host)) return;
        host.Dispose();
    }

    static HashSet<(int, int)> PresentDevices()
    {
        try { return [.. HidSharp.DeviceList.Local.GetHidDevices().Select(d => (d.VendorID, d.ProductID))]; }
        catch { return []; }
    }

    public void Dispose()
    {
        foreach (var h in _hosts.Values) h.Dispose();
        _hosts.Clear();
        _job.Dispose();
    }
}

/// <summary>One running plugin: its program, the protocol, and starting it again when it fails.</summary>
sealed class PluginHost(PluginInfo info, ChildJob job, PluginManager manager) : IDisposable
{
    const int GiveUpAfter = 5; // failures in a row
    const int LogLinesMax = 200;
    static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(5);

    public PluginInfo Info { get; } = info;
    public string Id => Info.Id;
    /// <summary>The plugin's program while it runs.</summary>
    internal Process? Process => _child?.Process;
    PluginManifest M => Info.Manifest!;
    ChildProcess? _child;
    int _failures, _logLines;
    long _retryAt; // Stopwatch timestamp; 0 = now
    string? _error;
    bool _changed;
    int _devices;

    /// <summary>A failure to show (by dropping its devices), or a restart that's due.</summary>
    public bool Due => _changed || (_child is null && _failures is > 0 and < GiveUpAfter && Stopwatch.GetTimestamp() >= _retryAt);

    public string Status =>
        _child is not null ? (_devices == 0 ? "On · no devices found" : $"On · {_devices} device{(_devices == 1 ? "" : "s")}") :
        _failures >= GiveUpAfter ? $"Stopped after {GiveUpAfter} failures in a row ({_error}). Switch it off and on to try again." :
        _failures > 0 ? $"Failed ({_error}); trying again in {Math.Max(1, Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), _retryAt).TotalSeconds):0} s" :
        "Starting";

    /// <summary>Starts the plugin if needed and asks for its devices. Empty while it can't run.</summary>
    public List<IPeripheral> Discover()
    {
        _changed = false;
        if (_child is null && (_failures >= GiveUpAfter || Stopwatch.GetTimestamp() < _retryAt)) return [];
        try
        {
            if (_child is null) Start();
            var reply = Call(new JsonObject { ["op"] = "discover" }, 10_000);
            var list = new List<IPeripheral>();
            foreach (var d in reply["devices"]?.AsArray() ?? [])
            {
                var key = (string?)d?["key"];
                var name = (string?)d?["name"];
                if (string.IsNullOrWhiteSpace(key) || key.Length > 64 || string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("a device without a key or name");
                var caps = d!["caps"]?.Deserialize<PeripheralCaps>(PluginManager.Json) ?? new PeripheralCaps();
                list.Add(new PluginPeripheral(manager, Info, key, name, (string?)d["kind"] ?? "Device", caps));
                if (list.Count > 32) throw new InvalidDataException("more than 32 devices");
            }
            _failures = 0;
            _devices = list.Count;
            return list;
        }
        catch (Exception e)
        {
            Fail(e.Message);
            return [];
        }
    }

    void Start()
    {
        var exe = PluginManager.ProgramPath(Info.Folder, M) ?? throw new FileNotFoundException("its program is gone");
        _child = M.Admin
            ? ChildProcess.StartAsAdmin(exe, M.Args, Info.Folder, job)
            : ChildProcess.StartWithUserRights(exe, M.Args, Info.Folder, job);
        var child = _child;
        _ = Task.Run(async () =>
        {
            try { while (await child.Error.ReadLineAsync() is { } line) Log(line); }
            catch { } // the plugin ended
        });
        AppConfig.Log?.Invoke($"plugin {Id} {M.Version}: started (pid {child.Process.Id}{(M.Admin ? ", as administrator" : "")})");
        var hello = Call(new JsonObject { ["op"] = "hello", ["protocol"] = PluginManager.Protocol, ["app"] = "RigControl" }, 10_000);
        if ((int?)hello["protocol"] != PluginManager.Protocol) throw new InvalidDataException($"it speaks protocol {hello["protocol"]}, not {PluginManager.Protocol}");
    }

    /// <summary>One request, one reply line. A reply with "error" throws (the plugin stays up); anything worse stops it.</summary>
    public JsonNode Call(JsonObject request, int timeoutMs = 3000)
    {
        if (_child is not { } child || child.Process.HasExited)
        {
            if (_child is null) throw new InvalidOperationException($"the {M.Name} plugin isn't running");
            var why = $"it exited (code 0x{_child.Process.ExitCode:X8})";
            Fail(why);
            throw new InvalidOperationException($"the {M.Name} plugin failed: {why}");
        }
        JsonNode? reply;
        try
        {
            child.Input.WriteLine(request.ToJsonString());
            var read = child.Output.ReadLineAsync();
            if (!read.Wait(timeoutMs)) throw new TimeoutException($"no answer to \"{request["op"]}\" within {timeoutMs / 1000.0:0.#} s");
            reply = read.Result is { } line ? JsonNode.Parse(line) : throw new EndOfStreamException("it exited");
            if (reply is not JsonObject) throw new InvalidDataException("its answer isn't a JSON object");
        }
        catch (Exception e) when (e is not InvalidOperationException)
        {
            var why = e is AggregateException { InnerException: { } inner } ? inner.Message : e.Message;
            Fail(why);
            throw new InvalidOperationException($"the {M.Name} plugin failed: {why}");
        }
        if (reply["error"] is { } err) throw new InvalidOperationException((string?)err ?? "the plugin reported an error");
        return reply;
    }

    void Fail(string why)
    {
        _error = why;
        _failures++;
        _changed = true;
        _devices = 0;
        // 4 s, 8 s, 16 s… up to 5 minutes
        var wait = TimeSpan.FromSeconds(Math.Min(MaxWait.TotalSeconds, 2 * Math.Pow(2, _failures)));
        _retryAt = Stopwatch.GetTimestamp() + (long)(wait.TotalSeconds * Stopwatch.Frequency);
        AppConfig.Log?.Invoke($"plugin {Id}: {why}" + (_failures >= GiveUpAfter ? $"; stopped after {GiveUpAfter} failures in a row" : $"; trying again in {wait.TotalSeconds:0} s"));
        _child?.Dispose();
        _child = null;
    }

    /// <summary>The plugin's own messages (stderr) go to rigcontrol.log, capped so a chatty one can't fill it.</summary>
    void Log(string line)
    {
        var n = Interlocked.Increment(ref _logLines);
        if (n <= LogLinesMax) AppConfig.Log?.Invoke($"plugin {Id}: {line}");
        else if (n == LogLinesMax + 1) AppConfig.Log?.Invoke($"plugin {Id}: more than {LogLinesMax} messages, the rest aren't logged");
    }

    public void Dispose()
    {
        if (_child is null) return;
        AppConfig.Log?.Invoke($"plugin {Id}: stopped");
        _child.Dispose(); // closing its input asks it to end; it's killed in any case
        _child = null;
    }
}

/// <summary>
/// A device a plugin found. Its key is the plugin's, prefixed with the plugin id, so two plugins can't collide. It asks
/// for the plugin's current host on each call: the device list keeps this object across rescans, while the plugin may
/// have been started again meanwhile.
/// </summary>
sealed class PluginPeripheral(PluginManager manager, PluginInfo plugin, string key, string name, string kind, PeripheralCaps caps) : IPeripheral
{
    public string Key { get; } = $"plugin/{plugin.Id}/{key}";
    public string Name => name;
    public string Kind => kind;
    public string Source => $"{plugin.Manifest!.Name} {plugin.Manifest.Version} (plugin)";
    public PeripheralCaps Caps => caps;

    PluginHost Host => manager.Host(plugin.Id) ?? throw new InvalidOperationException($"the {plugin.Name} plugin isn't running");

    public PeripheralState Read() =>
        Host.Call(new JsonObject { ["op"] = "read", ["key"] = key })["state"]?.Deserialize<PeripheralState>(PluginManager.Json) ?? new PeripheralState();

    public void Apply(PeripheralSettings settings, PeripheralState current) =>
        Host.Call(new JsonObject { ["op"] = "apply", ["key"] = key, ["settings"] = JsonSerializer.SerializeToNode(settings, PluginManager.Json) });

    public void Dispose() { } // the plugin belongs to the manager, which stops it
}

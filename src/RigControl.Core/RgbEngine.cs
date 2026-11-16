using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace RigControl.Core;

/// <summary>
/// Runs the bundled OpenRGB 1.1 headless (`--server`, no window) on a private localhost port and keeps an SDK
/// connection to it. OpenRGB is tied to RigControl with a kill-on-close job object, so it can't outlive us.
/// </summary>
public sealed class RgbEngine : IDisposable
{
    readonly string _configDir;
    Process? _proc;
    OpenRgbClient? _client;
    readonly ChildJob _job = new();
    int _port;
    volatile bool _disposed;

    public static string EngineDir => Path.Combine(AppContext.BaseDirectory, "openrgb");
    public static bool Available => File.Exists(Path.Combine(EngineDir, "OpenRGB.exe"));

    public List<RgbDevice> Devices { get; private set; } = [];
    public OpenRgbClient? Client => _client;
    public string? Problem { get; private set; }
    /// <summary>Raised after the device list was (re)loaded.</summary>
    public event Action? DevicesChanged;
    readonly Stopwatch _sinceChange = Stopwatch.StartNew();

    /// <summary>
    /// Waits until detection has settled: OpenRGB starts with an empty list and adds devices over a few seconds, so
    /// this returns once devices are listed and the list has been still for a moment (or after <paramref name="limit"/>).
    /// </summary>
    public async Task SettledAsync(TimeSpan limit)
    {
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < limit && !_disposed && (Devices.Count == 0 || _sinceChange.Elapsed < TimeSpan.FromSeconds(2.5)))
            await Task.Delay(250);
    }

    public RgbEngine(string configDir) => _configDir = configDir;

    /// <summary>Starts OpenRGB, waits for its SDK server and loads the device list. Detection takes a few seconds.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!Available) { Problem = "This build doesn't include the OpenRGB engine."; return; }
        Directory.CreateDirectory(_configDir);
        _port = FreePort();
        var psi = new ProcessStartInfo(Path.Combine(EngineDir, "OpenRGB.exe"))
        {
            WorkingDirectory = EngineDir,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "--server", "--server-host", "127.0.0.1", "--server-port", _port.ToString(), "--config", _configDir, "--noautoconnect" })
            psi.ArgumentList.Add(a);
        _proc = Process.Start(psi)!;
        _job.Add(_proc);
        AppConfig.Log?.Invoke($"rgb: OpenRGB started (pid {_proc.Id}, port {_port})");

        // The server comes up quickly; detection finishes later and announces itself with DeviceListUpdated.
        var client = new OpenRgbClient();
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await client.ConnectAsync("127.0.0.1", _port, "RigControl", ct);
                break;
            }
            catch when (attempt < 60 && !_proc.HasExited)
            {
                client.Dispose();
                client = new OpenRgbClient();
                await Task.Delay(500, ct);
            }
        }
        _client = client;
        _client.DeviceListChanged += ReloadSoon;
        _client.ConnectionLost += () => { if (!_disposed) ConnectionLost?.Invoke(); };
        await ReloadAsync();
    }

    /// <summary>OpenRGB closed the connection by itself (it crashed or was ended): the engine is dead and needs a fresh start.</summary>
    public event Action? ConnectionLost;

    int _reloadQueued;

    /// <summary>
    /// OpenRGB announces its list once per device while detecting (ten times in a second or two). Every rebuild puts
    /// each device back into direct mode, so announcements are gathered: one reload a second at most, with the latest list.
    /// </summary>
    void ReloadSoon()
    {
        if (Interlocked.Exchange(ref _reloadQueued, 1) == 1) return;
        _ = Task.Delay(1000).ContinueWith(_ =>
        {
            Volatile.Write(ref _reloadQueued, 0);
            return ReloadAsync();
        });
    }

    readonly SemaphoreSlim _reload = new(1, 1);
    string? _loaded; // what the last reload found, to skip lists that didn't change

    /// <summary>
    /// Reloads the device list. OpenRGB announces its list several times while detecting, so reloads run one at a
    /// time; one that finds another already running waits for it and then loads the latest list itself.
    /// </summary>
    public async Task ReloadAsync()
    {
        if (_client is null || _disposed) return;
        await _reload.WaitAsync();
        try
        {
            if (_disposed) return; // replaced by a rescan while waiting
            Devices = await _client.GetDevicesAsync();
            if (ApplyZoneSizes(Devices))
            {
                await Task.Delay(300); // let OpenRGB rebuild the resized controllers
                Devices = await _client.GetDevicesAsync();
            }
            var loaded = string.Join("\n", Devices.Select(d => $"{d.Index} {d.Key} {string.Join(',', d.Zones.Select(z => z.LedsCount))}"));
            if (loaded == _loaded) return; // same devices, same LEDs: nothing to rebuild (or log)
            _loaded = loaded;
            AppConfig.Log?.Invoke($"rgb: {Devices.Count} device(s): {string.Join(", ", Devices.Select(d => d.Name))}");
            var headers = Devices.SelectMany(d => d.Zones.Where(z => z.Resizable).Select(z => $"{d.Name} / {z.Name} = {z.LedsCount} ({z.LedsMin}-{z.LedsMax})")).ToList();
            if (headers.Count > 0) AppConfig.Log?.Invoke($"rgb: resizable headers: {string.Join("; ", headers)}");
            _sinceChange.Restart();
            DevicesChanged?.Invoke();
        }
        catch (Exception e)
        {
            Problem = e.Message;
            AppConfig.Log?.Invoke($"rgb: reload failed: {e.Message}");
        }
        finally { _reload.Release(); }
    }

    /// <summary>The LED count the user set for a resizable zone (device key, zone name), if any.</summary>
    public Func<string, string, int?>? WantedZoneSize { get; set; }

    /// <summary>Resizes headers whose saved LED count differs from OpenRGB's. True when anything was resized.</summary>
    bool ApplyZoneSizes(List<RgbDevice> devices)
    {
        if (_client is null || WantedZoneSize is null) return false;
        bool any = false;
        foreach (var d in devices)
            for (int i = 0; i < d.Zones.Count; i++)
            {
                var z = d.Zones[i];
                if (!z.Resizable || WantedZoneSize(d.Key, z.Name) is not int n || n == z.LedsCount || n < z.LedsMin || n > z.LedsMax) continue;
                _client.ResizeZone(d.Index, i, n);
                AppConfig.Log?.Invoke($"rgb: {d.Name} zone \"{z.Name}\" set to {n} LEDs");
                any = true;
            }
        return any;
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose()
    {
        _disposed = true;
        DevicesChanged = null;
        ConnectionLost = null;
        _client?.Dispose();
        try { if (_proc is { HasExited: false }) { _proc.Kill(); _proc.WaitForExit(3000); } } catch { }
        _proc?.Dispose();
        _job.Dispose();
    }
}

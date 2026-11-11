using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

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
    readonly Job _job = new();
    int _port;

    public static string EngineDir => Path.Combine(AppContext.BaseDirectory, "openrgb");
    public static bool Available => File.Exists(Path.Combine(EngineDir, "OpenRGB.exe"));

    public List<RgbDevice> Devices { get; private set; } = [];
    public OpenRgbClient? Client => _client;
    public string? Problem { get; private set; }
    /// <summary>Raised after the device list was (re)loaded.</summary>
    public event Action? DevicesChanged;

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
        _client.DeviceListChanged += () => _ = ReloadAsync();
        await ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        if (_client is null) return;
        try
        {
            Devices = await _client.GetDevicesAsync();
            if (ApplyZoneSizes(Devices))
            {
                await Task.Delay(300); // let OpenRGB rebuild the resized controllers
                Devices = await _client.GetDevicesAsync();
            }
            AppConfig.Log?.Invoke($"rgb: {Devices.Count} device(s): {string.Join(", ", Devices.Select(d => d.Name))}");
            var headers = Devices.SelectMany(d => d.Zones.Where(z => z.Resizable).Select(z => $"{d.Name} / {z.Name} = {z.LedsCount} ({z.LedsMin}-{z.LedsMax})")).ToList();
            if (headers.Count > 0) AppConfig.Log?.Invoke($"rgb: resizable headers: {string.Join("; ", headers)}");
            DevicesChanged?.Invoke();
        }
        catch (Exception e) { Problem = e.Message; }
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
        _client?.Dispose();
        try { if (_proc is { HasExited: false }) { _proc.Kill(); _proc.WaitForExit(3000); } } catch { }
        _proc?.Dispose();
        _job.Dispose();
    }

    /// <summary>Kill-on-close job: children die with this process, even if it crashes.</summary>
    sealed class Job : IDisposable
    {
        readonly IntPtr _handle = CreateJobObject(IntPtr.Zero, null);

        public Job()
        {
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
            int size = Marshal.SizeOf(info);
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(_handle, 9, ptr, (uint)size)) throw new Win32Exception();
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        public void Add(Process p)
        {
            if (!AssignProcessToJobObject(_handle, p.Handle)) AppConfig.Log?.Invoke($"rgb: couldn't tie OpenRGB to RigControl ({Marshal.GetLastWin32Error()})");
        }

        public void Dispose() => CloseHandle(_handle);

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateJobObject(IntPtr a, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int cls, IntPtr info, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    }
}

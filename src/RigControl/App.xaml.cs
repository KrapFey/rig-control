using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RigControl.Core;

namespace RigControl;

public partial class App : Application
{
    public static App? Instance { get; private set; }

    public HardwareHub Hub { get; private set; } = new();
    public FanEngine Engine { get; private set; } = null!;
    /// <summary>The live config the UI edits. Call <see cref="ConfigChanged"/> after changing it.</summary>
    public AppConfig Config { get; private set; } = null!;
    public DispatcherQueue Ui { get; private set; } = null!;
    public List<string> StartupNotes { get; } = [];
    public LightingEngine Lighting { get; private set; } = null!;
    public PeripheralHub Peripherals { get; private set; } = null!;
    public RgbEngine? Rgb { get; private set; }
    /// <summary>Why lighting isn't running, when it isn't.</summary>
    public string? LightingNotice { get; private set; }
    public event Action? LightingStateChanged;
    SeirenV3? _seiren;

    MainWindow? _window;
    TaskbarIcon? _tray;
    TrayPanel? _trayPanel;
    System.Drawing.Icon? _logo;
    DispatcherQueueTimer? _trayTimer;
    DispatcherQueueTimer? _saveTimer;

    public App()
    {
        Instance = this;
        AppConfig.Log = Log;
#if UITEST
        // RIGCONTROL_DEMO=1: made-up hardware for screenshots (RigControl.Core.Demo), in its own settings folder.
        Demo.On = Environment.GetEnvironmentVariable("RIGCONTROL_DEMO") == "1";
        AppConfig.Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Demo.On ? "RigControl-demo" : "RigControl-uitest");
#endif
        Log($"start pid {Environment.ProcessId} args [{string.Join(" ", Environment.GetCommandLineArgs().Skip(1))}]");
        InitializeComponent();
        // The tray keeps the app (and fan control) alive with no window open.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, e) => Crash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash(e.ExceptionObject as Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Ui = DispatcherQueue.GetForCurrentThread();
        if (Demo.On && !File.Exists(AppConfig.FilePath)) Demo.Config().Save();
        try { Config = AppConfig.Load(); }
        catch (Exception e)
        {
            var bad = AppConfig.FilePath + $".broken-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(AppConfig.FilePath, bad);
            StartupNotes.Add($"config.json couldn't be read ({e.Message}); it was moved to {Path.GetFileName(bad)} and a fresh one started.");
            Config = new AppConfig();
        }

        Hub.Open(Config.Drivers);
        Engine = new FanEngine(Hub, Config) { Paused = OtherFanAppRunning() };
        Engine.Start();

        // Two programs writing the same fans fight each other: stay hands-off while FanControl runs.
        var watch = Ui.CreateTimer();
        watch.Interval = TimeSpan.FromSeconds(2);
        watch.Tick += (_, _) => Engine.Paused = OtherFanAppRunning();
        watch.Start();

        _saveTimer = Ui.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(600);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) =>
        {
            try { Config.Save(); } catch (Exception e) { Log($"config save failed: {e.Message}"); }
        };

#if UITEST
        // the UI-test build reads devices but never writes settings (demo devices are made up, so they may)
        Peripherals = new PeripheralHub(() => Demo.On ? Config.Clone().Peripherals : []);
#else
        Peripherals = new PeripheralHub(() => Config.Clone().Peripherals);
#endif
        Peripherals.Changed += () => Ui.TryEnqueue(UpdateTrayTooltip);
        Peripherals.ChangedOnDevice += (key, state) => Ui.TryEnqueue(() =>
        {
            // Pressed the DPI button: remember where the mouse is now (saved only if this device has saved settings).
            if (!Config.Peripherals.TryGetValue(key, out var s)) return;
            if (state.Dpi is int dpi) s.Dpi = dpi;
            if (state.ActiveStage is int a) s.ActiveStage = a;
            if (state.Stages is { Count: > 0 } st) s.Stages = [.. st];
            if (state.GameMode is bool g && s.GameMode is not null) s.GameMode = g; // Fn+F10 on the keyboard
            Log($"peripheral {key}: changed on the device (dpi {state.Dpi}, stage {state.ActiveStage}, game mode {state.GameMode}), saved");
            ConfigChanged();
        });

        Lighting = new LightingEngine(id => Hub.Temps.FirstOrDefault(t => t.Id == id)?.Value);
        Lighting.Apply(Config.Lighting);
        _ = StartLightingAsync();

        CreateTray();
        if (!Environment.GetCommandLineArgs().Contains("--tray")) ShowWindow();
        _ = UpdateLoopAsync();
    }

    /// <summary>What a sensor is called in the app: the name you gave it, or the hardware's own.</summary>
    public string SensorLabel(TempSensor t) => Config.SensorNames.TryGetValue(t.Id, out var n) && !string.IsNullOrWhiteSpace(n) ? n : t.Name;

    /// <summary>A newer release, once a check found one (shown on the dashboard and in Settings).</summary>
    public UpdateInfo? Update { get; private set; }
    public event Action? UpdateFound;

    async Task UpdateLoopAsync()
    {
#if UITEST
        return;
#endif
        await Task.Delay(TimeSpan.FromSeconds(30)); // let start-up (sensors, fans, lighting) settle first
        while (true)
        {
            if (Config.CheckForUpdates) await CheckForUpdateAsync();
            await Task.Delay(TimeSpan.FromHours(24));
        }
    }

    /// <summary>Returns a line for the user: what was found, or why the check failed.</summary>
    public async Task<string> CheckForUpdateAsync()
    {
        try
        {
            var found = await Updates.CheckAsync();
            Log(found is null ? $"update check: {Updates.Current} is the latest" : $"update check: {found.Version} available");
            if (found is null) return $"RigControl {Updates.Current} is the latest version.";
            Update = found;
            Ui.TryEnqueue(() => UpdateFound?.Invoke());
            return $"RigControl {found.Version} is available.";
        }
        catch (Exception e)
        {
            Log($"update check failed: {e.Message}");
            return $"Couldn't check for updates: {e.Message}";
        }
    }

    async Task StartLightingAsync()
    {
#if UITEST
        if (Demo.On) { Lighting.SetTargets(Demo.RgbDevices(ZoneSize)); Ui.TryEnqueue(() => LightingStateChanged?.Invoke()); return; }
        LightingNotice = "The UI-test build doesn't start the lighting engine.";
        return;
#endif
        if (OtherOpenRgbRunning())
        {
            LightingNotice = "OpenRGB is already running, so RigControl leaves your lighting to it. Exit OpenRGB (and stop its service) and restart RigControl to take over.";
            Log("rgb: another OpenRGB is running, lighting not started");
            Ui.TryEnqueue(() => LightingStateChanged?.Invoke());
            return;
        }
        await StartRgbAsync();
    }

    async Task StartRgbAsync()
    {
        try
        {
            _seiren ??= SeirenV3.Open();
            var rgb = new RgbEngine(Path.Combine(AppConfig.Folder, "openrgb")) { WantedZoneSize = ZoneSize };
            Rgb = rgb;
            rgb.DevicesChanged += RebuildTargets;
            await Task.Run(() => rgb.StartAsync());
            LightingNotice = rgb.Problem;
            RebuildTargets();
        }
        catch (Exception e)
        {
            LightingNotice = $"Lighting couldn't start: {e.Message}";
            Log($"rgb: start failed: {e}");
        }
        Ui.TryEnqueue(() => LightingStateChanged?.Invoke());
    }

    /// <summary>
    /// Looks for lighting devices again by starting the lighting engine afresh: the detection that runs at start.
    /// (OpenRGB 1.1's own rescan request leaves it unable to send device details, so it isn't used.)
    /// </summary>
    public async Task RescanLightingAsync()
    {
        Log("rgb: rescan");
        if (Demo.On) { Lighting.SetTargets(Demo.RgbDevices(ZoneSize)); Ui.TryEnqueue(() => LightingStateChanged?.Invoke()); return; }
        if (Rgb is not { } old) return; // not running (another OpenRGB, or the UI-test build): nothing to rescan
        Rgb = null;
        Lighting.SetTargets([]);
        await Task.Run(old.Dispose); // ends our OpenRGB; the devices keep their last colours meanwhile
        await StartRgbAsync();
        if (Rgb is { } rgb) await rgb.SettledAsync(TimeSpan.FromSeconds(20)); // so the button stays busy until devices are back
        Log($"rgb: rescan done, {Rgb?.Devices.Count ?? 0} device(s)");
    }

    bool _rescanningFans;

    /// <summary>
    /// Looks for fans and sensors again: stops the fan loop (fans go back to their own control for the moment),
    /// closes the drivers, opens them afresh, and starts a new fan loop on what they find. The open page reloads.
    /// </summary>
    public async Task RescanFansAsync()
    {
        if (_rescanningFans) return;
        _rescanningFans = true;
        try
        {
            Log("rescan fans and sensors");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (oldEngine, oldHub) = (Engine, Hub);
            // Close before opening: some controllers (NZXT) can only be open once.
            await Task.Run(() => { oldEngine.Dispose(); oldHub.Dispose(); });
            var hub = new HardwareHub();
            await Task.Run(() => hub.Open(Config.Drivers));
            Hub = hub;
            Engine = new FanEngine(hub, Config) { Paused = OtherFanAppRunning() };
            Engine.Start();
            Live.Instance.Reload();
            _window?.ReloadPage();
            Log($"rescan fans and sensors: {oldHub.Fans.Count} -> {hub.Fans.Count} fans, {oldHub.Temps.Count} -> {hub.Temps.Count} sensors ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception e) { Log($"rescan fans and sensors failed: {e}"); }
        finally { _rescanningFans = false; }
    }

    int? ZoneSize(string device, string zone) => Config.Lighting.ZoneSizes.TryGetValue(device, out var z) && z.TryGetValue(zone, out var n) ? n : null;

    /// <summary>Saves how many LEDs an addressable header has and resizes it now.</summary>
    public void SetZoneSize(string device, string zone, int leds)
    {
        if (!Config.Lighting.ZoneSizes.TryGetValue(device, out var sizes)) Config.Lighting.ZoneSizes[device] = sizes = [];
        if (sizes.GetValueOrDefault(zone, -1) == leds) return;
        sizes[zone] = leds;
        Log($"lighting: {device} zone \"{zone}\" -> {leds} LEDs");
        ConfigChanged();
        if (Demo.On) { Lighting.SetTargets(Demo.RgbDevices(ZoneSize)); Ui.TryEnqueue(() => LightingStateChanged?.Invoke()); return; }
        if (Rgb is { } rgb) _ = Task.Run(rgb.ReloadAsync); // resizes, reloads, and rebuilds the targets
    }

    void RebuildTargets()
    {
        Lighting.SetTargets(LightingEngine.TargetsFrom(Rgb, _seiren));
        Ui.TryEnqueue(() => LightingStateChanged?.Invoke());
    }

    /// <summary>A separate OpenRGB (its service or window), not our own engine.</summary>
    static bool OtherOpenRgbRunning()
    {
        var ours = Path.GetFullPath(Path.Combine(RgbEngine.EngineDir, "OpenRGB.exe"));
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("OpenRGB"))
        {
            using (p)
            {
                try { if (!string.Equals(p.MainModule?.FileName, ours, StringComparison.OrdinalIgnoreCase)) return true; }
                catch { return true; } // can't tell: assume it's someone else's
            }
        }
        return false;
    }

    static bool OtherFanAppRunning()
    {
#if UITEST
        return !Demo.On; // the UI-test build never touches real fans (demo fans are made up)
#endif
        var p = System.Diagnostics.Process.GetProcessesByName("FanControl");
        foreach (var x in p) x.Dispose();
        return p.Length > 0;
    }

    /// <summary>A device setting (DPI, polling…) changed: save it and send it to the device.</summary>
    public void PeripheralSettingsChanged()
    {
        ConfigChanged();
#if !UITEST
        Peripherals.SettingsChanged();
#endif
    }

    /// <summary>Left click: the mini dashboard, or the full window when that's what Settings says.</summary>
    void TrayClick()
    {
        if (!Config.TrayPopup) { ShowWindow(); return; }
        (_trayPanel ??= new TrayPanel()).Toggle();
    }

    /// <summary>Redraws the tray icon now (after its setting changed).</summary>
    public void RefreshTray() => UpdateTrayTooltip();

    /// <summary>The live tray icon (a number on a tile, or the logo) and its tooltip.</summary>
    void UpdateTrayTooltip()
    {
        if (_tray is null) return;
        double? cpu = Live.CpuSensor(Hub.Temps)?.Value, gpu = Live.GpuSensor(Hub.Temps)?.Value, air = Live.Airflow(Hub.Fans);
        var (value, isTemp) = Config.TrayShows switch
        {
            TrayShow.CpuTemp => (cpu, true),
            TrayShow.GpuTemp => (gpu, true),
            TrayShow.Airflow => (air, false),
            _ => ((double?)null, false),
        };
        var icon = value is double v ? TrayArt.Render($"{Math.Round(v):0}", TrayArt.ColorFor(v, isTemp)) : _logo;
        if (icon is not null && !ReferenceEquals(icon, _tray.Icon)) _tray.Icon = icon;

        var summary = string.Join(" · ", new[]
        {
            cpu is double c ? $"CPU {c:0} °C" : null,
            gpu is double g ? $"GPU {g:0} °C" : null,
            air is double a ? $"airflow {a:0}%" : null,
        }.Where(s => s is not null));
        var states = Peripherals.States;
        var batteries = Peripherals.Devices
            .Where(d => states.TryGetValue(d.Key, out var s) && s.BatteryPercent is not null)
            .Select(d => $"{d.Name}: {states[d.Key].BatteryPercent}%{(states[d.Key].Charging == true ? " (charging)" : "")}");
        _tray.ToolTipText = string.Join("\n", new[] { "RigControl", summary }.Where(s => s.Length > 0).Concat(batteries));
    }

    public void ConfigChanged()
    {
        Engine.Apply(Config);
        Lighting?.Apply(Config.Lighting);
        _saveTimer?.Stop();
        _saveTimer?.Start();
    }

    public void ReplaceConfig(AppConfig config)
    {
        Config = config;
        ConfigChanged();
    }

    /// <summary>Appends a line to %APPDATA%\RigControl\rigcontrol.log (kept short: last 200 KB).</summary>
    public static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.Folder);
            var path = Path.Combine(AppConfig.Folder, "rigcontrol.log");
            if (File.Exists(path) && new FileInfo(path).Length > 200_000) File.Delete(path);
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {line}\n");
        }
        catch { }
    }

    public void ShowWindow() => Ui.TryEnqueue(() =>
    {
        Log($"ShowWindow (window exists: {_window is not null})");
        if (_window is null)
        {
            _window = new MainWindow();
            _window.Closed += (_, _) => _window = null; // closing frees the UI; the tray keeps running
        }
        _window.Activate();
        _window.BringToFront();
        Log("window shown");
    });

    void CreateTray()
    {
        var menu = new MenuFlyout();
        // PopupMenu mode runs each item's Command, never its Click event.
        var open = new MenuFlyoutItem { Text = "Open RigControl", Command = new Command(() => { Log("tray: open"); ShowWindow(); }) };
        var quit = new MenuFlyoutItem { Text = "Exit", Command = new Command(() => { Log("tray: exit"); Ui.TryEnqueue(Quit); }) };
        menu.Items.Add(open);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(quit);

        _tray = new TaskbarIcon
        {
            ToolTipText = "RigControl",
            Icon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "RigControl.ico")),
            ContextMenuMode = ContextMenuMode.PopupMenu,
            ContextFlyout = menu,
            NoLeftClickDelay = true,
            LeftClickCommand = new Command(() => Ui.TryEnqueue(TrayClick)),
            DoubleClickCommand = new Command(() => Ui.TryEnqueue(ShowWindow)),
        };
        _logo = _tray.Icon;
        _tray.ForceCreate(enablesEfficiencyMode: false);
        _trayTimer = Ui.CreateTimer();
        _trayTimer.Interval = TimeSpan.FromSeconds(2);
        _trayTimer.Tick += (_, _) => UpdateTrayTooltip();
        _trayTimer.Start();
        UpdateTrayTooltip();
    }

    /// <summary>Starts a fresh instance (it waits for this one to exit), then quits. Keeps admin, no UAC prompt.</summary>
    public void Restart()
    {
        Log("restart requested");
        var args = $"--after {Environment.ProcessId}" + (_window is null ? " --tray" : "");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = false });
        Quit();
    }

    public void Quit()
    {
        _saveTimer?.Stop();
        try { Config.Save(); } catch (Exception e) { Log($"config save on exit failed: {e.Message}"); }
        Log("quit");
        // Each step has its own time limit; a slow one is logged, since a restart waits for all of them.
        void Step(string name, Action? a)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { a?.Invoke(); } catch (Exception e) { Log($"quit: {name} failed: {e.Message}"); }
            if (sw.ElapsedMilliseconds > 1000) Log($"quit: {name} took {sw.ElapsedMilliseconds} ms");
        }
        Step("fans", Engine.Dispose); // hands every fan back to the hardware
        Step("lighting", Lighting is null ? null : Lighting.Dispose);
        Step("peripherals", Peripherals is null ? null : Peripherals.Dispose);
        Step("openrgb", Rgb is null ? null : Rgb.Dispose);
        Step("seiren", _seiren is null ? null : _seiren.Dispose);
        Step("sensors", Hub.Dispose);
        Log("quit: done");
        _trayTimer?.Stop();
        _tray?.Dispose();
        _window?.Close();
        Exit();
    }

    void Crash(Exception? e)
    {
        Log($"CRASH {e?.GetType().Name}: {e?.Message}");
        try { Engine?.ReleaseDriven(); } catch { }
        try
        {
            Directory.CreateDirectory(AppConfig.Folder);
            File.AppendAllText(Path.Combine(AppConfig.Folder, "crash.log"), $"{DateTime.Now:O}\n{e}\n\n");
        }
        catch { }
    }
}

sealed class Command(Action run) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => run();
}

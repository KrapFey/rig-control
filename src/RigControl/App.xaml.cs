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
    /// <summary>A start-up note was closed or went away by itself.</summary>
    public event Action? NotesChanged;
    string? _exitNote; // "didn't exit properly": gone once this run has gone well for a while
    readonly DateTime _startedAt = DateTime.Now;

    public void DismissNote(string note)
    {
        if (StartupNotes.Remove(note)) NotesChanged?.Invoke();
    }

    /// <summary>The note about the last run fades once this one has run cleanly for 5 minutes: it's old news by then.</summary>
    void ExpireExitNote()
    {
        if (_exitNote is null || DateTime.Now - _startedAt < TimeSpan.FromMinutes(5)) return;
        DismissNote(_exitNote);
        _exitNote = null;
    }
    public LightingEngine Lighting { get; private set; } = null!;
    public PeripheralHub Peripherals { get; private set; } = null!;
    public RgbEngine? Rgb { get; private set; }
    /// <summary>Why lighting isn't running, when it isn't.</summary>
    public string? LightingNotice { get; private set; }
    public event Action? LightingStateChanged;
    SeirenV3? _seiren;

    MainWindow? _window;
    TaskbarIcon? _tray;
    System.Drawing.Icon? _logo;
    DispatcherQueueTimer? _trayTimer, _historyTimer; // fields: a timer only a local holds gets collected and stops
    DispatcherQueueTimer? _saveTimer, _applyTimer, _watch; // _watch: FanControl check and app rules (games), every 2 s
    bool _applyPending, _peripheralsChanged;

    public App()
    {
        Instance = this;
        AppConfig.Log = Log;
        UseBuildFolder();
        Log($"start pid {Environment.ProcessId} args [{string.Join(" ", Environment.GetCommandLineArgs().Skip(1))}]");
        InitializeComponent();
        // The tray keeps the app (and fan control) alive with no window open.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, e) => Crash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash(e.ExceptionObject as Exception);
    }

    /// <summary>
    /// The settings folder for this build: the UI-test build never touches real settings, and RIGCONTROL_DEMO=1 gives it
    /// made-up hardware for screenshots (RigControl.Core.Demo) in a folder of its own. Also used by the watchdog.
    /// </summary>
    public static void UseBuildFolder()
    {
#if UITEST
        Demo.On = Environment.GetEnvironmentVariable("RIGCONTROL_DEMO") == "1";
        AppConfig.Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Demo.On ? "RigControl-demo" : "RigControl-uitest");
#endif
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

        // A run that ended without exiting (see CrashGuard) is said so on the overview; the watchdog started this one
        // again if it was a crash ("--crashed").
        var boot = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        bool restarted = Environment.GetCommandLineArgs().Contains("--crashed");
        if (CrashGuard.Begin(Environment.ProcessId, boot) is { } lastStart)
        {
            Log($"previous run (started {lastStart:HH:mm:ss}) ended without exiting{(restarted ? "; started again by the watchdog" : "")}");
            _exitNote = restarted
                ? $"RigControl stopped unexpectedly and was started again, so your fans are back on their curves. What it was doing is in rigcontrol.log, in the settings folder (Settings → About)."
                : $"RigControl didn't exit properly last time (it was started at {lastStart:t}). Its log, rigcontrol.log, is in the settings folder (Settings → About).";
            StartupNotes.Add(_exitNote);
        }
#if !UITEST
        Watchdog.Start();
#endif
        // Windows ending the session (logoff, shutdown, a fast-startup "shutdown") isn't a crash.
        Microsoft.Win32.SystemEvents.SessionEnded += (_, _) => { CrashGuard.End(Environment.ProcessId); Log("Windows is ending the session"); };

        // Opening the hardware takes seconds: do it in the background so the tray and window appear at once.
        // Until it's done, an engine over the empty hub stands in (it drives nothing).
        Engine = new FanEngine(Hub, Config);

        // A fan controller plugged in (or one that only showed up after logon): look for fans again.
        _fanWatch = Ui.CreateTimer();
        _fanWatch.Interval = TimeSpan.FromSeconds(2); // devices arrive as several interfaces; let them settle
        _fanWatch.IsRepeating = false;
        _fanWatch.Tick += async (_, _) =>
        {
            try
            {
                if (_openingFans || await Task.Run(HardwareHub.FanControllersPresent) == _fanControllers) return;
                await OpenFansAsync("a fan controller was plugged in or out");
            }
            catch (Exception e) { Log($"fan controller check failed: {e.Message}"); }
        };
        HidSharp.DeviceList.Local.Changed += (_, _) => Ui.TryEnqueue(() => { _fanWatch.Stop(); _fanWatch.Start(); });

        // Two programs writing the same fans fight each other: stay hands-off while FanControl runs.
        _watch = Ui.CreateTimer();
        _watch.Interval = TimeSpan.FromSeconds(2);
        _watch.Tick += (_, _) => { Engine.Paused = OtherFanAppRunning(); _ = CheckAppRulesAsync(); };
        _watch.Start();

        _saveTimer = Ui.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(600);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) =>
        {
            try { Config.Save(); } catch (Exception e) { Log($"config save failed: {e.Message}"); }
        };

        // Changes come in bursts (a slider drag, "Fill all" over a hundred LEDs) and applying one copies the config:
        // at most one apply every 30 ms, with the latest state.
        _applyTimer = Ui.CreateTimer();
        _applyTimer.Interval = TimeSpan.FromMilliseconds(30);
        _applyTimer.IsRepeating = false;
        _applyTimer.Tick += (_, _) => { _applyPending = false; ApplyConfig(); };

        // The peripherals thread reads the fan engine's copy of the config (made on the UI thread at each apply and
        // never changed afterwards), not the live one: that is being edited on the UI thread.
#if UITEST
        // the UI-test build reads devices but never writes settings (demo devices are made up, so they may)
        Peripherals = new PeripheralHub(() => Demo.On ? Engine.Config.Peripherals : [], NewPluginManager());
#else
        Peripherals = new PeripheralHub(() => Engine.Config.Peripherals, NewPluginManager());
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
        // Locked: the lights go dark, when that's switched on in Settings.
        Microsoft.Win32.SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason is not (Microsoft.Win32.SessionSwitchReason.SessionLock or Microsoft.Win32.SessionSwitchReason.SessionUnlock)) return;
            Ui.TryEnqueue(() =>
            {
                _locked = e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock;
                Log($"Windows {(_locked ? "locked" : "unlocked")}");
                ApplyConfig();
            });
        };
        _ = StartHardwareAsync();

        CreateTray();
        if (restarted)
            _tray?.ShowNotification("RigControl was restarted", "It stopped unexpectedly. Your fans are back on their curves.", H.NotifyIcon.Core.NotificationIcon.Warning);
        if (!Environment.GetCommandLineArgs().Contains("--tray")) ShowWindow();
        _ = UpdateLoopAsync();
    }

    /// <summary>Plugins from the plugins folder, switched on and allowed as Settings says (read from the engine's copy of the config, which no other thread edits).</summary>
    PluginManager NewPluginManager() => new(PluginManager.DefaultFolder, () => new PluginChoices(Engine.Config.Plugins, Engine.Config.AdminPlugins));

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

    /// <summary>Fans first, then OpenRGB, as before: both talk to the NZXT controllers, and fans must not lose that race.</summary>
    async Task StartHardwareAsync()
    {
#if !UITEST
        _ = Task.Run(() =>
        {
            try { if (Startup.FollowThisCopy()) Log($"start with Windows: task now starts {Environment.ProcessPath}"); }
            catch (Exception e) { Log($"start with Windows: couldn't update the task: {e.Message}"); }
        });
#endif
        await OpenFansAsync("start");
        await StartLightingAsync();
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
            rgb.ConnectionLost += () => Ui.TryEnqueue(() => _ = LightingLostAsync(rgb));
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

    long _lightingRestartedAt = -60_000;

    /// <summary>OpenRGB went away by itself: start it again, so lighting doesn't stay dead until RigControl restarts.</summary>
    async Task LightingLostAsync(RgbEngine lost)
    {
        if (_quitting || !ReferenceEquals(Rgb, lost)) return; // already replaced (a rescan) or on the way out
        // ponytail: one restart a minute; an OpenRGB that keeps dying stays off, with a notice
        if (Environment.TickCount64 - _lightingRestartedAt < 60_000)
        {
            LightingNotice = "The lighting engine (OpenRGB) stopped twice in a minute, so it's off for now. Use Rescan on the Lighting page to try again.";
            Log("rgb: OpenRGB connection lost again within a minute, not restarting");
            Lighting.SetTargets([]); // Rgb stays the dead engine, so Rescan can replace it
            LightingStateChanged?.Invoke();
            return;
        }
        _lightingRestartedAt = Environment.TickCount64;
        Log("rgb: OpenRGB connection lost, starting it again");
        await RescanLightingAsync();
    }

    bool _openingFans, _quitting;
    DispatcherQueueTimer? _fanWatch;
    string _fanControllers = ""; // what FanControllersPresent said when the hardware was last opened

    /// <summary>Looks for fans and sensors again (the Rescan button).</summary>
    public Task RescanFansAsync() => OpenFansAsync("rescan");

    /// <summary>
    /// Opens the fan and sensor drivers afresh: stops the fan loop (fans go back to their own control for the moment),
    /// closes the drivers, opens them again, and starts a new fan loop on what they find. The open page reloads.
    /// </summary>
    async Task OpenFansAsync(string why)
    {
        if (_openingFans) return;
        _openingFans = true;
        try
        {
            Log($"fans and sensors: opening ({why})");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (oldEngine, oldHub) = (Engine, Hub);
            var drivers = new Dictionary<string, string>(Config.Drivers);
            var hub = new HardwareHub();
            await Task.Run(() =>
            {
                // Close before opening: some controllers (NZXT) can only be open once.
                oldEngine.Dispose();
                oldHub.Dispose();
                // Taken before opening: a controller that turns up while the drivers open still counts as new.
                _fanControllers = HardwareHub.FanControllersPresent();
                hub.Open(drivers);
            });
            if (_quitting) { hub.Dispose(); return; } // Exit was chosen meanwhile: drive nothing
            Hub = hub;
            Engine = new FanEngine(hub, Config) { Paused = OtherFanAppRunning() };
            Engine.Start();
            Live.Instance.Reload();
            _window?.ReloadPage();
            Log($"fans and sensors: {oldHub.Fans.Count} -> {hub.Fans.Count} fans, {oldHub.Temps.Count} -> {hub.Temps.Count} sensors ({sw.ElapsedMilliseconds} ms)");
            foreach (var p in hub.Problems) Log($"fans and sensors: {p}");
        }
        catch (Exception e) { Log($"fans and sensors: opening failed: {e}"); }
        finally { _openingFans = false; }
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
        _peripheralsChanged = true; // sent once the change is applied (ApplyConfig), so the device gets the new settings
        ConfigChanged();
    }

    /// <summary>Redraws the tray icon now (after its setting changed).</summary>
    public void RefreshTray() { _trayKey = "redraw"; UpdateTrayTooltip(); }

    string? _trayKey = "first"; // what the tray icon shows now: number|colour, or null for the logo

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
        // Drawn again only when the number or its colour changes (a temperature sits still most of the time).
        var key = value is double v ? $"{Math.Round(v):0}|{TrayArt.ColorFor(v, isTemp).ToArgb()}" : null;
        if (key != _trayKey)
        {
            var icon = value is double w ? TrayArt.Render($"{Math.Round(w):0}", TrayArt.ColorFor(w, isTemp)) : _logo;
            if (icon is not null) _tray.Icon = icon;
            _trayKey = key;
        }

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

    /// <summary>
    /// The cooling profile changed (switched, added, renamed or deleted, from the Cooling page or the tray): the fans
    /// follow it at once, it's saved, and the open page shows the new one.
    /// </summary>
    public void CoolingProfileChanged()
    {
        Log($"cooling profile: {Config.ActiveCooling}");
        ConfigChanged();
        Live.Instance.Refresh();
        _window?.ReloadPage();
    }

    /// <summary>The config was edited: the fans, lights and tray menu follow within 30 ms (see _applyTimer), and it's saved.</summary>
    public void ConfigChanged()
    {
        if (!_applyPending && _applyTimer is not null) { _applyPending = true; _applyTimer.Start(); }
        _saveTimer?.Stop();
        _saveTimer?.Start();
    }

    bool _locked; // Windows is locked

    void ApplyConfig()
    {
        if (_quitting) return;
        Engine.Apply(Config);
        Lighting?.Apply(Config.Lighting);
        if (Lighting is not null) Lighting.Dark = _locked && Config.Lighting.OffWhenLocked;
        if (_tray is not null) BuildTrayMenu(); // a profile or lighting change shows in the tray menu
        if (_peripheralsChanged)
        {
            _peripheralsChanged = false;
#if !UITEST
            Peripherals.SettingsChanged();
#endif
        }
    }

    public void ReplaceConfig(AppConfig config)
    {
        Config = config;
        ConfigChanged();
    }

    static readonly object LogLock = new();

    /// <summary>
    /// Appends a line to %APPDATA%\RigControl\rigcontrol.log. Past 200 KB it becomes rigcontrol.old.log and a new one
    /// starts, so the lines before a problem survive. Any thread: one writer at a time, or lines get lost.
    /// </summary>
    public static void Log(string line)
    {
        var text = $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {line}\n";
        lock (LogLock)
        {
            try
            {
                Directory.CreateDirectory(AppConfig.Folder);
                var path = Path.Combine(AppConfig.Folder, "rigcontrol.log");
                if (File.Exists(path) && new FileInfo(path).Length > 200_000)
                    File.Move(path, Path.Combine(AppConfig.Folder, "rigcontrol.old.log"), overwrite: true);
                File.AppendAllText(path, text);
            }
            catch { }
        }
    }

    /// <summary>Goes to a page of the main window with something to open there (see <see cref="MainWindow.Open"/>).</summary>
    public void OpenPage(string tag, object? parameter) => _window?.Open(tag, parameter);

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

    readonly MenuFlyout _trayMenu = new();
    string _trayMenuShows = "";

    /// <summary>
    /// The tray icon's right-click menu: open, switch cooling profile (or make one from a preset), lighting on or off
    /// and its profile, exit. Rebuilt when something it shows changes. (PopupMenu mode runs each item's Command, never
    /// its Click event; commands come on the tray's thread, so each hops to the UI thread.)
    /// </summary>
    void BuildTrayMenu()
    {
        var cfg = Config;
        var shows = string.Join("|", cfg.ActiveCooling, string.Join(",", cfg.CoolingProfileNames()), cfg.Curves.Any(c => c.Kind == CurveKind.Graph),
            cfg.Lighting.Enabled, cfg.Lighting.Active, string.Join(",", cfg.Lighting.Profiles.Select(p => p.Name)));
        if (shows == _trayMenuShows) return;
        _trayMenuShows = shows;

        MenuFlyoutItem Item(string text, Action run) => new() { Text = text, Command = new Command(() => Ui.TryEnqueue(() => run())) };
        ToggleMenuFlyoutItem Check(string text, bool on, Action run) => new() { Text = text, IsChecked = on, Command = new Command(() => Ui.TryEnqueue(() => run())) };

        _trayMenu.Items.Clear();
        _trayMenu.Items.Add(Item("Open RigControl", () => { Log("tray: open"); ShowWindow(); }));
        _trayMenu.Items.Add(new MenuFlyoutSeparator());

        var cooling = new MenuFlyoutSubItem { Text = $"Cooling: {cfg.ActiveCooling}" };
        foreach (var name in cfg.CoolingProfileNames())
            cooling.Items.Add(Check(name, name == cfg.ActiveCooling, () => SwitchCoolingFromTray(name)));
        if (cfg.Curves.Any(c => c.Kind == CurveKind.Graph))
        {
            // One click to a preset: its profile, made from the one in use the first time.
            var missing = CoolingPresets.All.Where(p => !cfg.CoolingProfileNames().Contains(p.ToString())).ToList();
            if (missing.Count > 0)
            {
                cooling.Items.Add(new MenuFlyoutSeparator());
                foreach (var preset in missing)
                    cooling.Items.Add(Item($"New {preset} profile", () => CoolingPresetFromTray(preset)));
            }
        }
        _trayMenu.Items.Add(cooling);

        var lighting = new MenuFlyoutSubItem { Text = cfg.Lighting.Enabled ? $"Lighting: {cfg.Lighting.Current.Name}" : "Lighting: off" };
        lighting.Items.Add(Check("Lighting on", cfg.Lighting.Enabled, () =>
        {
            Config.Lighting.Enabled = !Config.Lighting.Enabled;
            Log($"tray: lighting {(Config.Lighting.Enabled ? "on" : "off")}");
            ConfigChanged();
            LightingStateChanged?.Invoke();
        }));
        if (cfg.Lighting.Profiles.Count > 1)
        {
            lighting.Items.Add(new MenuFlyoutSeparator());
            foreach (var p in cfg.Lighting.Profiles.Select(p => p.Name))
                lighting.Items.Add(Check(p, p == cfg.Lighting.Current.Name, () =>
                {
                    Config.Lighting.Active = p;
                    Log($"tray: lighting profile {p}");
                    ConfigChanged();
                    LightingStateChanged?.Invoke();
                }));
        }
        _trayMenu.Items.Add(lighting);

        _trayMenu.Items.Add(new MenuFlyoutSeparator());
        _trayMenu.Items.Add(Item("Exit", () => { Log("tray: exit"); Quit(); }));
    }

    void SwitchCoolingFromTray(string name)
    {
        if (name == Config.ActiveCooling) return;
        Config.SwitchCooling(name);
        CoolingProfileChanged();
    }

    void CoolingPresetFromTray(CoolingPreset preset)
    {
        Config.AddCooling(preset.ToString());
        int n = CoolingPresets.ApplyAll(Config.Curves, preset, id => Hub.Temps.FirstOrDefault(t => t.Id == id)?.Name);
        Log($"tray: cooling profile \"{preset}\" made from the preset ({n} curves reshaped)");
        CoolingProfileChanged();
    }

    void CreateTray()
    {
        BuildTrayMenu();

        _tray = new TaskbarIcon
        {
            ToolTipText = "RigControl",
            Icon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "RigControl.ico")),
            ContextMenuMode = ContextMenuMode.PopupMenu,
            ContextFlyout = _trayMenu,
            NoLeftClickDelay = true,
            LeftClickCommand = new Command(ShowWindow), // a click opens the app
            DoubleClickCommand = new Command(() => Ui.TryEnqueue(ShowWindow)),
        };
        _logo = _tray.Icon;
        _tray.ForceCreate(enablesEfficiencyMode: false);
        _trayTimer = Ui.CreateTimer();
        _trayTimer.Interval = TimeSpan.FromSeconds(2);
        _trayTimer.Tick += (_, _) => { UpdateTrayTooltip(); CheckStalls(); CheckAlerts(); CheckFanLoop(); ExpireExitNote(); };
        // The overview's history lines: a sample a second (see History).
        _historyTimer = Ui.CreateTimer();
        _historyTimer.Interval = TimeSpan.FromSeconds(1);
        _historyTimer.Tick += (_, _) => SampleHistory();
        _historyTimer.Start();
        _trayTimer.Start();
        UpdateTrayTooltip();
    }

    long _seenTicks = -1;
    int _stuckChecks;

    /// <summary>
    /// The fan loop must keep going: a driver call that never returns would leave every fan at its last speed with
    /// nothing on screen saying so. Counted in loop passes, not time, so sleep and resume can't look like a hang.
    /// After 30 s without a pass, RigControl ends itself; its watchdog starts it afresh (and says so).
    /// </summary>
    void CheckFanLoop()
    {
        var engine = Engine;
        if (_openingFans || _quitting || !engine.Running) { _stuckChecks = 0; _seenTicks = -1; return; }
        var ticks = engine.Ticks;
        if (ticks != _seenTicks) { _seenTicks = ticks; _stuckChecks = 0; return; }
        if (++_stuckChecks < 15) return; // the tray timer runs every 2 s
        Log("fan loop: no update for 30 s (a fan or sensor driver isn't answering); ending so the watchdog starts RigControl afresh");
#if UITEST
        _stuckChecks = 0; // no watchdog here: just say so
#else
        System.Diagnostics.Process.GetCurrentProcess().Kill();
#endif
    }

    /// <summary>The overview's last 3 minutes (see <see cref="RigControl.History"/>).</summary>
    public History History { get; } = new();

    void SampleHistory()
    {
        History.Add("cpu", Live.CpuSensor(Hub.Temps)?.Value);
        History.Add("gpu", Live.GpuSensor(Hub.Temps)?.Value);
        History.Add("liquid", Live.LiquidSensor(Hub.Temps)?.Value);
        History.Add("air", Live.Airflow(Hub.Fans));
    }

    readonly HashSet<string> _stallsShown = [];

    /// <summary>A Windows notification when a driven fan stops spinning (once, until it spins again).</summary>
    void CheckStalls()
    {
        foreach (var (id, st) in Engine.Status)
        {
            if (!st.Stalled) { _stallsShown.Remove(id); continue; }
            if (!_stallsShown.Add(id)) continue;
            var name = Config.Fans.TryGetValue(id, out var s) && !string.IsNullOrWhiteSpace(s.Name) ? s.Name : st.Channel.Name;
            Log($"fan {id} ({name}): not spinning at {st.Target:0}%");
            _tray?.ShowNotification("A fan isn't spinning",
                $"{name} ({st.Channel.Device}) reads 0 rpm at {st.Target:0}%. Check the fan and its cable.",
                H.NotifyIcon.Core.NotificationIcon.Warning);
        }
    }

    readonly HashSet<string> _alerted = []; // what a notification is out for, until it's over
    readonly Dictionary<string, int> _hotChecks = [];

    /// <summary>Windows notifications: a device's battery is low, or the CPU or GPU stays hot. Once each, until it's over.</summary>
    void CheckAlerts()
    {
        int low = Config.LowBatteryAlert;
        var states = Peripherals.States;
        foreach (var d in Peripherals.Devices)
        {
            if (!states.TryGetValue(d.Key, out var s) || s.BatteryPercent is not int pct) continue;
            if (low > 0 && pct <= low && s.Charging != true && s.Connected != false)
            {
                if (!_alerted.Add(d.Key)) continue;
                Log($"battery low: {d.Name} {pct}%");
                _tray?.ShowNotification("Battery low", $"{d.Name} is at {pct}%.", H.NotifyIcon.Core.NotificationIcon.Warning);
            }
            else if (low == 0 || pct > low + 5 || s.Charging == true) _alerted.Remove(d.Key); // 5% of slack: a reading that hovers doesn't notify twice
        }
        Hot("hot/cpu", "The CPU", Live.CpuSensor(Hub.Temps));
        Hot("hot/gpu", "The graphics card", Live.GpuSensor(Hub.Temps));
    }

    void Hot(string key, string what, TempSensor? sensor)
    {
        int limit = Config.HotAlert;
        float? t = sensor?.Value;
        if (limit <= 0 || t is null || t < limit)
        {
            _hotChecks[key] = 0;
            if (limit <= 0 || t is null || t < limit - 5) _alerted.Remove(key);
            return;
        }
        // 20 s at or above the limit (the tray timer runs every 2 s): a spike isn't worth a notification
        if ((_hotChecks[key] = _hotChecks.GetValueOrDefault(key) + 1) < 10 || !_alerted.Add(key)) return;
        Log($"hot: {sensor!.Name} {t:0} °C (limit {limit})");
        _tray?.ShowNotification($"{what} is hot", $"{SensorLabel(sensor)} has been at {limit} °C or more for 20 seconds ({t:0} °C now).",
            H.NotifyIcon.Core.NotificationIcon.Warning);
    }

    readonly GameDetector _games = new();
    readonly AppRule _gameRule = new(); // "any game", as a rule: the game running now and the profiles chosen for games
    /// <summary>The game found running at the last look (every 2 s, while profiles for games are chosen), or null.</summary>
    public string? RunningGame { get; private set; }
    AppRule? _rule; // the app rule in force
    (string Cooling, string Lighting) _beforeRule; // the profiles in use when the first rule took over
    bool _checkingRules;

    /// <summary>
    /// Profiles that follow running apps (Settings): while a rule's program runs, its cooling and lighting profiles are
    /// in use; when it ends, the ones from before come back. A profile you pick by hand meanwhile stays until then.
    /// </summary>
    async Task CheckAppRulesAsync()
    {
        bool games = Config.GameCooling is not null || Config.GameLighting is not null;
        if (_checkingRules || _quitting || (Config.AppRules.Count == 0 && !games && _rule is null)) return;
        _checkingRules = true;
        try
        {
            var (running, game) = await Task.Run(() =>
            {
                var all = System.Diagnostics.Process.GetProcesses();
                var names = all.Select(p => p.ProcessName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var game = games ? _games.Running(all) : null;
                foreach (var p in all) p.Dispose();
                return (names, game);
            });
            RunningGame = game;
            static string Name(string process) => process.Trim() is var n && n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
            // A program you listed comes first (so listing one with "No change" leaves it out); then any game.
            var rule = Config.AppRules.FirstOrDefault(r => Name(r.Process).Length > 0 && running.Contains(Name(r.Process)));
            if (rule is null && game is not null)
            {
                (_gameRule.Process, _gameRule.Cooling, _gameRule.Lighting) = (game, Config.GameCooling, Config.GameLighting);
                rule = _gameRule;
            }
            if (ReferenceEquals(rule, _rule)) return;
            if (_rule is null) _beforeRule = (Config.ActiveCooling, Config.Lighting.Active);
            // What the ending rule changed goes back; what the new one names comes in.
            string? cooling = rule?.Cooling ?? (_rule?.Cooling is null ? null : _beforeRule.Cooling);
            string? lighting = rule?.Lighting ?? (_rule?.Lighting is null ? null : _beforeRule.Lighting);
            Log(rule is null ? $"app rule: {_rule!.Process} ended" : $"app rule: {rule.Process} is running (cooling {rule.Cooling ?? "unchanged"}, lighting {rule.Lighting ?? "unchanged"})");
            _rule = rule;
            if (lighting is not null && lighting != Config.Lighting.Active && Config.Lighting.Profiles.Any(p => p.Name == lighting))
            {
                Config.Lighting.Active = lighting;
                ConfigChanged();
                LightingStateChanged?.Invoke();
            }
            if (cooling is not null && Config.CoolingProfiles.Any(p => p.Name == cooling)) SwitchCoolingFromTray(cooling);
        }
        catch (Exception e) { Log($"app rules: {e.Message}"); }
        finally { _checkingRules = false; }
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
        _quitting = true;
        _fanWatch?.Stop();
        _window?.Close(); // first: it notes where it was (saved just below), and the app looks gone at once
        _saveTimer?.Stop();
        _applyTimer?.Stop();
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
        CrashGuard.End(Environment.ProcessId); // a clean exit: the watchdog lets it be
        _trayTimer?.Stop();
        _tray?.Dispose();
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

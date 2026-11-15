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
            StartupNotes.Add(restarted
                ? $"RigControl stopped unexpectedly and was started again, so your fans are back on their curves. What it was doing is in rigcontrol.log, in the settings folder (Settings → About)."
                : $"RigControl didn't exit properly last time (it was started at {lastStart:t}). Its log, rigcontrol.log, is in the settings folder (Settings → About).");
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
        _ = StartHardwareAsync();

        CreateTray();
        if (restarted)
            _tray?.ShowNotification("RigControl was restarted", "It stopped unexpectedly. Your fans are back on their curves.", H.NotifyIcon.Core.NotificationIcon.Warning);
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

    /// <summary>Opens the tray's mini dashboard (or closes it), as clicking the tray icon does.</summary>
    public void ToggleMiniDashboard() => Ui.TryEnqueue(() => (_trayPanel ??= new TrayPanel()).Toggle());

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
        _trayTimer.Tick += (_, _) => { UpdateTrayTooltip(); CheckStalls(); SampleHistory(); CheckFanLoop(); };
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

    /// <summary>The overview's last 10 minutes (see <see cref="RigControl.History"/>).</summary>
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

using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RigControl.Core;

namespace RigControl;

public partial class App : Application
{
    public static App? Instance { get; private set; }

    public HardwareHub Hub { get; } = new();
    public FanEngine Engine { get; private set; } = null!;
    /// <summary>The live config the UI edits. Call <see cref="ConfigChanged"/> after changing it.</summary>
    public AppConfig Config { get; private set; } = null!;
    public DispatcherQueue Ui { get; private set; } = null!;
    public List<string> StartupNotes { get; } = [];
    public LightingEngine Lighting { get; private set; } = null!;
    public RgbEngine? Rgb { get; private set; }
    /// <summary>Why lighting isn't running, when it isn't.</summary>
    public string? LightingNotice { get; private set; }
    public event Action? LightingStateChanged;
    SeirenV3? _seiren;

    MainWindow? _window;
    TaskbarIcon? _tray;
    DispatcherQueueTimer? _saveTimer;

    public App()
    {
        Instance = this;
        AppConfig.Log = Log;
#if UITEST
        AppConfig.Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RigControl-uitest");
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

        Lighting = new LightingEngine(id => Hub.Temps.FirstOrDefault(t => t.Id == id)?.Value);
        Lighting.Apply(Config.Lighting);
        _ = StartLightingAsync();

        CreateTray();
        if (!Environment.GetCommandLineArgs().Contains("--tray")) ShowWindow();
    }

    async Task StartLightingAsync()
    {
#if UITEST
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
        try
        {
            _seiren = SeirenV3.Open();
            Rgb = new RgbEngine(Path.Combine(AppConfig.Folder, "openrgb"));
            Rgb.DevicesChanged += RebuildTargets;
            await Task.Run(() => Rgb.StartAsync());
            if (Rgb.Problem is string p) LightingNotice = p;
            RebuildTargets();
        }
        catch (Exception e)
        {
            LightingNotice = $"Lighting couldn't start: {e.Message}";
            Log($"rgb: start failed: {e}");
        }
        Ui.TryEnqueue(() => LightingStateChanged?.Invoke());
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
        return true; // the UI-test build never touches fans
#endif
        var p = System.Diagnostics.Process.GetProcessesByName("FanControl");
        foreach (var x in p) x.Dispose();
        return p.Length > 0;
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
            LeftClickCommand = new Command(() => { Log("tray left click"); ShowWindow(); }),
        };
        _tray.ForceCreate(enablesEfficiencyMode: false);
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
        Engine.Dispose(); // hands every fan back to the hardware
        Lighting?.Dispose();
        Rgb?.Dispose();
        _seiren?.Dispose();
        Hub.Dispose();
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

using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RigControl.Core;

namespace RigControl.Pages;

public sealed partial class SettingsPage : Page
{
    static readonly double[] Intervals = [0.5, 1, 2];
    bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _ = ShowAutoStartAsync();
            var i = Array.IndexOf(Intervals, App.Instance!.Config.IntervalSeconds);
            Interval.SelectedIndex = i < 0 ? 1 : i;
            var path = FanControlImport.DefaultPath;
            ImportButton.IsEnabled = File.Exists(path);
            ImportHint.Text = File.Exists(path)
                ? $"Reads {path}. Replaces your current curves and fan settings."
                : "FanControl's config wasn't found on this PC.";
            FolderText.Text = AppConfig.Folder;
            AboutTitle.Text = $"RigControl {typeof(App).Assembly.GetName().Version?.ToString(3)}";
            BuildDrivers();
            AutoUpdate.IsOn = App.Instance.Config.CheckForUpdates;
            TrayShowsBox.SelectedIndex = (int)App.Instance.Config.TrayShows;
            DarkLocked.IsOn = App.Instance.Config.Lighting.OffWhenLocked;
            BatteryAlert.Value = App.Instance.Config.LowBatteryAlert;
            HotAlert.Value = App.Instance.Config.HotAlert;
            BuildRules();
            _ = ShowGamesAsync();
            BuildPlugins();
            App.Instance.Peripherals.Changed += PluginsChanged;
            ShowUpdate();
            App.Instance.UpdateFound += ShowUpdate;
            _loading = false;
        };
        Unloaded += (_, _) =>
        {
            App.Instance!.UpdateFound -= ShowUpdate;
            App.Instance.Peripherals.Changed -= PluginsChanged;
        };
    }

    void TrayShows_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TrayShowsBox.SelectedIndex < 0) return;
        App.Instance!.Config.TrayShows = (TrayShow)TrayShowsBox.SelectedIndex;
        App.Instance.ConfigChanged();
        App.Instance.RefreshTray();
    }

    void DarkLocked_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Instance!.Config.Lighting.OffWhenLocked = DarkLocked.IsOn;
        App.Instance.ConfigChanged();
    }

    void Alert_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading) return;
        if (double.IsNaN(args.NewValue)) { sender.Value = 0; return; } // emptied: off
        var cfg = App.Instance!.Config;
        if (sender == BatteryAlert) cfg.LowBatteryAlert = (int)args.NewValue; else cfg.HotAlert = (int)args.NewValue;
        App.Instance.ConfigChanged();
    }

    // ---- profiles that follow apps (AppConfig.AppRules; App.CheckAppRulesAsync acts on them)

    static readonly GridLength[] RuleColumns = [new(1, GridUnitType.Star), new(180), new(180), new(36)];

    static Grid RuleRow(params FrameworkElement[] cells)
    {
        var row = new Grid { ColumnSpacing = 8 };
        for (int i = 0; i < RuleColumns.Length; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = RuleColumns[i] });
            if (i >= cells.Length) continue;
            Grid.SetColumn(cells[i], i);
            row.Children.Add(cells[i]);
        }
        return row;
    }

    void BuildRules()
    {
        var app = App.Instance!;
        var cfg = app.Config;
        const string Keep = "No change";
        RulesList.Children.Clear();
        TextBlock Head(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["Caption"] };
        RulesList.Children.Add(RuleRow(Head("WHILE THIS RUNS"), Head("COOLING PROFILE"), Head("LIGHTING PROFILE")));
        ComboBox Pick(string what, List<string> names, string? now, Action<string?> set)
        {
            var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = names.Prepend(Keep).ToList() };
            box.SelectedItem = now is not null && names.Contains(now) ? now : Keep;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, what);
            box.SelectionChanged += (_, _) =>
            {
                set(box.SelectedItem is string s && s != Keep ? s : null);
                app.ConfigChanged();
            };
            return box;
        }
        List<string> cooling = cfg.CoolingProfileNames(), lighting = [.. cfg.Lighting.Profiles.Select(p => p.Name)];
        var anyGame = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) };
        anyGame.Children.Add(new FontIcon { Glyph = "\uE7FC", FontSize = 16 });
        anyGame.Children.Add(new TextBlock { Text = "Any game", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        RulesList.Children.Add(RuleRow(anyGame,
            Pick("Cooling profile for games", cooling, cfg.GameCooling, v => cfg.GameCooling = v),
            Pick("Lighting profile for games", lighting, cfg.GameLighting, v => cfg.GameLighting = v)));
        foreach (var rule in cfg.AppRules)
        {
            var name = new TextBox { Text = rule.Process, PlaceholderText = "game.exe" };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(name, "Program file name");
            ToolTipService.SetToolTip(name, "The file name as Task Manager shows it (Details tab), e.g. game.exe");
            name.LostFocus += (_, _) =>
            {
                if (name.Text.Trim() == rule.Process) return;
                rule.Process = name.Text.Trim();
                app.ConfigChanged();
            };
            var remove = new Button { Content = new FontIcon { Glyph = "\uE711", FontSize = 11 }, Padding = new Thickness(8, 6, 8, 6) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, "Remove this program");
            ToolTipService.SetToolTip(remove, "Remove");
            remove.Click += (_, _) => { cfg.AppRules.Remove(rule); app.ConfigChanged(); BuildRules(); };
            RulesList.Children.Add(RuleRow(name,
                Pick("Cooling profile", cooling, rule.Cooling, v => rule.Cooling = v),
                Pick("Lighting profile", lighting, rule.Lighting, v => rule.Lighting = v),
                remove));
        }
    }

    /// <summary>What game detection has to go on here, and the game it sees now.</summary>
    async Task ShowGamesAsync()
    {
        var (exes, folders) = await Task.Run(GameDetector.Find);
        GamesText.Text = $"On this PC: {exes.Count} games Windows has seen and {folders.Count} game folders (Steam, Epic, GOG, Ubisoft, Xbox)."
            + (App.Instance!.RunningGame is { } game ? $" Running now: {game}." : "");
    }

    // ---- plugins (PluginManager): a row per plugin folder, each off until switched on

    readonly Dictionary<string, TextBlock> _pluginStatus = [];

    static PluginManager? Plugins => App.Instance!.Peripherals.Plugins;

    void BuildPlugins()
    {
        PluginsList.Children.Clear();
        _pluginStatus.Clear();
        if (Plugins is not { } manager) return;
        var installed = PluginManager.Installed(manager.Folder);
        PluginsHint.Text = installed.Count == 0
            ? $"A plugin is a folder with a plugin.json and its program, in {manager.Folder}. None are installed. Plugins start off, run with an ordinary user's rights, and only start for the devices they name."
            : "Each plugin is a separate program. It starts off; switched on, it runs with an ordinary user's rights whenever one of its devices is plugged in, and its devices appear under Devices. Only switch on plugins you trust.";
        var cfg = App.Instance!.Config;
        var caption = (Style)Application.Current.Resources["Caption"];
        foreach (var info in installed)
        {
            var m = info.Manifest;
            var row = new Grid { ColumnSpacing = 16 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock
            {
                Text = m is null ? info.Name : $"{m.Name} {m.Version}" + (m.Author is { Length: > 0 } a ? $" · by {a}" : ""),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            if (m?.Description is { Length: > 0 } d) text.Children.Add(new TextBlock { Text = d, Style = caption, TextWrapping = TextWrapping.Wrap });
            if (m is not null && info.Problem is null)
                text.Children.Add(new TextBlock
                {
                    Text = (m.Devices.Count == 0 ? "Looks for its own devices" : $"Handles {string.Join(", ", m.Devices)}") + (m.Admin ? " · asks for administrator rights" : ""),
                    Style = caption,
                    TextWrapping = TextWrapping.Wrap,
                });
            var status = new TextBlock { Style = caption, TextWrapping = TextWrapping.Wrap };
            _pluginStatus[info.Id] = status;
            text.Children.Add(status);
            row.Children.Add(text);

            var on = new ToggleSwitch { IsOn = cfg.Plugins.Contains(info.Id), IsEnabled = info.Problem is null, VerticalAlignment = VerticalAlignment.Top };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(on, $"{info.Name} plugin on");
            on.Toggled += async (_, _) =>
            {
                if (on.IsOn == cfg.Plugins.Contains(info.Id)) return;
                if (on.IsOn && m!.Admin && !await AllowAdmin(m))
                {
                    on.IsOn = false;
                    return;
                }
                cfg.Plugins.Remove(info.Id);
                cfg.AdminPlugins.Remove(info.Id);
                if (on.IsOn)
                {
                    cfg.Plugins.Add(info.Id);
                    if (m!.Admin) cfg.AdminPlugins.Add(info.Id);
                }
                App.Log($"plugin {info.Id}: switched {(on.IsOn ? "on" : "off")}");
                App.Instance.ConfigChanged();
                App.Instance.Peripherals.Rescan(); // starts or stops it, and its devices come or go
                status.Text = on.IsOn ? "Starting…" : "Off";
            };
            Grid.SetColumn(on, 1);
            row.Children.Add(on);
            PluginsList.Children.Add(row);
        }
        ShowPluginStatus();
    }

    void PluginsChanged() => DispatcherQueue.TryEnqueue(ShowPluginStatus);

    void ShowPluginStatus()
    {
        if (Plugins is not { } manager) return;
        var status = manager.Status;
        foreach (var info in PluginManager.Installed(manager.Folder))
            if (_pluginStatus.TryGetValue(info.Id, out var text))
                text.Text = info.Problem is { } p ? $"Can't be used: {p}" : status.GetValueOrDefault(info.Id) ?? (App.Instance!.Config.Plugins.Contains(info.Id) ? "Starting…" : "Off");
    }

    async Task<bool> AllowAdmin(PluginManifest m)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Run {m.Name} as administrator?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = $"This plugin asks for administrator rights{(m.Author is { Length: > 0 } a ? $" (it's from {a})" : "")}. With them it can change anything on this PC, not just its devices. Most devices don't need it: only allow it if you trust where the plugin came from.",
            },
            PrimaryButtonText = "Allow and switch on",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    void OpenPlugins_Click(object sender, RoutedEventArgs e)
    {
        if (Plugins is not { } manager) return;
        Directory.CreateDirectory(manager.Folder);
        Process.Start(new ProcessStartInfo("explorer.exe", manager.Folder) { UseShellExecute = true });
    }

    void AddRule_Click(object sender, RoutedEventArgs e)
    {
        App.Instance!.Config.AppRules.Add(new AppRule());
        App.Instance.ConfigChanged();
        BuildRules();
    }

    void AutoUpdate_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Instance!.Config.CheckForUpdates = AutoUpdate.IsOn;
        App.Instance.ConfigChanged();
    }

    async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        CheckNow.IsEnabled = false;
        UpdateStatus.Text = "Checking…";
        UpdateStatus.Text = await App.Instance!.CheckForUpdateAsync();
        CheckNow.IsEnabled = true;
        ShowUpdate();
    }

    void ShowUpdate()
    {
        if (App.Instance!.Update is not { } u) return;
        UpdateStatus.Text = $"RigControl {u.Version} is available (you have {Updates.Current}).";
        InstallUpdate.Content = u.SetupUrl is null ? "Open release page" : $"Update to {u.Version}";
        InstallUpdate.Visibility = Visibility.Visible;
        ReleaseNotes.NavigateUri = new Uri(u.PageUrl);
        ReleaseNotes.Visibility = Visibility.Visible;
    }

    void InstallUpdate_Click(object sender, RoutedEventArgs e) => _ = InstallUpdateAsync(XamlRoot);

    /// <summary>Downloads, verifies and runs the new setup, with progress; RigControl exits once setup starts.</summary>
    public static async Task InstallUpdateAsync(XamlRoot root)
    {
        if (App.Instance!.Update is not { } u) return;
        if (u.SetupUrl is null)
        {
            Process.Start(new ProcessStartInfo(u.PageUrl) { UseShellExecute = true });
            return;
        }
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Width = 320 };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = $"Updating to RigControl {u.Version}",
            Content = new StackPanel { Spacing = 10, Children = { new TextBlock { Text = "Downloading the installer from GitHub…" }, bar } },
        };
        _ = dialog.ShowAsync();
        string? error;
        try { error = await Updates.InstallAsync(u, new Progress<double>(v => bar.Value = v)); }
        catch (Exception ex) { error = ex.Message; }
        dialog.Hide();
        if (error is not null)
            await new ContentDialog { XamlRoot = root, Title = "Couldn't update", Content = error, CloseButtonText = "OK" }.ShowAsync();
    }

    // Start with Windows is a scheduled task, read and written by running schtasks. Never wait for that on the UI
    // thread: the wait lets other window messages run in the middle of a layout pass, and WinUI fails fast on it
    // (opening Settings crashed with 0xC000027B).
    bool _autoStartBusy;

    async Task ShowAutoStartAsync()
    {
        var on = await Task.Run(Startup.IsEnabled);
        _autoStartBusy = true;
        AutoStart.IsOn = on;
        _autoStartBusy = false;
    }

    async void AutoStart_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _autoStartBusy) return;
        bool on = AutoStart.IsOn;
        AutoStart.IsEnabled = false;
        try
        {
            await Task.Run(() => { if (on) Startup.Enable(); else Startup.Disable(); });
        }
        catch (Exception ex)
        {
            await ShowAutoStartAsync();
            _ = new ContentDialog { XamlRoot = XamlRoot, Title = "Couldn't change startup", Content = ex.Message, CloseButtonText = "OK" }.ShowAsync();
        }
        finally { AutoStart.IsEnabled = true; }
    }

    void BuildDrivers()
    {
        var choices = App.Instance!.Hub.DriverChoices;
        DriversCard.Visibility = choices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DriversList.Children.Clear();
        foreach (var choice in choices)
        {
            var row = new Grid { ColumnSpacing = 16 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = choice.Label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var box = new ComboBox { Width = 160, IsEnabled = choice.LiquidctlAvailable, Items = { "Built-in", "liquidctl" } };
            box.SelectedIndex = choice.Current == DriverChoice.Liquidctl ? 1 : 0;
            if (!choice.LiquidctlAvailable) ToolTipService.SetToolTip(box, "This build doesn't include liquidctl.");
            box.SelectionChanged += async (_, _) =>
            {
                var want = box.SelectedIndex == 1 ? DriverChoice.Liquidctl : DriverChoice.Native;
                if (want == choice.Current) return;
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = $"Switch {choice.Label} to {(want == DriverChoice.Liquidctl ? "liquidctl" : "the built-in driver")}?",
                    Content = "RigControl restarts to apply it. Fans hold their speed for the few seconds that takes.",
                    PrimaryButtonText = "Switch and restart",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    box.SelectedIndex = choice.Current == DriverChoice.Liquidctl ? 1 : 0;
                    return;
                }
                var app = App.Instance!;
                App.Log($"driver switch: {choice.Key} -> {want}");
                if (want == DriverChoice.Liquidctl) app.Config.Drivers[choice.Key] = want; else app.Config.Drivers.Remove(choice.Key);
                app.Config.MoveHue2Settings(choice.Key[(choice.Key.IndexOf('/') + 1)..], want == DriverChoice.Liquidctl);
                app.Config.Save();
                app.Restart();
            };
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            DriversList.Children.Add(row);
        }
    }

    void Interval_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Interval.SelectedIndex < 0) return;
        App.Instance!.Config.IntervalSeconds = Intervals[Interval.SelectedIndex];
        App.Instance.ConfigChanged();
    }

    async void Import_Click(object sender, RoutedEventArgs e) => await ImportFanControl(XamlRoot);

    /// <summary>The searchable list of supported devices, opened from About (Back returns here).</summary>
    void Supported_Click(object sender, RoutedEventArgs e) =>
        Frame.Navigate(typeof(SupportedPage), null, new Microsoft.UI.Xaml.Media.Animation.DrillInNavigationTransitionInfo());

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppConfig.Folder);
        Process.Start(new ProcessStartInfo("explorer.exe", AppConfig.Folder) { UseShellExecute = true });
    }

    /// <summary>Shows what will be imported, then applies it. Returns true when imported.</summary>
    public static async Task<bool> ImportFanControl(XamlRoot root)
    {
        var app = App.Instance!;
        ImportResult result;
        try { result = FanControlImport.Import(await File.ReadAllTextAsync(FanControlImport.DefaultPath), HardwareCatalog.From(app.Hub)); }
        catch (Exception ex)
        {
            await new ContentDialog { XamlRoot = root, Title = "Import failed", Content = ex.Message, CloseButtonText = "OK" }.ShowAsync();
            return false;
        }

        var cfg = result.Config;
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = $"{cfg.Curves.Count} curves and {cfg.Fans.Count} fans ({cfg.Fans.Values.Count(f => f.Enabled)} on curves) will replace your current setup.",
            TextWrapping = TextWrapping.Wrap,
        });
        foreach (var n in result.Notes)
            body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = n });
        if (Process.GetProcessesByName("FanControl").Length > 0)
            body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Message = "FanControl is still running. RigControl stays hands-off until you exit it." });

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Import FanControl setup",
            Content = new ScrollViewer { Content = body, MaxHeight = 420 },
            PrimaryButtonText = "Import",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;

        cfg.IntervalSeconds = app.Config.IntervalSeconds;
        cfg.SensorNames = app.Config.SensorNames;
        app.ReplaceConfig(cfg);
        return true;
    }
}

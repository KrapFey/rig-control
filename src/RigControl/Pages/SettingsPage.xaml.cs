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
            AutoStart.IsOn = Startup.IsEnabled();
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
            TrayPopupSwitch.IsOn = App.Instance.Config.TrayPopup;
            ShowUpdate();
            App.Instance.UpdateFound += ShowUpdate;
            _loading = false;
        };
        Unloaded += (_, _) => App.Instance!.UpdateFound -= ShowUpdate;
    }

    void TrayShows_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TrayShowsBox.SelectedIndex < 0) return;
        App.Instance!.Config.TrayShows = (TrayShow)TrayShowsBox.SelectedIndex;
        App.Instance.ConfigChanged();
        App.Instance.RefreshTray();
    }

    void TrayPopup_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Instance!.Config.TrayPopup = TrayPopupSwitch.IsOn;
        App.Instance.ConfigChanged();
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

    void AutoStart_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try
        {
            if (AutoStart.IsOn) Startup.Enable(); else Startup.Disable();
        }
        catch (Exception ex)
        {
            _loading = true;
            AutoStart.IsOn = Startup.IsEnabled();
            _loading = false;
            _ = new ContentDialog { XamlRoot = XamlRoot, Title = "Couldn't change startup", Content = ex.Message, CloseButtonText = "OK" }.ShowAsync();
        }
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

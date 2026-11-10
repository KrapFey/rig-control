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
            _loading = false;
        };
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

using Microsoft.UI.Xaml.Controls;

namespace RigControl.Pages;

public sealed partial class SensorsPage : Page
{
    public SensorsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => { Live.Instance.Attach(); Fill(); };
        Unloaded += (_, _) => Live.Instance.Detach();
    }

    void Fill()
    {
        var q = Filter.Text.Trim();
        var temps = Live.Instance.Temps.Where(t => q.Length == 0
            || t.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || t.OriginalName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || t.Device.Contains(q, StringComparison.OrdinalIgnoreCase));
        var groups = temps.GroupBy(t => t.Device).Select(g => new TempGroup(g.Key, g)).ToList();
        Groups.ItemsSource = groups;
        Count.Text = $"{groups.Sum(g => g.Count)} temperatures on {groups.Count} devices";
    }

    void Filter_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => Fill();

    async void RescanFans_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => await App.Instance!.RescanFansAsync(XamlRoot);
}

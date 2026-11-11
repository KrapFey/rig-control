using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RigControl.Core;

namespace RigControl.Pages;

/// <summary>Everything RigControl knows how to handle, searchable (fuzzy) and filterable by kind.</summary>
public sealed partial class SupportedPage : Page
{
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(120) };

    public SupportedPage()
    {
        InitializeComponent();
        var all = SupportedDevices.List;
        Summary.Text = $"{all.Count:N0} devices and device families, from RigControl's own drivers and the open projects it builds on. " +
                       "Families (\"any VIA keyboard\") cover models that aren't named one by one.";
        _debounce.Tick += (_, _) => { _debounce.Stop(); Filter(); };
        Loaded += (_, _) => { Filter(); Query.Focus(FocusState.Programmatic); };
    }

    void Query_TextChanged(object sender, TextChangedEventArgs e) { _debounce.Stop(); _debounce.Start(); }
    void KindFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) Filter(); }

    void Filter()
    {
        var kind = KindFilter.SelectedIndex > 0 ? (string)KindFilter.SelectedItem : null;
        var hits = SupportedDevices.Search(Query.Text, kind);
        Results.ItemsSource = hits;
        Count.Text = hits.Count == 0
            ? "Nothing matches. Families like \"QMK keyboards with VIA\" or \"Logitech mice\" may still cover it."
            : $"{hits.Count:N0} match{(hits.Count == 1 ? "" : "es")}";
    }
}

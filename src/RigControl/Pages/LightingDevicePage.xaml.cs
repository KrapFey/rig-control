using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using RigControl.Core;

namespace RigControl.Pages;

/// <summary>One lighting device's settings: what it shows, its own effect or built-in effect settings, layout and zones.</summary>
public sealed partial class LightingDevicePage : Page
{
    string? _key;

    static App AppI => App.Instance!;

    /// <summary>The device (rebuilt when the device list changes, e.g. after resizing a header or a rescan).</summary>
    public DeviceCard? Card { get; private set; }

    public LightingDevicePage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            AppI.LightingStateChanged += Load;
            CompositionTarget.Rendering += RenderFrame;
        };
        Unloaded += (_, _) =>
        {
            AppI.LightingStateChanged -= Load;
            CompositionTarget.Rendering -= RenderFrame;
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _key = e.Parameter as string;
        Load();
    }

    void Load()
    {
        var targets = AppI.Lighting.Targets;
        var card = LightingPage.Cards(targets).FirstOrDefault(c => c.Key == _key);
        if (Card is not null) Card.PropertyChanged -= CardChanged;
        Card = card;
        Missing.Visibility = card is null ? Visibility.Visible : Visibility.Collapsed;
        Body.Visibility = card is null ? Visibility.Collapsed : Visibility.Visible;
        if (card is not null) card.PropertyChanged += CardChanged;
        Bindings.Update();
        ShowParts();
    }

    void CardChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // A new choice in "Shows": the effect editor and the zone list follow it.
        if (string.IsNullOrEmpty(e.PropertyName)) ShowParts();
    }

    void ShowParts()
    {
        if (Card is null) return;
        if (Card.OwnEffect is { } own) Editor.Effect = own;
        Card.ShowZones(ZonesHost);
        SourceNote.Text = Card.Selected switch
        {
            0 => "Shows the synced effect from the Lighting page, along with every other device set to it.",
            1 => "An effect just for this device. Change it below; the synced effect and other devices aren't affected.",
            _ when Card.OwnEffect is null && Card.HardwareVisibility == Visibility.Visible => "An effect the device runs by itself, with the settings it offers.",
            _ => "",
        };
    }

    void RenderFrame(object? sender, object e) => Card?.Render(AppI.Lighting.Frames.GetValueOrDefault(Card.Key));

    void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack(); else Frame.Navigate(typeof(LightingPage));
    }

    void Identify_Click(object sender, RoutedEventArgs e) => Card?.Identify();

    void PickColor_Click(object sender, RoutedEventArgs e) => Card?.PickColor(sender, e);
}

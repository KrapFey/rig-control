using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using RigControl.Core;

namespace RigControl.Pages;

/// <summary>All settings of one device (opened from a Devices tile).</summary>
public sealed partial class DevicePage : Page
{
    string? _key;
    PeripheralCard? _card;

    public DevicePage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            App.Instance!.Peripherals.Changed += OnChanged;
            App.Instance.Peripherals.Watch(true);
            Build();
        };
        Unloaded += (_, _) =>
        {
            App.Instance!.Peripherals.Changed -= OnChanged;
            App.Instance.Peripherals.Watch(false);
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) => _key = e.Parameter as string;

    void Build()
    {
        var hub = App.Instance!.Peripherals;
        var device = hub.Devices.FirstOrDefault(d => d.Key == _key);
        if (device is null) { Gone.Visibility = Visibility.Visible; CardHost.Content = null; _card = null; return; }
        Gone.Visibility = Visibility.Collapsed;
        _card = new PeripheralCard(device, hub.States.GetValueOrDefault(device.Key));
        CardHost.Content = _card;
    }

    void OnChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        var hub = App.Instance!.Peripherals;
        if (_card is not null && hub.Devices.Any(d => d.Key == _key)) _card.Update(hub.States.GetValueOrDefault(_key!));
        else Build(); // unplugged, or plugged back in
    });

    void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack(); else Frame.Navigate(typeof(DevicesPage));
    }

    // Device settings are only written for changes the user makes. While a card is being set up, a slider can
    // get its value before its range (5000 DPI clamped to the default maximum of 100), and that must never
    // reach the mouse: so ignore value changes unless the control has focus (being dragged, clicked, typed in).
    void Slider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (sender is not Slider { FocusState: not FocusState.Unfocused, DataContext: PeripheralCard card } s) return;
        switch (s.Tag as string)
        {
            case "dpi": card.Dpi = e.NewValue; break;
            case "sleep": card.SleepMinutes = e.NewValue; break;
            case "lowbat": card.LowBattery = e.NewValue; break;
            case "sidetone" or "micvolume" or "inactive" or "actuation" or "lbright" or "lspeed" or "lhue" or "lsat" or "backlight": card.Set((string)s.Tag, e.NewValue); break;
        }
    }

    // A NumberBox keeps focus on the text box inside it, so its own FocusState reads Unfocused even while you type.
    // Instead: once you've focused a box (clicked or tabbed into it), its committed values (Enter or clicking away) count.
    readonly HashSet<NumberBox> _touched = [];
    void Number_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is NumberBox box) _touched.Add(box);
    }

    void Dpi_NumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_touched.Contains(sender) || sender.DataContext is not PeripheralCard card || double.IsNaN(args.NewValue)) return;
        card.Dpi = args.NewValue;
    }

    void Stage_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_touched.Contains(sender) || sender.DataContext is not StageVm stage || double.IsNaN(args.NewValue)) return;
        stage.Value = args.NewValue;
    }

    void HeadsetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { FocusState: not FocusState.Unfocused, DataContext: PeripheralCard card, SelectedIndex: >= 0 } box)
            card.Set((string)box.Tag, box.SelectedIndex);
    }

    void HeadsetToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { FocusState: not FocusState.Unfocused, DataContext: PeripheralCard card } t)
            card.Set((string)t.Tag, t.IsOn ? 1 : 0);
    }

    void Poll_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { FocusState: not FocusState.Unfocused, DataContext: PeripheralCard card } box) card.PollIndex = box.SelectedIndex;
    }

    void Stage_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { FocusState: not FocusState.Unfocused, DataContext: StageVm stage }) stage.Active = true;
    }

}
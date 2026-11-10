using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using RigControl.Core;

namespace RigControl.Pages;

/// <summary>Two-way view of one fan's settings; every change goes straight to the engine and is saved.</summary>
public sealed class FanEdit(FanVm live, List<string> curveNames) : Observable
{
    public FanVm Live { get; } = live;
    public List<string> CurveNames { get; } = curveNames;

    bool _expanded;
    public bool Expanded { get => _expanded; set => Set(ref _expanded, value); }

    static readonly FanSettings Defaults = new();

    /// <summary>Read side: the saved settings or defaults; nothing is written until a value actually changes.</summary>
    FanSettings S => App.Instance!.Config.Fans.TryGetValue(Live.Id, out var s) ? s : Defaults;

    /// <summary>Write side: creates the entry on first change.</summary>
    FanSettings W
    {
        get
        {
            var fans = App.Instance!.Config.Fans;
            if (!fans.TryGetValue(Live.Id, out var s)) fans[Live.Id] = s = new FanSettings();
            return s;
        }
    }

    void Changed([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        Raise(name);
        App.Instance!.ConfigChanged();
        Live.Name = string.IsNullOrWhiteSpace(S.Name) ? Live.Channel.Name : S.Name!;
    }

    public string Name { get => S.Name ?? ""; set { var v = string.IsNullOrWhiteSpace(value) ? null : value; if (v == S.Name) return; W.Name = v; Changed(); } }
    public bool Enabled { get => S.Enabled; set { if (value == S.Enabled) return; W.Enabled = value; Changed(); } }
    public string? Curve
    {
        get => S.Curve;
        set { if (value is null || value == S.Curve) return; W.Curve = value; Changed(); } // ComboBox briefly pushes null while re-templating
    }
    public bool ManualOn
    {
        get => S.Manual is not null;
        set { if (value == ManualOn) return; W.Manual = value ? Math.Round(Live.Duty.Target) : null; Changed(); Raise(nameof(ManualValue)); }
    }
    public double ManualValue
    {
        get => S.Manual ?? Live.Duty.Target;
        set { if (S.Manual is null || Math.Round(value) == S.Manual) return; W.Manual = Math.Round(value); Changed(); }
    }
    public double Min { get => S.MinPercent; set { if (Clean(value) == S.MinPercent) return; W.MinPercent = Clean(value); Changed(); } }
    public double Start { get => S.StartPercent; set { if (Clean(value) == S.StartPercent) return; W.StartPercent = Clean(value); Changed(); } }
    public double Stop { get => S.StopPercent; set { if (Clean(value) == S.StopPercent) return; W.StopPercent = Clean(value); Changed(); } }
    public double StepUp { get => S.StepUp; set { if (Clean(value) == S.StepUp) return; W.StepUp = Clean(value); Changed(); } }
    public double StepDown { get => S.StepDown; set { if (Clean(value) == S.StepDown) return; W.StepDown = Clean(value); Changed(); } }

    static double Clean(double v) => double.IsNaN(v) ? 0 : Math.Clamp(v, 0, 100);

    public void Identify() => App.Instance!.Engine.Identify(Live.Id);

    /// <summary>Re-read everything after the settings were replaced underneath (swap).</summary>
    public void Reload()
    {
        Raise(string.Empty);
        Live.Name = string.IsNullOrWhiteSpace(S.Name) ? Live.Channel.Name : S.Name!;
    }
}

public sealed partial class FansPage : Page
{
    List<FanEdit> _all = [];
    string? _focus;

    public FansPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Live.Instance.Attach();
            var curves = App.Instance!.Config.Curves.Select(c => c.Name).ToList();
            _all = [.. Live.Instance.Fans.OrderByDescending(f => f.Present).ThenBy(f => f.Device).ThenBy(f => f.Name).Select(f => new FanEdit(f, curves))];
            Fill();
        };
        Unloaded += (_, _) => Live.Instance.Detach();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) => _focus = e.Parameter as string;

    void Fill()
    {
        var items = _all.Where(f => ShowEmpty.IsOn || f.Live.Present || f.Enabled || f.Live.Id == _focus).ToList();
        List.ItemsSource = items;
        if (_focus is not null && items.FirstOrDefault(f => f.Live.Id == _focus) is { } hit)
        {
            hit.Expanded = true;
            List.ScrollIntoView(hit);
        }
    }

    void ShowEmpty_Toggled(object sender, RoutedEventArgs e) { if (IsLoaded) Fill(); }

    // Built when opened, so it lists the current names.
    void SwapMenu_Opening(object sender, object e)
    {
        var menu = (MenuFlyout)sender;
        var me = (FanEdit)((FrameworkElement)menu.Target).Tag;
        menu.Items.Clear();
        foreach (var other in _all.Where(f => f != me && (f.Live.Present || f.Enabled)))
        {
            var item = new MenuFlyoutItem { Text = $"{other.Live.Name}  ·  {other.Live.Short}" };
            item.Click += (_, _) => Swap(me, other);
            menu.Items.Add(item);
        }
    }

    static void Swap(FanEdit a, FanEdit b)
    {
        var fans = App.Instance!.Config.Fans;
        fans.TryGetValue(a.Live.Id, out var sa);
        fans.TryGetValue(b.Live.Id, out var sb);
        fans.Remove(a.Live.Id);
        fans.Remove(b.Live.Id);
        if (sb is not null) fans[a.Live.Id] = sb;
        if (sa is not null) fans[b.Live.Id] = sa;
        App.Instance.ConfigChanged();
        a.Reload();
        b.Reload();
    }
}

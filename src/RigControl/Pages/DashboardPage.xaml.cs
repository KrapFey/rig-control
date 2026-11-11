using System.ComponentModel;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RigControl.Controls;
using RigControl.Core;

namespace RigControl.Pages;

public sealed partial class DashboardPage : Page
{
    readonly Live _live = Live.Instance;
    readonly List<(Smooth s, PropertyChangedEventHandler h)> _hooks = [];
    readonly Smooth _air = new(5);

    public DashboardPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        _live.Attach();
        FanGrid.ItemsSource = _live.Fans.OrderByDescending(f => f.Present).ThenBy(f => f.Device).ThenBy(f => f.Name).ToList();

        Hook(_live.Cpu, CpuGauge, CpuName);
        Hook(_live.Gpu, GpuGauge, GpuName);
        PropertyChangedEventHandler air = (_, _) => AirGauge.SetValue(_air.Value);
        _air.PropertyChanged += air;
        _hooks.Add((_air, air));

        _live.Refreshed += OnRefreshed;
        OnRefreshed();
        BuildNotices();
    }

    void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _live.Refreshed -= OnRefreshed;
        foreach (var (s, h) in _hooks) s.PropertyChanged -= h;
        _hooks.Clear();
        _live.Detach();
    }

    void Hook(TempVm? temp, Gauge gauge, TextBlock caption)
    {
        if (temp is null) { gauge.SetValue(0); caption.Text = "no sensor found"; return; }
        caption.Text = $"{temp.Name} · {temp.Device}";
        PropertyChangedEventHandler h = (_, _) => gauge.SetValue(temp.Value.Value);
        temp.Value.PropertyChanged += h;
        _hooks.Add((temp.Value, h));
        gauge.SetValue(temp.Value.Value);
    }

    void OnRefreshed()
    {
        var spinning = _live.Fans.Where(f => f.Present).ToList();
        var avg = spinning.Count == 0 ? 0 : spinning.Average(f => f.Duty.Target);
        _air.Target = avg;
        var driven = _live.Fans.Count(f => f.Active);
        AirText.Text = $"{spinning.Count} fans · {driven} on curves";
        Summary.Text = App.Instance!.Engine.Paused
            ? "FanControl is running, so RigControl is only watching. Exit FanControl to take over."
            : $"{_live.Temps.Count} sensors · {_live.Fans.Count} fan channels · updated every {App.Instance.Config.IntervalSeconds:0.#} s";
    }

    void BuildNotices()
    {
        Notices.Children.Clear();
        var app = App.Instance!;
        foreach (var note in app.StartupNotes.Concat(app.Hub.Problems))
            Notices.Children.Add(new InfoBar { IsOpen = true, Severity = InfoBarSeverity.Warning, Message = note });

        if (app.Config.Curves.Count == 0 && !app.Config.Fans.Values.Any(f => f.Enabled) && File.Exists(FanControlImport.DefaultPath))
        {
            var bar = new InfoBar
            {
                IsOpen = true,
                Severity = InfoBarSeverity.Informational,
                Title = "Coming from FanControl?",
                Message = "Bring over your curves, fan names and settings in one go.",
            };
            var button = new Button { Content = "Import FanControl setup", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            button.Click += async (_, _) =>
            {
                if (await SettingsPage.ImportFanControl(XamlRoot)) { bar.IsOpen = false; _live.Refresh(); }
            };
            bar.ActionButton = button;
            Notices.Children.Add(bar);
        }
    }

    void Tile_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FanVm vm)
            Frame.Navigate(typeof(FanPage), vm.Id, new Microsoft.UI.Xaml.Media.Animation.DrillInNavigationTransitionInfo());
    }

    // Staggered entrance: each tile rises and fades in a beat after the previous one (GPU composition).
    void FanGrid_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (_entered.Contains(args.Index)) return;
        _entered.Add(args.Index);
        var visual = ElementCompositionPreview.GetElementVisual(args.Element);
        ElementCompositionPreview.SetIsTranslationEnabled(args.Element, true);
        var comp = visual.Compositor;
        var ease = comp.CreateCubicBezierEasingFunction(new(0.2f, 0.9f), new(0.3f, 1f));
        var delay = TimeSpan.FromMilliseconds(Math.Min(args.Index, 20) * 35 + 120);

        var rise = comp.CreateScalarKeyFrameAnimation();
        rise.InsertKeyFrame(0, 46);
        rise.InsertKeyFrame(1, 0, ease);
        rise.Duration = TimeSpan.FromMilliseconds(520);
        rise.DelayTime = delay;
        rise.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

        var fade = comp.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, ease);
        fade.Duration = TimeSpan.FromMilliseconds(420);
        fade.DelayTime = delay;
        fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

        visual.StartAnimation("Translation.Y", rise);
        visual.StartAnimation("Opacity", fade);
    }

    readonly HashSet<int> _entered = [];

    // Hover lift: implicit scale animation on the composition layer.
    void Tile_PointerEntered(object sender, PointerRoutedEventArgs e) => Lift((UIElement)sender, 1.035f);
    void Tile_PointerExited(object sender, PointerRoutedEventArgs e) => Lift((UIElement)sender, 1f);

    // Composition only: the tile's visual is already owned by the entrance animation, so XAML Scale/CenterPoint are off limits.
    static void Lift(UIElement el, float scale)
    {
        var visual = ElementCompositionPreview.GetElementVisual(el);
        var size = el.ActualSize;
        visual.CenterPoint = new Vector3(size.X / 2, size.Y / 2, 0);
        var comp = visual.Compositor;
        var anim = comp.CreateVector3KeyFrameAnimation();
        anim.InsertKeyFrame(1, new Vector3(scale, scale, 1), comp.CreateCubicBezierEasingFunction(new(0.2f, 0.9f), new(0.3f, 1.2f)));
        anim.Duration = TimeSpan.FromMilliseconds(240);
        visual.StartAnimation("Scale", anim);
    }
}

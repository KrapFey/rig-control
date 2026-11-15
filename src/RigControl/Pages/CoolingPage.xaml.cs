using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using RigControl.Core;
using Windows.ApplicationModel.DataTransfer;

namespace RigControl.Pages;

/// <summary>A curve and the fans that follow it; with no curve, the fans that follow none (manual or the hardware's own control).</summary>
public sealed class CurveGroup(CurveDef? def) : Observable
{
    public CurveDef? Def { get; } = def;
    public ObservableCollection<FanVm> Members { get; } = [];

    public string Title => Def?.Name ?? "Not on a curve";
    public void Renamed() => Raise(nameof(Title));
    public string Glyph => Def?.Kind switch { CurveKind.Mix => "", CurveKind.Flat => "", null => "", _ => "" };


    string _detail = "", _output = "";
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public string Output { get => _output; set => Set(ref _output, value); }
    public string EmptyHint => Members.Count > 0 ? "" : Def is null ? "Every fan follows a curve." : "No fans yet: drag one here.";
    public void MembersChanged() => Raise(nameof(EmptyHint));

    bool _selected;
    public bool Selected { get => _selected; set { if (Set(ref _selected, value)) { Raise(nameof(Stroke)); Raise(nameof(StrokeThickness)); } } }
    public Brush Stroke => (Brush)Application.Current.Resources[Selected ? "AccentGradient" : "CardStroke"];
    public Thickness StrokeThickness => new(Selected ? 2 : 1);
}

public sealed record TempOption(string Id, string Label);

/// <summary>Two-way view of one fan's settings; every change goes straight to the engine and is saved.</summary>
public sealed class FanEdit(FanVm live, List<string> curveNames) : Observable
{
    public FanVm Live { get; } = live;
    public List<string> CurveNames { get; } = curveNames;

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

    /// <summary>Re-read everything after the settings were changed underneath (a swap, a drag onto a curve).</summary>
    public void Reload()
    {
        Raise(string.Empty);
        Live.Name = string.IsNullOrWhiteSpace(S.Name) ? Live.Channel.Name : S.Name!;
    }
}

/// <summary>
/// Everything about cooling on one page: the curves with the fans that follow them (drag a fan to another curve),
/// the selected curve's editor, and the selected fan's own settings beside it.
/// </summary>
public sealed partial class CoolingPage : Page
{
    readonly ObservableCollection<CurveGroup> _groups = [];
    readonly CurveGroup _unassigned = new(null);
    readonly List<ListView> _lists = [];
    List<TempOption> _temps = [];
    CurveGroup? _sel;
    FanEdit? _fan;
    string? _openFan, _openCurve; // asked for when navigating here (from the overview)
    bool _openSensors;
    bool _loading;

    static AppConfig Cfg => App.Instance!.Config;

    public CoolingPage()
    {
        InitializeComponent();
        Editor.PointsChanged += pts =>
        {
            if (_sel?.Def is null) return;
            _sel.Def.Points = pts;
            App.Instance!.ConfigChanged();
            if (!RowsMatch()) BuildPointRows(); // a drag, an undo, or typed values that got sorted or kept in range
        };
        Editor.UndoChanged += () => UndoButton.IsEnabled = Editor.CanUndo;
        Loaded += (_, _) =>
        {
            _temps = [.. App.Instance!.Hub.Temps.OrderBy(t => t.Device).ThenBy(t => t.Name).Select(t => new TempOption(t.Id, $"{App.Instance.SensorLabel(t)} · {t.Device}"))];
            Live.Instance.Attach();
            BuildGroups();
            Groups.ItemsSource = _groups;
            ShowProfiles();
            // Picked once loaded: before that, the bar resets itself to its first view.
            if (_openSensors) Views.SelectedItem = SensorsView;
            var fan = Live.Instance.Fans.FirstOrDefault(f => f.Id == _openFan);
            Select(_groups.FirstOrDefault(g => g.Def is not null && g.Def.Name == _openCurve)
                   ?? (fan is not null ? GroupOf(fan) : null)
                   ?? _groups.FirstOrDefault(g => g.Def is not null));
            if (fan is not null) ShowFan(fan);
            Live.Instance.Refreshed += OnLive;
            OnLive();
        };
        Unloaded += (_, _) => { Live.Instance.Refreshed -= OnLive; Live.Instance.Detach(); };
    }

    /// <summary>"curve:Name" selects that curve; a fan id opens that fan's settings; "sensors" opens the Sensors view.</summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not string p) return;
        if (p == "sensors") _openSensors = true;
        else if (p.StartsWith("curve:", StringComparison.Ordinal)) _openCurve = p["curve:".Length..];
        else _openFan = p;
    }

    // ---- the curves and their fans

    /// <summary>The fans worth listing: those plugged in, and any RigControl controls.</summary>
    static IEnumerable<FanVm> ListedFans() =>
        Live.Instance.Fans.Where(f => f.Present || Cfg.Fans.TryGetValue(f.Id, out var s) && s.Enabled);

    /// <summary>The curve a fan follows now, if any.</summary>
    static string? CurveOf(FanVm fan) =>
        Cfg.Fans.TryGetValue(fan.Id, out var s) && s is { Enabled: true, Manual: null } ? s.Curve : null;

    CurveGroup? GroupOf(FanVm fan) => _groups.FirstOrDefault(g => g.Def is not null && g.Def.Name == CurveOf(fan));

    void BuildGroups()
    {
        var keep = _groups.Where(g => g.Def is not null).ToDictionary(g => g.Def!);
        _groups.Clear();
        foreach (var c in Cfg.Curves) _groups.Add(keep.TryGetValue(c, out var g) ? g : new CurveGroup(c));
        _groups.Add(_unassigned);
        FillMembers();
    }

    /// <summary>Puts each fan in the card of the curve it follows (after any change to who follows what).</summary>
    void FillMembers()
    {
        foreach (var g in _groups) g.Members.Clear();
        foreach (var fan in ListedFans())
            ((_groups.FirstOrDefault(g => g.Def is not null && g.Def.Name == CurveOf(fan))) ?? _unassigned).Members.Add(fan);
        foreach (var g in _groups) g.MembersChanged();
        DispatcherQueue.TryEnqueue(MarkSelectedFan); // once the lists have their new items
        OnLive();
    }

    void OnLive()
    {
        var engine = App.Instance!.Engine;
        foreach (var g in _groups)
        {
            if (g.Def is not { } d) { g.Detail = "Manual, or the hardware's own control"; g.Output = ""; continue; }
            g.Output = engine.CurveOutputs.TryGetValue(d.Name, out var v) ? $"{v:0}%" : "–";
            g.Detail = d.Kind switch
            {
                CurveKind.Mix => $"{d.Mix} of {d.Inputs.Count} curve{(d.Inputs.Count == 1 ? "" : "s")}",
                CurveKind.Flat => "Fixed speed",
                _ => (App.Instance.Hub.Temps.FirstOrDefault(t => t.Id == d.Source) is { } t ? App.Instance.SensorLabel(t) : "No sensor")
                     + (engine.CurveInputs.TryGetValue(d.Name, out var i) ? $" · {i:0}°" : ""),
            };
        }
        if (_sel?.Def?.Kind == CurveKind.Graph)
        {
            double? input = engine.CurveInputs.TryGetValue(_sel.Def.Name, out var i) ? i : null;
            double? output = engine.CurveOutputs.TryGetValue(_sel.Def.Name, out var o) ? o : null;
            Editor.SetLive(input, output);
        }
    }

    void Group_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CurveGroup { Def: not null } g) Select(g);
    }

    void Select(CurveGroup? g)
    {
        if (g?.Def is null) g = null;
        foreach (var x in _groups) x.Selected = x == g;
        _sel = g;
        ShowEditor();
        OnLive();
    }

    // ---- dragging a fan onto a curve

    void Fans_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.FirstOrDefault() is not FanVm fan) { e.Cancel = true; return; }
        e.Data.SetText(fan.Id);
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    void Group_DragOver(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CurveGroup g || !e.DataView.Contains(StandardDataFormats.Text)) return;
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.Caption = g.Def is null ? "Take it off its curve" : $"Follow \"{g.Title}\"";
    }

    async void Group_Drop(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CurveGroup g || !e.DataView.Contains(StandardDataFormats.Text)) return;
        var deferral = e.GetDeferral();
        var id = await e.DataView.GetTextAsync();
        deferral.Complete();
        if (Live.Instance.Fans.FirstOrDefault(f => f.Id == id) is { } fan) Assign(fan, g);
    }

    /// <summary>Makes a fan follow a group's curve; the no-curve group hands it back to the hardware.</summary>
    void Assign(FanVm fan, CurveGroup g)
    {
        if (!Cfg.Fans.TryGetValue(fan.Id, out var fs)) Cfg.Fans[fan.Id] = fs = new FanSettings();
        if (g.Def is { } d) { fs.Enabled = true; fs.Manual = null; fs.Curve = d.Name; }
        else fs.Curve = null; // no curve: back to the hardware's own control (a fixed speed stays)
        App.Instance!.ConfigChanged();
        FillMembers();
        if (_fan?.Live == fan) _fan.Reload();
    }

    // ---- the selected fan

    void Fans_Loaded(object sender, RoutedEventArgs e)
    {
        var list = (ListView)sender;
        if (!_lists.Contains(list)) _lists.Add(list);
        MarkSelectedFan();
    }

    void Fan_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FanVm fan) ShowFan(fan);
    }

    void ShowFan(FanVm fan)
    {
        if (_fan is not null) _fan.PropertyChanged -= FanChanged;
        _fan = new FanEdit(fan, [.. Cfg.Curves.Select(c => c.Name)]);
        _fan.PropertyChanged += FanChanged;
        FanHost.Content = _fan;
        FanCard.Visibility = Visibility.Visible;
        FanColumn.Width = new GridLength(320);
        MarkSelectedFan();
    }

    /// <summary>The panel changed who the fan follows: move it to its new card.</summary>
    void FanChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FanEdit.Curve) or nameof(FanEdit.Enabled) or nameof(FanEdit.ManualOn)) FillMembers();
    }

    void CloseFan_Click(object sender, RoutedEventArgs e)
    {
        if (_fan is not null) _fan.PropertyChanged -= FanChanged;
        _fan = null;
        FanHost.Content = null;
        FanCard.Visibility = Visibility.Collapsed;
        FanColumn.Width = new GridLength(0);
        MarkSelectedFan();
    }

    /// <summary>Highlights the open fan in whichever card holds it now.</summary>
    void MarkSelectedFan()
    {
        foreach (var list in _lists)
            list.SelectedItem = _fan is not null && list.Items.Contains(_fan.Live) ? _fan.Live : null;
    }

    // Built when opened, so it lists the current names.
    void SwapMenu_Opening(object sender, object e)
    {
        var menu = (MenuFlyout)sender;
        menu.Items.Clear();
        if (_fan is not { } fan) return;
        foreach (var other in ListedFans().Where(f => f != fan.Live))
        {
            var item = new MenuFlyoutItem { Text = $"{other.Name}  ·  {other.Short}" };
            item.Click += (_, _) => Swap(fan, other);
            menu.Items.Add(item);
        }
    }

    /// <summary>Exchanges two fans' names, curves and settings (fixes mixed-up identical controllers).</summary>
    void Swap(FanEdit a, FanVm b)
    {
        var fans = Cfg.Fans;
        fans.TryGetValue(a.Live.Id, out var sa);
        fans.TryGetValue(b.Id, out var sb);
        fans.Remove(a.Live.Id);
        fans.Remove(b.Id);
        if (sb is not null) fans[a.Live.Id] = sb;
        if (sa is not null) fans[b.Id] = sa;
        App.Instance!.ConfigChanged();
        a.Reload();
        new FanEdit(b, a.CurveNames).Reload(); // its name on its row
        FillMembers();
    }

    void Rescan_Click(object sender, RoutedEventArgs e) => _ = App.Instance!.RescanFansAsync();

    // ---- cooling profiles (each its own curves and fan settings; the page is rebuilt on a change)

    bool _showingProfiles;

    void ShowProfiles()
    {
        _showingProfiles = true;
        ProfileBox.ItemsSource = Cfg.CoolingProfileNames();
        ProfileBox.SelectedItem = Cfg.ActiveCooling;
        DeleteProfileItem.IsEnabled = Cfg.CoolingProfiles.Count > 0;
        _showingProfiles = false;
    }

    void ProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_showingProfiles || ProfileBox.SelectedItem is not string name || name == Cfg.ActiveCooling) return;
        Cfg.SwitchCooling(name);
        App.Instance!.CoolingProfileChanged();
    }

    // ---- presets: ready-made curve shapes for each kind of sensor (CoolingPresets)

    static string? SensorName(string id) => App.Instance!.Hub.Temps.FirstOrDefault(t => t.Id == id)?.Name;

    /// <summary>A new profile: a copy of the one in use with every graph curve shaped like the preset.</summary>
    async void PresetProfile_Click(object sender, RoutedEventArgs e)
    {
        var preset = Enum.Parse<CoolingPreset>((string)((FrameworkElement)sender).Tag);
        if (!Cfg.Curves.Any(c => c.Kind == CurveKind.Graph))
        {
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "No curves to shape yet",
                Content = "A preset reshapes your temperature curves and keeps which fan follows which. Make a curve first (New curve), or import your FanControl setup.",
                CloseButtonText = "OK",
            }.ShowAsync();
            return;
        }
        if (await Dialogs.AskName(XamlRoot, $"New {preset} profile", Dialogs.Unique(preset.ToString(), Cfg.CoolingProfileNames())) is not { } name) return;
        Cfg.AddCooling(Dialogs.Unique(name, Cfg.CoolingProfileNames()));
        int n = CoolingPresets.ApplyAll(Cfg.Curves, preset, SensorName);
        App.Log($"cooling profile \"{Cfg.ActiveCooling}\" made from the {preset} preset ({n} curves reshaped)");
        App.Instance!.CoolingProfileChanged();
    }

    /// <summary>This curve only, shaped like the preset for its sensor; Undo takes the points back.</summary>
    void ShapeCurve_Click(object sender, RoutedEventArgs e)
    {
        if (_sel?.Def is not { Kind: CurveKind.Graph } d) return;
        var preset = Enum.Parse<CoolingPreset>((string)((FrameworkElement)sender).Tag);
        Editor.SetTypedPoints(CoolingPresets.Shape(CoolingPresets.KindOf(d.Source, d.Source is null ? null : SensorName(d.Source)), preset));
        (DeadbandBox.Value, ResponseBox.Value) = CoolingPresets.Tuning(preset); // saved through their own change handler
    }

    async void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        if (await Dialogs.AskName(XamlRoot, "New cooling profile", Dialogs.Unique($"{Cfg.ActiveCooling} copy", Cfg.CoolingProfileNames())) is not { } name) return;
        Cfg.AddCooling(Dialogs.Unique(name, Cfg.CoolingProfileNames()));
        App.Instance!.CoolingProfileChanged();
    }

    async void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        if (await Dialogs.AskName(XamlRoot, "Rename cooling profile", Cfg.ActiveCooling) is not { } name || name == Cfg.ActiveCooling) return;
        Cfg.ActiveCooling = Dialogs.Unique(name, Cfg.CoolingProfileNames());
        App.Instance!.CoolingProfileChanged();
    }

    async void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (Cfg.CoolingProfiles.Count == 0) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Delete \"{Cfg.ActiveCooling}\"?",
            Content = $"Its curves and fan settings go with it. RigControl switches to \"{Cfg.CoolingProfiles[0].Name}\".",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        Cfg.DeleteCooling();
        App.Instance!.CoolingProfileChanged();
    }

    const string CurvesCaption = "Curves turn temperatures into fan speeds. Drag a fan onto a curve to make it follow, or click it for its settings.";

    /// <summary>Curves (with their fans) or Sensors (every temperature, to rename).</summary>
    void Views_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => ShowView(sender.SelectedItem == SensorsView);

    void ShowView(bool sensors)
    {
        if (sensors && SensorsFrame.Content is null) SensorsFrame.Navigate(typeof(SensorsPage));
        SensorsFrame.Visibility = sensors ? Visibility.Visible : Visibility.Collapsed;
        CurvesColumn.Visibility = EditorCard.Visibility = sensors ? Visibility.Collapsed : Visibility.Visible;
        FanCard.Visibility = !sensors && _fan is not null ? Visibility.Visible : Visibility.Collapsed;
        FanColumn.Width = new GridLength(!sensors && _fan is not null ? 320 : 0);
        PageCaption.Text = sensors ? "Rename a sensor and the new name is used everywhere: curves, the overview, temperature lighting." : CurvesCaption;
    }

    // ---- the selected curve

    /// <summary>A narrow editor (the fan panel is open) puts deadband and response under the name and sensor.</summary>
    void EditorHeader_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < 620;
        ((Grid)sender).RowSpacing = narrow ? 12 : 0;
        Grid.SetRow(DeadbandBox, narrow ? 1 : 0);
        Grid.SetColumn(DeadbandBox, narrow ? 0 : 2);
        Grid.SetRow(ResponseBox, narrow ? 1 : 0);
        Grid.SetColumn(ResponseBox, narrow ? 1 : 3);
        Grid.SetRow(ShapeButton, narrow ? 1 : 0);
        Grid.SetColumn(ShapeButton, narrow ? 2 : 4);
    }

    // ---- the curve's points as numbers

    readonly List<(NumberBox temp, NumberBox percent)> _pointRows = [];
    bool _buildingRows;

    /// <summary>A row per point: temperature and fan speed, typed exactly; a point can be removed while two remain.</summary>
    void BuildPointRows()
    {
        _buildingRows = true;
        PointRows.Children.Clear();
        _pointRows.Clear();
        var points = Editor.Points;
        for (int i = 0; i < points.Count; i++)
        {
            var temp = new NumberBox { Value = points[i].Temp, Minimum = 20, Maximum = 100, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 120 };
            var percent = new NumberBox { Value = points[i].Percent, Minimum = 0, Maximum = 100, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 120 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(temp, $"Point {i + 1} temperature, °C");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(percent, $"Point {i + 1} fan speed, %");
            temp.ValueChanged += (_, _) => ApplyTypedPoints();
            percent.ValueChanged += (_, _) => ApplyTypedPoints();
            int index = i;
            var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 11 }, IsEnabled = points.Count > 2, Padding = new Thickness(8, 6, 8, 6) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, $"Remove point {i + 1}");
            ToolTipService.SetToolTip(remove, "Remove this point");
            remove.Click += (_, _) => Editor.SetTypedPoints(Editor.Points.Where((_, k) => k != index));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(temp);
            row.Children.Add(new TextBlock { Text = "°C  →", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 });
            row.Children.Add(percent);
            row.Children.Add(new TextBlock { Text = "%", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 });
            row.Children.Add(remove);
            PointRows.Children.Add(row);
            _pointRows.Add((temp, percent));
        }
        _buildingRows = false;
    }

    IEnumerable<CurvePoint> TypedPoints() =>
        _pointRows.Where(r => !double.IsNaN(r.temp.Value) && !double.IsNaN(r.percent.Value)).Select(r => new CurvePoint(r.temp.Value, r.percent.Value));

    /// <summary>The rows show exactly the curve's points (then they needn't be rebuilt, and focus stays where it is).</summary>
    bool RowsMatch() => TypedPoints().SequenceEqual(Editor.Points);

    void ApplyTypedPoints()
    {
        if (_buildingRows || _sel?.Def?.Kind != CurveKind.Graph) return;
        Editor.SetTypedPoints(TypedPoints());
    }

    /// <summary>A new point in the middle of the widest gap between two points.</summary>
    void AddPoint_Click(object sender, RoutedEventArgs e)
    {
        var p = Editor.Points;
        int gap = Enumerable.Range(1, p.Count - 1).MaxBy(i => p[i].Temp - p[i - 1].Temp);
        var mid = new CurvePoint(Math.Round((p[gap].Temp + p[gap - 1].Temp) / 2), Math.Round((p[gap].Percent + p[gap - 1].Percent) / 2));
        if (p.Any(x => x.Temp == mid.Temp)) return; // the points are a degree apart everywhere: no room
        Editor.SetTypedPoints(p.Append(mid));
    }

    void Undo_Click(object sender, RoutedEventArgs e) => Editor.Undo();

    /// <summary>Ctrl+Z: the curve's, unless you're typing (a text box undoes its own text).</summary>
    void Undo_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox) return;
        Editor.Undo();
        args.Handled = true;
    }

    void ShowEditor()
    {
        _loading = true;
        var d = _sel?.Def;
        Empty.Visibility = d is null ? Visibility.Visible : Visibility.Collapsed;
        ImportButton.Visibility = File.Exists(FanControlImport.DefaultPath) ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.IsEnabled = d is not null;
        bool graph = d?.Kind == CurveKind.Graph, mix = d?.Kind == CurveKind.Mix, flat = d?.Kind == CurveKind.Flat;
        NameBox.Visibility = d is null ? Visibility.Collapsed : Visibility.Visible;
        Editor.Visibility = SourceBox.Visibility = DeadbandBox.Visibility = ResponseBox.Visibility = UndoButton.Visibility = PointsExpander.Visibility = ShapeButton.Visibility = graph ? Visibility.Visible : Visibility.Collapsed;
        MixPanel.Visibility = MixBox.Visibility = mix ? Visibility.Visible : Visibility.Collapsed;
        FlatPanel.Visibility = flat ? Visibility.Visible : Visibility.Collapsed;
        if (d is null) { _loading = false; return; }

        NameBox.Text = d.Name;
        if (graph)
        {
            var opts = _temps.ToList();
            if (d.Source is not null && opts.All(o => o.Id != d.Source)) opts.Insert(0, new(d.Source, $"Missing sensor ({d.Source})"));
            SourceBox.ItemsSource = opts;
            SourceBox.SelectedItem = opts.FirstOrDefault(o => o.Id == d.Source);
            DeadbandBox.Value = d.Deadband;
            ResponseBox.Value = d.ResponseSeconds;
            Editor.SetPoints(d.Points);
            BuildPointRows();
        }
        if (mix)
        {
            MixBox.SelectedIndex = (int)d.Mix;
            MixInputs.Items.Clear();
            foreach (var other in Cfg.Curves.Where(c => c != d))
            {
                var cb = new CheckBox { Content = other.Name, IsChecked = d.Inputs.Contains(other.Name) };
                cb.Checked += (_, _) => { if (!d.Inputs.Contains(other.Name)) d.Inputs.Add(other.Name); App.Instance!.ConfigChanged(); };
                cb.Unchecked += (_, _) => { d.Inputs.Remove(other.Name); App.Instance!.ConfigChanged(); };
                MixInputs.Items.Add(cb);
            }
        }
        if (flat)
        {
            FlatSlider.Value = d.Percent;
            FlatText.Text = $"{d.Percent:0}%";
        }
        _loading = false;
    }

    /// <summary>The fan panel's curve list changes with the curves: open it again with the new names.</summary>
    void RefreshFanPanel()
    {
        if (_fan is { } f) ShowFan(f.Live);
    }

    void NameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_sel?.Def is not { } d) return;
        var old = d.Name;
        var name = NameBox.Text.Trim();
        if (name.Length == 0 || name == old) { NameBox.Text = old; return; }
        name = Unique(name);
        // Keep everything that pointed at the old name pointing at the curve.
        foreach (var f in Cfg.Fans.Values.Where(f => f.Curve == old)) f.Curve = name;
        foreach (var c in Cfg.Curves) c.Inputs = [.. c.Inputs.Select(i => i == old ? name : i)];
        d.Name = name;
        _sel.Renamed();
        NameBox.Text = name;
        App.Instance!.ConfigChanged();
        RefreshFanPanel();
    }

    void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _sel?.Def is null || SourceBox.SelectedItem is not TempOption o) return;
        _sel.Def.Source = o.Id;
        App.Instance!.ConfigChanged();
    }

    void Tuning_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || _sel?.Def is null || double.IsNaN(args.NewValue)) return;
        if (sender == DeadbandBox) _sel.Def.Deadband = args.NewValue; else _sel.Def.ResponseSeconds = args.NewValue;
        App.Instance!.ConfigChanged();
    }

    void MixBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _sel?.Def is null || MixBox.SelectedIndex < 0) return;
        _sel.Def.Mix = (MixFunction)MixBox.SelectedIndex;
        App.Instance!.ConfigChanged();
    }

    void FlatSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        FlatText.Text = $"{e.NewValue:0}%";
        if (_loading || _sel?.Def is null) return;
        _sel.Def.Percent = e.NewValue;
        App.Instance!.ConfigChanged();
    }

    void New_Click(object sender, RoutedEventArgs e)
    {
        var kind = Enum.Parse<CurveKind>((string)((FrameworkElement)sender).Tag);
        var def = new CurveDef { Name = Unique(kind == CurveKind.Graph ? "New curve" : $"New {kind.ToString().ToLower()}"), Kind = kind };
        if (kind == CurveKind.Graph)
        {
            def.Source = Live.Instance.Cpu?.Id ?? _temps.FirstOrDefault()?.Id;
            def.Points = [new(35, 30), new(60, 50), new(85, 100)];
            def.Deadband = 2;
            def.ResponseSeconds = 2;
        }
        if (kind == CurveKind.Flat) def.Percent = 50;
        Cfg.Curves.Add(def);
        App.Instance!.ConfigChanged();
        BuildGroups();
        Select(_groups.First(g => g.Def == def));
        RefreshFanPanel();
    }

    async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_sel?.Def is not { } d) return;
        var name = d.Name;
        var users = Cfg.Fans.Values.Count(f => f.Curve == name);
        var message = users == 0 ? "No fan uses it." : $"{users} fan(s) use it; they go back to the hardware's own control.";
        // A mix of only this curve is left with nothing to mix, and an empty mix runs its fans at 100% (fail-safe).
        var emptied = Cfg.Curves.Where(c => c.Kind == CurveKind.Mix && c.Inputs.Count > 0 && c.Inputs.All(i => i == name)).Select(c => $"\"{c.Name}\"").ToList();
        if (emptied.Count > 0)
            message += $"\n\nThe mix curve{(emptied.Count > 1 ? "s" : "")} {string.Join(", ", emptied)} use{(emptied.Count > 1 ? "" : "s")} only this one: with nothing left to mix, fans on {(emptied.Count > 1 ? "them" : "it")} run at 100%.";
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Delete \"{name}\"?",
            Content = message,
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        foreach (var f in Cfg.Fans.Values.Where(f => f.Curve == name)) f.Curve = null;
        foreach (var c in Cfg.Curves) c.Inputs.Remove(name);
        Cfg.Curves.Remove(d);
        App.Instance!.ConfigChanged();
        BuildGroups();
        Select(_groups.FirstOrDefault(g => g.Def is not null));
        RefreshFanPanel();
    }

    /// <summary>The starter setup: curves for this PC and a guess at which fan follows which, confirmed first.</summary>
    async void Setup_Click(object sender, RoutedEventArgs e) => await StarterSetupDialog.RunAsync(XamlRoot);

    async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (!await SettingsPage.ImportFanControl(XamlRoot)) return;
        Live.Instance.Refresh(); // imported names
        BuildGroups();
        Select(_groups.FirstOrDefault(g => g.Def is not null));
    }

    static string Unique(string name)
    {
        var taken = Cfg.Curves.Select(c => c.Name).ToHashSet();
        if (!taken.Contains(name)) return name;
        for (int i = 2; ; i++) if (!taken.Contains($"{name} {i}")) return $"{name} {i}";
    }
}

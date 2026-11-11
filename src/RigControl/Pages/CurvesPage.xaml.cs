using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RigControl.Core;

namespace RigControl.Pages;

public sealed class CurveItem(CurveDef def) : Observable
{
    public CurveDef Def { get; } = def;
    public string Name { get => Def.Name; set { Def.Name = value; Raise(); } }
    public string Kind => Def.Kind switch { CurveKind.Mix => $"Mix · {Def.Mix}", CurveKind.Flat => "Flat", _ => "Graph" };
    public string Glyph => Def.Kind switch { CurveKind.Mix => "", CurveKind.Flat => "", _ => "" };
    string _output = "–";
    public string Output { get => _output; set => Set(ref _output, value); }
    public void KindChanged() { Raise(nameof(Kind)); Raise(nameof(Glyph)); }
}

public sealed record TempOption(string Id, string Label);

public sealed partial class CurvesPage : Page
{
    readonly ObservableCollection<CurveItem> _items = [];
    List<TempOption> _temps = [];
    CurveItem? _sel;
    bool _loading;

    static AppConfig Cfg => App.Instance!.Config;

    public CurvesPage()
    {
        InitializeComponent();
        Editor.PointsChanged += pts => { if (_sel is null) return; _sel.Def.Points = pts; App.Instance!.ConfigChanged(); };
        Loaded += (_, _) =>
        {
            _temps = [.. App.Instance!.Hub.Temps.OrderBy(t => t.Device).ThenBy(t => t.Name).Select(t => new TempOption(t.Id, $"{App.Instance.SensorLabel(t)} · {t.Device}"))];
            foreach (var c in Cfg.Curves) _items.Add(new CurveItem(c));
            CurveList.ItemsSource = _items;
            CurveList.SelectedIndex = _items.Count > 0 ? 0 : -1;
            ShowEditor();
            Live.Instance.Attach();
            Live.Instance.Refreshed += OnLive;
            OnLive();
        };
        Unloaded += (_, _) => { Live.Instance.Refreshed -= OnLive; Live.Instance.Detach(); };
    }

    void OnLive()
    {
        var engine = App.Instance!.Engine;
        foreach (var it in _items)
            it.Output = engine.CurveOutputs.TryGetValue(it.Name, out var v) ? $"{v:0}%" : "–";
        if (_sel?.Def.Kind == CurveKind.Graph)
        {
            double? input = engine.CurveInputs.TryGetValue(_sel.Name, out var i) ? i : null;
            double? output = engine.CurveOutputs.TryGetValue(_sel.Name, out var o) ? o : null;
            Editor.SetLive(input, output);
        }
    }

    void CurveList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _sel = CurveList.SelectedItem as CurveItem;
        ShowEditor();
        OnLive();
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
        Editor.Visibility = SourceBox.Visibility = DeadbandBox.Visibility = ResponseBox.Visibility = graph ? Visibility.Visible : Visibility.Collapsed;
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

    void NameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_sel is null) return;
        var old = _sel.Name;
        var name = NameBox.Text.Trim();
        if (name.Length == 0 || name == old) { NameBox.Text = old; return; }
        name = Unique(name);
        // Keep everything that pointed at the old name pointing at the curve.
        foreach (var f in Cfg.Fans.Values.Where(f => f.Curve == old)) f.Curve = name;
        foreach (var c in Cfg.Curves) c.Inputs = [.. c.Inputs.Select(i => i == old ? name : i)];
        _sel.Name = name;
        NameBox.Text = name;
        App.Instance!.ConfigChanged();
    }

    void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _sel is null || SourceBox.SelectedItem is not TempOption o) return;
        _sel.Def.Source = o.Id;
        App.Instance!.ConfigChanged();
    }

    void Tuning_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || _sel is null || double.IsNaN(args.NewValue)) return;
        if (sender == DeadbandBox) _sel.Def.Deadband = args.NewValue; else _sel.Def.ResponseSeconds = args.NewValue;
        App.Instance!.ConfigChanged();
    }

    void MixBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _sel is null || MixBox.SelectedIndex < 0) return;
        _sel.Def.Mix = (MixFunction)MixBox.SelectedIndex;
        _sel.KindChanged();
        App.Instance!.ConfigChanged();
    }

    void FlatSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        FlatText.Text = $"{e.NewValue:0}%";
        if (_loading || _sel is null) return;
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
        var item = new CurveItem(def);
        _items.Add(item);
        CurveList.SelectedItem = item;
        App.Instance!.ConfigChanged();
    }

    async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_sel is null) return;
        var name = _sel.Name;
        var users = Cfg.Fans.Values.Count(f => f.Curve == name);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Delete \"{name}\"?",
            Content = users == 0 ? "No fan uses it." : $"{users} fan(s) use it; they go back to the hardware's own control.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        foreach (var f in Cfg.Fans.Values.Where(f => f.Curve == name)) f.Curve = null;
        foreach (var c in Cfg.Curves) c.Inputs.Remove(name);
        Cfg.Curves.Remove(_sel.Def);
        _items.Remove(_sel);
        CurveList.SelectedIndex = _items.Count > 0 ? 0 : -1;
        App.Instance!.ConfigChanged();
    }

    async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (!await SettingsPage.ImportFanControl(XamlRoot)) return;
        _items.Clear();
        foreach (var c in Cfg.Curves) _items.Add(new CurveItem(c));
        CurveList.SelectedIndex = _items.Count > 0 ? 0 : -1;
        ShowEditor();
    }

    static string Unique(string name)
    {
        var taken = Cfg.Curves.Select(c => c.Name).ToHashSet();
        if (!taken.Contains(name)) return name;
        for (int i = 2; ; i++) if (!taken.Contains($"{name} {i}")) return $"{name} {i}";
    }
}

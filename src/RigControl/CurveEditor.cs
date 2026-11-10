using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using RigControl.Core;
using Windows.Foundation;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace RigControl.Controls;

/// <summary>
/// Drag points to shape a temperature -> fan % curve. Double-click adds a point, right-click removes one.
/// A glowing marker shows where the curve is right now and glides as the temperature moves.
/// </summary>
public sealed class CurveEditor : Grid
{
    const double TMin = 20, TMax = 100, Pad = 40;
    readonly Canvas _canvas = new() { Background = new SolidColorBrush(Colors.Transparent) };
    readonly Path _area = new();
    readonly Polyline _line = new() { StrokeThickness = 3, StrokeLineJoin = PenLineJoin.Round };
    readonly List<Ellipse> _thumbs = [];
    readonly Ellipse _marker = new() { Width = 14, Height = 14, Fill = new SolidColorBrush(Colors.White) };
    readonly Ellipse _markerRing = new() { Width = 14, Height = 14, StrokeThickness = 2 };
    readonly Line _markerDrop = new() { StrokeThickness = 1, StrokeDashArray = [3, 3], Stroke = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)) };
    readonly TextBlock _markerText = new() { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    readonly Smooth _mx = new(6), _my = new(6);
    List<CurvePoint> _points = [new(30, 30), new(80, 100)];
    int _drag = -1;
    bool _hasMarker;

    public event Action<List<CurvePoint>>? PointsChanged;

    public CurveEditor()
    {
        MinHeight = 320;
        Children.Add(_canvas);
        var accent = (Brush)Application.Current.Resources["AccentGradient"];
        _line.Stroke = accent;
        _area.Fill = new LinearGradientBrush
        {
            StartPoint = new(0, 0),
            EndPoint = new(0, 1),
            GradientStops =
            {
                new GradientStop { Offset = 0, Color = Color.FromArgb(90, 124, 92, 255) },
                new GradientStop { Offset = 1, Color = Color.FromArgb(0, 0, 212, 255) },
            },
        };
        _markerRing.Stroke = new SolidColorBrush(Color.FromArgb(255, 0, 212, 255));
        _markerRing.RenderTransform = new ScaleTransform { CenterX = 7, CenterY = 7 };

        SizeChanged += (_, _) => Redraw();
        _canvas.PointerMoved += OnMove;
        _canvas.PointerReleased += (_, e) => EndDrag(e);
        _canvas.PointerCanceled += (_, e) => EndDrag(e);
        _canvas.DoubleTapped += OnDoubleTap;

        PropertyChangedEventHandler moveMarker = (_, _) => PlaceMarker();
        _mx.PropertyChanged += moveMarker;
        _my.PropertyChanged += moveMarker;

        // Pulsing ring around the live marker.
        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        foreach (var prop in new[] { "ScaleX", "ScaleY" })
        {
            var a = new DoubleAnimation { From = 1, To = 2.8, Duration = TimeSpan.FromSeconds(1.6), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(a, _markerRing.RenderTransform);
            Storyboard.SetTargetProperty(a, prop);
            sb.Children.Add(a);
        }
        var fade = new DoubleAnimation { From = 0.9, To = 0, Duration = TimeSpan.FromSeconds(1.6) };
        Storyboard.SetTarget(fade, _markerRing);
        Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);
        Loaded += (_, _) => sb.Begin();
        Unloaded += (_, _) => sb.Stop();
    }

    public void SetPoints(IEnumerable<CurvePoint> points)
    {
        _points = [.. points.OrderBy(p => p.Temp)];
        if (_points.Count < 2) _points = [new(30, 30), new(80, 100)];
        Redraw();
    }

    /// <summary>Where the curve is now: input temperature and resulting percent.</summary>
    public void SetLive(double? temp, double? percent)
    {
        _hasMarker = temp is not null && percent is not null;
        if (!_hasMarker) { PlaceMarker(); return; }
        if (_mx.Value == 0 && _my.Value == 0) { _mx.Snap(temp!.Value); _my.Snap(percent!.Value); }
        _mx.Target = temp!.Value;
        _my.Target = percent!.Value;
    }

    double W => Math.Max(ActualWidth - Pad * 1.5, 10);
    double H => Math.Max(ActualHeight - Pad * 1.5, 10);
    Point ToScreen(double t, double p) => new(Pad + (t - TMin) / (TMax - TMin) * W, Pad * 0.5 + (1 - p / 100) * H);
    (double t, double p) FromScreen(Point s) => (TMin + (s.X - Pad) / W * (TMax - TMin), (1 - (s.Y - Pad * 0.5) / H) * 100);

    void Redraw()
    {
        if (ActualWidth <= 0 || _points.Count == 0) return;
        _canvas.Children.Clear();
        var grid = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255));
        var label = new SolidColorBrush(Color.FromArgb(130, 255, 255, 255));
        for (int t = (int)TMin; t <= TMax; t += 10)
        {
            var a = ToScreen(t, 0); var b = ToScreen(t, 100);
            _canvas.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = grid });
            var tb = new TextBlock { Text = $"{t}°", FontSize = 11, Foreground = label };
            Canvas.SetLeft(tb, a.X - 9); Canvas.SetTop(tb, a.Y + 6);
            _canvas.Children.Add(tb);
        }
        for (int p = 0; p <= 100; p += 20)
        {
            var a = ToScreen(TMin, p); var b = ToScreen(TMax, p);
            _canvas.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = grid });
            var tb = new TextBlock { Text = $"{p}%", FontSize = 11, Foreground = label };
            Canvas.SetLeft(tb, 2); Canvas.SetTop(tb, a.Y - 8);
            _canvas.Children.Add(tb);
        }

        // Line spans the whole range: flat before the first and after the last point (that's how it runs).
        var pts = new List<Point> { ToScreen(TMin, _points[0].Percent) };
        pts.AddRange(_points.Select(p => ToScreen(p.Temp, p.Percent)));
        pts.Add(ToScreen(TMax, _points[^1].Percent));
        _line.Points.Clear();
        foreach (var p in pts) _line.Points.Add(p);
        var fig = new PathFigure { StartPoint = ToScreen(TMin, 0), IsClosed = true };
        foreach (var p in pts) fig.Segments.Add(new LineSegment { Point = p });
        fig.Segments.Add(new LineSegment { Point = ToScreen(TMax, 0) });
        _area.Data = new PathGeometry { Figures = { fig } };
        _canvas.Children.Add(_area);
        _canvas.Children.Add(_line);

        _canvas.Children.Add(_markerDrop);
        _canvas.Children.Add(_markerRing);
        _canvas.Children.Add(_marker);
        _canvas.Children.Add(_markerText);

        _thumbs.Clear();
        for (int i = 0; i < _points.Count; i++)
        {
            var thumb = Thumb(i);
            _thumbs.Add(thumb);
            _canvas.Children.Add(thumb);
        }
        PlaceThumbs();
        PlaceMarker();
    }

    Ellipse Thumb(int index)
    {
        var e = new Ellipse
        {
            Width = 16, Height = 16,
            Fill = new SolidColorBrush(Color.FromArgb(255, 20, 18, 40)),
            Stroke = (Brush)Application.Current.Resources["AccentGradient"],
            StrokeThickness = 3,
            RenderTransform = new ScaleTransform { CenterX = 8, CenterY = 8 },
        };
        ToolTipService.SetToolTip(e, "Drag to move · right-click to remove");
        e.PointerEntered += (_, _) => { SetCursor(InputSystemCursorShape.SizeAll); Grow(e, 1.4); };
        e.PointerExited += (_, _) => { if (_drag < 0) { SetCursor(InputSystemCursorShape.Arrow); Grow(e, 1); } };
        e.PointerPressed += (_, args) =>
        {
            if (args.GetCurrentPoint(e).Properties.IsRightButtonPressed)
            {
                if (_points.Count > 2) { _points.RemoveAt(_thumbs.IndexOf(e)); Redraw(); PointsChanged?.Invoke([.. _points]); }
            }
            else
            {
                _drag = _thumbs.IndexOf(e);
                _canvas.CapturePointer(args.Pointer);
            }
            args.Handled = true;
        };
        return e;
    }

    void SetCursor(InputSystemCursorShape shape) => base.ProtectedCursor = InputSystemCursor.Create(shape);

    static void Grow(UIElement e, double s)
    {
        var st = (ScaleTransform)e.RenderTransform;
        var sb = new Storyboard();
        foreach (var prop in new[] { "ScaleX", "ScaleY" })
        {
            var a = new DoubleAnimation { To = s, Duration = TimeSpan.FromMilliseconds(160), EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(a, st);
            Storyboard.SetTargetProperty(a, prop);
            sb.Children.Add(a);
        }
        sb.Begin();
    }

    void PlaceThumbs()
    {
        for (int i = 0; i < _thumbs.Count; i++)
        {
            var s = ToScreen(_points[i].Temp, _points[i].Percent);
            Canvas.SetLeft(_thumbs[i], s.X - 8);
            Canvas.SetTop(_thumbs[i], s.Y - 8);
        }
    }

    void OnMove(object sender, PointerRoutedEventArgs e)
    {
        if (_drag < 0) return;
        var (t, p) = FromScreen(e.GetCurrentPoint(_canvas).Position);
        // Stay between the neighbours so the curve stays a function of temperature.
        double lo = _drag > 0 ? _points[_drag - 1].Temp + 1 : TMin;
        double hi = _drag < _points.Count - 1 ? _points[_drag + 1].Temp - 1 : TMax;
        _points[_drag] = new(Math.Round(Math.Clamp(t, lo, hi)), Math.Round(Math.Clamp(p, 0, 100)));
        Redraw();
        _canvas.CapturePointer(e.Pointer);
        if (_drag < _thumbs.Count) ((ScaleTransform)_thumbs[_drag].RenderTransform).ScaleX = ((ScaleTransform)_thumbs[_drag].RenderTransform).ScaleY = 1.4;
        ToolTipService.SetToolTip(_thumbs[_drag], $"{_points[_drag].Temp:0} °C → {_points[_drag].Percent:0}%");
    }

    void EndDrag(PointerRoutedEventArgs e)
    {
        if (_drag < 0) return;
        _drag = -1;
        _canvas.ReleasePointerCapture(e.Pointer);
        SetCursor(InputSystemCursorShape.Arrow);
        PointsChanged?.Invoke([.. _points]);
    }

    void OnDoubleTap(object sender, DoubleTappedRoutedEventArgs e)
    {
        var (t, p) = FromScreen(e.GetPosition(_canvas));
        t = Math.Round(Math.Clamp(t, TMin, TMax));
        if (_points.Any(x => Math.Abs(x.Temp - t) < 1)) return;
        _points.Add(new(t, Math.Round(Math.Clamp(p, 0, 100))));
        _points.Sort((a, b) => a.Temp.CompareTo(b.Temp));
        Redraw();
        PointsChanged?.Invoke([.. _points]);
    }

    void PlaceMarker()
    {
        var vis = _hasMarker ? Visibility.Visible : Visibility.Collapsed;
        _marker.Visibility = _markerRing.Visibility = _markerDrop.Visibility = _markerText.Visibility = vis;
        if (!_hasMarker || ActualWidth <= 0) return;
        var s = ToScreen(Math.Clamp(_mx.Value, TMin, TMax), _my.Value);
        Canvas.SetLeft(_marker, s.X - 7); Canvas.SetTop(_marker, s.Y - 7);
        Canvas.SetLeft(_markerRing, s.X - 7); Canvas.SetTop(_markerRing, s.Y - 7);
        var bottom = ToScreen(_mx.Value, 0);
        _markerDrop.X1 = _markerDrop.X2 = s.X; _markerDrop.Y1 = s.Y; _markerDrop.Y2 = bottom.Y;
        _markerText.Text = $"{_mx.Value:0.0}° → {_my.Value:0}%";
        _markerText.Foreground = new SolidColorBrush(Heat.Of(_mx.Value));
        Canvas.SetLeft(_markerText, s.X + 12); Canvas.SetTop(_markerText, s.Y - 24);
    }
}

using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace RigControl.Controls;

static class Heat
{
    static readonly (double t, Color c)[] Stops =
    [
        (25, Color.FromArgb(255, 0, 212, 255)),
        (50, Color.FromArgb(255, 124, 92, 255)),
        (72, Color.FromArgb(255, 255, 150, 60)),
        (90, Color.FromArgb(255, 255, 70, 100)),
    ];

    /// <summary>Cyan when cool, violet, orange, red when hot.</summary>
    public static Color Of(double t)
    {
        if (t <= Stops[0].t) return Stops[0].c;
        for (int i = 1; i < Stops.Length; i++)
        {
            if (t > Stops[i].t) continue;
            var (a, b) = (Stops[i - 1], Stops[i]);
            var k = (t - a.t) / (b.t - a.t);
            byte L(byte x, byte y) => (byte)(x + (y - x) * k);
            return Color.FromArgb(255, L(a.c.R, b.c.R), L(a.c.G, b.c.G), L(a.c.B, b.c.B));
        }
        return Stops[^1].c;
    }

    public static Color WithAlpha(this Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
}

/// <summary>
/// A small line of recent values (the overview's history), drawn right-aligned over the whole window: averaged into
/// narrow columns and drawn as a smooth curve that never overshoots (see <see cref="RigControl.Core.SparklineMath"/>).
/// </summary>
public sealed class Sparkline : Grid
{
    readonly Path _line = new() { StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    // A new sample moves the line one step left: it glides there over the second until the next one (on the
    // compositor, no UI thread work) instead of jumping.
    readonly TranslateTransform _slide = new();
    readonly DoubleAnimation _glide = new() { To = 0, Duration = TimeSpan.FromSeconds(1) };
    readonly Storyboard _story = new();
    IReadOnlyList<double> _values = [];
    long _added = -1;

    /// <summary>The smallest range drawn top to bottom, so a steady 40° doesn't look like a storm.</summary>
    public double MinSpan { get; set; } = 10;

    public Sparkline()
    {
        Height = 34;
        _line.Stroke = (Brush)Application.Current.Resources["AccentGradient"];
        Children.Add(_line);
        _line.RenderTransform = _slide;
        Storyboard.SetTarget(_glide, _slide);
        Storyboard.SetTargetProperty(_glide, "X");
        _story.Children.Add(_glide);
        SizeChanged += (_, _) => { Clip = new RectangleGeometry { Rect = new Rect(0, 0, ActualWidth, ActualHeight) }; Draw(); };
    }

    /// <param name="added">Samples ever added to this series (see <see cref="History.Added"/>).</param>
    public void SetValues(IReadOnlyList<double> values, long added)
    {
        long fresh = _added < 0 ? 0 : added - _added;
        if (fresh == 0 && _values.Count > 0) return; // nothing new: leave the line (and its glide) alone
        _values = values;
        _added = added;
        Draw();
        if (fresh is > 0 and < 5 && ActualWidth > 0)
        {
            _story.Stop();
            _glide.From = fresh * ActualWidth / (History.Samples - 1);
            _story.Begin();
        }
    }

    void Draw()
    {
        var points = RigControl.Core.SparklineMath.Layout(_values, History.Samples, ActualWidth, ActualHeight, MinSpan, first: _added - _values.Count);
        if (points.Count < 2) { _line.Data = null; return; }
        var figure = new PathFigure { StartPoint = new Point(points[0].X, points[0].Y) };
        foreach (var (c1, c2, end) in RigControl.Core.SparklineMath.Curve(points))
            figure.Segments.Add(new BezierSegment { Point1 = new Point(c1.X, c1.Y), Point2 = new Point(c2.X, c2.Y), Point3 = new Point(end.X, end.Y) });
        _line.Data = new PathGeometry { Figures = { figure } };
    }
}

/// <summary>Fan blades that really spin, at a speed that follows the fan's RPM (GPU composition, no UI thread work).</summary>
public sealed class FanGlyph : Grid
{
    static string? _bladeData;
    readonly Path _blades = new();
    Visual? _visual;
    AnimationController? _spin;
    double _rpm;

    public FanGlyph()
    {
        Width = Height = 46;
        _blades.Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), _bladeData ??= Blades());
        _blades.Stretch = Stretch.Uniform;
        _blades.Fill = (Brush)Application.Current.Resources["AccentGradient"];
        Children.Add(new Ellipse { Stroke = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), StrokeThickness = 1.5 });
        var inner = new Grid { Margin = new Thickness(5) };
        inner.Children.Add(_blades);
        Children.Add(inner);
        Children.Add(new Ellipse { Width = 9, Height = 9, Fill = new SolidColorBrush(Color.FromArgb(255, 30, 24, 60)) });

        _blades.SizeChanged += (_, e) =>
        {
            _visual ??= ElementCompositionPreview.GetElementVisual(_blades);
            _visual.CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
            if (_spin is null) StartSpin();
        };
        Unloaded += (_, _) => { _visual?.StopAnimation("RotationAngleInDegrees"); _spin = null; };
    }

    public double Rpm
    {
        get => _rpm;
        set { _rpm = value; ApplyRate(); }
    }

    void StartSpin()
    {
        var compositor = _visual!.Compositor;
        var anim = compositor.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(0, 0, compositor.CreateLinearEasingFunction());
        anim.InsertKeyFrame(1, 360, compositor.CreateLinearEasingFunction());
        anim.Duration = TimeSpan.FromSeconds(1);
        anim.IterationBehavior = AnimationIterationBehavior.Forever;
        _visual.StartAnimation("RotationAngleInDegrees", anim);
        _spin = _visual.TryGetAnimationController("RotationAngleInDegrees");
        ApplyRate();
    }

    void ApplyRate()
    {
        if (_spin is null) return;
        // 1000 rpm reads as ~2 turns a second: fast enough to feel alive, slow enough not to strobe.
        var rate = _rpm / 500.0;
        if (rate < 0.02) _spin.Pause();
        else
        {
            _spin.PlaybackRate = (float)Math.Min(rate, 8);
            _spin.Resume();
        }
    }

    static string Blades()
    {
        // One curved blade, rotated four times around (50,50).
        (double x, double y)[] blade = [(50, 50), (53, 20), (79, 16), (76, 40), (72, 46), (60, 50), (50, 50)];
        var parts = new List<string>();
        for (int k = 0; k < 4; k++)
        {
            double a = k * Math.PI / 2, cos = Math.Cos(a), sin = Math.Sin(a);
            string P((double x, double y) p) =>
                FormattableString.Invariant($"{50 + (p.x - 50) * cos - (p.y - 50) * sin:0.##},{50 + (p.x - 50) * sin + (p.y - 50) * cos:0.##}");
            parts.Add($"M{P(blade[0])} C{P(blade[1])} {P(blade[2])} {P(blade[3])} C{P(blade[4])} {P(blade[5])} {P(blade[6])} Z");
        }
        return string.Join(" ", parts);
    }
}

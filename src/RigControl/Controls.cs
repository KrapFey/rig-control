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

/// <summary>A small line of recent values (the overview's last 10 minutes), drawn right-aligned over the whole window.</summary>
public sealed class Sparkline : Grid
{
    readonly Polyline _line = new() { StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
    IReadOnlyList<double> _values = [];

    /// <summary>The smallest range drawn top to bottom, so a steady 40° doesn't look like a storm.</summary>
    public double MinSpan { get; set; } = 10;

    public Sparkline()
    {
        Height = 34;
        _line.Stroke = (Brush)Application.Current.Resources["AccentGradient"];
        Children.Add(_line);
        SizeChanged += (_, _) => Draw();
    }

    public void SetValues(IReadOnlyList<double> values)
    {
        _values = values;
        Draw();
    }

    void Draw()
    {
        var points = new PointCollection();
        double w = ActualWidth, h = ActualHeight - 4;
        if (_values.Count >= 2 && w > 0)
        {
            double lo = _values.Min(), hi = _values.Max(), span = Math.Max(hi - lo, MinSpan), bottom = (lo + hi - span) / 2;
            double step = w / (History.Samples - 1);
            for (int i = 0; i < _values.Count; i++)
                points.Add(new Point(w - (_values.Count - 1 - i) * step, 2 + h - (_values[i] - bottom) / span * h));
        }
        _line.Points = points;
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

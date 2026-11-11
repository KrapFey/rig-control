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

/// <summary>270° arc gauge with a heat-tinted, breathing glow behind it.</summary>
public sealed class Gauge : Grid
{
    const double Size = 140, Stroke = 11, Start = 135, Sweep = 270;
    readonly Path _track = new() { StrokeThickness = Stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    readonly Path _arc = new() { StrokeThickness = Stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    readonly Ellipse _glow = new() { Width = Size * 1.25, Height = Size * 1.25, IsHitTestVisible = false };
    readonly TextBlock _value = new() { FontSize = 36, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    readonly TextBlock _label = new() { FontSize = 12, Opacity = 0.65, HorizontalAlignment = HorizontalAlignment.Center, CharacterSpacing = 80 };
    readonly GradientStop _glowStop = new() { Offset = 0 };
    readonly LinearGradientBrush _arcBrush = new() { StartPoint = new(0, 1), EndPoint = new(1, 0) };

    public double Max { get; set; } = 100;
    public string Unit { get; set; } = "°";
    public string Label { get => _label.Text; set => _label.Text = value.ToUpperInvariant(); }

    public Gauge()
    {
        Width = Height = Size * 1.25;
        _track.Stroke = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
        _track.Data = Arc(Sweep);
        _arc.Stroke = _arcBrush;
        _glow.Fill = new RadialGradientBrush { GradientStops = { _glowStop, new GradientStop { Offset = 1, Color = Colors.Transparent } } };

        var canvas = new Grid { Width = Size, Height = Size };
        canvas.Children.Add(_track);
        canvas.Children.Add(_arc);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = -4 };
        text.Children.Add(_value);
        text.Children.Add(_label);
        canvas.Children.Add(text);
        Children.Add(_glow);
        Children.Add(canvas);

        // Slow "breathing" glow.
        var breathe = new DoubleAnimation { From = 0.55, To = 1, Duration = TimeSpan.FromSeconds(2.6), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        Storyboard.SetTarget(breathe, _glow);
        Storyboard.SetTargetProperty(breathe, "Opacity");
        var sb = new Storyboard { Children = { breathe } };
        Loaded += (_, _) => sb.Begin();
        Unloaded += (_, _) => sb.Stop();
        SetValue(0);
    }

    public void SetValue(double v)
    {
        _value.Text = $"{v:0}{Unit}";
        var frac = Math.Clamp(v / Max, 0, 1);
        _arc.Data = frac <= 0.001 ? null : Arc(Sweep * frac);
        var c = Heat.Of(v);
        _glowStop.Color = c.WithAlpha(90);
        _arcBrush.GradientStops.Clear();
        _arcBrush.GradientStops.Add(new GradientStop { Offset = 0, Color = Color.FromArgb(255, 0, 212, 255) });
        _arcBrush.GradientStops.Add(new GradientStop { Offset = 1, Color = c });
    }

    static Geometry Arc(double sweep)
    {
        double r = (Size - Stroke) / 2, c = Size / 2;
        Point P(double deg) => new(c + r * Math.Cos(deg * Math.PI / 180), c + r * Math.Sin(deg * Math.PI / 180));
        var fig = new PathFigure { StartPoint = P(Start), IsClosed = false };
        fig.Segments.Add(new ArcSegment
        {
            Point = P(Start + Math.Max(sweep, 0.5)),
            Size = new Size(r, r),
            IsLargeArc = sweep > 180,
            SweepDirection = SweepDirection.Clockwise,
        });
        return new PathGeometry { Figures = { fig } };
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

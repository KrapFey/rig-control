namespace RigControl.Core;

/// <summary>
/// How the overview's small history lines are drawn: samples averaged into columns a few pixels wide (the line shows
/// the trend, not the 2-second jitter of a sensor), then a smooth curve through them that never overshoots: it
/// doesn't swing above a real peak or below a real dip, only rounds the corners between them.
/// </summary>
public static class SparklineMath
{
    /// <summary>
    /// Points to draw, left to right. The newest sample sits at the right edge and the whole <paramref name="window"/>
    /// spans the width, so a short history only fills the right-hand part.
    /// </summary>
    /// <param name="minSpan">The smallest range drawn top to bottom, so a steady 40° doesn't look like a storm.</param>
    /// <param name="column">Width (px) of the columns samples are averaged into.</param>
    public static List<(double X, double Y)> Layout(IReadOnlyList<double> values, int window, double width, double height, double minSpan, double column = 4)
    {
        if (values.Count < 2 || width <= 0 || height <= 0 || window < 2) return [];
        double step = width / (window - 1);
        var columns = new SortedDictionary<long, (double x, double v, int n)>();
        for (int i = 0; i < values.Count; i++)
        {
            double x = width - (values.Count - 1 - i) * step;
            long key = (long)Math.Floor(x / column);
            columns[key] = columns.TryGetValue(key, out var c) ? (c.x + x, c.v + values[i], c.n + 1) : (x, values[i], 1);
        }
        var averaged = columns.Values.Select(c => (x: c.x / c.n, v: c.v / c.n)).ToList();
        if (averaged.Count < 2) return [];
        double lo = averaged.Min(p => p.v), hi = averaged.Max(p => p.v), span = Math.Max(hi - lo, minSpan), bottom = (lo + hi - span) / 2;
        const double pad = 2;
        double h = height - 2 * pad;
        return [.. averaged.Select(p => (p.x, pad + h - (p.v - bottom) / span * h))];
    }

    /// <summary>
    /// A smooth curve through the points as cubic Bézier segments (each: two control points and its end), with
    /// tangents kept monotone (Fritsch–Carlson) so no segment leaves the band between its two points.
    /// </summary>
    public static List<((double X, double Y) C1, (double X, double Y) C2, (double X, double Y) End)> Curve(IReadOnlyList<(double X, double Y)> p)
    {
        int n = p.Count;
        if (n < 2) return [];
        var d = new double[n - 1];
        for (int k = 0; k < n - 1; k++) d[k] = (p[k + 1].Y - p[k].Y) / Math.Max(p[k + 1].X - p[k].X, 1e-9);
        var m = new double[n];
        m[0] = d[0];
        m[n - 1] = d[n - 2];
        for (int k = 1; k < n - 1; k++) m[k] = d[k - 1] * d[k] <= 0 ? 0 : (d[k - 1] + d[k]) / 2; // a peak or dip stays flat
        for (int k = 0; k < n - 1; k++)
        {
            if (d[k] == 0) { m[k] = m[k + 1] = 0; continue; }
            double a = m[k] / d[k], b = m[k + 1] / d[k], s = a * a + b * b;
            if (s > 9) { double t = 3 / Math.Sqrt(s); m[k] = t * a * d[k]; m[k + 1] = t * b * d[k]; }
        }
        var segments = new List<((double, double), (double, double), (double, double))>(n - 1);
        for (int k = 0; k < n - 1; k++)
        {
            double dx = (p[k + 1].X - p[k].X) / 3;
            segments.Add(((p[k].X + dx, p[k].Y + m[k] * dx), (p[k + 1].X - dx, p[k + 1].Y - m[k + 1] * dx), p[k + 1]));
        }
        return segments;
    }
}

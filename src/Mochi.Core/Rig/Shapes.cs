using SkiaSharp;

namespace Mochi.Core.Rig;

/// <summary>
/// A run of fur tufts along a closed outline, expressed as fractions [0,1) of the outline.
/// <paramref name="Soft"/> 0 gives pointed locks, 1 gives rounded plush lobes.
/// </summary>
public readonly record struct Tufts(float Start, float End, int Count, float Length, float Lean = 0f, float Jitter = 0.35f, float Soft = 0.7f);

/// <summary>Geometry helpers for building soft, furry silhouettes.</summary>
public static class Shapes
{
    public static SKPoint[] Ellipse(float cx, float cy, float rx, float ry, int count)
    {
        var pts = new SKPoint[count];
        for (int i = 0; i < count; i++)
        {
            float a = i / (float)count * MathF.Tau;
            pts[i] = new SKPoint(cx + MathF.Cos(a) * rx, cy + MathF.Sin(a) * ry);
        }
        return pts;
    }

    /// <summary>Samples a closed Catmull-Rom spline through <paramref name="control"/>.</summary>
    public static SKPoint[] ClosedSpline(SKPoint[] control, int samplesPerSegment)
    {
        int n = control.Length;
        var result = new SKPoint[n * samplesPerSegment];
        int k = 0;
        for (int i = 0; i < n; i++)
        {
            SKPoint p0 = control[(i - 1 + n) % n], p1 = control[i], p2 = control[(i + 1) % n], p3 = control[(i + 2) % n];
            for (int s = 0; s < samplesPerSegment; s++)
                result[k++] = CatmullRom(p0, p1, p2, p3, s / (float)samplesPerSegment);
        }
        return result;
    }

    static SKPoint CatmullRom(SKPoint p0, SKPoint p1, SKPoint p2, SKPoint p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return new SKPoint(
            0.5f * (2 * p1.X + (-p0.X + p2.X) * t + (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2 + (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3),
            0.5f * (2 * p1.Y + (-p0.Y + p2.Y) * t + (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 + (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3));
    }

    /// <summary>
    /// Turns a dense closed polyline into a path whose edge carries soft pointed fur tufts on the given ranges.
    /// The same seed always produces the same silhouette, so the character stays consistent.
    /// </summary>
    public static SKPath Fluffy(SKPoint[] ring, int seed, params Tufts[] tufts)
    {
        int n = ring.Length;
        var rng = new Random(seed);
        var centroid = Centroid(ring);

        // Explode ranges into individual tufts as (startIndex, endIndex, length, lean, soft).
        var list = new List<(int s, int e, float len, float lean, float soft)>();
        foreach (var t in tufts)
        {
            float span = t.End >= t.Start ? t.End - t.Start : t.End + 1 - t.Start;
            int startIdx = (int)MathF.Round(t.Start * n);
            int total = Math.Max(t.Count, (int)MathF.Round(span * n));
            for (int j = 0; j < t.Count; j++)
            {
                int s = startIdx + (int)MathF.Round(j * total / (float)t.Count);
                int e = startIdx + (int)MathF.Round((j + 1) * total / (float)t.Count);
                if (e <= s) e = s + 1;
                float len = t.Length * (1f - t.Jitter + (float)rng.NextDouble() * t.Jitter * 2f);
                float lean = t.Lean + ((float)rng.NextDouble() - 0.5f) * 0.3f;
                list.Add((s % n, s % n + (e - s), len, lean, t.Soft));
            }
        }
        list.Sort((a, b) => a.s.CompareTo(b.s));

        // Start at an index that is not inside a tuft so the path closes cleanly.
        var covered = new bool[n];
        foreach (var t in list) for (int i = t.s; i < t.e; i++) covered[i % n] = true;
        int start = Array.IndexOf(covered, false);
        if (start < 0) start = list.Count > 0 ? list[0].s : 0;

        var byStart = new Dictionary<int, (int s, int e, float len, float lean, float soft)>();
        foreach (var t in list) byStart.TryAdd(t.s, t);

        var b = new SKPathBuilder();
        b.MoveTo(ring[start]);
        int idx = start;
        while (idx < start + n)
        {
            int i = idx % n;
            if (byStart.TryGetValue(i, out var tuft) && idx + (tuft.e - tuft.s) <= start + n)
            {
                var p0 = ring[i];
                var p1 = ring[(i + (tuft.e - tuft.s)) % n];
                var mid = new SKPoint((p0.X + p1.X) / 2, (p0.Y + p1.Y) / 2);
                var tangent = Normalize(p1 - p0);
                var normal = new SKPoint(-tangent.Y, tangent.X);
                if (Dot(normal, mid - centroid) < 0) normal = new SKPoint(-normal.X, -normal.Y);
                float w = Length(p1 - p0);
                var tip = mid + Scale(normal, tuft.len) + Scale(tangent, tuft.lean * w * 0.9f);
                // Rounded lobes keep tangent continuity through the tip; pointed locks collapse it.
                float round = w * 0.34f * tuft.soft;
                var c1 = p0 + Scale(normal, tuft.len * 0.85f) + Scale(tangent, w * 0.02f);
                var c2 = tip - Scale(tangent, round);
                var c3 = tip + Scale(tangent, round);
                var c4 = p1 + Scale(normal, tuft.len * 0.3f);
                b.CubicTo(c1, c2, tip);
                b.CubicTo(c3, c4, p1);
                idx += tuft.e - tuft.s;
            }
            else
            {
                idx++;
                b.LineTo(ring[idx % n]);
            }
        }
        b.Close();
        return b.Detach();
    }

    public static SKPath Smooth(SKPoint[] ring)
    {
        var b = new SKPathBuilder();
        b.MoveTo(ring[0]);
        for (int i = 1; i < ring.Length; i++) b.LineTo(ring[i]);
        b.Close();
        return b.Detach();
    }

    /// <summary>Short curved strokes that suggest fur texture; drawn clipped to the part they belong to.</summary>
    public static SKPath Strands(SKRect area, int count, int seed, Func<SKPoint, SKPoint> flow, float minLen, float maxLen, float curl = 0.35f)
    {
        var rng = new Random(seed);
        var b = new SKPathBuilder();
        for (int i = 0; i < count; i++)
        {
            var p = new SKPoint(area.Left + (float)rng.NextDouble() * area.Width, area.Top + (float)rng.NextDouble() * area.Height);
            var d = Normalize(flow(p));
            float len = minLen + (float)rng.NextDouble() * (maxLen - minLen);
            var perp = new SKPoint(-d.Y, d.X);
            float bend = ((float)rng.NextDouble() - 0.5f) * len * curl;
            var end = p + Scale(d, len);
            var ctrl = p + Scale(d, len * 0.5f) + Scale(perp, bend);
            b.MoveTo(p);
            b.QuadTo(ctrl, end);
        }
        return b.Detach();
    }

    public static SKPoint Centroid(SKPoint[] pts)
    {
        float x = 0, y = 0;
        foreach (var p in pts) { x += p.X; y += p.Y; }
        return new SKPoint(x / pts.Length, y / pts.Length);
    }

    public static SKPoint Normalize(SKPoint v)
    {
        float l = Length(v);
        return l < 1e-5f ? new SKPoint(1, 0) : new SKPoint(v.X / l, v.Y / l);
    }

    public static float Length(SKPoint v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y);
    public static float Dot(SKPoint a, SKPoint b) => a.X * b.X + a.Y * b.Y;
    public static SKPoint Scale(SKPoint v, float s) => new(v.X * s, v.Y * s);
    public static SKPoint Lerp(SKPoint a, SKPoint b, float t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
    public static SKPoint Rotate(SKPoint v, float radians)
    {
        float c = MathF.Cos(radians), s = MathF.Sin(radians);
        return new SKPoint(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }
}

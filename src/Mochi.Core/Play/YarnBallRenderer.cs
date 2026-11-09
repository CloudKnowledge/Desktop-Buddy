using Mochi.Core.Rig;
using SkiaSharp;

namespace Mochi.Core.Play;

/// <summary>Draws the yarn ball centred at the origin in model units: shadow, loose strand, wound yarn, shading.</summary>
public sealed class YarnBallRenderer : IDisposable
{
    readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    readonly SKPath _circle;

    public YarnBallRenderer()
    {
        using var b = new SKPathBuilder();
        b.AddCircle(0, 0, YarnBall.Radius, SKPathDirection.Clockwise);
        _circle = b.Detach();
    }

    public void Draw(SKCanvas c, YarnBall ball, SKColor color)
    {
        const float r = YarnBall.Radius;
        float scale = ball.Scale;
        var dark = PetRenderer.Darken(color, 0.62f);
        var light = PetRenderer.Mix(color, SKColors.White, 0.42f);

        // Soft contact shadow on the floor, fading as the ball rises.
        float floorY = (ball.Floor.Y - ball.Position.Y) / scale;
        float height = floorY - r;
        if (height < 45f && ball.State != BallState.InMouth)
        {
            float k = 1f - Math.Clamp(height / 45f, 0f, 1f);
            using var sh = SKShader.CreateRadialGradient(SKPoint.Empty, 1f,
                [new SKColor(40, 25, 10, 90), new SKColor(40, 25, 10, 0)], [0f, 1f], SKShaderTileMode.Clamp);
            c.Save();
            c.Translate(0, floorY - 1f);
            c.Scale(r * (0.7f + 0.3f * k), 2.6f);
            _fill.Shader = sh;
            _fill.Color = PetRenderer.Alpha(SKColors.White, k);
            c.DrawCircle(0, 0, 1f, _fill);
            _fill.Shader = null;
            c.Restore();
        }

        // The loose strand, behind the ball.
        var pts = ball.Strand;
        using (var pb = new SKPathBuilder())
        {
            SKPoint L(int i) => new((pts[i].X - ball.Position.X) / scale, (pts[i].Y - ball.Position.Y) / scale);
            pb.MoveTo(L(0));
            for (int i = 1; i < pts.Length - 1; i++) pb.QuadTo(L(i), Shapes.Lerp(L(i), L(i + 1), 0.5f));
            pb.LineTo(L(pts.Length - 1));
            using var strand = pb.Detach();
            Stroke(dark, 2.3f);
            c.DrawPath(strand, _stroke);
            Stroke(color, 1.5f);
            c.DrawPath(strand, _stroke);
            Stroke(PetRenderer.Alpha(light, 0.8f), 0.5f);
            c.DrawPath(strand, _stroke);
        }

        // The ball, with wound strands that turn as it rolls.
        _fill.Color = color;
        c.DrawCircle(0, 0, r, _fill);
        c.Save();
        c.ClipPath(_circle, SKClipOperation.Intersect, true);
        for (int family = 0; family < 3; family++)
        {
            float a = ball.Angle + family * 1.05f + 0.3f;
            var dir = new SKPoint(MathF.Cos(a), MathF.Sin(a));
            var nrm = new SKPoint(-dir.Y, dir.X);
            for (int j = -2; j <= 2; j++)
            {
                float d = j * r * 0.34f;
                float bulge = 0.3f * r * (1f - MathF.Abs(d) / r);
                var mid = Shapes.Scale(nrm, d);
                var p0 = mid - Shapes.Scale(dir, r * 1.1f);
                var p1 = mid + Shapes.Scale(dir, r * 1.1f);
                using var lb = new SKPathBuilder();
                lb.MoveTo(p0);
                lb.QuadTo(mid + Shapes.Scale(nrm, bulge), p1);
                using var line = lb.Detach();
                Stroke(PetRenderer.Alpha(dark, 0.55f), 1.5f);
                c.DrawPath(line, _stroke);
                Stroke(PetRenderer.Alpha(light, 0.75f), 0.8f);
                c.DrawPath(line, _stroke);
            }
        }
        // Round shading and a soft highlight, which stay put while the yarn turns.
        using (var shade = SKShader.CreateRadialGradient(new SKPoint(-2.8f, -3.2f), r * 1.35f,
                   [PetRenderer.Alpha(SKColors.White, 0f), PetRenderer.Alpha(SKColors.White, 0f), PetRenderer.Alpha(dark, 0.55f)], [0f, 0.45f, 1f], SKShaderTileMode.Clamp))
        {
            _fill.Shader = shade;
            _fill.Color = SKColors.White;
            c.DrawCircle(0, 0, r, _fill);
            _fill.Shader = null;
        }
        c.Restore();
        _fill.Color = PetRenderer.Alpha(SKColors.White, 0.45f);
        c.DrawOval(new SKRect(-6f, -6.6f, -1.6f, -3.6f), _fill);
        Stroke(PetRenderer.Alpha(PetRenderer.Darken(color, 0.45f), 0.7f), 0.9f);
        c.DrawCircle(0, 0, r, _stroke);
    }

    void Stroke(SKColor color, float width)
    {
        _stroke.Color = color;
        _stroke.StrokeWidth = width;
    }

    public void Dispose()
    {
        _fill.Dispose();
        _stroke.Dispose();
        _circle.Dispose();
    }
}

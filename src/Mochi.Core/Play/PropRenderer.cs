using Mochi.Core.Rig;
using SkiaSharp;

namespace Mochi.Core.Play;

/// <summary>Draws Mochi's props at their spot on the ground: milk saucer, treat bowl, and a retro TV showing cat TV.</summary>
public sealed class PropRenderer : IDisposable
{
    readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    readonly SKPaint _layer = new();
    readonly SKPath _fish, _kibble, _bird;

    public PropRenderer()
    {
        using (var f = new SKPathBuilder())
        {
            f.MoveTo(-5, 0);
            f.CubicTo(-3, -3.3f, 2, -3.3f, 3.6f, 0);
            f.CubicTo(2, 3.3f, -3, 3.3f, -5, 0);
            f.Close();
            f.MoveTo(3.2f, 0);
            f.LineTo(6.4f, -2.7f);
            f.LineTo(5.8f, 0);
            f.LineTo(6.4f, 2.7f);
            f.Close();
            _fish = f.Detach();
        }
        using (var k = new SKPathBuilder())
        {
            // A little fish-shaped treat.
            k.MoveTo(-2.2f, 0);
            k.CubicTo(-1.3f, -1.5f, 1f, -1.5f, 1.6f, 0);
            k.CubicTo(1f, 1.5f, -1.3f, 1.5f, -2.2f, 0);
            k.Close();
            k.MoveTo(1.4f, 0);
            k.LineTo(2.7f, -1.1f);
            k.LineTo(2.7f, 1.1f);
            k.Close();
            _kibble = k.Detach();
        }
        using (var b = new SKPathBuilder())
        {
            b.AddOval(new SKRect(-3.6f, -2.6f, 3.6f, 2.6f), SKPathDirection.Clockwise);
            b.AddCircle(3.4f, -2.4f, 1.9f, SKPathDirection.Clockwise);
            _bird = b.Detach();
        }
    }

    public void Draw(SKCanvas c, Prop prop)
    {
        float a = Math.Clamp(prop.Alpha, 0f, 1f);
        if (a < 0.01f) return;
        c.Save();
        c.Translate(prop.X, 0);
        c.Scale(0.86f + 0.14f * a, 0.86f + 0.14f * a); // a little pop as it appears
        int layer = -1;
        if (a < 0.99f)
        {
            _layer.Color = PetRenderer.Alpha(SKColors.White, a);
            layer = c.SaveLayer(_layer);
        }
        switch (prop.Kind)
        {
            case PropKind.Milk: DrawSaucer(c, prop.Fill); break;
            case PropKind.Treats: DrawBowl(c, prop.Fill, prop.Seed); break;
            case PropKind.Tv: DrawTv(c, prop); break;
            case PropKind.Laptop: DrawLaptop(c, prop); break;
        }
        if (layer >= 0) c.RestoreToCount(layer);
        c.Restore();
    }

    /// <summary>Where the star of the TV show is, relative to the TV's ground point (for Mochi's eyes).</summary>
    public static SKPoint TvFocus(Prop tv) => tv.Seed % 2 == 0
        ? new SKPoint(-5f + 8.5f * MathF.Sin(tv.Time * 1.3f), -25f + 2.5f * MathF.Sin(tv.Time * 2.7f))
        : new SKPoint(-17f + (tv.Time * 7f % 30f), -18f - 4f * MathF.Abs(MathF.Sin(tv.Time * 6f)));

    void Shadow(SKCanvas c, float rx)
    {
        using var sh = SKShader.CreateRadialGradient(SKPoint.Empty, 1f, [new SKColor(40, 25, 10, 80), new SKColor(40, 25, 10, 0)], [0f, 1f], SKShaderTileMode.Clamp);
        c.Save();
        c.Translate(0, -0.6f);
        c.Scale(rx, 2.8f);
        _fill.Shader = sh;
        _fill.Color = SKColors.White;
        c.DrawCircle(0, 0, 1f, _fill);
        _fill.Shader = null;
        c.Restore();
    }

    void DrawSaucer(SKCanvas c, float fill)
    {
        Shadow(c, 16f);
        using (var b = new SKPathBuilder())
        {
            b.MoveTo(-14f, -5f);
            b.CubicTo(-13f, -1.2f, -10f, 0f, -8f, 0f);
            b.LineTo(8f, 0f);
            b.CubicTo(10f, 0f, 13f, -1.2f, 14f, -5f);
            b.Close();
            using var body = b.Detach();
            using var sh = SKShader.CreateLinearGradient(new SKPoint(0, -5), new SKPoint(0, 0),
                [new SKColor(0xC4, 0xE0, 0xF7), new SKColor(0x8C, 0xB8, 0xE3)], [0f, 1f], SKShaderTileMode.Clamp);
            _fill.Shader = sh;
            c.DrawPath(body, _fill);
            _fill.Shader = null;
            Stroke(new SKColor(0x5A, 0x8C, 0xC0, 0xD0), 0.9f);
            c.DrawPath(body, _stroke);
        }
        // Rim, then the milk inside it (lower and smaller as it goes).
        _fill.Color = new SKColor(0xE8, 0xF4, 0xFF);
        c.DrawOval(new SKRect(-14f, -8.2f, 14f, -1.8f), _fill);
        Stroke(new SKColor(0x5A, 0x8C, 0xC0, 0xD0), 0.9f);
        c.DrawOval(new SKRect(-14f, -8.2f, 14f, -1.8f), _stroke);
        float f = Math.Clamp(fill, 0f, 1f);
        if (f > 0.03f)
        {
            float rx = 9f + 2.6f * f, ry = 1.7f + 0.8f * f, y = -5f + 0.8f * (1f - f);
            _fill.Color = new SKColor(0xFF, 0xFD, 0xF7);
            c.DrawOval(new SKRect(-rx, y - ry, rx, y + ry), _fill);
            _fill.Color = new SKColor(0xFF, 0xFF, 0xFF, 0xE0);
            c.DrawOval(new SKRect(-rx * 0.55f, y - ry * 0.55f, -rx * 0.05f, y - ry * 0.1f), _fill);
        }
        // A tiny pink paw print on the front.
        _fill.Color = new SKColor(0xF2, 0x9F, 0xB2, 0xC8);
        c.DrawCircle(0, -1.6f, 0.9f, _fill);
        foreach (float dx in new[] { -1.3f, 0f, 1.3f }) c.DrawCircle(dx, -3f, 0.45f, _fill);
    }

    void DrawBowl(SKCanvas c, float fill, int seed)
    {
        Shadow(c, 14f);
        // The inside of the bowl, then the treats heaped in it, then the front of the bowl over them.
        _fill.Color = new SKColor(0xA8, 0x45, 0x65);
        c.DrawOval(new SKRect(-12f, -11.6f, 12f, -6.4f), _fill);
        int count = (int)MathF.Round(Math.Clamp(fill, 0f, 1f) * 8f);
        var rng = new Random(seed);
        for (int i = 0; i < count; i++)
        {
            float x = -8f + 16f * (i + 0.5f) / 8f + (float)(rng.NextDouble() - 0.5) * 2f;
            float y = -9.6f - 2.2f * (1f - MathF.Abs(x) / 10f) + (float)(rng.NextDouble() - 0.5) * 1.4f;
            c.Save();
            c.Translate(x, y);
            c.RotateDegrees((float)(rng.NextDouble() - 0.5) * 70f);
            _fill.Color = i % 3 == 0 ? new SKColor(0xD9, 0x8C, 0x4A) : new SKColor(0xC2, 0x76, 0x38);
            c.DrawPath(_kibble, _fill);
            Stroke(new SKColor(0x7A, 0x42, 0x18, 0xB0), 0.45f);
            c.DrawPath(_kibble, _stroke);
            c.Restore();
        }
        using (var b = new SKPathBuilder())
        {
            b.MoveTo(-12f, -9f);
            b.QuadTo(0f, -3.4f, 12f, -9f);
            b.CubicTo(11.5f, -3f, 9.5f, 0f, 7.5f, 0f);
            b.LineTo(-7.5f, 0f);
            b.CubicTo(-9.5f, 0f, -11.5f, -3f, -12f, -9f);
            b.Close();
            using var front = b.Detach();
            using var sh = SKShader.CreateLinearGradient(new SKPoint(0, -9), new SKPoint(0, 0),
                [new SKColor(0xF7, 0xA8, 0xBC), new SKColor(0xDA, 0x6C, 0x8C)], [0f, 1f], SKShaderTileMode.Clamp);
            _fill.Shader = sh;
            c.DrawPath(front, _fill);
            _fill.Shader = null;
            Stroke(new SKColor(0xA8, 0x45, 0x65), 0.9f);
            c.DrawPath(front, _stroke);
        }
        Stroke(new SKColor(0xA8, 0x45, 0x65), 0.9f);
        c.DrawOval(new SKRect(-12f, -11.6f, 12f, -6.4f), _stroke);
        // A white fish on the front of the bowl.
        c.Save();
        c.Translate(0, -3.6f);
        c.Scale(0.62f);
        _fill.Color = new SKColor(0xFF, 0xFF, 0xFF, 0xD8);
        c.DrawPath(_fish, _fill);
        c.Restore();
    }

    void DrawTv(SKCanvas c, Prop tv)
    {
        float time = tv.Time;
        Shadow(c, 26f);
        // The screen's glow spills onto the floor.
        using (var glow = SKShader.CreateRadialGradient(SKPoint.Empty, 1f, [new SKColor(0x7F, 0xD0, 0xFF, 0x50), new SKColor(0x7F, 0xD0, 0xFF, 0)], [0f, 1f], SKShaderTileMode.Clamp))
        {
            c.Save();
            c.Translate(-8f, -1f);
            c.Scale(30f, 5f);
            _fill.Shader = glow;
            _fill.Color = PetRenderer.Alpha(SKColors.White, 0.8f + 0.2f * MathF.Sin(time * 9f));
            c.DrawCircle(0, 0, 1f, _fill);
            _fill.Shader = null;
            c.Restore();
        }
        // Legs and antenna.
        Stroke(new SKColor(0x6B, 0x40, 0x22), 2.2f);
        c.DrawLine(-14f, -9f, -17f, 0f, _stroke);
        c.DrawLine(14f, -9f, 17f, 0f, _stroke);
        Stroke(new SKColor(0x4A, 0x4A, 0x52), 0.9f);
        c.DrawLine(-1.5f, -41f, -12f, -56f, _stroke);
        c.DrawLine(1.5f, -41f, 10f, -57f, _stroke);
        _fill.Color = new SKColor(0xE2, 0x5B, 0x5B);
        c.DrawCircle(-12f, -56f, 1.4f, _fill);
        c.DrawCircle(10f, -57f, 1.4f, _fill);

        // Wooden cabinet.
        var cabinet = new SKRoundRect(new SKRect(-24f, -43f, 24f, -8f), 6f);
        using (var wood = SKShader.CreateLinearGradient(new SKPoint(0, -43), new SKPoint(0, -8),
                   [new SKColor(0xD2, 0x95, 0x62), new SKColor(0xA4, 0x67, 0x3C)], [0f, 1f], SKShaderTileMode.Clamp))
        {
            _fill.Shader = wood;
            c.DrawRoundRect(cabinet, _fill);
            _fill.Shader = null;
        }
        Stroke(new SKColor(0x5E, 0x36, 0x1A), 1.1f);
        c.DrawRoundRect(cabinet, _stroke);

        // Speaker grille and knobs.
        Stroke(new SKColor(0x6B, 0x40, 0x22, 0xC0), 0.8f);
        for (int i = 0; i < 5; i++) c.DrawLine(13.5f, -37f + i * 3f, 20.5f, -37f + i * 3f, _stroke);
        _fill.Color = new SKColor(0xF1, 0xE3, 0xC8);
        c.DrawCircle(14.8f, -16f, 2.2f, _fill);
        c.DrawCircle(19.6f, -16f, 2.2f, _fill);
        Stroke(new SKColor(0x5E, 0x36, 0x1A), 0.7f);
        c.DrawCircle(14.8f, -16f, 2.2f, _stroke);
        c.DrawCircle(19.6f, -16f, 2.2f, _stroke);
        c.DrawLine(14.8f, -16f, 14.8f + 1.6f * MathF.Cos(time * 0.4f), -16f - 1.6f * MathF.Sin(time * 0.4f), _stroke);

        // The screen and the show on it.
        var screen = new SKRoundRect(new SKRect(-19.5f, -38.5f, 10f, -12.5f), 4.5f);
        _fill.Color = new SKColor(0x2A, 0x22, 0x26);
        c.DrawRoundRect(new SKRoundRect(new SKRect(-21f, -40f, 11.5f, -11f), 5.5f), _fill);
        bool fishShow = tv.Seed % 2 == 0;
        using (var sky = SKShader.CreateLinearGradient(new SKPoint(0, -38), new SKPoint(0, -13),
                   fishShow ? [new SKColor(0x8E, 0xE0, 0xF5), new SKColor(0x2A, 0x86, 0xC4)] : [new SKColor(0xBF, 0xE8, 0xFF), new SKColor(0x9F, 0xD8, 0x8C)],
                   [0f, 1f], SKShaderTileMode.Clamp))
        {
            _fill.Shader = sky;
            c.DrawRoundRect(screen, _fill);
            _fill.Shader = null;
        }
        c.Save();
        c.ClipRoundRect(screen, SKClipOperation.Intersect, true);
        var focus = TvFocus(tv);
        if (fishShow)
        {
            // Bubbles, seaweed and a little orange fish swimming to and fro.
            _fill.Color = new SKColor(0x3F, 0xA8, 0x6A);
            for (int i = 0; i < 3; i++)
            {
                float sx = -15f + i * 9f, sway = 1.6f * MathF.Sin(time * 1.6f + i);
                using var wb = new SKPathBuilder();
                wb.MoveTo(sx - 1.2f, -12.5f);
                wb.QuadTo(sx + sway, -17f, sx + sway * 0.5f, -21f - i);
                wb.QuadTo(sx + 1.6f + sway, -17f, sx + 1.2f, -12.5f);
                wb.Close();
                using var weed = wb.Detach();
                c.DrawPath(weed, _fill);
            }
            Stroke(new SKColor(0xFF, 0xFF, 0xFF, 0xB0), 0.6f);
            for (int i = 0; i < 4; i++)
            {
                float rise = (time * 7f + i * 6.5f) % 26f;
                c.DrawCircle(-13f + i * 6f + 1.2f * MathF.Sin(time * 3f + i), -13f - rise, 0.9f + 0.25f * i, _stroke);
            }
            c.Save();
            c.Translate(focus.X, focus.Y);
            if (MathF.Cos(tv.Time * 1.3f) < 0) c.Scale(-1f, 1f);
            c.Scale(-1.1f, 1.1f);
            _fill.Color = new SKColor(0xFF, 0x96, 0x3A);
            c.DrawPath(_fish, _fill);
            _fill.Color = new SKColor(0x1A, 0x1A, 0x22);
            c.DrawCircle(-3f, -0.6f, 0.65f, _fill);
            c.Restore();
        }
        else
        {
            // A little bird hopping across the grass.
            _fill.Color = new SKColor(0x6F, 0xBF, 0x5A);
            c.DrawRect(new SKRect(-20f, -17f, 11f, -12f), _fill);
            c.Save();
            c.Translate(focus.X, focus.Y);
            _fill.Color = new SKColor(0x7A, 0x5A, 0x3E);
            c.DrawPath(_bird, _fill);
            _fill.Color = new SKColor(0xF2, 0x9A, 0x3A);
            using (var bk = new SKPathBuilder())
            {
                bk.MoveTo(5.1f, -2.8f);
                bk.LineTo(7.2f, -2.2f);
                bk.LineTo(5.1f, -1.6f);
                bk.Close();
                using var beak = bk.Detach();
                c.DrawPath(beak, _fill);
            }
            _fill.Color = new SKColor(0x1A, 0x1A, 0x22);
            c.DrawCircle(3.9f, -2.8f, 0.55f, _fill);
            Stroke(new SKColor(0x5A, 0x40, 0x2A), 0.8f);
            float flap = 2f * MathF.Sin(time * 20f);
            c.DrawLine(-1f, -1f, -3.5f, -2.5f - flap, _stroke);
            c.Restore();
        }
        // A faint flicker and a glass glare.
        _fill.Color = PetRenderer.Alpha(SKColors.White, 0.05f + 0.04f * MathF.Sin(time * 37f));
        c.DrawRect(new SKRect(-20f, -39f, 11f, -12f), _fill);
        c.Restore();
        using (var gb = new SKPathBuilder())
        {
            gb.MoveTo(-17f, -36f);
            gb.LineTo(-9f, -36f);
            gb.LineTo(-16f, -24f);
            gb.Close();
            using var glare = gb.Detach();
            _fill.Color = PetRenderer.Alpha(SKColors.White, 0.28f);
            c.DrawPath(glare, _fill);
        }
        Stroke(new SKColor(0x1A, 0x14, 0x18), 0.9f);
        c.DrawRoundRect(screen, _stroke);
    }

    /// <summary>A little laptop seen from behind its lid, which opens from the hinge; the screen lights Mochi's face.</summary>
    void DrawLaptop(SKCanvas c, Prop laptop)
    {
        float open = Math.Clamp(laptop.Fill, 0f, 1f);
        Shadow(c, 26f);
        var deck = new SKRoundRect(new SKRect(-23f, -4f, 23f, 0f), 1.6f);
        using (var sh = SKShader.CreateLinearGradient(new SKPoint(0, -4), new SKPoint(0, 0),
                   [new SKColor(0x9A, 0xA0, 0xAA), new SKColor(0x6E, 0x74, 0x7E)], [0f, 1f], SKShaderTileMode.Clamp))
        {
            _fill.Shader = sh;
            c.DrawRoundRect(deck, _fill);
            _fill.Shader = null;
        }
        Stroke(new SKColor(0x4A, 0x4E, 0x56), 0.9f);
        c.DrawRoundRect(deck, _stroke);
        if (open < 0.02f) return;

        float top = -4f - 30f * open, inset = 1.5f * open;
        using (var b = new SKPathBuilder())
        {
            b.MoveTo(-21.5f, -3.5f);
            b.LineTo(21.5f, -3.5f);
            b.LineTo(21.5f - inset, top + 3f);
            b.QuadTo(21.5f - inset, top, 18.5f - inset, top);
            b.LineTo(-18.5f + inset, top);
            b.QuadTo(-21.5f + inset, top, -21.5f + inset, top + 3f);
            b.Close();
            using var lid = b.Detach();
            using var metal = SKShader.CreateLinearGradient(new SKPoint(-20, top), new SKPoint(20, -3.5f),
                [new SKColor(0xEE, 0xF1, 0xF5), new SKColor(0xC9, 0xCE, 0xD6), new SKColor(0xB4, 0xBA, 0xC4)], [0f, 0.55f, 1f], SKShaderTileMode.Clamp);
            _fill.Shader = metal;
            c.DrawPath(lid, _fill);
            _fill.Shader = null;
            Stroke(new SKColor(0x6E, 0x74, 0x7E, 0xE0), 1f);
            c.DrawPath(lid, _stroke);
        }

        // A little glowing paw-print logo.
        float ly = (top - 3.5f) / 2f, pulse = 0.85f + 0.15f * MathF.Sin(laptop.Time * 2f);
        using (var halo = SKShader.CreateRadialGradient(new SKPoint(0, ly), 9f, [new SKColor(0xFF, 0xFF, 0xFF, 0x90), new SKColor(0xFF, 0xFF, 0xFF, 0)], [0f, 1f], SKShaderTileMode.Clamp))
        {
            _fill.Shader = halo;
            _fill.Color = PetRenderer.Alpha(SKColors.White, open * pulse);
            c.DrawCircle(0, ly, 9f, _fill);
            _fill.Shader = null;
        }
        _fill.Color = PetRenderer.Alpha(new SKColor(0xF2, 0x8F, 0xAE), open);
        c.DrawOval(new SKRect(-2.6f, ly - 0.4f, 2.6f, ly + 2.8f), _fill);
        foreach (var (dx, dy) in new[] { (-3.1f, -1.6f), (-1.1f, -3.2f), (1.1f, -3.2f), (3.1f, -1.6f) })
            c.DrawOval(new SKRect(dx - 0.85f, ly + dy - 1f, dx + 0.85f, ly + dy + 1f), _fill);

        // Light from the screen spills over the top edge onto Mochi's face.
        using (var light = SKShader.CreateRadialGradient(SKPoint.Empty, 1f, [new SKColor(0xBF, 0xE8, 0xFF, 0x60), new SKColor(0xBF, 0xE8, 0xFF, 0)], [0f, 1f], SKShaderTileMode.Clamp))
        {
            c.Save();
            c.Translate(0, top - 4f);
            c.Scale(26f, 16f);
            _fill.Shader = light;
            _fill.Color = PetRenderer.Alpha(SKColors.White, open * (0.9f + 0.1f * MathF.Sin(laptop.Time * 7f)));
            c.DrawCircle(0, 0, 1f, _fill);
            _fill.Shader = null;
            c.Restore();
        }
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
        _layer.Dispose();
        _fish.Dispose();
        _kibble.Dispose();
        _bird.Dispose();
    }
}

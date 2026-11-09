using SkiaSharp;

namespace Mochi.Core.Effects;

public enum EffectKind { Heart, Zzz, Sparkle, Note, Exclaim, Question, Dust, Star, Sweat, Purr, Mew, Say }

/// <summary>Short-lived decorative particles drawn around Mochi, in un-mirrored model space.</summary>
public sealed class EffectSystem : IDisposable
{
    sealed class Particle
    {
        public EffectKind Kind;
        public float X, Y, Vx, Vy, Age, Life, Size, Rot, Spin, Phase;
        public int Variant;
        public string? Text;
    }

    static readonly string[] MewWords = ["mew!", "mrrp?", "nya~", "mew~", "prrt!"];

    readonly List<Particle> _particles = [];
    readonly Random _rng;
    readonly SKPaint _fill = new() { IsAntialias = true };
    readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
    readonly SKFont _font;
    readonly SKPath _heart, _star, _fish, _yarn;

    /// <summary>0..1 visibility of the dream bubble shown while dreaming.</summary>
    public float DreamBubble { get; set; }
    public int DreamIcon { get; set; }
    public bool Enabled { get; set; } = true;
    public int Count => _particles.Count;
    /// <summary>The words in the most recent speech bubble.</summary>
    public string? LastSaid { get; private set; }

    public EffectSystem(int seed = 7)
    {
        _rng = new Random(seed);
        _font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default, 12f, 1f, 0f);

        var h = new SKPathBuilder();
        h.MoveTo(0, 3.2f);
        h.CubicTo(-7.2f, -1.6f, -3.6f, -7.6f, 0, -3.8f);
        h.CubicTo(3.6f, -7.6f, 7.2f, -1.6f, 0, 3.2f);
        h.Close();
        _heart = h.Detach();

        var s = new SKPathBuilder();
        s.MoveTo(0, -5);
        s.QuadTo(0.7f, -0.7f, 5, 0);
        s.QuadTo(0.7f, 0.7f, 0, 5);
        s.QuadTo(-0.7f, 0.7f, -5, 0);
        s.QuadTo(-0.7f, -0.7f, 0, -5);
        s.Close();
        _star = s.Detach();

        var f = new SKPathBuilder();
        f.MoveTo(-7, 0);
        f.CubicTo(-4, -4.6f, 3, -4.6f, 5, 0);
        f.CubicTo(3, 4.6f, -4, 4.6f, -7, 0);
        f.Close();
        f.MoveTo(4.4f, 0);
        f.LineTo(9, -3.8f);
        f.LineTo(8.1f, 0);
        f.LineTo(9, 3.8f);
        f.Close();
        _fish = f.Detach();

        var y = new SKPathBuilder();
        y.MoveTo(-4.6f, -2.6f); y.QuadTo(0, -5.8f, 4.6f, -2.1f);
        y.MoveTo(-5.2f, 0.6f); y.QuadTo(0, -2.6f, 5.2f, 1f);
        y.MoveTo(-4.2f, 3.1f); y.QuadTo(0.5f, 0.5f, 4.1f, 3.6f);
        y.MoveTo(-1.5f, -5.4f); y.QuadTo(-4f, 0f, -1.2f, 5.4f);
        _yarn = y.Detach();
    }

    public void Clear() => _particles.Clear();

    /// <summary>A speech bubble with the given words; replaces any bubble already showing.</summary>
    public void Say(string text, float x, float y, float size = 1.55f, float life = 2.4f)
    {
        LastSaid = text;
        if (!Enabled) return;
        _particles.RemoveAll(p => p.Kind is EffectKind.Say or EffectKind.Mew);
        _particles.Add(new Particle { Kind = EffectKind.Say, X = x, Y = y, Vy = -4f, Size = size, Life = life, Text = text });
    }

    public void Emit(EffectKind kind, float x, float y, float vx = 0, float vy = -18, float size = 1f, float life = 1.6f, float spin = 0)
    {
        if (!Enabled || _particles.Count > 60) return;
        _particles.Add(new Particle
        {
            Kind = kind, X = x, Y = y, Vx = vx, Vy = vy, Size = size, Life = life,
            Spin = spin, Rot = kind is EffectKind.Mew or EffectKind.Purr ? 0f : (float)(_rng.NextDouble() - 0.5) * 20f,
            Phase = (float)_rng.NextDouble() * MathF.Tau, Variant = _rng.Next(MewWords.Length),
        });
    }

    public void Burst(EffectKind kind, float x, float y, int count, float speed, float size, float life)
    {
        for (int i = 0; i < count; i++)
        {
            float a = (float)(_rng.NextDouble() * MathF.Tau);
            float sp = speed * (0.5f + (float)_rng.NextDouble() * 0.7f);
            Emit(kind, x, y, MathF.Cos(a) * sp, MathF.Sin(a) * sp - speed * 0.4f, size * (0.7f + (float)_rng.NextDouble() * 0.6f), life, (float)(_rng.NextDouble() - 0.5) * 240f);
        }
    }

    /// <param name="shiftX">model units to shift existing particles so they stay put while Mochi moves.</param>
    public void Update(float dt, float shiftX = 0)
    {
        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            p.Age += dt;
            if (p.Age >= p.Life) { _particles.RemoveAt(i); continue; }
            p.X += p.Vx * dt - shiftX;
            p.Y += p.Vy * dt;
            p.Rot += p.Spin * dt;
            switch (p.Kind)
            {
                case EffectKind.Heart:
                case EffectKind.Note:
                    p.X += MathF.Sin(p.Age * 4f + p.Phase) * 10f * dt;
                    break;
                case EffectKind.Zzz:
                    p.X += MathF.Sin(p.Age * 2.2f + p.Phase) * 6f * dt;
                    break;
                case EffectKind.Dust:
                    p.Vx *= 1f - 3f * dt;
                    p.Vy *= 1f - 3f * dt;
                    break;
                case EffectKind.Sparkle:
                case EffectKind.Star:
                    p.Vx *= 1f - 1.5f * dt;
                    p.Vy = p.Vy * (1f - 1.5f * dt) + 10f * dt;
                    break;
                case EffectKind.Sweat:
                    p.Vy += 40f * dt;
                    break;
            }
        }
    }

    public void Draw(SKCanvas c, SKPoint headTop)
    {
        if (DreamBubble > 0.01f) DrawDreamBubble(c, headTop);
        foreach (var p in _particles)
        {
            float t = p.Age / p.Life;
            float alpha = MathF.Min(1f, p.Age / 0.15f) * (t > 0.6f ? 1f - (t - 0.6f) / 0.4f : 1f);
            if (alpha <= 0.01f) continue;
            c.Save();
            c.Translate(p.X, p.Y);
            c.RotateDegrees(p.Rot);
            switch (p.Kind)
            {
                case EffectKind.Heart: DrawHeart(c, p.Size * (0.8f + 0.3f * MathF.Min(1, p.Age * 4)), alpha); break;
                case EffectKind.Zzz: DrawText(c, "z", 9f + 7f * t, new SKColor(0x6A, 0x7E, 0xC8), alpha, p.Size); break;
                case EffectKind.Note: DrawNote(c, p.Size, alpha); break;
                case EffectKind.Exclaim: DrawText(c, "!", 15f * Pop(p.Age), new SKColor(0xFF, 0x6B, 0x3D), alpha, p.Size); break;
                case EffectKind.Question: DrawText(c, "?", 15f * Pop(p.Age), new SKColor(0x4F, 0x86, 0xE8), alpha, p.Size); break;
                case EffectKind.Sparkle: DrawStar(c, p.Size * (1f - 0.3f * t), new SKColor(0xFF, 0xE6, 0x7A), alpha); break;
                case EffectKind.Star: DrawStar(c, p.Size, new SKColor(0xFF, 0xD2, 0x3F), alpha); break;
                case EffectKind.Dust:
                    _fill.Shader = null;
                    _fill.Color = new SKColor(0xD8, 0xC4, 0xA4, (byte)(140 * alpha));
                    c.DrawCircle(0, 0, p.Size * (2.5f + 4f * t), _fill);
                    break;
                case EffectKind.Sweat: DrawSweat(c, p.Size, alpha); break;
                case EffectKind.Purr: DrawPurr(c, p.Size, alpha, p.Age); break;
                case EffectKind.Mew: DrawMew(c, MewWords[p.Variant], p.Size * Pop(p.Age) / 1.2f, alpha); break;
                case EffectKind.Say: DrawMew(c, p.Text ?? "", p.Size * Pop(p.Age) / 1.2f, alpha); break;
            }
            c.Restore();
        }
    }

    static float Pop(float age) => age < 0.18f ? 0.6f + 0.6f * (age / 0.18f) : 1.2f - 0.2f * MathF.Min(1, (age - 0.18f) / 0.15f);

    void DrawHeart(SKCanvas c, float size, float alpha)
    {
        c.Save();
        c.Scale(size * 1.15f);
        using var sh = SKShader.CreateLinearGradient(new SKPoint(0, -6), new SKPoint(0, 3.5f),
            [new SKColor(0xFF, 0x9F, 0xB8, (byte)(255 * alpha)), new SKColor(0xF5, 0x4E, 0x7B, (byte)(255 * alpha))], [0f, 1f], SKShaderTileMode.Clamp);
        _fill.Shader = sh;
        _fill.Color = SKColors.White;
        c.DrawPath(_heart, _fill);
        _fill.Shader = null;
        _stroke.Color = new SKColor(0xC2, 0x2E, 0x5A, (byte)(160 * alpha));
        _stroke.StrokeWidth = 0.7f;
        c.DrawPath(_heart, _stroke);
        _fill.Color = new SKColor(255, 255, 255, (byte)(200 * alpha));
        c.DrawOval(new SKRect(-4.4f, -4.6f, -2.2f, -3.0f), _fill);
        c.Restore();
    }

    void DrawStar(SKCanvas c, float size, SKColor color, float alpha)
    {
        c.Save();
        c.Scale(size);
        _fill.Shader = null;
        _fill.Color = color.WithAlpha((byte)(255 * alpha));
        c.DrawPath(_star, _fill);
        _fill.Color = new SKColor(255, 255, 255, (byte)(220 * alpha));
        c.DrawCircle(0, 0, 1.3f, _fill);
        c.Restore();
    }

    void DrawNote(SKCanvas c, float size, float alpha)
    {
        // An eighth note: tilted head, stem and flag — drawn as paths so no font is needed.
        c.Scale(size);
        var color = new SKColor(0x9B, 0x6C, 0xE0, (byte)(255 * alpha));
        _stroke.Color = new SKColor(255, 255, 255, (byte)(220 * alpha));
        _stroke.StrokeWidth = 2.6f;
        c.Save();
        c.Translate(-2.2f, 4.2f);
        c.RotateDegrees(-22);
        c.DrawOval(new SKRect(-3f, -2.1f, 3f, 2.1f), _stroke);
        _fill.Shader = null;
        _fill.Color = color;
        c.DrawOval(new SKRect(-3f, -2.1f, 3f, 2.1f), _fill);
        c.Restore();
        using var b = new SKPathBuilder();
        b.MoveTo(0.4f, 3.6f);
        b.LineTo(0.4f, -7f);
        b.QuadTo(3.2f, -5.2f, 4.6f, -2.2f);
        using var stem = b.Detach();
        c.DrawPath(stem, _stroke);
        var thin = _stroke.Color;
        _stroke.Color = color;
        _stroke.StrokeWidth = 1.3f;
        c.DrawPath(stem, _stroke);
        _stroke.Color = thin;
    }

    /// <summary>Little vibration squiggles that rise off a purring kitten.</summary>
    void DrawPurr(SKCanvas c, float size, float alpha, float age)
    {
        c.Scale(size);
        using var b = new SKPathBuilder();
        b.MoveTo(-6, 0);
        for (int i = 0; i < 4; i++)
            b.QuadTo(-4.5f + i * 3f, (i % 2 == 0 ? -2.2f : 2.2f) * (1f - 0.3f * MathF.Sin(age * 20f)), -3f + i * 3f, 0);
        using var path = b.Detach();
        _stroke.Color = new SKColor(255, 255, 255, (byte)(200 * alpha));
        _stroke.StrokeWidth = 2.6f;
        c.DrawPath(path, _stroke);
        _stroke.Color = new SKColor(0xD9, 0x8C, 0xB3, (byte)(235 * alpha));
        _stroke.StrokeWidth = 1.2f;
        c.DrawPath(path, _stroke);
    }

    /// <summary>A tiny rounded speech bubble: "mew!".</summary>
    void DrawMew(SKCanvas c, string word, float size, float alpha)
    {
        c.Scale(size);
        _font.Size = 9.5f;
        float w = _font.MeasureText(word) + 9f;
        var rect = new SKRect(-w / 2, -9.5f, w / 2, 3.5f);
        using var b = new SKPathBuilder();
        b.AddRoundRect(rect, 6.5f, 6.5f, SKPathDirection.Clockwise);
        b.MoveTo(-6f, 2.5f);
        b.LineTo(-9f, 8f);
        b.LineTo(-1.5f, 3f);
        b.Close();
        using var raw = b.Detach();
        using var bubble = raw.Simplify() ?? raw;
        _fill.Shader = null;
        _fill.Color = new SKColor(255, 255, 255, (byte)(240 * alpha));
        c.DrawPath(bubble, _fill);
        _stroke.Color = new SKColor(0xE0, 0x9A, 0xB8, (byte)(230 * alpha));
        _stroke.StrokeWidth = 1.1f;
        c.DrawPath(bubble, _stroke);
        _fill.Color = new SKColor(0xB9, 0x4A, 0x7C, (byte)(255 * alpha));
        c.DrawText(word, 0, 0, SKTextAlign.Center, _font, _fill);
    }

    void DrawSweat(SKCanvas c, float size, float alpha)
    {
        using var b = new SKPathBuilder();
        b.MoveTo(0, -4);
        b.CubicTo(2.8f, 0, 2.8f, 3.2f, 0, 3.2f);
        b.CubicTo(-2.8f, 3.2f, -2.8f, 0, 0, -4);
        b.Close();
        using var path = b.Detach();
        c.Scale(size);
        _fill.Shader = null;
        _fill.Color = new SKColor(0x8C, 0xC8, 0xF5, (byte)(230 * alpha));
        c.DrawPath(path, _fill);
        _fill.Color = new SKColor(255, 255, 255, (byte)(200 * alpha));
        c.DrawCircle(-0.8f, 0.6f, 0.8f, _fill);
    }

    void DrawText(SKCanvas c, string text, float size, SKColor color, float alpha, float scale)
    {
        _font.Size = size * scale;
        _stroke.Color = new SKColor(255, 255, 255, (byte)(230 * alpha));
        _stroke.StrokeWidth = 3f;
        c.DrawText(text, 0, 0, SKTextAlign.Center, _font, _stroke);
        _fill.Shader = null;
        _fill.Color = color.WithAlpha((byte)(255 * alpha));
        c.DrawText(text, 0, 0, SKTextAlign.Center, _font, _fill);
    }

    void DrawDreamBubble(SKCanvas c, SKPoint headTop)
    {
        float a = Math.Clamp(DreamBubble, 0f, 1f);
        float bob = MathF.Sin(Environment.TickCount64 / 600f) * 1.5f;
        var bubbleFill = new SKColor(255, 255, 255, (byte)(235 * a));
        var bubbleLine = new SKColor(0x9A, 0xA8, 0xD8, (byte)(200 * a));
        float bx = headTop.X + 30f, by = headTop.Y - 22f + bob;

        _fill.Shader = null;
        _stroke.StrokeWidth = 1.1f;
        foreach (var (dx, dy, r) in new[] { (-20f, 22f, 2.2f), (-14f, 15f, 3.2f) })
        {
            _fill.Color = bubbleFill;
            c.DrawCircle(bx + dx, by + dy, r * a, _fill);
            _stroke.Color = bubbleLine;
            c.DrawCircle(bx + dx, by + dy, r * a, _stroke);
        }

        using var cloud = new SKPathBuilder();
        foreach (var (dx, dy, r) in new[] { (-9f, 1f, 8f), (0f, -4f, 10f), (10f, 0f, 8.5f), (2f, 5f, 9f), (-5f, 6f, 7f) })
            cloud.AddCircle(bx + dx, by + dy, r * (0.6f + 0.4f * a), SKPathDirection.Clockwise);
        using var raw = cloud.Detach();
        using var merged = raw.Simplify() ?? raw;
        _fill.Color = bubbleFill;
        c.DrawPath(merged, _fill);
        _stroke.Color = bubbleLine;
        c.DrawPath(merged, _stroke);

        c.Save();
        c.Translate(bx + 0.5f, by + 1f);
        c.Scale(0.95f * a);
        if (DreamIcon % 2 == 0)
        {
            // A little blue fish.
            using var sh = SKShader.CreateLinearGradient(new SKPoint(0, -4), new SKPoint(0, 4),
                [new SKColor(0x9C, 0xD4, 0xF5, (byte)(255 * a)), new SKColor(0x4F, 0x93, 0xD8, (byte)(255 * a))], [0f, 1f], SKShaderTileMode.Clamp);
            c.RotateDegrees(-12);
            _fill.Shader = sh;
            _fill.Color = SKColors.White;
            c.DrawPath(_fish, _fill);
            _fill.Shader = null;
            _stroke.Color = new SKColor(0x2E, 0x5F, 0x96, (byte)(255 * a));
            _stroke.StrokeWidth = 0.8f;
            c.DrawPath(_fish, _stroke);
            _fill.Color = new SKColor(0x1A, 0x1A, 0x22, (byte)(255 * a));
            c.DrawCircle(-3.8f, -0.7f, 0.8f, _fill);
        }
        else
        {
            // A ball of yarn with a loose thread.
            _fill.Shader = null;
            _fill.Color = new SKColor(0xF2, 0x7F, 0xA6, (byte)(255 * a));
            c.DrawCircle(0, 0, 5.8f, _fill);
            _stroke.Color = new SKColor(0xC2, 0x4A, 0x78, (byte)(255 * a));
            _stroke.StrokeWidth = 0.75f;
            c.DrawPath(_yarn, _stroke);
            c.DrawCircle(0, 0, 5.8f, _stroke);
            using var tb = new SKPathBuilder();
            tb.MoveTo(4.6f, 3.6f);
            tb.QuadTo(8, 6.5f, 10.5f, 3f);
            using var thread = tb.Detach();
            c.DrawPath(thread, _stroke);
        }
        c.Restore();
    }

    public void Dispose()
    {
        _fill.Dispose();
        _stroke.Dispose();
        _font.Dispose();
        _heart.Dispose();
        _star.Dispose();
        _fish.Dispose();
        _yarn.Dispose();
    }
}

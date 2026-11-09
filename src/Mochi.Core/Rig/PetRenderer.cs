using SkiaSharp;

namespace Mochi.Core.Rig;

/// <summary>
/// Draws Mochi — a fluffy golden tabby kitten — from a <see cref="PetPose"/> in model space (facing right,
/// ground at y = 0). Fur-heavy parts (head and chest fluff, body, haunches, ears, tabby markings) are baked once
/// into supersampled sprites with rim light and ambient occlusion; joints, tail, facial features and fairy
/// lights are drawn as live vectors each frame. Sitting square-on to the viewer uses a dedicated front-view body.
/// </summary>
public sealed class PetRenderer : IDisposable
{
    const float Deg = MathF.PI / 180f;
    const float Supersample = 2f;
    static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.None);

    sealed class Sprite(SKImage image, SKRect dest) : IDisposable
    {
        public SKImage Image { get; } = image;
        public SKRect Dest { get; } = dest;
        public void Dispose() => Image.Dispose();
    }

    sealed record Palette(SKColor Fur, SKColor Light, SKColor Shade, SKColor Deep, SKColor Cream, SKColor Outline);

    /// <summary>A tabby stripe: a tapered band along a quadratic curve (an arch tapers at both ends).</summary>
    readonly record struct Band(SKPoint A, SKPoint C, SKPoint B, float W0, float W1, bool Arch);

    static Band B(float ax, float ay, float cx, float cy, float bx, float by, float w0, float w1 = 0.5f, bool arch = false) =>
        new(new SKPoint(ax, ay), new SKPoint(cx, cy), new SKPoint(bx, by), w0, w1, arch);

    static Band[] Mirrored(params Band[] right) =>
        [.. right, .. right.Select(b => b with { A = new(-b.A.X, b.A.Y), C = new(-b.C.X, b.C.Y), B = new(-b.B.X, b.B.Y) })];

    // Forehead bars, "mascara" lines from the outer eye corners and cheek bars, in head space.
    static readonly Band[] HeadBands =
    [
        B(0f, -26.5f, 0.3f, -21f, 0f, -15.5f, 3f, 0.7f),
        .. Mirrored(
            B(5f, -25.8f, 4.8f, -20.5f, 3.8f, -16f, 2.6f, 0.6f),
            B(10f, -24f, 9f, -19.5f, 7.6f, -16.5f, 2.2f),
            B(21.8f, -2.4f, 25.2f, -0.8f, 28.8f, -3f, 1.8f),
            B(29.5f, 4.5f, 25f, 5.5f, 20.5f, 6f, 2.8f, 0.4f),
            B(29f, 10f, 24.5f, 10.5f, 20f, 10.5f, 2.6f, 0.4f)),
    ];
    static readonly Band[] BodyBands =
    [
        B(-21f, -19.5f, -22.5f, -9f, -24.5f, 1.5f, 4.2f, 0.6f), B(-13f, -20f, -14.5f, -9f, -16.5f, 2f, 4.2f, 0.6f),
        B(-5f, -20f, -6.5f, -9f, -8.5f, 2f, 4.2f, 0.6f), B(3f, -20f, 1.5f, -9f, -0.5f, 2f, 4.2f, 0.6f),
        B(11f, -19.5f, 9.5f, -9f, 7.5f, 1.5f, 3.8f, 0.6f), B(-27f, -15f, -29.5f, -9f, -30.5f, -3f, 3.4f, 0.6f),
    ];
    static readonly Band[] ThighBands =
    [
        B(-10f, -5f, 0f, -14f, 10f, -6f, 3.2f, arch: true), B(-11.5f, 2f, 0f, -7f, 11.5f, 1f, 3f, arch: true),
        B(-9.5f, 8.5f, 0f, 1.5f, 9.5f, 8.5f, 2.6f, arch: true),
    ];
    static readonly Band[] FrontBodyBands = Mirrored(
        B(18f, -13f, 14f, -12f, 10f, -10f, 3f), B(20.5f, -5f, 15f, -4f, 10f, -1f, 3.4f),
        B(22f, 3f, 16.5f, 4f, 11f, 7f, 3.6f), B(21.5f, 11f, 16.5f, 12f, 12f, 15f, 3.4f));
    static readonly Band[] HaunchBands =
    [
        B(-6f, -7f, 3f, -12f, 10.5f, -3f, 3.2f, arch: true), B(-7f, 0f, 3f, -5f, 11.5f, 4f, 3.2f, arch: true),
        B(-5f, 7f, 3f, 3f, 10f, 9f, 2.6f, arch: true),
    ];

    // Geometry, in each part's local space.
    readonly SKPath _ruff, _head, _headStrands, _ruffStrands, _body, _bodyStrands, _chest, _chestStrands, _thigh, _thighStrands,
        _thighCrease, _ear, _earInner, _earTufts, _nose, _unitCircle, _band, _bowLoops, _irisLines,
        _frontBody, _frontBodyStrands, _frontChest, _frontChestStrands, _haunch;

    readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    readonly SKPaint _layer = new();

    PetLook _look = PetLook.Golden;
    Palette _near = null!, _far = null!;
    readonly List<IDisposable> _lookResources = [];
    SKShader _faceMask = null!, _pad = null!, _noseShader = null!, _iris = null!, _blush = null!, _shadow = null!, _bowShader = null!, _bellShader = null!, _glow = null!;
    SKShader _legNearFront = null!, _legNearHind = null!, _legFarFront = null!, _legFarHind = null!, _pawNear = null!, _pawFar = null!;

    Sprite? _sHead, _sHeadMarks, _sBody, _sChest, _sThighNear, _sThighFar, _sEar, _sFrontBody, _sHaunch;
    float _bakedFor = -1;

    public PetRenderer(PetLook? look = null)
    {
        // Fluffy chest bib under the chin; it bridges the head and the body in every pose.
        _ruff = Shapes.Fluffy(Shapes.Ellipse(0, 13, 22f, 18f, 140), 7,
            new Tufts(0.0f, 0.5f, 10, 3.6f, 0f, 0.35f, 0.85f));
        _ruffStrands = Shapes.Strands(new SKRect(-20, 0, 20, 30), 36, 8, p => new SKPoint(p.X * 0.35f, p.Y + 4f), 3f, 5.5f, 0.5f);

        _head = Shapes.Fluffy(Shapes.Ellipse(0, 0, 29.5f, 27f, 170), 11,
            new Tufts(0.0f, 0.15f, 4, 4.4f, 0.3f, 0.3f, 0.9f),
            new Tufts(0.19f, 0.31f, 3, 2.0f, 0f, 0.3f, 1f),
            new Tufts(0.35f, 0.50f, 4, 4.4f, -0.3f, 0.3f, 0.9f),
            new Tufts(0.60f, 0.70f, 3, 1.4f, -0.1f, 0.3f, 1f),
            new Tufts(0.725f, 0.775f, 2, 2.4f, 0.3f, 0.2f, 0.7f),
            new Tufts(0.80f, 0.90f, 3, 1.4f, 0.1f, 0.3f, 1f));
        _headStrands = Shapes.Strands(new SKRect(-29, -27, 29, 26), 80, 21, p => new SKPoint(p.X, p.Y - 5f), 3f, 5.5f, 0.5f);

        var bodyCtl = new SKPoint[]
        {
            new(26, -15), new(32, -2), new(27, 12), new(8, 17), new(-14, 17),
            new(-28, 9), new(-32, -4), new(-26, -14), new(-10, -18), new(10, -18),
        };
        _body = Shapes.Fluffy(Shapes.ClosedSpline(bodyCtl, 14), 31,
            new Tufts(0.06f, 0.18f, 3, 3.2f, 0.3f, 0.3f, 0.9f),
            new Tufts(0.19f, 0.45f, 9, 4.4f, 0.3f, 0.35f, 0.8f),
            new Tufts(0.48f, 0.62f, 3, 3.6f, 0.2f, 0.3f, 0.9f),
            new Tufts(0.74f, 0.96f, 5, 1.7f, 0.2f, 0.3f, 1f));
        _bodyStrands = Shapes.Strands(new SKRect(-31, -17, 30, 16), 80, 32, p => new SKPoint(-1f, 0.2f + p.Y * 0.035f), 4.5f, 8.5f);

        _chest = Shapes.Fluffy(Shapes.ClosedSpline(
            [new(17, -13), new(28, -11), new(35, 0), new(33, 15), new(25, 22), new(16, 15), new(14, 2)], 14), 41,
            new Tufts(0.28f, 0.78f, 7, 4.6f, 0.22f, 0.35f, 0.85f));
        _chestStrands = Shapes.Strands(new SKRect(15, -9, 35, 21), 24, 42, _ => new SKPoint(0.1f, 1f), 3.5f, 6.5f);

        _thigh = Shapes.Fluffy(Shapes.Ellipse(0, 0, 13f, 13.5f, 96), 51,
            new Tufts(0.28f, 0.62f, 5, 4.2f, 0.25f, 0.3f, 0.85f));
        _thighStrands = Shapes.Strands(new SKRect(-12, -12, 12, 12), 20, 52, _ => new SKPoint(-0.4f, 1f), 3f, 6f);
        using (var cb = new SKPathBuilder())
        {
            cb.ArcTo(new SKRect(-13f, -13.5f, 13f, 13.5f), -75f, 160f, true);
            _thighCrease = cb.Detach();
        }

        // Square-on sitting body: a soft pear with a fluffy chest, plus a haunch for either side.
        _frontBody = Shapes.Fluffy(Shapes.ClosedSpline(
            [new(0, -27), new(9, -25), new(14, -16), new(19, -2), new(22, 10), new(19, 20), new(10, 25), new(0, 26),
             new(-10, 25), new(-19, 20), new(-22, 10), new(-19, -2), new(-14, -16), new(-9, -25)], 12), 71,
            new Tufts(0.10f, 0.28f, 5, 2.6f, 0.2f, 0.35f, 0.9f),
            new Tufts(0.72f, 0.90f, 5, 2.6f, -0.2f, 0.35f, 0.9f));
        _frontBodyStrands = Shapes.Strands(new SKRect(-20, -24, 20, 24), 50, 72, p => new SKPoint(p.X * 0.08f, 1f), 3.5f, 6.5f);
        _frontChest = Shapes.Fluffy(Shapes.Ellipse(0, -9, 11.5f, 15f, 110), 73, new Tufts(0.0f, 0.5f, 8, 3.2f, 0f, 0.35f, 0.85f));
        _frontChestStrands = Shapes.Strands(new SKRect(-11, -22, 11, 6), 26, 74, _ => new SKPoint(0, 1f), 3f, 5.5f);
        _haunch = Shapes.Fluffy(Shapes.Ellipse(0, 0, 11f, 11.5f, 96), 75, new Tufts(0.9f, 0.3f, 6, 3f, 0.25f, 0.3f, 0.85f));

        _ear = Shapes.Fluffy(Shapes.ClosedSpline(
            [new(-11f, 2.5f), new(-9f, -7f), new(-4.6f, -16.5f), new(0, -22f), new(4.2f, -16.5f), new(9f, -7f), new(11f, 2.5f), new(0, 5f)], 14), 61,
            new Tufts(0.02f, 0.12f, 2, 1.6f, 0.2f, 0.3f, 1f),
            new Tufts(0.76f, 0.86f, 2, 1.6f, -0.2f, 0.3f, 1f));
        _earInner = Shapes.Smooth(Shapes.ClosedSpline(
            [new(-6.8f, 1f), new(-4.2f, -9f), new(0, -17f), new(3.8f, -9f), new(6.8f, 1f), new(0, 2.4f)], 12));
        using (var tb = new SKPathBuilder())
        {
            // Pale wisps of fur curling out of the ear.
            foreach (var (x0, y0, x1, y1, cx, cy) in new[]
            {
                (-3.4f, 1f, -7.4f, -8.5f, -6.8f, -3f), (-1.4f, 0.5f, -3.4f, -11f, -3.8f, -5f), (0.4f, 0.8f, 0.2f, -12f, -0.6f, -6f),
                (1.8f, 0.5f, 3.6f, -10.4f, 3.8f, -4.6f), (3.6f, 1f, 7f, -8f, 6.6f, -2.8f),
            })
            {
                tb.MoveTo(x0, y0);
                tb.QuadTo(cx, cy, x1, y1);
            }
            _earTufts = tb.Detach();
        }

        using (var nb = new SKPathBuilder())
        {
            nb.MoveTo(-3.7f, -1.5f);
            nb.CubicTo(-3.9f, -3.5f, 3.9f, -3.5f, 3.7f, -1.5f);
            nb.CubicTo(3.3f, 0.5f, 1.1f, 2.4f, 0f, 2.6f);
            nb.CubicTo(-1.1f, 2.4f, -3.3f, 0.5f, -3.7f, -1.5f);
            nb.Close();
            _nose = nb.Detach();
        }
        using (var ub = new SKPathBuilder())
        {
            ub.AddCircle(0, 0, 1f, SKPathDirection.Clockwise);
            _unitCircle = ub.Detach();
        }
        using (var ib = new SKPathBuilder())
        {
            for (int i = 0; i < 14; i++)
            {
                float a = i / 14f * MathF.Tau + 0.2f;
                ib.MoveTo(MathF.Cos(a) * 0.36f, MathF.Sin(a) * 0.36f);
                ib.LineTo(MathF.Cos(a + 0.05f) * 0.86f, MathF.Sin(a + 0.05f) * 0.86f);
            }
            _irisLines = ib.Detach();
        }
        using (var bb = new SKPathBuilder())
        {
            bb.MoveTo(-14, -3);
            bb.QuadTo(0, 3f, 14, -3);
            _band = bb.Detach();
        }
        using (var lb = new SKPathBuilder())
        {
            // Two rounded ribbon loops either side of the knot.
            lb.MoveTo(0, 0);
            lb.CubicTo(-4, -7, -12, -7, -11, 0);
            lb.CubicTo(-12, 7, -4, 7, 0, 0);
            lb.Close();
            lb.MoveTo(0, 0);
            lb.CubicTo(4, -7, 12, -7, 11, 0);
            lb.CubicTo(12, 7, 4, 7, 0, 0);
            lb.Close();
            _bowLoops = lb.Detach();
        }

        SetLook(look ?? PetLook.Golden);
    }

    public PetLook Look => _look;

    public void SetLook(PetLook look)
    {
        _look = look;
        _near = new Palette(look.Fur, look.FurLight, look.FurShade, look.FurDeep, look.Cream, look.Outline);
        _far = new Palette(Darken(look.Fur, 0.88f), Darken(look.FurLight, 0.88f), Darken(look.FurShade, 0.86f),
            Darken(look.FurDeep, 0.86f), Darken(look.Cream, 0.9f), look.Outline);

        foreach (var r in _lookResources) r.Dispose();
        _lookResources.Clear();
        T Keep<T>(T r) where T : IDisposable { _lookResources.Add(r); return r; }

        _faceMask = Keep(Radial(SKPoint.Empty, 1f, [Alpha(look.Cream, 0.75f), Alpha(look.Cream, 0.32f), Alpha(look.Cream, 0f)], [0f, 0.55f, 1f]));
        _pad = Keep(Radial(SKPoint.Empty, 1f, [look.Cream, look.Cream, Alpha(look.Cream, 0f)], [0f, 0.62f, 1f]));
        _noseShader = Keep(SKShader.CreateLinearGradient(new SKPoint(0, -3.5f), new SKPoint(0, 2.6f),
            [Mix(look.Nose, SKColors.White, 0.35f), look.Nose], [0f, 1f], SKShaderTileMode.Clamp));
        _iris = Keep(Radial(new SKPoint(0, 0.28f), 1.12f, [look.IrisLight, look.Iris, look.IrisDark], [0f, 0.55f, 1f]));
        _blush = Keep(Radial(SKPoint.Empty, 1f, [Alpha(look.Blush, 0.6f), Alpha(look.Blush, 0f)], [0f, 1f]));
        _shadow = Keep(Radial(SKPoint.Empty, 1f, [new SKColor(40, 25, 10, 80), new SKColor(40, 25, 10, 34), SKColors.Transparent], [0f, 0.55f, 1f]));
        var acc = look.AccessoryColor;
        _bowShader = Keep(SKShader.CreateLinearGradient(new SKPoint(0, -7), new SKPoint(0, 7),
            [Mix(acc, SKColors.White, 0.3f), acc, Darken(acc, 0.78f)], [0f, 0.45f, 1f], SKShaderTileMode.Clamp));
        _bellShader = Keep(Radial(new SKPoint(-1.2f, -1.4f), 4.6f, [new SKColor(0xFF, 0xF2, 0xB0), new SKColor(0xF2, 0xC2, 0x3C), new SKColor(0xB8, 0x82, 0x14)], [0f, 0.55f, 1f]));
        _glow = Keep(Radial(SKPoint.Empty, 1f, [new SKColor(0xFF, 0xE8, 0xAE, 0xD8), new SKColor(0xFF, 0xC6, 0x6A, 0x5A), new SKColor(0xFF, 0xB0, 0x40, 0x00)], [0f, 0.32f, 1f]));
        _legNearFront = Keep(LegShader(_near, 12.5f));
        _legNearHind = Keep(LegShader(_near, 13f));
        _legFarFront = Keep(LegShader(_far, 11.5f));
        _legFarHind = Keep(LegShader(_far, 12f));
        _pawNear = Keep(PawShader(_near));
        _pawFar = Keep(PawShader(_far));

        _bakedFor = -1; // sprites carry colours, rebake lazily
    }

    /// <summary>Draws the kitten (no particles) for <paramref name="p"/> into model space.</summary>
    public void Draw(SKCanvas c, PetPose p)
    {
        EnsureSprites(c);
        var s = Skeleton.Compute(p);
        float held = Math.Clamp(p.Held, 0, 1);
        float lie = Math.Clamp(p.Lie, 0, 1);
        float curl = Math.Clamp(p.Curl * lie, 0, 1);
        float puff = Math.Clamp(p.Puff, 0, 1);
        // Turning to face the viewer cross-fades the side-on body into the square-on one; the head is shared.
        float front = Math.Clamp(p.Front, 0, 1);
        bool lights = _look.Accessory == AccessoryKind.FairyLights;
        // A grooming or swatting paw comes up in front of the face.
        bool pawAtFace = p.PawToFace > 0.3f || p.Wave > 0.3f || (p.FrontPawRaise > 0.3f && p.Leap <= 0.5f && held <= 0.5f);

        c.Save();
        if (MathF.Abs(p.SwingAngle) > 0.01f && held > 0.01f)
            c.RotateDegrees(p.SwingAngle * held, p.GrabX, p.GrabY);
        if (MathF.Abs(p.Flip) > 0.01f)
            c.RotateDegrees(p.Flip, s.BodyCenter.X, s.BodyCenter.Y - 8f);

        DrawShadow(c, s, p);
        if (front < 0.99f)
        {
            int layer = BeginFade(c, 1f - front);
            DrawSideBody(c, s, p, held, curl, puff, pawAtFace);
            if (lights) DrawSideLights(c, s, p, held);
            EndFade(c, layer);
        }
        if (front > 0.01f)
        {
            int layer = BeginFade(c, front);
            DrawFrontBody(c, s, p, puff);
            EndFade(c, layer);
        }

        DrawHead(c, s, p);
        DrawCollar(c, s, p, lie, curl);

        if (pawAtFace && front < 0.99f)
        {
            int layer = BeginFade(c, 1f - front);
            DrawLeg(c, s.ShoulderNear, s.FrontPawNear, 12.5f, s.FrontPawNearLift, _near, _legNearFront, _pawNear);
            EndFade(c, layer);
        }
        if (front > 0.01f)
        {
            // Paws (and the string of lights they hold) sit in front of the chest fluff.
            int layer = BeginFade(c, front);
            if (lights) DrawFrontChestLights(c, s, p);
            DrawMitten(c, s.FrontPawFar, 5.9f, 4.7f, 1.9f);
            DrawMitten(c, s.FrontPawNear, 5.9f, 4.7f, 1.9f, beans: Math.Clamp(p.Wave * 2f - 1f, 0f, 1f));
            if (lights) DrawFrontFloorLights(c, s, p);
            EndFade(c, layer);
        }
        c.Restore();
    }

    int BeginFade(SKCanvas c, float alpha)
    {
        if (alpha >= 0.99f) return -1;
        _layer.Color = Alpha(SKColors.White, alpha);
        return c.SaveLayer(_layer);
    }

    static void EndFade(SKCanvas c, int layer)
    {
        if (layer >= 0) c.RestoreToCount(layer);
    }

    void DrawSideBody(SKCanvas c, Skeleton s, PetPose p, float held, float curl, float puff, bool pawAtFace)
    {
        float wrap = MathF.Max(curl, Math.Clamp(p.TailWrap, 0, 1) * (1f - held));
        if (wrap < 0.5f) DrawTail(c, s, p, wrap, puff);

        DrawLeg(c, s.ShoulderFar, s.FrontPawFar, 11.5f, s.FrontPawFarLift, _far, _legFarFront, _pawFar);
        DrawLeg(c, s.HipFar, s.HindPawFar, 12f, s.HindPawFarLift, _far, _legFarHind, _pawFar);
        DrawPart(c, _sThighFar!, s.ThighFar, s.ThighAngle, 1f + 0.12f * puff);

        bool pawUp = p.FrontPawRaise > 0.3f || p.Leap > 0.5f || held > 0.5f;
        if (!pawUp && !pawAtFace) DrawLeg(c, s.ShoulderNear, s.FrontPawNear, 12.5f, s.FrontPawNearLift, _near, _legNearFront, _pawNear);
        DrawLeg(c, s.HipNear, s.HindPawNear, 13f, s.HindPawNearLift, _near, _legNearHind, _pawNear);

        c.Save();
        c.Concat(s.Body);
        Blit(c, _sBody!);
        float arch = Math.Clamp(p.Arch, 0, 1);
        if (arch > 0.02f)
        {
            // Bend the body into the classic arched back: each half pivots up around the top of the spine.
            float th = 18f * arch;
            c.Save();
            c.ClipRect(new SKRect(-60, -40, -1, 40));
            c.RotateDegrees(-th, -4, -15);
            Blit(c, _sBody!);
            c.Restore();
            c.Save();
            c.ClipRect(new SKRect(-7, -40, 60, 40));
            c.RotateDegrees(th, -4, -15);
            Blit(c, _sBody!);
            c.Restore();
        }
        c.Restore();

        // A tucked tail comes out from under the haunch; curled up asleep it lies over the front of the body.
        bool tucked = wrap >= 0.5f && curl < 0.5f;
        if (tucked) DrawTail(c, s, p, wrap, puff);
        DrawPart(c, _sThighNear!, s.ThighNear, s.ThighAngle, 1f + 0.12f * puff);

        c.Save();
        c.Concat(s.Body);
        Blit(c, _sChest!);
        c.Restore();

        if (pawUp && !pawAtFace) DrawLeg(c, s.ShoulderNear, s.FrontPawNear, 12.5f, s.FrontPawNearLift, _near, _legNearFront, _pawNear);
        if (wrap >= 0.5f && !tucked) DrawTail(c, s, p, wrap, puff);
    }

    /// <summary>Sitting square-on to the viewer: tail out to the side, haunches, front legs and hind feet.</summary>
    void DrawFrontBody(SKCanvas c, Skeleton s, PetPose p, float puff)
    {
        var bc = s.BodyCenter;
        float k = 1f + 0.1f * puff;
        float sway = p.TailSway * MathF.Sin(p.TailSwayPhase);
        float raise = Math.Clamp(p.TailRaise, -1f, 1f);
        DrawTailCurve(c, bc + new SKPoint(12f, 21f), bc + new SKPoint(26f, 26f), bc + new SKPoint(38f + 0.15f * sway, 17f),
            bc + new SKPoint(34f + 0.45f * sway, 1f - 4f * raise), (1f + 0.75f * puff) * 0.92f, 0.6f);

        c.Save();
        c.Translate(bc.X, bc.Y);
        c.RotateDegrees(p.Wiggle + 0.3f * p.BodyTilt);
        c.Scale(s.BodySx, s.BodySy);
        Blit(c, _sFrontBody!);
        c.Restore();

        DrawPart(c, _sHaunch!, bc + new SKPoint(15f, 15.5f), 0f, k);
        c.Save();
        c.Translate(bc.X - 15f, bc.Y + 15.5f);
        c.Scale(-k, k);
        Blit(c, _sHaunch!);
        c.Restore();

        // Front legs come down between the haunches, or bend up to hold the paws at the chest.
        float up = Math.Clamp(p.PawsUp, 0, 1);
        float shoulderX = Mixf(7f, 13.5f, up), shoulderY = Mixf(-8f, 3f, up), width = Mixf(11.5f, 9f, up);
        float wave = Math.Clamp(p.Wave, 0, 1);
        DrawLimb(c, bc + new SKPoint(-shoulderX, shoulderY), s.FrontPawFar, width, _near, _legNearFront, bandSpan: 1f);
        DrawLimb(c, Shapes.Lerp(bc + new SKPoint(shoulderX, shoulderY), bc + new SKPoint(11f, -12f), wave), s.FrontPawNear, width, _near, _legNearFront, bandSpan: 1f);
        DrawMitten(c, s.HindPawFar + new SKPoint(0, 0.6f), 7.4f, 4.8f, 2.4f);
        DrawMitten(c, s.HindPawNear + new SKPoint(0, 0.6f), 7.4f, 4.8f, 2.4f);
    }

    /// <summary>A round paw seen from the front, with two toe creases.</summary>
    void DrawMitten(SKCanvas c, SKPoint at, float rx, float ry, float toe, float beans = 0f)
    {
        c.Save();
        c.Translate(at.X, at.Y);
        var r = Oval(0, 0, rx, ry);
        _fill.Shader = _pawNear;
        _fill.Color = SKColors.White;
        c.DrawOval(r, _fill);
        _fill.Shader = null;
        Stroke(Alpha(_near.Outline, 0.5f), 1f);
        c.DrawOval(r, _stroke);
        Stroke(Alpha(_near.Outline, 0.36f), 0.85f);
        c.DrawLine(-toe, ry * 0.15f, -toe * 1.08f, ry * 0.95f, _stroke);
        c.DrawLine(toe, ry * 0.15f, toe * 1.08f, ry * 0.95f, _stroke);
        if (beans > 0.01f)
        {
            // A raised paw shows its pink toe beans.
            _fill.Color = Alpha(_look.PawPad, beans);
            c.DrawOval(Oval(0, 1.2f, 2.6f, 2f), _fill);
            c.DrawOval(Oval(-2.9f, -2f, 1.15f, 1.3f), _fill);
            c.DrawOval(Oval(0, -3.1f, 1.15f, 1.3f), _fill);
            c.DrawOval(Oval(2.9f, -2f, 1.15f, 1.3f), _fill);
        }
        c.Restore();
    }

    // ---- fairy lights ------------------------------------------------------------------------

    static readonly SKColor Wire = new(0x5C, 0x3B, 0x1E);

    void DrawFrontChestLights(SKCanvas c, Skeleton s, PetPose p)
    {
        var bc = s.BodyCenter;
        // Draped across the chest (held by the paws when they are up) and down one side to the floor.
        DrawGarland(c, bc + new SKPoint(-20f, -14f), bc + new SKPoint(-12f, -5f), bc + new SKPoint(12f, -5f), bc + new SKPoint(20f, -14f),
            p.LightsPhase, 0, 1f, 0.12f, 0.32f, 0.5f, 0.68f, 0.88f);
        DrawGarland(c, bc + new SKPoint(20f, -14f), bc + new SKPoint(27f, -6f), bc + new SKPoint(29f, 8f), bc + new SKPoint(27f, 22f),
            p.LightsPhase, 5, 1f, 0.45f, 0.85f);
    }

    void DrawFrontFloorLights(SKCanvas c, Skeleton s, PetPose p)
    {
        var bc = s.BodyCenter;
        DrawGarland(c, bc + new SKPoint(-31f, 20f), bc + new SKPoint(-18f, 27.5f), bc + new SKPoint(18f, 27.5f), bc + new SKPoint(27f, 22f),
            p.LightsPhase, 7, 1f, 0.1f, 0.27f, 0.44f, 0.61f, 0.78f);
    }

    void DrawSideLights(SKCanvas c, Skeleton s, PetPose p, float held)
    {
        // Draped along the flank (moves with the body) and looped on the floor round the paws.
        var m = s.Body;
        DrawGarland(c, m.MapPoint(-27f, -12f), m.MapPoint(-12f, 11f), m.MapPoint(10f, 11f), m.MapPoint(24f, -10f),
            p.LightsPhase, 0, 1f, 0.15f, 0.38f, 0.62f, 0.85f);
        float floor = (1f - held) * (1f - Math.Clamp(p.GaitAmount, 0f, 1f)) * (1f - Math.Clamp(s.Airborne / 12f, 0f, 1f));
        if (floor < 0.02f) return;
        float x0 = MathF.Min(s.HindPawNear.X, s.HindPawFar.X) - 12f, x1 = MathF.Max(s.FrontPawNear.X, s.FrontPawFar.X) + 12f;
        DrawGarland(c, new SKPoint(x0, -4f), new SKPoint(x0 + 10f, 0.5f), new SKPoint(x1 - 10f, 0.5f), new SKPoint(x1, -4f),
            p.LightsPhase, 4, floor, 0.12f, 0.37f, 0.63f, 0.88f);
    }

    void DrawGarland(SKCanvas c, SKPoint a, SKPoint c1, SKPoint c2, SKPoint b, float phase, int seed, float alpha, params float[] bulbsAt)
    {
        using (var pb = new SKPathBuilder())
        {
            pb.MoveTo(a);
            pb.CubicTo(c1, c2, b);
            using var wire = pb.Detach();
            StrokePath(c, wire, Alpha(Wire, 0.9f * alpha), 0.9f);
            StrokePath(c, wire, Alpha(new SKColor(0xC9, 0x8E, 0x4E), 0.5f * alpha), 0.35f);
        }
        for (int i = 0; i < bulbsAt.Length; i++)
        {
            int n = seed + i;
            // Integer multiples of the phase keep the twinkle continuous when the phase wraps.
            float tw = 0.5f + 0.5f * MathF.Sin(phase * (1 + n % 3) + n * 2.1f);
            DrawBulb(c, Bezier(a, c1, c2, b, bulbsAt[i]), tw, alpha);
        }
    }

    void DrawBulb(SKCanvas c, SKPoint at, float tw, float alpha)
    {
        c.Save();
        c.Translate(at.X, at.Y + 1.2f);
        c.Save();
        c.Scale(6.5f + 1.5f * tw);
        _fill.Shader = _glow;
        _fill.Color = Alpha(SKColors.White, alpha * (0.45f + 0.55f * tw));
        c.DrawCircle(0, 0, 1f, _fill);
        _fill.Shader = null;
        c.Restore();
        _fill.Color = Alpha(new SKColor(0x4A, 0x5A, 0x2E), alpha);
        c.DrawRect(new SKRect(-0.9f, -2.4f, 0.9f, -1.1f), _fill);
        _fill.Color = Alpha(Mix(new SKColor(0xFF, 0xC8, 0x6A), new SKColor(0xFF, 0xFC, 0xEA), tw), alpha);
        c.DrawOval(Oval(0, 0.3f, 1.45f, 1.9f), _fill);
        c.Restore();
    }

    /// <summary>Draws only the head, centred at the origin — used for icons.</summary>
    public void DrawHeadOnly(SKCanvas c, PetPose p)
    {
        EnsureSprites(c);
        DrawHead(c, new Skeleton { Head = SKPoint.Empty, HeadAngle = p.HeadTilt }, p);
    }

    // ---- sprite baking ---------------------------------------------------------------------

    void EnsureSprites(SKCanvas c)
    {
        var m = c.TotalMatrix;
        float scale = MathF.Sqrt(m.ScaleX * m.ScaleX + m.SkewY * m.SkewY);
        float target = Math.Clamp(scale * Supersample, 1.5f, 14f);
        if (_bakedFor > 0 && MathF.Abs(target - _bakedFor) / _bakedFor < 0.08f) return;
        Bake(target);
    }

    void Bake(float k)
    {
        foreach (var sp in new[] { _sHead, _sHeadMarks, _sBody, _sChest, _sThighNear, _sThighFar, _sEar, _sFrontBody, _sHaunch }) sp?.Dispose();
        _bakedFor = k;

        _sHead = BakeSprite(k, new SKRect(-44, -36, 44, 44), c =>
        {
            // Fluffy chest bib behind the chin.
            using (var soft = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 0.5f))
            {
                _fill.MaskFilter = soft;
                FillPath(c, _ruff, SKShader.CreateLinearGradient(new SKPoint(0, -2), new SKPoint(0, 32),
                    [Mix(_near.Fur, _near.Light, 0.3f), Mix(_near.Light, _near.Cream, 0.55f), _near.Cream], [0f, 0.5f, 1f], SKShaderTileMode.Clamp), dispose: true);
                _fill.MaskFilter = null;
            }
            c.Save();
            c.ClipPath(_ruff, SKClipOperation.Intersect, true);
            StrokePath(c, _ruffStrands, Alpha(SKColors.White, 0.45f), 0.8f);
            c.Translate(0.8f, 1f);
            StrokePath(c, _ruffStrands, Alpha(_near.Shade, 0.2f), 0.7f);
            c.Restore();
            InnerGlow(c, _ruff, -4, 32, Alpha(SKColors.White, 0f), Alpha(_near.Shade, 0.3f), 4f, 1.6f);
            StrokePath(c, _ruff, Alpha(_near.Outline, 0.24f), 1f);

            FillPath(c, _head, Radial(new SKPoint(-6, -12), 42, [Mix(_near.Fur, _near.Light, 0.5f), _near.Fur, _near.Shade], [0f, 0.55f, 1f]), dispose: true);
            c.Save();
            c.ClipPath(_head, SKClipOperation.Intersect, true);
            FillRect(c, new SKRect(-32, -30, 32, 0), SKShader.CreateLinearGradient(new SKPoint(0, -28), new SKPoint(0, -6),
                [Alpha(_near.Deep, 0.25f), Alpha(_near.Deep, 0f)], [0f, 1f], SKShaderTileMode.Clamp), dispose: true);
            StrokePath(c, _headStrands, Alpha(_near.Light, 0.42f), 0.75f);
            c.Translate(0.8f, 1f);
            StrokePath(c, _headStrands, Alpha(_near.Deep, 0.18f), 0.65f);
            c.Restore();
            InnerGlow(c, _head, -28, 27, Alpha(SKColors.White, 0.5f), Alpha(_near.Deep, 0.3f), 4.5f, 1.6f);
            StrokePath(c, _head, Alpha(_near.Outline, 0.5f), 1.1f);
        });
        _sHeadMarks = BakeSprite(k, new SKRect(-33, -30, 33, 16), c => PaintBands(c, HeadBands, _look.Stripe, 0.85f));

        _sBody = BakeSprite(k, new SKRect(-40, -24, 40, 27), c =>
        {
            FillPath(c, _body, SKShader.CreateLinearGradient(new SKPoint(0, -19), new SKPoint(0, 18),
                [Mix(_near.Deep, _near.Fur, 0.45f), _near.Fur, _near.Shade], [0f, 0.42f, 1f], SKShaderTileMode.Clamp), dispose: true);
            c.Save();
            c.ClipPath(_body, SKClipOperation.Intersect, true);
            PaintBands(c, BodyBands, _look.Stripe, 0.8f);
            FillRect(c, new SKRect(-40, -24, 40, 27), Radial(new SKPoint(2, 18), 26, [Alpha(_near.Cream, 0.75f), Alpha(_near.Cream, 0f)], [0f, 1f]), dispose: true);
            FillRect(c, new SKRect(-40, -24, 40, 27), Radial(new SKPoint(-6, -12), 26, [Alpha(SKColors.White, 0.2f), Alpha(SKColors.White, 0f)], [0f, 1f]), dispose: true);
            StrokePath(c, _bodyStrands, Alpha(_near.Light, 0.42f), 0.8f);
            c.Translate(1.4f, 1.1f);
            StrokePath(c, _bodyStrands, Alpha(_near.Deep, 0.22f), 0.75f);
            c.Restore();
            InnerGlow(c, _body, -19, 20, Alpha(SKColors.White, 0.42f), Alpha(_near.Deep, 0.42f), 5f, 1.8f);
            StrokePath(c, _body, Alpha(_near.Outline, 0.5f), 1.1f);
        });

        _sChest = BakeSprite(k, new SKRect(8, -18, 44, 32), c =>
        {
            using var soft = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 0.6f);
            _fill.MaskFilter = soft;
            FillPath(c, _chest, SKShader.CreateLinearGradient(new SKPoint(26, -13), new SKPoint(26, 24),
                [Alpha(_near.Cream, 0f), Mix(_near.Cream, _near.Light, 0.3f), _near.Cream], [0f, 0.3f, 1f], SKShaderTileMode.Clamp), dispose: true);
            _fill.MaskFilter = null;
            c.Save();
            c.ClipPath(_chest, SKClipOperation.Intersect, true);
            StrokePath(c, _chestStrands, Alpha(SKColors.White, 0.5f), 0.75f);
            c.Translate(0.7f, 0.9f);
            StrokePath(c, _chestStrands, Alpha(_near.Shade, 0.18f), 0.65f);
            c.Restore();
        });

        _sThighNear = BakeSprite(k, new SKRect(-20, -20, 20, 22), c => PaintThigh(c, _near, strands: true));
        _sThighFar = BakeSprite(k, new SKRect(-20, -20, 20, 22), c => PaintThigh(c, _far, strands: false));

        _sFrontBody = BakeSprite(k, new SKRect(-30, -34, 30, 32), c =>
        {
            FillPath(c, _frontBody, SKShader.CreateLinearGradient(new SKPoint(0, -27), new SKPoint(0, 26),
                [Mix(_near.Fur, _near.Light, 0.25f), _near.Fur, _near.Shade], [0f, 0.5f, 1f], SKShaderTileMode.Clamp), dispose: true);
            c.Save();
            c.ClipPath(_frontBody, SKClipOperation.Intersect, true);
            FillRect(c, new SKRect(-30, -34, 30, 32), Radial(new SKPoint(0, 8), 17, [Alpha(_near.Cream, 0.8f), Alpha(_near.Cream, 0f)], [0f, 1f]), dispose: true);
            PaintBands(c, FrontBodyBands, _look.Stripe, 0.8f);
            StrokePath(c, _frontBodyStrands, Alpha(_near.Light, 0.4f), 0.8f);
            c.Restore();
            InnerGlow(c, _frontBody, -27, 26, Alpha(SKColors.White, 0.35f), Alpha(_near.Deep, 0.45f), 5f, 1.8f);
            StrokePath(c, _frontBody, Alpha(_near.Outline, 0.5f), 1.1f);
            using (var soft = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 0.5f))
            {
                _fill.MaskFilter = soft;
                FillPath(c, _frontChest, SKShader.CreateLinearGradient(new SKPoint(0, -24), new SKPoint(0, 8),
                    [_near.Cream, Mix(_near.Cream, _near.Light, 0.4f), Alpha(_near.Light, 0f)], [0f, 0.6f, 1f], SKShaderTileMode.Clamp), dispose: true);
                _fill.MaskFilter = null;
            }
            c.Save();
            c.ClipPath(_frontChest, SKClipOperation.Intersect, true);
            StrokePath(c, _frontChestStrands, Alpha(SKColors.White, 0.5f), 0.7f);
            c.Restore();
        });

        _sHaunch = BakeSprite(k, new SKRect(-18, -18, 18, 18), c =>
        {
            FillPath(c, _haunch, Radial(new SKPoint(-3, -5), 17, [Mix(_near.Fur, _near.Light, 0.4f), _near.Fur, _near.Shade], [0f, 0.55f, 1f]), dispose: true);
            c.Save();
            c.ClipPath(_haunch, SKClipOperation.Intersect, true);
            PaintBands(c, HaunchBands, _look.Stripe, 0.8f);
            c.Restore();
            InnerGlow(c, _haunch, -11, 12, Alpha(SKColors.White, 0.3f), Alpha(_near.Deep, 0.4f), 3.5f, 1.4f);
            StrokePath(c, _haunch, Alpha(_near.Outline, 0.48f), 1.05f);
        });

        _sEar = BakeSprite(k, new SKRect(-16, -28, 16, 10), c =>
        {
            FillPath(c, _ear, SKShader.CreateLinearGradient(new SKPoint(0, -16), new SKPoint(0, 4),
                [_near.Deep, _near.Shade, _near.Fur], [0f, 0.4f, 1f], SKShaderTileMode.Clamp), dispose: true);
            using (var soft = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 0.5f))
            {
                _fill.MaskFilter = soft;
                FillPath(c, _earInner, SKShader.CreateLinearGradient(new SKPoint(0, -11), new SKPoint(0, 1),
                    [Mix(_look.EarInner, _near.Deep, 0.35f), _look.EarInner, Mix(_look.EarInner, SKColors.White, 0.3f)], [0f, 0.45f, 1f], SKShaderTileMode.Clamp), dispose: true);
                _fill.MaskFilter = null;
            }
            StrokePath(c, _earTufts, Alpha(SKColors.White, 0.7f), 0.6f);
            StrokePath(c, _ear, Alpha(_near.Outline, 0.5f), 1.05f);
        });
    }

    void PaintThigh(SKCanvas c, Palette pal, bool strands)
    {
        FillPath(c, _thigh, Radial(new SKPoint(3, -6), 19, [Mix(pal.Fur, pal.Light, 0.4f), pal.Fur, pal.Shade], [0f, 0.55f, 1f]), dispose: true);
        c.Save();
        c.ClipPath(_thigh, SKClipOperation.Intersect, true);
        PaintBands(c, ThighBands, pal == _near ? _look.Stripe : Darken(_look.Stripe, 0.88f), 0.8f);
        if (strands) StrokePath(c, _thighStrands, Alpha(pal.Light, 0.4f), 0.8f);
        c.Restore();
        using var blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.2f);
        _stroke.MaskFilter = blur;
        c.Save();
        c.Translate(1.2f, 0);
        StrokePath(c, _thighCrease, Alpha(pal.Deep, 0.32f), 2.6f);
        c.Restore();
        _stroke.MaskFilter = null;
        StrokePath(c, _thighCrease, Alpha(pal.Outline, 0.42f), 1.05f);
    }

    Sprite BakeSprite(float k, SKRect bounds, Action<SKCanvas> paint)
    {
        int w = (int)MathF.Ceiling(bounds.Width * k), h = (int)MathF.Ceiling(bounds.Height * k);
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);
        c.Scale(k);
        c.Translate(-bounds.Left, -bounds.Top);
        paint(c);
        return new Sprite(surface.Snapshot(), new SKRect(bounds.Left, bounds.Top, bounds.Left + w / k, bounds.Top + h / k));
    }

    /// <summary>Soft light along the top edge and occlusion along the bottom edge, inside the shape.</summary>
    void InnerGlow(SKCanvas c, SKPath path, float top, float bottom, SKColor light, SKColor dark, float width, float blur)
    {
        using var sh = SKShader.CreateLinearGradient(new SKPoint(0, top), new SKPoint(0, bottom),
            [light, light.WithAlpha(0), dark.WithAlpha(0), dark], [0f, 0.42f, 0.62f, 1f], SKShaderTileMode.Clamp);
        using var mf = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blur);
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width, Shader = sh, MaskFilter = mf };
        c.Save();
        c.ClipPath(path, SKClipOperation.Intersect, true);
        c.DrawPath(path, paint);
        c.Restore();
    }

    void Blit(SKCanvas c, Sprite s, SKPaint? paint = null) => c.DrawImage(s.Image, s.Dest, Sampling, paint);

    void DrawPart(SKCanvas c, Sprite s, SKPoint at, float angle, float scale = 1f)
    {
        c.Save();
        c.Translate(at.X, at.Y);
        c.RotateDegrees(angle);
        if (scale != 1f) c.Scale(scale);
        Blit(c, s);
        c.Restore();
    }

    // ---- live parts ------------------------------------------------------------------------

    void DrawShadow(SKCanvas c, Skeleton s, PetPose p)
    {
        float k = Math.Clamp(1f - s.Airborne / 110f, 0f, 1f) * (1f - Math.Clamp(p.Held, 0, 1));
        if (k < 0.02f) return;
        float rx = (36f + 8f * p.Lie) * (0.6f + 0.4f * k);
        float ry = 6f * (0.7f + 0.3f * k);
        c.Save();
        c.Translate(s.BodyCenter.X * 0.5f + 4f, -1.5f);
        c.Scale(rx, ry);
        _fill.Shader = _shadow;
        _fill.Color = Alpha(SKColors.White, k);
        c.DrawCircle(0, 0, 1f, _fill);
        _fill.Shader = null;
        c.Restore();
    }

    void DrawLeg(SKCanvas c, SKPoint top, SKPoint paw, float width, float lift, Palette pal, SKShader legShader, SKShader pawShader)
    {
        DrawLimb(c, top, paw, width, pal, legShader);
        var d = paw - top;
        float angle = MathF.Atan2(d.Y, d.X) / Deg - 90f;
        lift = Math.Clamp(lift, 0f, 1f);

        // Paw: a round mitten on the ground; shows its pink toe beans when lifted.
        c.Save();
        c.Translate(paw.X, paw.Y);
        c.RotateDegrees(angle * lift);
        float rx = Mixf(8f, 7f, lift), ry = Mixf(5.6f, 6.8f, lift);
        var pr = new SKRect(-rx + 1f, -ry, rx + 1f, ry);
        _fill.Shader = pawShader;
        _fill.Color = SKColors.White;
        c.DrawOval(pr, _fill);
        _fill.Shader = null;
        Stroke(Alpha(pal.Outline, 0.52f), 1.05f);
        c.DrawOval(pr, _stroke);
        if (lift > 0.3f)
        {
            float a = Math.Clamp((lift - 0.3f) / 0.4f, 0f, 1f);
            _fill.Color = Alpha(_look.PawPad, a);
            c.DrawOval(Oval(1f, 2.2f, 3.1f, 2.3f), _fill);
            c.DrawOval(Oval(-2.6f, -1.8f, 1.35f, 1.5f), _fill);
            c.DrawOval(Oval(1f, -3.2f, 1.35f, 1.5f), _fill);
            c.DrawOval(Oval(4.5f, -1.6f, 1.35f, 1.5f), _fill);
            _fill.Color = Alpha(SKColors.White, 0.5f * a);
            c.DrawOval(Oval(0.3f, 1.4f, 1.2f, 0.6f), _fill);
        }
        else
        {
            Stroke(Alpha(pal.Outline, 0.35f), 0.85f);
            c.DrawLine(3.6f, 1.6f, 4.0f, 4.6f, _stroke);
            c.DrawLine(6.4f, 0.8f, 7.1f, 3.6f, _stroke);
            // Tufts of fur between the toes.
            Stroke(Alpha(pal.Light, 0.85f), 0.8f);
            c.DrawLine(8.4f, 2.4f, 10.2f, 3.6f, _stroke);
            c.DrawLine(5.6f, 4.2f, 6.6f, 6.0f, _stroke);
        }
        c.Restore();
    }

    static float Mixf(float a, float b, float t) => a + (b - a) * t;

    /// <summary>Little round work glasses: tinted lenses with a glint, thin frames, a bridge and arms.</summary>
    void DrawGlasses(SKCanvas c, float exL, float exR, float y, float alpha)
    {
        const float r = 9.8f;
        _fill.Shader = null;
        _fill.Color = Alpha(new SKColor(0xD8, 0xF0, 0xFF), 0.2f * alpha);
        c.DrawCircle(exL, y, r, _fill);
        c.DrawCircle(exR, y, r, _fill);
        Stroke(Alpha(new SKColor(0x3A, 0x2A, 0x22), 0.95f * alpha), 1.5f);
        c.DrawCircle(exL, y, r, _stroke);
        c.DrawCircle(exR, y, r, _stroke);
        using (var b = new SKPathBuilder())
        {
            b.MoveTo(exL + r * 0.9f, y - 2f);
            b.QuadTo((exL + exR) / 2f, y - 4.5f, exR - r * 0.9f, y - 2f);
            b.MoveTo(exL - r, y - 1f);
            b.LineTo(exL - r - 6f, y - 3f);
            b.MoveTo(exR + r, y - 1f);
            b.LineTo(exR + r + 6f, y - 3f);
            using var frame = b.Detach();
            c.DrawPath(frame, _stroke);
        }
        Stroke(Alpha(SKColors.White, 0.6f * alpha), 1.1f);
        foreach (float x in new[] { exL, exR })
            c.DrawArc(new SKRect(x - r + 2.4f, y - r + 2.4f, x + r - 2.4f, y + r - 2.4f), 200f, 50f, false, _stroke);
    }

    void DrawLimb(SKCanvas c, SKPoint top, SKPoint paw, float width, Palette pal, SKShader legShader, float bandSpan = 0.45f)
    {
        var d = paw - top;
        float len = Shapes.Length(d);
        float angle = MathF.Atan2(d.Y, d.X) / Deg - 90f;
        float hw = width / 2f, hb = hw * 0.86f;

        c.Save();
        c.Translate(top.X, top.Y);
        c.RotateDegrees(angle);
        using (var b = new SKPathBuilder())
        {
            b.MoveTo(-hw, 0);
            b.LineTo(-hb, len);
            b.ArcTo(new SKRect(-hb, len - hb, hb, len + hb), 180f, -180f, false);
            b.LineTo(hw, 0);
            b.ArcTo(new SKRect(-hw, -hw, hw, hw), 0f, -180f, false);
            b.Close();
            using var leg = b.Detach();
            _fill.Shader = legShader;
            _fill.Color = SKColors.White;
            c.DrawPath(leg, _fill);
            _fill.Shader = null;
            if (_look.Stripes > 0.01f && len > 6f)
            {
                c.Save();
                c.ClipPath(leg, SKClipOperation.Intersect, true);
                Stroke(Alpha(pal == _near ? _look.Stripe : Darken(_look.Stripe, 0.88f), 0.62f * _look.Stripes), 2.3f);
                for (int i = 0; i < 3; i++)
                {
                    float y = len * (0.28f + 0.2f * i);
                    c.DrawLine(-hw - 1f, y - 1.2f, hw * bandSpan + (bandSpan >= 1f ? 1f : 0f), y + 0.6f - (bandSpan >= 1f ? 1.2f : 0f), _stroke);
                }
                c.Restore();
            }
            // Fur feathering down the back of the leg.
            Stroke(Alpha(pal.Light, 0.5f), 0.8f);
            c.DrawLine(hw * 0.3f, len * 0.12f, hb * 0.25f, len * 0.7f, _stroke);
            Stroke(Alpha(pal.Shade, 0.5f), 0.8f);
            for (int i = 0; i < 3; i++)
            {
                float y = len * (0.25f + 0.22f * i);
                c.DrawLine(-hw * 0.9f, y, -hw - 1.6f, y + 2.6f, _stroke);
            }
            StrokePath(c, leg, Alpha(pal.Outline, 0.48f), 1.05f);
        }
        c.Restore();
    }

    void PaintBands(SKCanvas c, Band[] bands, SKColor color, float strength)
    {
        if (_look.Stripes <= 0.01f) return;
        using var soft = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 0.45f);
        _fill.MaskFilter = soft;
        _fill.Shader = null;
        _fill.Color = Alpha(color, strength * _look.Stripes);
        foreach (var band in bands)
        {
            using var path = BandPath(band);
            c.DrawPath(path, _fill);
        }
        _fill.MaskFilter = null;
    }

    static SKPath BandPath(Band b)
    {
        const int n = 10;
        Span<SKPoint> l = stackalloc SKPoint[n + 1];
        Span<SKPoint> r = stackalloc SKPoint[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n, u = 1 - t;
            var pt = new SKPoint(u * u * b.A.X + 2 * u * t * b.C.X + t * t * b.B.X, u * u * b.A.Y + 2 * u * t * b.C.Y + t * t * b.B.Y);
            var tg = Shapes.Normalize(new SKPoint(2 * u * (b.C.X - b.A.X) + 2 * t * (b.B.X - b.C.X), 2 * u * (b.C.Y - b.A.Y) + 2 * t * (b.B.Y - b.C.Y)));
            var nrm = new SKPoint(-tg.Y, tg.X);
            float w = 0.5f * (b.Arch ? b.W0 * MathF.Sin(MathF.PI * (0.08f + 0.84f * t)) : Mixf(b.W0, b.W1, t));
            l[i] = pt + Shapes.Scale(nrm, w);
            r[i] = pt - Shapes.Scale(nrm, w);
        }
        using var pb = new SKPathBuilder();
        pb.MoveTo(l[0]);
        for (int i = 1; i <= n; i++) pb.LineTo(l[i]);
        for (int i = n; i >= 0; i--) pb.LineTo(r[i]);
        pb.Close();
        return pb.Detach();
    }

    static float SmoothStep(float a, float b, float x)
    {
        float u = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return u * u * (3f - 2f * u);
    }

    void DrawTail(SKCanvas c, Skeleton s, PetPose p, float wrap, float puff)
    {
        float a = s.TailAngle * Deg;
        const float len = 34f;
        float held = Math.Clamp(p.Held, 0, 1);
        float curl = (0.25f + 0.9f * Math.Clamp(p.TailCurl, 0, 1)) * (1f - held * 0.8f);
        // The sway travels from the base to the tip, like a real cat's tail.
        float sway = p.TailSway * Deg;
        float ph = p.TailSwayPhase;
        float a1 = a + 0.3f * sway * MathF.Sin(ph - 1.2f);
        float a2 = a + 0.7f * sway * MathF.Sin(ph - 0.6f) + curl * 0.45f;
        float a3 = a + sway * MathF.Sin(ph) + curl;
        var b0 = s.TailBase;
        var b1 = b0 + Shapes.Scale(new SKPoint(MathF.Cos(a1), MathF.Sin(a1)), len * 0.4f);
        var b2 = b1 + Shapes.Scale(new SKPoint(MathF.Cos(a2), MathF.Sin(a2)), len * 0.38f);
        var b3 = b2 + Shapes.Scale(new SKPoint(MathF.Cos(a3), MathF.Sin(a3)), len * 0.3f);

        float curled = Math.Clamp(p.Curl * p.Lie, 0, 1);
        float tuck = wrap * (1f - curled); // wrapped around the paws while sitting or loafing
        if (wrap > 0.001f)
        {
            // Tucks out from under the haunch and wraps snugly around the front paws, the tip curling up
            // (or, curled up asleep, sweeps round over the nose).
            var root = b0;
            var tip = Shapes.Lerp(new SKPoint(s.FrontPawNear.X + 5f, -10.5f), new SKPoint(s.Head.X + 9f, s.Head.Y + 17f), curled);
            float flick = 0.25f * p.TailSway * MathF.Sin(ph);
            b0 = Shapes.Lerp(b0, Shapes.Lerp(new SKPoint(root.X + 4f, -4.5f), root, curled), wrap);
            b1 = Shapes.Lerp(b1, Shapes.Lerp(new SKPoint(root.X + 14f, -2.5f), new SKPoint(root.X - 9f, root.Y + (-6f - root.Y) * 0.65f), curled), wrap);
            b2 = Shapes.Lerp(b2, Shapes.Lerp(new SKPoint(tip.X - 8f, -3.2f), new SKPoint(root.X + 8f, -5.5f), curled), wrap);
            b3 = Shapes.Lerp(b3, tip + new SKPoint(0, flick * 0.3f), wrap);
        }

        float thick = (1f + 0.75f * puff) * (1f - 0.12f * wrap);
        DrawTailCurve(c, b0, b1, b2, b3, thick, tuck);
    }

    void DrawTailCurve(SKCanvas c, SKPoint b0, SKPoint b1, SKPoint b2, SKPoint b3, float thick, float tuck)
    {
        const int n = 18;
        Span<SKPoint> upper = stackalloc SKPoint[n + 1];
        Span<SKPoint> lower = stackalloc SKPoint[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n;
            var pt = Bezier(b0, b1, b2, b3, t);
            var tg = Shapes.Normalize(BezierTangent(b0, b1, b2, b3, t));
            var nrm = new SKPoint(-tg.Y, tg.X);
            // Free: a plume from a thick root. Tucked: slim under the haunch, full and round around the paws.
            float body = Mixf(MathF.Sin(MathF.PI * MathF.Pow(t, 0.62f)) * (1f - 0.18f * t),
                SmoothStep(0f, 0.55f, t) * (1f - 0.3f * SmoothStep(0.8f, 1f, t)), tuck);
            float wu = (Mixf(3.4f, 1.6f, tuck) + 4.6f * body) * thick;
            float wl = (Mixf(3.6f, 1.2f, tuck) + Mixf(5.4f, 3.6f, tuck) * body) * thick;
            float lobe = (i % 2 == 0 ? 1.1f + 0.5f * tuck : -0.2f) * body * thick; // soft plume scallops
            upper[i] = pt - Shapes.Scale(nrm, wu + lobe * Mixf(0.6f, 1f, tuck));
            lower[i] = pt + Shapes.Scale(nrm, wl + lobe);
        }
        var tipDir = Shapes.Normalize(BezierTangent(b0, b1, b2, b3, 1f));
        var tipPt = b3 + Shapes.Scale(tipDir, 6f * thick);
        // Free tails end in a soft point; a tucked tail ends in a round, bushy tip.
        float cap = SKPoint.Distance(upper[n], lower[n]) * 0.62f;
        var cu = Shapes.Lerp(Shapes.Lerp(upper[n], tipPt, 2f / 3f), upper[n] + Shapes.Scale(tipDir, cap), tuck);
        var cl = Shapes.Lerp(Shapes.Lerp(lower[n], tipPt, 2f / 3f), lower[n] + Shapes.Scale(tipDir, cap), tuck);

        using var pb = new SKPathBuilder();
        pb.MoveTo(upper[0]);
        for (int i = 1; i <= n; i++) pb.QuadTo(upper[i - 1], Shapes.Lerp(upper[i - 1], upper[i], 0.5f));
        pb.LineTo(upper[n]);
        pb.CubicTo(cu, cl, lower[n]);
        for (int i = n - 1; i >= 0; i--)
            pb.QuadTo(lower[i + 1], Shapes.Lerp(lower[i + 1], lower[i], 0.5f));
        pb.LineTo(lower[0]);
        pb.Close();
        using var path = pb.Detach();

        using var sh = SKShader.CreateLinearGradient(b0, tipPt,
            [_near.Fur, Mix(_near.Fur, _near.Deep, 0.35f), Mix(_near.Fur, _near.Light, 0.35f)], [0f, 0.55f, 1f], SKShaderTileMode.Clamp);
        FillPath(c, path, sh);
        c.Save();
        c.ClipPath(path, SKClipOperation.Intersect, true);
        if (_look.Stripes > 0.01f)
        {
            // Tabby rings, darker towards the tip.
            for (int i = 3; i <= n; i += 3)
            {
                var across = Shapes.Normalize(lower[i] - upper[i]);
                Stroke(Alpha(_look.Stripe, (0.5f + 0.3f * i / n) * _look.Stripes), 2.6f + 0.6f * i / n);
                c.DrawLine(upper[i] - Shapes.Scale(across, 2f), lower[i] + Shapes.Scale(across, 2f), _stroke);
            }
        }
        Stroke(Alpha(_near.Light, 0.45f), 0.8f);
        for (int i = 1; i < n - 1; i += 2)
        {
            var q = Shapes.Lerp(upper[i], lower[i], 0.35f);
            var r = Shapes.Lerp(upper[i + 2], lower[i + 2], 0.45f);
            c.DrawLine(q, r, _stroke);
        }
        c.Restore();
        StrokePath(c, path, Alpha(_near.Outline, 0.48f), 1.05f);
    }

    void DrawCollar(SKCanvas c, Skeleton s, PetPose p, float lie, float curl)
    {
        if (_look.Accessory != AccessoryKind.Bell || curl > 0.95f) return;
        var col = _look.AccessoryColor;
        float turn = Math.Clamp(p.HeadTurn, -1f, 1f);
        c.Save();
        c.Translate(s.Head.X, s.Head.Y);
        c.RotateDegrees(s.HeadAngle);
        c.Translate(turn * 5f - 1f, 28.5f - 3f * lie);
        int layer = -1;
        if (curl > 0.01f)
        {
            _layer.Color = Alpha(SKColors.White, 1f - curl);
            layer = c.SaveLayer(_layer);
        }
        // The collar disappears into the long fur of the ruff; only a little ribbon and the bell peek out.
        var dark = Darken(col, 0.72f);
        using (var sh = SKShader.CreateLinearGradient(new SKPoint(-12, 0), new SKPoint(12, 0),
            [Alpha(dark, 0f), dark, dark, Alpha(dark, 0f)], [0f, 0.32f, 0.68f, 1f], SKShaderTileMode.Clamp))
        {
            Stroke(SKColors.White, 4.2f);
            _stroke.Shader = sh;
            c.DrawPath(_band, _stroke);
        }
        using (var sh = SKShader.CreateLinearGradient(new SKPoint(-12, 0), new SKPoint(12, 0),
            [Alpha(col, 0f), col, col, Alpha(col, 0f)], [0f, 0.32f, 0.68f, 1f], SKShaderTileMode.Clamp))
        {
            Stroke(SKColors.White, 2.8f);
            _stroke.Shader = sh;
            c.DrawPath(_band, _stroke);
        }
        _stroke.Shader = null;

        // A little golden bell under the chin.
        FillCircle(c, 0, 4.4f, 3.9f, _bellShader);
        Stroke(new SKColor(0x8A, 0x5E, 0x0C), 0.85f);
        c.DrawCircle(0, 4.4f, 3.9f, _stroke);
        c.DrawLine(0, 5.6f, 0, 8.0f, _stroke);
        c.DrawLine(-2.6f, 5.2f, 2.6f, 5.2f, _stroke);
        _fill.Color = new SKColor(0x4A, 0x32, 0x08);
        c.DrawCircle(0, 6.0f, 0.9f, _fill);
        _fill.Color = Alpha(SKColors.White, 0.8f);
        c.DrawOval(Oval(-1.4f, 2.8f, 1.1f, 0.7f), _fill);
        if (layer >= 0) c.RestoreToCount(layer);
        c.Restore();
    }

    void DrawBow(SKCanvas c, float turn)
    {
        if (_look.Accessory != AccessoryKind.Bow) return;
        var col = _look.AccessoryColor;
        c.Save();
        c.Translate(-13f + turn * 3f, -21f);
        c.RotateDegrees(-22f);
        c.Scale(0.85f);
        // Ribbon tails, loops, then the knot on top.
        Stroke(Darken(col, 0.8f), 2.6f);
        c.DrawLine(-1, 1, -4.5f, 8f, _stroke);
        c.DrawLine(1, 1, 4.5f, 8.5f, _stroke);
        FillPath(c, _bowLoops, _bowShader);
        StrokePath(c, _bowLoops, Alpha(Darken(col, 0.55f), 0.85f), 0.9f);
        _fill.Color = Alpha(SKColors.White, 0.45f);
        c.DrawOval(Oval(-6.5f, -2.6f, 2.2f, 1.0f), _fill);
        c.DrawOval(Oval(6.0f, -2.6f, 2.2f, 1.0f), _fill);
        FillCircle(c, 0, 0, 2.6f, _bowShader);
        Stroke(Alpha(Darken(col, 0.55f), 0.85f), 0.8f);
        c.DrawCircle(0, 0, 2.6f, _stroke);
        c.Restore();
    }

    void DrawHead(SKCanvas c, Skeleton s, PetPose p)
    {
        float turn = Math.Clamp(p.HeadTurn, -1f, 1f);
        float lift = Math.Clamp(p.HeadLift, -1f, 1f);

        c.Save();
        c.Translate(s.Head.X, s.Head.Y);
        c.RotateDegrees(s.HeadAngle);
        float puff = 1f + 0.1f * Math.Clamp(p.Puff, 0, 1);

        // Ears sit behind the crown fur.
        DrawEar(c, p, right: false, turn);
        DrawEar(c, p, right: true, turn);

        if (puff != 1f)
        {
            c.Save();
            c.Scale(puff);
            Blit(c, _sHead!);
            c.Restore();
        }
        else Blit(c, _sHead!);

        if (_look.Stripes > 0.01f)
        {
            // Tabby markings ride on the face as it turns, clipped to the head.
            c.Save();
            c.ClipPath(_head, SKClipOperation.Intersect, true);
            c.Translate(turn * 6f, -lift * 1.2f);
            c.Scale(1f - 0.1f * MathF.Abs(turn), 1f);
            Blit(c, _sHeadMarks!);
            c.Restore();
        }

        // Lighter mask around the eyes and muzzle that follows the turn of the head.
        c.Save();
        c.Translate(turn * 5.6f, 4.5f - lift * 1.5f);
        c.Scale(20f * (1f - 0.08f * MathF.Abs(turn)), 19f);
        _fill.Shader = _faceMask;
        _fill.Color = SKColors.White;
        c.DrawCircle(0, 0, 1f, _fill);
        _fill.Shader = null;
        c.Restore();

        DrawFace(c, p, turn, lift);
        DrawBow(c, turn);
        c.Restore();
    }

    void DrawEar(SKCanvas c, PetPose p, bool right, float turn)
    {
        float side = right ? 1f : -1f;
        float lift = Math.Clamp(p.EarLift, -1f, 1f);
        // Big upright kitten ears; flattened ears swing out sideways.
        float rot = -15f + (lift > 0 ? lift * 9f : lift * 50f)
                    + p.EarAsym * 15f * (right ? -1f : 1f)
                    - (right ? p.EarFlapR : p.EarFlapL);
        float far = MathF.Max(0f, side * turn);
        float x = side * (16.5f - 3.5f * far) + turn * 2.5f;
        float y = -17.5f + MathF.Max(0, -lift) * 3.5f;

        c.Save();
        c.Translate(x, y);
        if (right) c.Scale(-1f, 1f);
        c.RotateDegrees(rot);
        c.Scale(1f - 0.18f * far, 1f + (lift > 0 ? lift * 0.06f : lift * 0.3f));
        Blit(c, _sEar!);
        c.Restore();
    }

    void DrawFace(SKCanvas c, PetPose p, float turn, float lift)
    {
        float squeeze = 1f - 0.1f * MathF.Abs(turn);
        float eyeY = -3.2f - lift * 2.2f;
        float exL = -13.4f * squeeze + turn * 6.5f, exR = 13.4f * squeeze + turn * 6.5f;
        float sxL = 1f - 0.22f * MathF.Max(0, -turn), sxR = 1f - 0.22f * MathF.Max(0, turn);
        float nx = turn * 8.5f, ny = 5.6f - lift * 1.2f;

        // Blush.
        if (p.Blush > 0.01f)
        {
            _fill.Shader = _blush;
            _fill.Color = Alpha(SKColors.White, Math.Clamp(p.Blush, 0, 1));
            foreach (var (ex, side) in new[] { (exL, -1f), (exR, 1f) })
            {
                c.Save();
                c.Translate(ex + side * 5f, eyeY + 11f);
                c.Scale(5.4f, 3.2f);
                c.DrawCircle(0, 0, 1f, _fill);
                c.Restore();
            }
            _fill.Shader = null;
        }

        DrawEye(c, exL, eyeY, sxL, p, p.EyeOpen * p.EyeOpenL, left: true);
        DrawEye(c, exR, eyeY, sxR, p, p.EyeOpen * p.EyeOpenR, left: false);
        DrawBrowWhiskers(c, exL, eyeY, -1f, p);
        DrawBrowWhiskers(c, exR, eyeY, 1f, p);
        if (p.Glasses > 0.01f) DrawGlasses(c, exL, exR, eyeY, Math.Clamp(p.Glasses, 0f, 1f));

        // Puffy whisker pads, chin and the little "ω" mouth.
        _fill.Shader = _pad;
        _fill.Color = SKColors.White;
        foreach (var side in new[] { -1f, 1f })
        {
            c.Save();
            c.Translate(nx + side * 4.3f * squeeze, ny + 6.6f);
            c.Scale(5.2f * squeeze, 4.1f);
            c.DrawCircle(0, 0, 1f, _fill);
            c.Restore();
        }
        c.Save();
        c.Translate(nx, ny + 11.8f);
        c.Scale(4.4f, 2.6f);
        c.DrawCircle(0, 0, 1f, _fill);
        c.Restore();
        _fill.Shader = null;
        _fill.Color = Alpha(_near.Deep, 0.35f);
        foreach (var side in new[] { -1f, 1f })
            foreach (var (dx, dy) in new[] { (3.2f, 5.6f), (5.4f, 6.4f), (3.8f, 7.8f) })
                c.DrawCircle(nx + side * dx * squeeze, ny + dy, 0.42f, _fill);

        DrawMouth(c, nx, ny + 2.4f, p);
        DrawWhiskers(c, nx, ny, squeeze, p);

        c.Save();
        c.Translate(nx, ny);
        c.Scale(1f - 0.12f * MathF.Abs(turn), 1f);
        FillPath(c, _nose, _noseShader);
        StrokePath(c, _nose, Alpha(Darken(_look.Nose, 0.62f), 0.8f), 0.75f);
        _fill.Color = Alpha(SKColors.White, 0.7f);
        c.DrawOval(Oval(-1.1f, -1.7f, 1.2f, 0.55f), _fill);
        c.Restore();
    }

    void DrawEye(SKCanvas c, float x, float y, float sx, PetPose p, float open, bool left)
    {
        float size = Math.Clamp(p.EyeSize, 0.7f, 1.4f);
        float rx = 8.2f * sx * size, ry = 8.8f * size;
        float happy = Math.Clamp(p.HappyEyes, 0f, 1f);
        open = Math.Clamp(open, 0f, 1f);
        var rim = _look.EyeRim;

        if (happy < 0.99f && open > 0.06f)
        {
            // Nearly-closed eyes cross-fade into the peaceful closed curve (a sliver of iris reads as "smug").
            float vis = SmoothStep(0.1f, 0.3f, open);
            int layer = -1;
            float alpha = (1f - happy) * vis;
            if (alpha < 0.99f)
            {
                _layer.Color = Alpha(SKColors.White, alpha);
                layer = c.SaveLayer(_layer);
            }
            float lidY = -1f + 2f * (1f - open) * 0.94f; // in unit-eye space
            float lidPx = y + lidY * ry;
            // Heavy lids droop towards the outer corner, which reads as sleepy and soft rather than bored.
            float droop = 0.24f * ry * (1f - open);
            float yl = lidPx + (left ? droop : -0.35f * droop), yr = lidPx + (left ? -0.35f * droop : droop);
            float ex = rx + 1.5f, yc = 2f * (lidPx - 0.8f) - 0.5f * (yl + yr);
            using var lb = new SKPathBuilder();
            lb.MoveTo(x - ex, yl);
            lb.QuadTo(x, yc, x + ex, yr);
            using var lid = lb.Detach();

            c.Save();
            if (open < 0.995f)
            {
                using var below = new SKPathBuilder();
                below.MoveTo(x - ex, yl);
                below.QuadTo(x, yc, x + ex, yr);
                below.LineTo(x + ex, y + ry + 3f);
                below.LineTo(x - ex, y + ry + 3f);
                below.Close();
                using var belowPath = below.Detach();
                c.ClipPath(belowPath, SKClipOperation.Intersect, true);
            }
            c.Save();
            c.Translate(x, y);
            c.Scale(rx, ry);
            c.ClipPath(_unitCircle, SKClipOperation.Intersect, true);

            FillCircle(c, 0, 0, 1f, _iris);
            Stroke(Alpha(_look.IrisDark, 0.28f), 0.05f);
            c.DrawPath(_irisLines, _stroke);
            Stroke(Alpha(_look.IrisDark, 0.55f), 0.12f);
            c.DrawCircle(0, 0, 0.93f, _stroke);

            // Vertical cat pupil that widens when excited or hunting.
            float lx = Math.Clamp(p.LookX, -1f, 1f), ly = Math.Clamp(p.LookY, -1f, 1f);
            float pupil = Math.Clamp(p.PupilSize, 0f, 1f);
            float pw = Mixf(0.15f, 0.64f, pupil), ph = Mixf(0.84f, 0.7f, pupil);
            _fill.Color = _look.Pupil;
            c.DrawOval(Oval(lx * 0.26f, ly * 0.18f + 0.04f, pw, ph), _fill);
            _fill.Color = Alpha(_look.IrisLight, 0.4f);
            c.DrawOval(Oval(lx * 0.12f, 0.66f, 0.6f, 0.24f), _fill);

            float spark = 1f + 0.35f * Math.Clamp(p.Sparkle, 0, 1);
            float hlY = MathF.Max(-0.4f, lidY + 0.32f + 0.3f * droop / ry);
            float hlScale = open < 0.8f ? 0.78f : 1f;
            _fill.Color = Alpha(SKColors.White, 0.97f);
            c.DrawOval(Oval(-0.3f + lx * 0.08f, hlY, 0.4f * spark * hlScale, 0.34f * spark * hlScale), _fill);
            _fill.Color = Alpha(SKColors.White, 0.85f);
            c.DrawOval(Oval(0.4f, 0.36f, 0.17f * spark, 0.15f * spark), _fill);
            if (p.Sparkle > 0.05f)
            {
                _fill.Color = Alpha(SKColors.White, 0.8f * Math.Clamp(p.Sparkle, 0, 1));
                c.DrawOval(Oval(0.08f, -0.62f, 0.09f, 0.08f), _fill);
            }
            c.Restore();

            // Dark "eyeliner" rim, clipped to the visible part, and the eyelid.
            Stroke(Alpha(rim, 0.95f), 1.3f);
            c.DrawOval(Oval(x, y, rx, ry), _stroke);
            c.Restore();
            if (open < 0.995f)
            {
                c.Save();
                c.Translate(x, y);
                c.Scale(rx + 0.8f, ry + 0.8f);
                c.ClipPath(_unitCircle, SKClipOperation.Intersect, true);
                c.Scale(1f / (rx + 0.8f), 1f / (ry + 0.8f));
                c.Translate(-x, -y);
                Stroke(Alpha(_near.Fur, 0.9f), 2.6f);
                c.Save();
                c.Translate(0, -1.3f);
                c.DrawPath(lid, _stroke);
                c.Restore();
                Stroke(Alpha(rim, 0.95f), 1.3f);
                c.DrawPath(lid, _stroke);
                c.Restore();
            }
            if (layer >= 0) c.RestoreToCount(layer);
        }

        float closedA = (1f - SmoothStep(0.1f, 0.3f, open)) * (1f - happy);
        if (closedA > 0.01f)
        {
            // Peacefully closed, with a little flick of lashes at the outer corner.
            using var b = new SKPathBuilder();
            b.MoveTo(x - rx * 1.05f, y + ry * 0.05f);
            b.QuadTo(x, y + ry * 0.7f, x + rx * 1.05f, y + ry * 0.05f);
            using var path = b.Detach();
            Stroke(Alpha(rim, 0.9f * closedA), 1.5f);
            c.DrawPath(path, _stroke);
            float side = left ? -1f : 1f;
            c.DrawLine(x + side * rx * 0.95f, y + ry * 0.12f, x + side * rx * 1.3f, y - ry * 0.15f, _stroke);
        }

        if (happy > 0.01f)
        {
            using var b = new SKPathBuilder();
            b.MoveTo(x - rx * 1.05f, y + ry * 0.3f);
            b.QuadTo(x, y - ry * 0.85f, x + rx * 1.05f, y + ry * 0.3f);
            using var path = b.Detach();
            Stroke(Alpha(rim, happy), 2.1f);
            c.DrawPath(path, _stroke);
        }
    }

    void DrawBrowWhiskers(SKCanvas c, float ex, float ey, float side, PetPose p)
    {
        float bx = ex + side * 1.5f, by = ey - 11.8f - p.BrowRaise * 2.2f + p.BrowSad * 0.8f;
        for (int i = 0; i < 2; i++)
        {
            float ox = side * (i * 2.6f - 1f);
            using var b = new SKPathBuilder();
            b.MoveTo(bx + ox, by + i * 0.8f);
            b.QuadTo(bx + ox + side * 3f, by - 3.4f - p.BrowRaise * 1.2f + i, bx + ox + side * (6.5f + i), by - 4.6f + i * 1.6f - p.BrowSad * 1.4f);
            using var path = b.Detach();
            StrokePath(c, path, Alpha(_near.Outline, 0.22f), 1.1f);
            StrokePath(c, path, Alpha(_look.Whisker, 0.85f), 0.6f);
        }
    }

    void DrawWhiskers(SKCanvas c, float nx, float ny, float squeeze, PetPose p)
    {
        float w = Math.Clamp(p.Whisker, -1f, 1f);
        float rot = w > 0 ? -0.2f * w : 0.32f * -w;
        float len = w > 0 ? 1f + 0.08f * w : 1f + 0.25f * w;
        foreach (var side in new[] { -1f, 1f })
        {
            for (int i = 0; i < 3; i++)
            {
                float a = -0.2f + 0.21f * i + rot;
                float l = (24f - i * 2f) * len * (side * p.HeadTurn > 0 ? 1f - 0.25f * MathF.Abs(p.HeadTurn) : 1f);
                var start = new SKPoint(nx + side * 6.4f * squeeze, ny + 5.6f + i * 1.3f);
                var dir = new SKPoint(side * MathF.Cos(a), MathF.Sin(a));
                var end = start + Shapes.Scale(dir, l);
                var ctrl = start + Shapes.Scale(dir, l * 0.55f) + new SKPoint(0, -1.6f + i * 0.4f);
                end += new SKPoint(0, 2.2f);
                using var b = new SKPathBuilder();
                b.MoveTo(start);
                b.QuadTo(ctrl, end);
                using var path = b.Detach();
                StrokePath(c, path, Alpha(_near.Outline, 0.2f), 1.15f);
                StrokePath(c, path, Alpha(_look.Whisker, 0.9f), 0.62f);
            }
        }
    }

    void DrawMouth(SKCanvas c, float mx, float my, PetPose p)
    {
        float open = Math.Clamp(p.MouthOpen, 0f, 1f);
        float tongue = Math.Clamp(p.Tongue, 0f, 1f);
        float smile = Math.Clamp(p.Smile, -1f, 1f);
        var line = Mix(_near.Outline, new SKColor(0x3A, 0x22, 0x1A), 0.5f);

        if (open > 0.02f)
        {
            using var b = new SKPathBuilder();
            float w = 3.6f + 1.6f * open;
            b.MoveTo(mx - w, my + 3.6f);
            b.CubicTo(mx - w * 0.9f, my + 3.6f + 9f * open, mx + w * 0.9f, my + 3.6f + 9f * open, mx + w, my + 3.6f);
            b.QuadTo(mx, my + 5f, mx - w, my + 3.6f);
            b.Close();
            using var cavity = b.Detach();
            _fill.Shader = null;
            _fill.Color = _look.MouthInside;
            c.DrawPath(cavity, _fill);
            c.Save();
            c.ClipPath(cavity, SKClipOperation.Intersect, true);
            _fill.Color = _look.Tongue;
            c.DrawOval(Oval(mx, my + 3.6f + 7.4f * open, 3.4f + open, 3f * open + 0.4f), _fill);
            // Two tiny fangs.
            _fill.Color = SKColors.White;
            foreach (var side in new[] { -1f, 1f })
            {
                using var fb = new SKPathBuilder();
                fb.MoveTo(mx + side * (w - 1.8f), my + 3.8f);
                fb.LineTo(mx + side * (w - 0.6f), my + 3.8f);
                fb.LineTo(mx + side * (w - 1.2f), my + 5.6f + 1.4f * open);
                fb.Close();
                using var fang = fb.Detach();
                c.DrawPath(fang, _fill);
            }
            c.Restore();
            StrokePath(c, cavity, Alpha(line, 0.75f), 0.85f);
        }

        if (tongue > 0.02f)
        {
            // A little "blep".
            float tl = 1.6f + 5f * tongue;
            float top = my + 4.2f + 4f * open;
            using var b = new SKPathBuilder();
            b.MoveTo(mx - 2.6f, top);
            b.CubicTo(mx - 3f, top + tl, mx + 3f, top + tl, mx + 2.6f, top);
            b.Close();
            using var path = b.Detach();
            using var sh = SKShader.CreateLinearGradient(new SKPoint(0, top), new SKPoint(0, top + tl),
                [Darken(_look.Tongue, 0.88f), _look.Tongue, Mix(_look.Tongue, SKColors.White, 0.25f)], [0f, 0.4f, 1f], SKShaderTileMode.Clamp);
            FillPath(c, path, sh);
            StrokePath(c, path, Alpha(Darken(_look.Tongue, 0.6f), 0.8f), 0.7f);
        }

        using (var b = new SKPathBuilder())
        {
            b.MoveTo(mx, my);
            b.LineTo(mx, my + 3.4f);
            b.MoveTo(mx, my + 3.4f);
            b.QuadTo(mx - 2.4f, my + 5.8f + smile * 1.1f, mx - 4.8f, my + 3.8f - smile * 0.6f);
            b.MoveTo(mx, my + 3.4f);
            b.QuadTo(mx + 2.4f, my + 5.8f + smile * 1.1f, mx + 4.8f, my + 3.8f - smile * 0.6f);
            using var path = b.Detach();
            StrokePath(c, path, Alpha(line, 0.9f), 1.05f);
        }
    }

    // ---- helpers ---------------------------------------------------------------------------

    void FillPath(SKCanvas c, SKPath path, SKShader shader, bool dispose = false)
    {
        _fill.Shader = shader;
        _fill.Color = SKColors.White;
        c.DrawPath(path, _fill);
        _fill.Shader = null;
        if (dispose) shader.Dispose();
    }

    void FillRect(SKCanvas c, SKRect r, SKShader shader, bool dispose = false)
    {
        _fill.Shader = shader;
        _fill.Color = SKColors.White;
        c.DrawRect(r, _fill);
        _fill.Shader = null;
        if (dispose) shader.Dispose();
    }

    void FillCircle(SKCanvas c, float x, float y, float r, SKShader shader)
    {
        _fill.Shader = shader;
        _fill.Color = SKColors.White;
        c.DrawCircle(x, y, r, _fill);
        _fill.Shader = null;
    }

    void StrokePath(SKCanvas c, SKPath path, SKColor color, float width)
    {
        Stroke(color, width);
        c.DrawPath(path, _stroke);
    }

    void Stroke(SKColor color, float width)
    {
        _stroke.Shader = null;
        _stroke.Color = color;
        _stroke.StrokeWidth = width;
    }

    static SKShader LegShader(Palette pal, float width) => SKShader.CreateLinearGradient(
        new SKPoint(-width / 2, 0), new SKPoint(width / 2, 0),
        [pal.Shade, pal.Fur, Mix(pal.Fur, pal.Light, 0.55f)], [0f, 0.55f, 1f], SKShaderTileMode.Clamp);

    static SKShader PawShader(Palette pal) => SKShader.CreateLinearGradient(new SKPoint(0, -6), new SKPoint(0, 6),
        [Mix(pal.Cream, pal.Light, 0.4f), Mix(pal.Fur, pal.Cream, 0.5f), pal.Shade], [0f, 0.5f, 1f], SKShaderTileMode.Clamp);

    static SKRect Oval(float cx, float cy, float rx, float ry) => new(cx - rx, cy - ry, cx + rx, cy + ry);

    static SKShader Radial(SKPoint center, float radius, SKColor[] colors, float[] pos) =>
        SKShader.CreateRadialGradient(center, radius, colors, pos, SKShaderTileMode.Clamp);

    static SKPoint Bezier(SKPoint a, SKPoint b, SKPoint c, SKPoint d, float t)
    {
        float u = 1 - t;
        return new SKPoint(
            u * u * u * a.X + 3 * u * u * t * b.X + 3 * u * t * t * c.X + t * t * t * d.X,
            u * u * u * a.Y + 3 * u * u * t * b.Y + 3 * u * t * t * c.Y + t * t * t * d.Y);
    }

    static SKPoint BezierTangent(SKPoint a, SKPoint b, SKPoint c, SKPoint d, float t)
    {
        float u = 1 - t;
        return new SKPoint(
            3 * u * u * (b.X - a.X) + 6 * u * t * (c.X - b.X) + 3 * t * t * (d.X - c.X),
            3 * u * u * (b.Y - a.Y) + 6 * u * t * (c.Y - b.Y) + 3 * t * t * (d.Y - c.Y));
    }

    public static SKColor Mix(SKColor a, SKColor b, float t) => new(
        (byte)(a.Red + (b.Red - a.Red) * t), (byte)(a.Green + (b.Green - a.Green) * t),
        (byte)(a.Blue + (b.Blue - a.Blue) * t), (byte)(a.Alpha + (b.Alpha - a.Alpha) * t));

    public static SKColor Alpha(SKColor c, float a) => c.WithAlpha((byte)Math.Clamp(a * 255f, 0f, 255f));

    public static SKColor Darken(SKColor c, float k) => new((byte)(c.Red * k), (byte)(c.Green * k), (byte)(c.Blue * k), c.Alpha);

    public void Dispose()
    {
        foreach (var r in _lookResources) r.Dispose();
        foreach (var sp in new[] { _sHead, _sHeadMarks, _sBody, _sChest, _sThighNear, _sThighFar, _sEar, _sFrontBody, _sHaunch }) sp?.Dispose();
        foreach (var p in new[] { _ruff, _head, _headStrands, _ruffStrands, _body, _bodyStrands, _chest, _chestStrands, _thigh, _thighStrands,
                     _thighCrease, _ear, _earInner, _earTufts, _nose, _unitCircle, _band, _bowLoops, _irisLines,
                     _frontBody, _frontBodyStrands, _frontChest, _frontChestStrands, _haunch })
            p.Dispose();
        _fill.Dispose();
        _stroke.Dispose();
        _layer.Dispose();
    }
}

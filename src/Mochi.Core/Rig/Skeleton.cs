using SkiaSharp;

namespace Mochi.Core.Rig;

/// <summary>
/// Joint positions in model space (units ≈ logical pixels, facing right, ground at y = 0, up is −y)
/// derived from a <see cref="PetPose"/>. Posture modes are blended continuously.
/// </summary>
public struct Skeleton
{
    public const float PawGroundY = -5.2f;
    public const float HeadRadius = 30f;

    public SKMatrix Body;
    public SKPoint BodyCenter;
    public float BodyAngle, BodySx, BodySy;
    public SKPoint Head;
    public float HeadAngle;
    public SKPoint ShoulderNear, ShoulderFar, HipNear, HipFar;
    public SKPoint FrontPawNear, FrontPawFar, HindPawNear, HindPawFar;
    public float FrontPawNearLift, FrontPawFarLift, HindPawNearLift, HindPawFarLift; // 0 grounded .. 1 sole showing
    public SKPoint ThighNear, ThighFar;
    public float ThighAngle;
    public SKPoint TailBase;
    public float TailAngle; // degrees, 180 = straight back, 270 = straight up
    public float Airborne;

    public static Skeleton Compute(PetPose p)
    {
        var s = new Skeleton();
        float sit = Clamp01(p.Sit), lie = Clamp01(p.Lie), bow = Clamp01(p.Bow), held = Clamp01(p.Held);
        float crouch = Clamp01(p.Crouch), arch = Clamp01(p.Arch), leap = Clamp01(p.Leap), curl = Clamp01(p.Curl) * lie;
        float front = Clamp01(p.Front);
        float stand = Clamp01(1f - MathF.Max(MathF.Max(MathF.Max(sit, lie), MathF.Max(bow, held)), MathF.Max(MathF.Max(crouch, leap), front)));

        float walk = Clamp01(p.GaitAmount) * MathF.Max(stand, crouch * 0.8f);
        float run = Clamp01(p.GaitAmount - 1f) * stand;
        float ph = p.GaitPhase;

        // Body centre / angle per posture.
        var c = new SKPoint(-4, -29);
        float ang = 0, sx = 1, sy = 1;
        c = L(c, new SKPoint(-8, -27), sit); ang = Lf(ang, -50, sit);
        c = L(c, new SKPoint(-2, -19), lie); ang = Lf(ang, 0, lie); sx = Lf(sx, 1.06f, lie); sy = Lf(sy, 0.95f, lie);
        c = L(c, new SKPoint(-4, -18), curl); sx = Lf(sx, 0.98f, curl); sy = Lf(sy, 1.02f, curl);
        c = L(c, new SKPoint(-4, -31), bow); ang = Lf(ang, 17, bow);
        c = L(c, new SKPoint(-2, -18), crouch); ang = Lf(ang, 4, crouch); sx = Lf(sx, 1.05f, crouch); sy = Lf(sy, 0.94f, crouch);
        c = L(c, new SKPoint(-4, -41), arch); sx = Lf(sx, 0.9f, arch); sy = Lf(sy, 1.22f, arch);
        c = L(c, new SKPoint(0, -40), leap); ang = Lf(ang, -10, leap); sx = Lf(sx, 1.16f, leap); sy = Lf(sy, 0.92f, leap);
        c = L(c, new SKPoint(0, -42), held); ang = Lf(ang, -80, held); sx = Lf(sx, 1.18f, held); // "liquid" kitten
        c = L(c, new SKPoint(0, -27), front); ang = Lf(ang, 0, front); sx = Lf(sx, 1f, front); sy = Lf(sy, 1f, front);

        // Gait bob and run bounce/pitch; a cat's walk is smooth, so the bob is small.
        float bob = -1.0f * walk * MathF.Abs(MathF.Sin(ph)) * (1f - run);
        float bounce = -5f * run * MathF.Abs(MathF.Sin(ph));
        float pitch = 6f * run * MathF.Sin(ph);
        c.Y += bob + bounce;
        ang += pitch + p.BodyTilt + p.Wiggle;
        sx *= 1f + 0.06f * run * MathF.Cos(ph);

        float puff = 1f + 0.1f * Clamp01(p.Puff);
        sy *= (1f + 0.022f * p.Breath) * puff;
        sx *= (1f + 0.008f * p.Breath) * puff;
        sx *= 1f + 0.18f * p.Squash;
        sy *= 1f - 0.18f * p.Squash;
        c.Y += 4f * p.Squash;

        s.Airborne = MathF.Max(0, p.Hop);
        c.Y -= s.Airborne;

        s.BodyCenter = c; s.BodyAngle = ang; s.BodySx = sx; s.BodySy = sy;
        s.Body = SKMatrix.CreateTranslation(c.X, c.Y)
            .PreConcat(SKMatrix.CreateRotationDegrees(ang))
            .PreConcat(SKMatrix.CreateScale(sx, sy));

        s.ShoulderNear = s.Body.MapPoint(16, 6);
        s.ShoulderFar = s.Body.MapPoint(11, 4);
        s.HipNear = s.Body.MapPoint(-18, 6);
        s.HipFar = s.Body.MapPoint(-22, 4);
        s.ThighNear = s.Body.MapPoint(-18, 3);
        s.ThighFar = s.Body.MapPoint(-22, 1);
        s.ThighAngle = ang * 0.35f;
        s.TailBase = s.Body.MapPoint(-29, -7);

        // Head.
        var h = new SKPoint(22, -60);
        h = L(h, new SKPoint(14, -76), sit);
        h = L(h, new SKPoint(24, -44), lie);
        h = L(h, new SKPoint(20, -30), curl);
        h = L(h, new SKPoint(31, -40), bow);
        h = L(h, new SKPoint(31, -35), crouch);
        h = L(h, new SKPoint(24, -66), arch);
        h = L(h, new SKPoint(33, -56), leap);
        h = L(h, new SKPoint(6, -82), held);
        h = L(h, new SKPoint(0, -74), front);
        h.Y += bob * 0.6f + bounce * 0.85f - s.Airborne + 4f * p.Squash;
        h.Y -= p.HeadLift * 3.5f;
        h.X += p.HeadDx + run * 3f;
        h.Y += p.HeadDy + 2f * run * MathF.Sin(ph + 0.6f);
        s.Head = h;
        s.HeadAngle = p.HeadTilt + 16f * curl - 0.5f * p.Wiggle;

        // Paws.
        float gy = PawGroundY - s.Airborne;
        const float aWalk = 6f, lWalk = 4.5f, aRun = 11f, lRun = 8f;
        SKPoint Gait(SKPoint top, float offset, float runOffset)
        {
            float phw = ph + offset, phr = ph + runOffset;
            float dx = walk * (1f - run) * aWalk * MathF.Sin(phw) + run * aRun * MathF.Sin(phr);
            float up = walk * (1f - run) * lWalk * MathF.Max(0, MathF.Cos(phw)) + run * lRun * MathF.Max(0, MathF.Cos(phr));
            return new SKPoint(top.X + 2 + dx, gy - up);
        }

        var fn = Gait(s.ShoulderNear, 0, 0);
        var ff = Gait(s.ShoulderFar, MathF.PI, 0.5f);
        var hn = Gait(s.HipNear, MathF.PI, MathF.PI);
        var hf = Gait(s.HipFar, 0, MathF.PI + 0.5f);
        hn.X -= 2; hf.X -= 2;

        // Sitting: front paws together under the chest, hind feet in front of the haunch.
        fn = L(fn, new SKPoint(s.ShoulderNear.X + 4, gy), sit);
        ff = L(ff, new SKPoint(s.ShoulderFar.X + 4, gy), sit);
        hn = L(hn, new SKPoint(s.ThighNear.X + 13, gy), sit);
        hf = L(hf, new SKPoint(s.ThighFar.X + 11, gy), sit);

        // Loaf: paws tucked, just the toes peeking out under the chest.
        fn = L(fn, new SKPoint(s.ShoulderNear.X + 9, gy + 1.5f), lie);
        ff = L(ff, new SKPoint(s.ShoulderFar.X + 7, gy + 1.5f), lie);
        hn = L(hn, new SKPoint(s.ThighNear.X + 10, gy + 1f), lie);
        hf = L(hf, new SKPoint(s.ThighFar.X + 8, gy + 1f), lie);

        // Stretch: front paws reach far forward.
        fn = L(fn, new SKPoint(s.ShoulderNear.X + 22, gy), bow);
        ff = L(ff, new SKPoint(s.ShoulderFar.X + 19, gy), bow);

        // Hunting crouch: front paws just ahead of the shoulders, hind legs folded under.
        fn = L(fn, new SKPoint(s.ShoulderNear.X + 7, gy), crouch);
        ff = L(ff, new SKPoint(s.ShoulderFar.X + 6, gy), crouch);
        hn = L(hn, new SKPoint(s.ThighNear.X + 9, gy), crouch);
        hf = L(hf, new SKPoint(s.ThighFar.X + 7, gy), crouch);

        // Arched: stiff straight legs right under the body.
        fn = L(fn, new SKPoint(s.ShoulderNear.X + 1, gy), arch);
        ff = L(ff, new SKPoint(s.ShoulderFar.X + 1, gy), arch);
        hn = L(hn, new SKPoint(s.HipNear.X - 1, gy), arch);
        hf = L(hf, new SKPoint(s.HipFar.X - 1, gy), arch);

        // Pounce: front paws reach ahead, hind legs stream behind.
        fn = L(fn, s.ShoulderNear + new SKPoint(20, -2), leap);
        ff = L(ff, s.ShoulderFar + new SKPoint(17, 1), leap);
        hn = L(hn, s.HipNear + new SKPoint(-17, 9), leap);
        hf = L(hf, s.HipFar + new SKPoint(-14, 11), leap);

        // Held under the arms: front paws up beside the face, hind legs dangle (with an optional paddle).
        float pad = p.PawPaddle * 4f;
        float sw = -p.SwingAngle * MathF.PI / 180f * 0.6f;
        var dangleH = Shapes.Rotate(new SKPoint(1, 22), sw);
        fn = L(fn, s.ShoulderNear + new SKPoint(9, -4), held);
        ff = L(ff, s.ShoulderFar + new SKPoint(1, -8), held);
        hn = L(hn, s.HipNear + dangleH + new SKPoint(pad * MathF.Sin(p.PaddlePhase + MathF.PI), 0), held);
        hf = L(hf, s.HipFar + dangleH + new SKPoint(pad * MathF.Sin(p.PaddlePhase), 0), held);

        // Square-on to the viewer: front paws side by side (or held up at the chest), hind feet either side.
        float up = Clamp01(p.PawsUp);
        fn = L(fn, L(new SKPoint(6.5f, gy), new SKPoint(5.5f, c.Y - 9f), up), front);
        ff = L(ff, L(new SKPoint(-6.5f, gy), new SKPoint(-5.5f, c.Y - 9f), up), front);
        hn = L(hn, new SKPoint(15.5f, gy), front);
        hf = L(hf, new SKPoint(-15.5f, gy), front);

        // Waving hello: the near paw up beside the face, waggling.
        if (p.Wave > 0.001f)
        {
            float wag = 3f * MathF.Sin(p.PaddlePhase);
            fn = L(fn, L(s.ShoulderNear + new SKPoint(16f + wag, -30f), new SKPoint(17f + wag, c.Y - 31f), front), p.Wave);
        }

        // Dream paddling while lying: paws twitch in place.
        if (p.PawPaddle > 0.01f && held < 0.5f)
        {
            fn.X += pad * 0.5f * MathF.Sin(p.PaddlePhase);
            ff.X += pad * 0.5f * MathF.Sin(p.PaddlePhase + MathF.PI);
        }

        // Kneading ("making biscuits"): front paws take turns pressing down.
        float kneadNear = 0, kneadFar = 0;
        if (p.Knead > 0.01f)
        {
            kneadNear = p.Knead * MathF.Max(0, MathF.Sin(p.PaddlePhase * 0.6f));
            kneadFar = p.Knead * MathF.Max(0, MathF.Sin(p.PaddlePhase * 0.6f + MathF.PI));
            fn += new SKPoint(1.5f * kneadNear, -5.5f * kneadNear);
            ff += new SKPoint(1.5f * kneadFar, -5.5f * kneadFar);
        }

        // Near front paw: swat/wave, or up at the mouth for grooming.
        if (p.FrontPawRaise > 0.001f)
            fn = L(fn, s.ShoulderNear + new SKPoint(24, -13), p.FrontPawRaise);
        if (p.PawToFace > 0.001f)
            fn = L(fn, h + new SKPoint(7f, 15f), p.PawToFace);

        s.FrontPawNear = fn; s.FrontPawFar = ff; s.HindPawNear = hn; s.HindPawFar = hf;
        s.FrontPawNearLift = MathF.Max(MathF.Max(held, leap), MathF.Max(MathF.Max(p.FrontPawRaise, p.PawToFace), MathF.Max(kneadNear * 0.6f, p.Wave)));
        s.FrontPawFarLift = MathF.Max(MathF.Max(held, leap), kneadFar * 0.6f);
        s.HindPawNearLift = MathF.Max(held, leap) * 0.6f;
        s.HindPawFarLift = MathF.Max(held, leap) * 0.6f;

        // Tail direction (degrees).
        float raise = Math.Clamp(p.TailRaise, -1f, 1f);
        float tail = 180f + 75f * raise;
        tail = Lf(tail, 172f + 12f * raise, sit);
        tail = Lf(tail, 176f + 8f * raise, lie);
        tail = Lf(tail, 240f + 15f * raise, bow);
        tail = Lf(tail, 182f + 20f * raise, crouch);
        tail = Lf(tail, 262f, arch);
        tail = Lf(tail, 188f, leap);
        tail = Lf(tail, 102f, held);
        tail += ang * 0.5f * (1f - held);
        s.TailAngle = tail;
        return s;
    }

    static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
    static float Lf(float a, float b, float t) => a + (b - a) * t;
    static SKPoint L(SKPoint a, SKPoint b, float t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
}

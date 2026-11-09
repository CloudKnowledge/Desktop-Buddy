using Mochi.Core.Behaviour;
using Mochi.Core.Rig;
using SkiaSharp;

namespace Mochi.Core.Play;

public enum BallState { Free, HeldByUser, InMouth }

/// <summary>
/// A yarn ball loose on the desktop, in physical screen pixels: gravity, soft bounces off the floor (top of
/// the taskbar), the monitor edges and the top of the screen, and rolling friction. A short strand of loose
/// yarn hangs off it as a tiny rope simulation.
/// </summary>
public sealed class YarnBall
{
    public const float Radius = 9f;          // model units
    public const float Gravity = 1900f;      // model units / s², same as the kitten
    const int StrandPoints = 9;
    const float StrandSegment = 3.4f;        // model units

    readonly SKPoint[] _strandPrev = new SKPoint[StrandPoints];

    public YarnBall(SKPoint position, float scale, IPetEnvironment env)
    {
        Position = position;
        Scale = scale;
        Floor = env.FloorAt(position);
        ResetStrand();
    }

    public SKPoint Position { get; set; }
    /// <summary>Physical pixels per second.</summary>
    public SKPoint Velocity { get; set; }
    /// <summary>Roll angle in radians (positive turns clockwise on screen).</summary>
    public float Angle { get; private set; }
    /// <summary>Radians per second while in the air.</summary>
    public float Spin { get; set; }
    /// <summary>Physical pixels per model unit; follows the kitten's size and the monitor DPI.</summary>
    public float Scale { get; set; }
    public BallState State { get; set; }
    public Floor Floor { get; private set; }
    public bool OnFloor { get; private set; }
    /// <summary>The loose strand, from where it leaves the ball to its end, in screen pixels.</summary>
    public SKPoint[] Strand { get; } = new SKPoint[StrandPoints];

    public float R => Radius * Scale;
    /// <summary>Speed in model units per second.</summary>
    public float SpeedUnits => Shapes.Length(Velocity) / Scale;
    public bool Resting => State == BallState.Free && OnFloor && MathF.Abs(Velocity.X) < 8f * Scale;

    /// <summary>Where the centre will be after <paramref name="t"/> seconds of free flight (ignoring bounces).</summary>
    public SKPoint PredictAt(float t) =>
        new(Position.X + Velocity.X * t, Position.Y + Velocity.Y * t + 0.5f * Gravity * Scale * t * t);

    /// <summary>Screen x where the ball will next touch down, if nothing catches it first.</summary>
    public float PredictLandingX()
    {
        if (OnFloor || State != BallState.Free) return Position.X;
        float g = Gravity * Scale, drop = MathF.Max(0f, Floor.Y - R - Position.Y), vy = Velocity.Y;
        float t = (-vy + MathF.Sqrt(vy * vy + 2f * g * drop)) / g;
        return Math.Clamp(Position.X + Velocity.X * t, Floor.Left + R, Floor.Right - R);
    }

    public void Step(float dt, IPetEnvironment env)
    {
        if (dt <= 0) return;
        Floor = env.FloorAt(Position);
        if (State == BallState.Free) Integrate(dt);
        else
        {
            OnFloor = false;
            Velocity = default;
        }
        StepStrand(dt);
    }

    void Integrate(float dt)
    {
        var f = Floor;
        float r = R;
        var v = new SKPoint(Velocity.X * (1f - 0.05f * dt), Velocity.Y + Gravity * Scale * dt);
        var p = Position + Shapes.Scale(v, dt);

        if (p.X < f.Left + r) { p.X = f.Left + r; v.X = MathF.Abs(v.X) * 0.6f; Spin = -Spin * 0.5f; }
        if (p.X > f.Right - r) { p.X = f.Right - r; v.X = -MathF.Abs(v.X) * 0.6f; Spin = -Spin * 0.5f; }
        if (p.Y < f.Top + r) { p.Y = f.Top + r; v.Y = MathF.Abs(v.Y) * 0.4f; }

        OnFloor = false;
        if (p.Y >= f.Y - r)
        {
            p.Y = f.Y - r;
            // Yarn is soft: it bounces a little, then rolls.
            if (v.Y > 140f * Scale) v.Y = -v.Y * 0.42f;
            else
            {
                v.Y = 0;
                OnFloor = true;
            }
        }

        if (OnFloor)
        {
            float decel = 140f * Scale * dt;
            v.X = MathF.Abs(v.X) <= decel ? 0f : (v.X - MathF.Sign(v.X) * decel) * (1f - 0.9f * dt);
            Spin = v.X / r;
        }
        Angle += Spin * dt;
        Position = p;
        Velocity = v;
    }

    void ResetStrand()
    {
        for (int i = 0; i < StrandPoints; i++)
        {
            Strand[i] = Position + new SKPoint((Radius + i * StrandSegment) * 0.75f * Scale, (Radius * 0.5f + i * StrandSegment * 0.6f) * Scale);
            _strandPrev[i] = Strand[i];
        }
    }

    void StepStrand(float dt)
    {
        float seg = StrandSegment * Scale;
        if (Shapes.Length(Strand[^1] - Position) > (Radius + StrandPoints * StrandSegment) * Scale * 3f) ResetStrand(); // teleported

        // The strand leaves the ball at a point that turns with it, so rolling winds and unwinds it.
        float a = Angle + 2.3f;
        var anchor = Position + new SKPoint(MathF.Cos(a) * R * 0.9f, MathF.Sin(a) * R * 0.9f);
        float g = Gravity * Scale * 0.6f * dt * dt;
        float floorY = Floor.Y - 0.8f * Scale;

        // Verlet integration with damping, then a few constraint passes.
        for (int i = 1; i < StrandPoints; i++)
        {
            var cur = Strand[i];
            var vel = Shapes.Scale(cur - _strandPrev[i], 0.94f);
            _strandPrev[i] = cur;
            Strand[i] = cur + vel + new SKPoint(0, g);
        }
        for (int iter = 0; iter < 4; iter++)
        {
            Strand[0] = anchor;
            for (int i = 1; i < StrandPoints; i++)
            {
                var d = Strand[i] - Strand[i - 1];
                float len = Shapes.Length(d);
                if (len < 1e-4f) continue;
                float diff = (len - seg) / len;
                if (i == 1) Strand[i] -= Shapes.Scale(d, diff);
                else
                {
                    Strand[i - 1] += Shapes.Scale(d, 0.5f * diff);
                    Strand[i] -= Shapes.Scale(d, 0.5f * diff);
                }
            }
            for (int i = 1; i < StrandPoints; i++)
            {
                var pt = Strand[i];
                if (pt.Y > floorY)
                {
                    pt.Y = floorY;
                    _strandPrev[i] = new SKPoint(Shapes.Lerp(_strandPrev[i], pt, 0.5f).X, floorY); // friction on the floor
                }
                var d = pt - Position;
                float l = Shapes.Length(d);
                if (l < R) pt = Position + Shapes.Scale(d, R / MathF.Max(l, 1e-3f));
                Strand[i] = pt;
            }
        }
    }
}

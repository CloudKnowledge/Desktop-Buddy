using Mochi.Core.Rig;

namespace Mochi.Core.Behaviour;

/// <summary>
/// One behaviour (state) of the state machine. Activities write a target pose every frame; the
/// animator blends between them, so entering/leaving never pops. Each has an implicit
/// enter → loop → exit choreography driven by its elapsed time <see cref="T"/>.
/// </summary>
public abstract class Activity
{
    public abstract PetState State { get; }
    /// <summary>Seconds since the activity started.</summary>
    public float T { get; internal set; }
    public bool Done { get; protected set; }
    /// <summary>How far the cursor-gaze overlay may steer the eyes/head.</summary>
    public virtual Gaze Gaze => Gaze.EyesAndHead;
    /// <summary>Calm autonomous activities may be interrupted by drowsiness, curiosity and tricks.</summary>
    public virtual bool IsCalm => false;
    /// <summary>Self-chosen activities that may be cut short for something more important (like greeting you).</summary>
    public virtual bool Interruptible => IsCalm;
    /// <summary>True while the activity is steering the gaze itself (e.g. a fidget glance).</summary>
    public virtual bool GazeSuppressed => false;
    /// <summary>Optional activity to run after this one finishes (otherwise the brain chooses).</summary>
    public Activity? Next { get; protected set; }

    public virtual void Enter(PetBrain b) { }
    public abstract void Tick(PetBrain b, PetPose t, float dt);
    public virtual void Exit(PetBrain b) { }

    // ---- choreography helpers -----------------------------------------------------------

    /// <summary>0→1 smoothstep from <paramref name="start"/> over <paramref name="duration"/>.</summary>
    public static float Ramp(float t, float start, float duration)
    {
        float u = Math.Clamp((t - start) / Math.Max(1e-4f, duration), 0f, 1f);
        return u * u * (3f - 2f * u);
    }

    /// <summary>0→1→0 bump.</summary>
    public static float Pulse(float t, float start, float duration)
    {
        float u = (t - start) / Math.Max(1e-4f, duration);
        return u is <= 0f or >= 1f ? 0f : MathF.Sin(MathF.PI * u);
    }

    /// <summary>Parabolic hop height.</summary>
    public static float Hop(float t, float start, float duration, float height)
    {
        float u = (t - start) / Math.Max(1e-4f, duration);
        return u is <= 0f or >= 1f ? 0f : height * 4f * u * (1f - u);
    }

    /// <summary>Squash right after a hop lands.</summary>
    public static float LandSquash(float t, float landAt, float amount = 0.35f) => Pulse(t, landAt, 0.22f) * amount;

    /// <summary>The "cat kiss": eyes close slowly, hold, and open again.</summary>
    public static float SlowBlink(float t, float start) =>
        1f - 0.88f * Ramp(t, start, 0.45f) * (1f - Ramp(t, start + 0.85f, 0.5f));

    public static float Lerp(float a, float b, float k) => a + (b - a) * k;
}

/// <summary>
/// Small spontaneous movements layered onto calm activities: glances, ear flicks, slow blinks, tail-tip
/// twitches, whisker twitches, sniffs and lip licks, with randomised timing so nothing loops visibly.
/// </summary>
public sealed class Fidget
{
    enum Kind { None, Look, EarFlick, Tilt, Sniff, TailFlick, NoticeYou, Shift, Lick, Whiskers }

    readonly Random _rng;
    Kind _kind;
    float _clock, _duration, _wait;
    float _lookX, _lookY, _turn, _dir;

    public Fidget(Random rng)
    {
        _rng = rng;
        _wait = 0.8f + (float)rng.NextDouble() * 1.5f;
    }

    /// <summary>True while the fidget is steering gaze, so the cursor overlay should back off.</summary>
    public bool SteersGaze => _kind is Kind.Look or Kind.Sniff or Kind.NoticeYou;

    public void Apply(PetPose t, float dt, bool standing, float curiosity)
    {
        if (_kind == Kind.None)
        {
            _wait -= dt;
            if (_wait <= 0) Start(standing, curiosity);
            return;
        }

        _clock += dt;
        float k = Activity.Pulse(_clock, 0, _duration);
        float hold = Activity.Ramp(_clock, 0, 0.25f) * (1f - Activity.Ramp(_clock, _duration - 0.3f, 0.3f));
        switch (_kind)
        {
            case Kind.Look:
                t.LookX = Activity.Lerp(t.LookX, _lookX, hold);
                t.LookY = Activity.Lerp(t.LookY, _lookY, hold);
                t.HeadTurn = Activity.Lerp(t.HeadTurn, _turn, hold);
                break;
            case Kind.EarFlick:
                // Cat ears flick fast, often twice.
                t.EarFlapL += _dir < 0 ? 22f * (Activity.Pulse(_clock, 0, 0.14f) + Activity.Pulse(_clock, 0.22f, 0.14f)) : 0f;
                t.EarFlapR += _dir > 0 ? 22f * (Activity.Pulse(_clock, 0, 0.14f) + Activity.Pulse(_clock, 0.22f, 0.14f)) : 0f;
                break;
            case Kind.Tilt:
                t.HeadTilt += _dir * 9f * hold;
                t.BrowRaise += 0.4f * hold;
                t.EarLift += 0.3f * hold;
                break;
            case Kind.Sniff:
                t.HeadLift = Activity.Lerp(t.HeadLift, -0.6f, hold);
                t.LookY = Activity.Lerp(t.LookY, 0.9f, hold);
                t.HeadTurn = Activity.Lerp(t.HeadTurn, 0.5f, hold);
                t.Whisker = Activity.Lerp(t.Whisker, 0.8f, hold);
                t.HeadDy += 0.5f * MathF.Sin(_clock * 28f) * hold;
                break;
            case Kind.TailFlick:
                t.TailSway = MathF.Max(t.TailSway, 20f * hold);
                t.TailSwaySpeed = MathF.Max(t.TailSwaySpeed, 2.4f);
                break;
            case Kind.NoticeYou:
                t.HeadTurn = Activity.Lerp(t.HeadTurn, 0f, hold);
                t.LookX = Activity.Lerp(t.LookX, 0f, hold);
                t.LookY = Activity.Lerp(t.LookY, 0.05f, hold);
                t.EyeOpen *= Activity.SlowBlink(_clock, 0.5f);
                t.Smile = Activity.Lerp(t.Smile, 0.8f, hold);
                break;
            case Kind.Shift:
                t.BodyTilt += 2f * k * _dir;
                t.Hop += 0.8f * k;
                break;
            case Kind.Lick:
                t.Tongue = MathF.Max(t.Tongue, 0.4f * Activity.Pulse(_clock, 0.1f, 0.35f));
                t.MouthOpen = MathF.Max(t.MouthOpen, 0.12f * Activity.Pulse(_clock, 0.05f, 0.45f));
                break;
            case Kind.Whiskers:
                t.Whisker += 0.8f * (Activity.Pulse(_clock, 0, 0.18f) + Activity.Pulse(_clock, 0.3f, 0.18f));
                break;
        }

        if (_clock >= _duration)
        {
            _kind = Kind.None;
            _wait = 1.1f + (float)_rng.NextDouble() * 2.8f;
        }
    }

    void Start(bool standing, float curiosity)
    {
        _clock = 0;
        _dir = _rng.NextDouble() < 0.5 ? -1f : 1f;
        double r = _rng.NextDouble();
        double look = 0.34 + curiosity * 0.15;
        if (r < look)
        {
            _kind = Kind.Look;
            _duration = 1.2f + (float)_rng.NextDouble() * 1.8f;
            _turn = (float)(_rng.NextDouble() * 1.3 - 0.45);
            _lookX = Math.Clamp(_turn * 1.4f + (float)(_rng.NextDouble() - 0.5) * 0.4f, -1f, 1f);
            _lookY = (float)(_rng.NextDouble() * 0.8 - 0.45);
        }
        else if (r < look + 0.14) { _kind = Kind.EarFlick; _duration = 0.45f; }
        else if (r < look + 0.22) { _kind = Kind.Tilt; _duration = 1.1f + (float)_rng.NextDouble() * 0.6f; }
        else if (r < look + 0.30 && standing) { _kind = Kind.Sniff; _duration = 1.3f + (float)_rng.NextDouble(); }
        else if (r < look + 0.40) { _kind = Kind.TailFlick; _duration = 1.2f + (float)_rng.NextDouble(); }
        else if (r < look + 0.52) { _kind = Kind.NoticeYou; _duration = 2.2f; }
        else if (r < look + 0.58) { _kind = Kind.Lick; _duration = 0.6f; }
        else if (r < look + 0.64) { _kind = Kind.Whiskers; _duration = 0.55f; }
        else { _kind = Kind.Shift; _duration = 0.5f; }
    }
}

using Mochi.Core.Animation;
using Mochi.Core.Effects;
using Mochi.Core.Interaction;
using Mochi.Core.Persistence;
using Mochi.Core.Play;
using Mochi.Core.Rig;
using SkiaSharp;

namespace Mochi.Core.Behaviour;

public sealed class BrainOptions
{
    public bool AutoSleep { get; set; } = true;
    public double DrowsyAfterSeconds { get; set; } = 90;
    public double SleepAfterSeconds { get; set; } = 240;
    public float Size { get; set; } = 1f;
    public float Liveliness { get; set; } = 0.5f;

    public void ApplyFrom(MochiSettings s)
    {
        AutoSleep = s.AutoSleep;
        DrowsyAfterSeconds = s.DrowsyAfterSeconds;
        SleepAfterSeconds = s.SleepAfterSeconds;
        Size = s.Size;
        Liveliness = s.Liveliness;
    }
}

/// <summary>
/// Mochi's behaviour state machine. Each frame it perceives (cursor, OS idle time, clock), applies
/// policies (sleep/wake, curiosity, surprise), runs the current <see cref="Activity"/>, integrates
/// physics and hands a target pose to the animator. Platform-neutral: positions are physical screen
/// pixels supplied by the host; poses are in model units.
/// </summary>
public sealed class PetBrain
{
    public const float Gravity = 1900f;          // model units / s²
    public const float HalfWidth = 46f;          // keeps Mochi fully on screen, model units
    public static readonly SKPoint HeldGrabPoint = new(4, -58);

    readonly IPetEnvironment _env;
    readonly PetPose _target = new();
    Activity _activity;
    float _gaitAdvance, _turnPulse, _shiftX;
    SKPoint _accelModel, _prevPos, _prevVel;
    bool _hasPrevMotion;
    double _cursorMovedAt = double.NegativeInfinity, _maxIdle, _lastNapAt = double.NegativeInfinity;
    SKPoint _lastCursor;
    float _curiousCooldown = 6f, _surpriseCooldown = 15f, _trickCooldown = 25f, _huntCooldown = 10f, _tagCooldown = 5f, _preyWiggle, _chaseSpeed;
    double _lastSnackAt, _lastTvAt, _lastWorkAt;
    GreetingKind? _deferredGreeting;
    string? _lastChoice;
    GreetingKind? _pendingGreeting;
    double _pendingGreetingAt;

    // Carrying state.
    SKPoint _holdCursor, _grabOffset, _holdPrev, _holdPrevVel;
    float _swing, _swingVel;
    bool _holdHasPrev;

    // The yarn ball.
    double _ballThrownAt = double.NegativeInfinity;
    float _ballCooldown;
    SKPoint _ballGrabOffset, _ballPrevPos;

    public PetBrain(BrainOptions options, Personality personality, PetMemory memory, IPetEnvironment env, int seed = 0)
    {
        Options = options;
        Personality = personality;
        Memory = memory;
        _env = env;
        Rng = seed == 0 ? new Random() : new Random(seed);
        Animator = new PoseAnimator(Rng.Next());
        Effects = new EffectSystem(Rng.Next());
        Floor = env.FloorAt(default);
        _activity = new IdleActivity();
        _activity.Enter(this);
    }

    public BrainOptions Options { get; }
    public Personality Personality { get; }
    public PetMemory Memory { get; }
    public Random Rng { get; }
    public PoseAnimator Animator { get; }
    public EffectSystem Effects { get; }

    /// <summary>Anchor point (ground under the body centre) in physical screen pixels.</summary>
    public SKPoint Position { get; private set; }
    public SKPoint Velocity { get; private set; }
    public int Facing { get; private set; } = 1;
    public bool Airborne { get; private set; }
    public Floor Floor { get; private set; }
    /// <summary>Physical pixels per model unit.</summary>
    public float Scale => MathF.Max(0.2f, Options.Size * Floor.DpiScale);
    public float MinX => Floor.Left + HalfWidth * Scale;
    public float MaxX => Floor.Right - HalfWidth * Scale;
    public PetState State => _activity.State;
    public Activity Activity => _activity;
    public double Now { get; private set; }
    public Senses Senses { get; private set; }
    public bool FollowCursor { get; private set; }
    public bool SleepRequested { get; private set; }
    public bool IsHeld => _activity is HeldActivity;
    public bool IsAsleep => _activity is SleepActivity;
    public bool IsUserActive => Senses.IdleSeconds < 2.5;
    public bool IsNight => Senses.LocalTime.Hour is >= 23 or < 5;

    // Perception (model units). CursorRel.X is relative to facing: positive = in front of Mochi.
    public SKPoint HeadWorld { get; private set; }
    public SKPoint HeadModelUnflipped { get; private set; }
    public SKPoint CursorRel { get; private set; }
    public float CursorDistance { get; private set; } = float.MaxValue;
    public float CursorInterest { get; private set; }
    public float CursorSpeed { get; private set; }
    public float HoldSpeed { get; private set; }

    /// <summary>The yarn ball, while it is out.</summary>
    public YarnBall? Ball { get; private set; }
    /// <summary>Swats and pounces that connected with the ball.</summary>
    public int BallHits { get; private set; }
    /// <summary>Balls caught in mid-air.</summary>
    public int BallCatches { get; private set; }
    public float SinceBallThrown => (float)(Now - _ballThrownAt);
    /// <summary>A milk saucer, treat bowl or TV that Mochi is using right now.</summary>
    public Prop? Prop { get; private set; }
    /// <summary>The pointer is coming along the floor towards Mochi, as if chasing it.</summary>
    public bool CursorChasing { get; private set; }

    public string MoodText => State switch
    {
        PetState.Sleeping => "is sleeping",
        PetState.Dreaming => "is dreaming",
        PetState.Drowsy => "is getting sleepy",
        PetState.BeingPetted => "is purring",
        PetState.Kneading => "is making biscuits",
        PetState.Grooming => "is grooming",
        PetState.Hunting => "is on the hunt",
        PetState.Happy or PetState.Excited or PetState.Celebrating or PetState.Greeting => "is happy",
        PetState.Curious => "is curious",
        PetState.ChasingBall => "is chasing the yarn ball",
        PetState.CarryingBall => "is bringing the ball back",
        PetState.Eating => "is having a snack",
        PetState.WatchingTv => "is watching cat TV",
        PetState.Working => "is busy working",
        PetState.Playful => "wants to play",
        PetState.Surprised or PetState.Scared => "is startled",
        PetState.Walking or PetState.Running => "is exploring",
        PetState.FollowingCursor => "is following you",
        PetState.Held => "is being carried",
        PetState.Falling or PetState.Landing => "is flying",
        PetState.Stretching => "is waking up",
        _ => "is relaxing",
    };

    // ---- lifecycle ---------------------------------------------------------------------------

    public void Place(SKPoint anchor, int facing)
    {
        Floor = _env.FloorAt(anchor);
        Facing = facing >= 0 ? 1 : -1;
        Position = new SKPoint(Math.Clamp(anchor.X, MinX, MaxX), Floor.Y);
        Airborne = false;
        Velocity = default;
        _hasPrevMotion = false;
    }

    /// <summary>Starts life on screen with a greeting suited to the time and how long it has been.</summary>
    /// <param name="greetNow">False at sign-in: Mochi sits facing you and saves its greeting for <see cref="GreetNow"/>.</param>
    public void Start(DateTime nowLocal, DateTime lastSeenUtc, bool greetNow = true)
    {
        var pose = new PetPose();
        pose.ResetToNeutral();
        pose.Sit = 1;
        Animator.Snap(pose);
        Senses = Senses with { LocalTime = nowLocal };
        double hoursAway = (DateTime.UtcNow - lastSeenUtc).TotalHours;
        var kind = ConsumeMorningGreeting(nowLocal) ? GreetingKind.Morning
            : hoursAway > 8 ? GreetingKind.LongAbsence
            : GreetingKind.Hello;
        if (greetNow) SwitchTo(new GreetingActivity(kind));
        else
        {
            _deferredGreeting = kind;
            SwitchTo(new SitActivity(facing: true));
        }
    }

    /// <summary>A start-up greeting is waiting for you to be at the computer.</summary>
    public bool GreetPending => _deferredGreeting != null;

    public void GreetNow()
    {
        if (_deferredGreeting is not { } kind || IsHeld || Airborne) return;
        _deferredGreeting = null;
        SleepRequested = false;
        SwitchTo(new GreetingActivity(kind));
    }

    public void Update(float dt, Senses senses)
    {
        dt = Math.Clamp(dt, 0f, 0.1f);
        if (dt <= 0) return;
        Now += dt;
        Senses = senses;
        _gaitAdvance = 0;
        _curiousCooldown -= dt;
        _surpriseCooldown -= dt;
        _trickCooldown -= dt;
        _huntCooldown -= dt;
        _ballCooldown -= dt;
        _tagCooldown -= dt;
        _turnPulse = MathF.Max(0, _turnPulse - dt);
        Animator.BreathRate = 0.42f;
        Animator.BreathDepth = 1f;
        Animator.AllowBlink = true;
        Animator.BlinkGap = IsNight ? 1.4f : 1f;

        if (!Airborne && !IsHeld) FollowFloor();
        Perceive(dt);
        ApplyPolicies(dt);

        _target.ResetToNeutral();
        _activity.T += dt;
        _activity.Tick(this, _target, dt);
        if (_activity.Done) SwitchTo(_activity.Next ?? ChooseNext());

        ApplyOverlays();
        Physics(dt);
        TrackMotion(dt);
        Animator.Update(_target, dt, _gaitAdvance, _accelModel);
        Effects.Update(dt, _shiftX);

        if (Ball is { } ball)
        {
            ball.Scale = Scale;
            if (ball.State == BallState.InMouth) ball.Position = MouthWorld() + new SKPoint(Facing * 2f * Scale, 2f * Scale);
            _ballPrevPos = ball.Position;
            ball.Step(dt, _env);
        }
        if (Prop is { } prop) prop.Time += dt;
    }

    // ---- perception & policies -----------------------------------------------------------------

    void FollowFloor()
    {
        var f = _env.FloorAt(Position);
        Floor = f;
        if (Position.Y < f.Y - 2f)
        {
            // The floor dropped away (taskbar hidden, monitor changed): fall gracefully.
            Airborne = true;
            Velocity = default;
            SwitchTo(new FallActivity(thrown: false));
        }
        else Position = new SKPoint(Math.Clamp(Position.X, MinX, MaxX), f.Y);
    }

    void Perceive(float dt)
    {
        var s = Skeleton.Compute(Animator.Output);
        HeadModelUnflipped = new SKPoint(s.Head.X * Facing, s.Head.Y);
        HeadWorld = new SKPoint(Position.X + s.Head.X * Facing * Scale, Position.Y + s.Head.Y * Scale);
        if (!Senses.CursorKnown)
        {
            CursorDistance = float.MaxValue;
            CursorInterest = 0;
            CursorSpeed = 0;
            CursorChasing = false;
            _chaseSpeed = 0;
            return;
        }

        var d = Senses.Cursor - HeadWorld;
        CursorRel = new SKPoint(d.X * Facing / Scale, d.Y / Scale);
        CursorDistance = MathF.Sqrt(d.X * d.X + d.Y * d.Y) / Scale;
        var moved = Senses.Cursor - _lastCursor;
        float movedPx = MathF.Sqrt(moved.X * moved.X + moved.Y * moved.Y);
        CursorSpeed = movedPx / Scale / dt;
        if (movedPx > 1.5f) _cursorMovedAt = Now;
        _lastCursor = Senses.Cursor;

        // Chasing: the pointer itself moving along the floor towards Mochi (not Mochi walking up to it).
        float toward = moved.X / Scale / dt * MathF.Sign(Position.X - Senses.Cursor.X);
        _chaseSpeed = Activity.Lerp(_chaseSpeed, Math.Clamp(toward, -3000f, 3000f), Math.Clamp(dt * 10f, 0f, 1f));
        bool level = Senses.Cursor.Y > Position.Y - 260f * Scale && Senses.Cursor.Y < Position.Y + 40f * Scale;
        CursorChasing = level && MathF.Abs(Senses.Cursor.X - Position.X) / Scale < 300f && _chaseSpeed > 260f;

        // Interest fades if the cursor has been still for a while (you are typing or reading).
        float still = (float)(Now - _cursorMovedAt);
        float recency = 1f - Activity.Ramp(still, 2.5f, 4f);
        float near = 1f - Activity.Ramp(CursorDistance, 420f, 480f);
        CursorInterest = MathF.Max(recency * near, CursorDistance < 110f ? 0.5f : 0f);
    }

    void ApplyPolicies(float dt)
    {
        double idle = Senses.IdleSeconds;
        bool returned = false;
        double awayFor = 0;
        if (idle > _maxIdle) _maxIdle = idle;
        if (idle < 1.0)
        {
            if (_maxIdle > 20) { returned = true; awayFor = _maxIdle; }
            _maxIdle = idle;
        }

        if (IsHeld || Airborne) return;

        switch (_activity)
        {
            case SleepActivity:
                if (returned && !SleepRequested) Wake(gentle: true, awayFor);
                return;
            case DrowsyActivity:
                if (idle < 1.0) SwitchTo(new IdleActivity(perk: true));
                else if (Options.AutoSleep && idle >= Options.SleepAfterSeconds) SwitchTo(new SleepActivity());
                return;
        }

        if (returned && _activity.Interruptible)
        {
            var greeting = GreetingFor(awayFor);
            if (greeting is { } kind) { SwitchTo(new GreetingActivity(kind)); return; }
        }
        else if (returned && GreetingFor(awayFor) is { } pending)
        {
            // Busy right now (being petted, carried…): greet as soon as Mochi is free.
            _pendingGreeting = pending;
            _pendingGreetingAt = Now;
        }

        if (_pendingGreeting is { } waiting)
        {
            if (Now - _pendingGreetingAt > 12) _pendingGreeting = null;
            else if (_activity.Interruptible)
            {
                _pendingGreeting = null;
                SwitchTo(new GreetingActivity(waiting));
                return;
            }
        }

        if (Ball is { State: BallState.Free } ball && _activity is not BallPlayActivity && WantsBall(ball, dt))
        {
            SwitchTo(new BallPlayActivity());
            return;
        }

        if (_activity.IsCalm && Options.AutoSleep && idle >= Options.DrowsyAfterSeconds)
        {
            SwitchTo(new DrowsyActivity());
            return;
        }

        if (_activity.IsCalm && Senses.CursorKnown)
        {
            // Your pointer coming after Mochi along the floor: a game of chase!
            if (_tagCooldown <= 0 && CursorChasing)
            {
                _tagCooldown = 6f;
                if (Rng.NextDouble() < 0.6 + 0.3 * Personality.Playful - 0.3 * Personality.Lazy)
                {
                    SwitchTo(new TagActivity());
                    return;
                }
            }

            // Prey instinct: a pointer wiggling about nearby for a second is irresistible.
            bool wiggling = CursorDistance < 260f && CursorSpeed is > 300f and < 2600f;
            _preyWiggle = wiggling ? _preyWiggle + dt : MathF.Max(0f, _preyWiggle - dt * 0.5f);

            if (_surpriseCooldown <= 0 && CursorDistance < 95f && CursorSpeed > 2600f)
            {
                _surpriseCooldown = 30f;
                SwitchTo(new StartleActivity());
            }
            else if (_huntCooldown <= 0 && _preyWiggle > 1f)
            {
                _preyWiggle = 0f;
                _huntCooldown = 25f + (float)Rng.NextDouble() * 20f - Personality.Playful * 10f;
                if (Rng.NextDouble() < 0.45 + Personality.Playful * 0.35 + Personality.Mischievous * 0.2 - Personality.Lazy * 0.25)
                    SwitchTo(new HuntActivity());
            }
            else if (_curiousCooldown <= 0 && CursorDistance < 160f && CursorInterest > 0.8f && CursorSpeed < 500f)
            {
                _curiousCooldown = 12f + (float)Rng.NextDouble() * 12f - Personality.Curious * 6f;
                if (Rng.NextDouble() < 0.35 + Personality.Curious * 0.5) SwitchTo(new CuriousActivity());
            }
        }
    }

    GreetingKind? GreetingFor(double awayFor)
    {
        if (awayFor >= 20 * 60 && ConsumeMorningGreeting(Senses.LocalTime)) return GreetingKind.Morning;
        if (awayFor >= 4 * 3600) return GreetingKind.LongAbsence;
        if (awayFor >= 15 * 60) return GreetingKind.Return;
        return null;
    }

    bool ConsumeMorningGreeting(DateTime local)
    {
        var today = local.ToString("yyyy-MM-dd");
        if (local.Hour is < 5 or >= 12 || Memory.LastMorningGreeting == today) return false;
        Memory.LastMorningGreeting = today;
        return true;
    }

    void ApplyOverlays()
    {
        var t = _target;
        // Eyes (and head) follow a flying ball, and stay on it during play.
        if (Ball is { State: not BallState.InMouth } ball
            && (_activity is BallPlayActivity || (ball.SpeedUnits > 120f && _activity.Gaze != Gaze.None && !_activity.GazeSuppressed)))
        {
            var d = ball.Position - HeadWorld;
            var rel = new SKPoint(d.X * Facing / Scale, d.Y / Scale);
            t.LookX = Math.Clamp(rel.X / 110f, -1f, 1f);
            t.LookY = Math.Clamp(rel.Y / 110f, -1f, 1f);
            t.HeadTurn = Activity.Lerp(t.HeadTurn, Math.Clamp(rel.X / 200f, -0.5f, 0.7f), 0.6f);
            t.HeadLift = Activity.Lerp(t.HeadLift, Math.Clamp(-rel.Y / 300f, -0.6f, 0.6f), 0.5f);
        }
        else if (_activity.Gaze != Gaze.None && !_activity.GazeSuppressed && CursorInterest > 0.01f)
        {
            float k = CursorInterest;
            t.LookX = Activity.Lerp(t.LookX, Math.Clamp(CursorRel.X / 110f, -1f, 1f), k);
            t.LookY = Activity.Lerp(t.LookY, Math.Clamp(CursorRel.Y / 110f, -1f, 1f), k);
            if (_activity.Gaze == Gaze.EyesAndHead)
            {
                t.HeadTurn = Activity.Lerp(t.HeadTurn, Math.Clamp(CursorRel.X / 200f, -0.5f, 0.7f), k * 0.85f);
                t.HeadLift = Activity.Lerp(t.HeadLift, Math.Clamp(-CursorRel.Y / 300f, -0.5f, 0.6f), k * 0.6f);
            }
        }
        if (_turnPulse > 0)
        {
            float k = MathF.Sin(MathF.PI * _turnPulse / 0.2f);
            t.Squash += 0.22f * k;
            t.Hop += 2.5f * k;
        }
    }

    // ---- choosing what to do next ---------------------------------------------------------------

    Activity ChooseNext()
    {
        if (FollowCursor) return new FollowActivity();
        var p = Personality;
        float live = 0.5f + Options.Liveliness;
        bool busy = IsUserActive && CursorInterest < 0.2f; // typing or reading: stay calm
        bool night = IsNight;
        var options = new List<(string Key, float Weight, Func<Activity> Make)>();
        void Add(string key, float w, Func<Activity> make)
        {
            if (w <= 0.001f) return;
            if (key == _lastChoice) w *= 0.35f;
            options.Add((key, w, make));
        }

        Add("idle", 2.0f, () => new IdleActivity());
        Add("sit", 2.4f + p.Lazy * 2f, () => new SitActivity());
        Add("loaf", (1.2f + p.Lazy * 2.6f) * (night ? 1.6f : 1f), () => new LoafActivity());
        Add("wander", (1.3f + p.Curious * 1.1f + p.Playful * 0.6f - p.Lazy * 0.7f) * live * (busy ? 0.7f : 1f),
            () => new WalkActivity(WalkPurpose.Wander));
        Add("zoomies", (0.06f + p.Playful * 0.3f) * live * (busy ? 0.3f : 1f) * (night ? 0.5f : 1f),
            () => new WalkActivity(WalkPurpose.Zoomies));
        Add("hunt", (0.12f + p.Playful * 0.4f + p.Mischievous * 0.35f) * live * (busy ? 0.5f : 1f),
            () => new HuntActivity());
        if (Senses.CursorKnown && CursorInterest > 0.3f && MathF.Abs(CursorRel.X) > 160f)
            Add("approach", 0.25f + p.Affectionate * 1.1f + p.Curious * 0.4f - p.Shy * 0.6f,
                () => new WalkActivity(WalkPurpose.Approach));
        Add("corner", p.Shy * 1.1f + p.Lazy * 0.2f, () => new WalkActivity(WalkPurpose.Corner));
        if (Options.AutoSleep && Now - _lastNapAt > 900)
            Add("nap", (0.12f + p.Lazy * 0.5f) * (night ? 2f : 1f), () => { _lastNapAt = Now; return new SleepActivity(nap: true); });
        if (Now - _lastSnackAt > 480)
            Add("snack", 0.3f + p.Lazy * 0.15f, () =>
            {
                _lastSnackAt = Now;
                return new SnackActivity(Rng.NextDouble() < 0.5 ? PropKind.Milk : PropKind.Treats);
            });
        if (Now - _lastTvAt > 420)
            Add("tv", (0.25f + p.Curious * 0.35f + p.Lazy * 0.2f) * (busy ? 0.6f : 1f), () =>
            {
                _lastTvAt = Now;
                return new WatchTvActivity();
            });
        if (Now - _lastWorkAt > 360)
            Add("work", (0.2f + p.Curious * 0.2f) * (busy ? 2.5f : 1f), () =>
            {
                _lastWorkAt = Now;
                return new WorkActivity();
            });
        if (_trickCooldown <= 0)
            Add("trick", 0.6f * live, () =>
            {
                _trickCooldown = 30f + (float)Rng.NextDouble() * 45f;
                return new TrickActivity(PickTrick());
            });

        float total = options.Sum(o => o.Weight);
        double r = Rng.NextDouble() * total;
        foreach (var o in options)
        {
            r -= o.Weight;
            if (r <= 0)
            {
                _lastChoice = o.Key;
                return o.Make();
            }
        }
        return new IdleActivity();
    }

    Trick PickTrick()
    {
        var p = Personality;
        var w = new (Trick T, float W)[]
        {
            (Trick.Groom, 1.4f), (Trick.Stretch, 0.8f), (Trick.SlowBlink, 0.6f + p.Affectionate * 0.6f),
            (Trick.ChaseTail, 0.2f + p.Playful * 0.6f), (Trick.CrabHop, 0.2f + p.Playful * 0.5f),
            (Trick.PawSwat, 0.2f + p.Mischievous * 0.6f), (Trick.Wink, 0.2f + p.Mischievous * 0.6f),
            (Trick.Blep, 0.5f), (Trick.StareAtNothing, 0.3f + p.Curious * 0.6f), (Trick.Chirp, 0.2f + p.Curious * 0.4f),
            (Trick.Knead, 0.3f + p.Affectionate * 0.4f), (Trick.Doze, p.Lazy * 0.8f + (IsNight ? 0.6f : 0f)),
            (Trick.Sneeze, 0.4f), (Trick.ShakeOff, 0.4f),
        };
        double r = Rng.NextDouble() * w.Sum(x => x.W);
        foreach (var (t, wt) in w)
        {
            r -= wt;
            if (r <= 0) return t;
        }
        return Trick.Groom;
    }

    void SwitchTo(Activity next)
    {
        _activity.Exit(this);
        _activity = next;
        _activity.Enter(this);
    }

    void Wake(bool gentle, double awayFor)
    {
        SleepRequested = false;
        bool pretend = gentle && Rng.NextDouble() < 0.12 + Personality.Mischievous * 0.1;
        Activity? then = GreetingFor(awayFor) is { } kind ? new GreetingActivity(kind) : null;
        SwitchTo(new WakeActivity(gentle, pretend, then));
    }

    // ---- movement & physics --------------------------------------------------------------------

    /// <summary>Walks/runs toward a screen X. Returns true on arrival. Feet never slide: the gait advances by distance.</summary>
    public bool MoveToward(float targetX, float unitsPerSecond, float dt, PetPose t, float gait)
    {
        targetX = Math.Clamp(targetX, MinX, MaxX);
        float dx = targetX - Position.X;
        float dist = MathF.Abs(dx);
        if (dist < 1.5f) return true;
        int dir = dx > 0 ? 1 : -1;
        if (dir != Facing) Turn(dir);
        float ease = Math.Clamp(dist / (22f * Scale), 0.35f, 1f);
        float step = MathF.Min(dist, unitsPerSecond * Scale * dt * ease);
        Position = new SKPoint(Position.X + dir * step, Position.Y);
        AdvanceGait(step / Scale / (gait >= 1.5f ? 52f : 30f) * MathF.Tau);
        t.GaitAmount = gait;
        return false;
    }

    /// <summary>Small sideways shift in model units, relative to facing (hops, flinches).</summary>
    public void Nudge(float units)
    {
        float x = Position.X + units * Facing * Scale;
        Position = new SKPoint(Math.Clamp(x, MinX, MaxX), Position.Y);
    }

    public void AdvanceGait(float radians) => _gaitAdvance += radians;

    public void Turn(int dir)
    {
        if (dir == Facing) return;
        Facing = dir;
        _turnPulse = 0.2f;
    }

    public void TurnAround() => Turn(-Facing);

    void Physics(float dt)
    {
        if (IsHeld || !Airborne) return;
        var f = _env.FloorAt(Position);
        Floor = f;
        float g = Gravity * Scale;
        var v = new SKPoint(Velocity.X * (1f - 0.12f * dt), Velocity.Y + g * dt);
        var p = new SKPoint(Position.X + v.X * dt, Position.Y + v.Y * dt);

        if (p.X < MinX) { p.X = MinX; v.X = MathF.Abs(v.X) * 0.45f; }
        if (p.X > MaxX) { p.X = MaxX; v.X = -MathF.Abs(v.X) * 0.45f; }
        float ceiling = f.Top + 115f * Scale;
        if (p.Y < ceiling) { p.Y = ceiling; v.Y = MathF.Abs(v.Y) * 0.3f; }

        if (p.Y >= f.Y)
        {
            p.Y = f.Y;
            float impact = v.Y / Scale;
            bool thrown = _activity is FallActivity { Thrown: true };
            if (impact > 650f)
            {
                // A small bounce first, then settle.
                v = new SKPoint(v.X * 0.6f, -v.Y * 0.26f);
                EmitFeet(2);
            }
            else
            {
                Airborne = false;
                v = default;
                Position = p;
                Velocity = v;
                SwitchTo(new LandActivity(MathF.Max(impact, _peakFallSpeed), thrown));
                _peakFallSpeed = 0;
                return;
            }
        }
        _peakFallSpeed = MathF.Max(_peakFallSpeed, v.Y / Scale);
        Position = p;
        Velocity = v;
    }

    float _peakFallSpeed;

    void TrackMotion(float dt)
    {
        if (!_hasPrevMotion)
        {
            _prevPos = Position;
            _prevVel = default;
            _hasPrevMotion = true;
            _accelModel = default;
            _shiftX = 0;
            return;
        }
        var vel = new SKPoint((Position.X - _prevPos.X) / dt, (Position.Y - _prevPos.Y) / dt);
        var acc = new SKPoint((vel.X - _prevVel.X) / dt, (vel.Y - _prevVel.Y) / dt);
        _shiftX = (Position.X - _prevPos.X) / Scale;
        _prevPos = Position;
        _prevVel = vel;
        _accelModel = new SKPoint(acc.X * Facing / Scale, acc.Y / Scale);
    }

    // ---- carrying ------------------------------------------------------------------------------

    /// <summary>Drives position and pendulum swing while carried. Called by <see cref="HeldActivity"/>.</summary>
    internal void UpdateHold(PetPose t, float dt)
    {
        var desired = new SKPoint(-HeldGrabPoint.X * Facing * Scale, -HeldGrabPoint.Y * Scale);
        float k = Activity.Ramp(_activity.T, 0f, 0.25f);
        var offset = new SKPoint(Activity.Lerp(_grabOffset.X, desired.X, k), Activity.Lerp(_grabOffset.Y, desired.Y, k));
        Position = _holdCursor + offset;

        // Pendulum: the body swings opposite to the hand's acceleration.
        var vel = _holdHasPrev ? new SKPoint((_holdCursor.X - _holdPrev.X) / dt, (_holdCursor.Y - _holdPrev.Y) / dt) : default;
        var acc = _holdHasPrev ? new SKPoint((vel.X - _holdPrevVel.X) / dt, (vel.Y - _holdPrevVel.Y) / dt) : default;
        _holdPrev = _holdCursor;
        _holdPrevVel = vel;
        _holdHasPrev = true;
        HoldSpeed = MathF.Sqrt(vel.X * vel.X + vel.Y * vel.Y) / Scale;

        const float length = 55f;
        float aModel = Math.Clamp(acc.X / Scale * Facing, -20000f, 20000f);
        int steps = Math.Max(1, (int)MathF.Ceiling(dt / 0.008f));
        float h = dt / steps;
        for (int i = 0; i < steps; i++)
        {
            float alpha = -(Gravity / length) * MathF.Sin(_swing) - 3.2f * _swingVel + aModel / length;
            _swingVel += alpha * h;
            _swing += _swingVel * h;
        }
        _swing = Math.Clamp(_swing, -0.9f, 0.9f);
        t.SwingAngle = _swing * 57.2958f;
        t.GrabX = HeldGrabPoint.X;
        t.GrabY = HeldGrabPoint.Y;
    }

    // ---- interaction -----------------------------------------------------------------------------

    /// <summary>Which part of Mochi is at a screen point (for petting vs. body interactions).</summary>
    public HitRegion HitTest(SKPoint screen)
    {
        var s = Skeleton.Compute(Animator.Output);
        var m = new SKPoint((screen.X - Position.X) / Scale * Facing, (screen.Y - Position.Y) / Scale);
        var d = m - s.Head;
        return d.X * d.X + d.Y * d.Y < (Skeleton.HeadRadius + 8f) * (Skeleton.HeadRadius + 8f) ? HitRegion.Head : HitRegion.Body;
    }

    public void Handle(Gesture g)
    {
        switch (g.Kind)
        {
            case GestureKind.Click: OnClick(g); break;
            case GestureKind.DoubleClick:
                if (g.Streak >= 3 || IsHeld || Airborne || IsAsleep) OnClick(g);
                else
                {
                    Memory.TimesClicked++;
                    SwitchTo(new HappyActivity(special: true));
                }
                break;
            case GestureKind.GrabStart: OnGrab(g.Screen); break;
            case GestureKind.DragMove: if (IsHeld) _holdCursor = g.Screen; break;
            case GestureKind.Release: OnRelease(g.Velocity); break;
            case GestureKind.PetStroke: OnStroke(g); break;
        }
    }

    void OnClick(Gesture g)
    {
        Memory.TimesClicked++;
        if (IsHeld || Airborne) return;
        if (_activity is SleepActivity or DrowsyActivity)
        {
            SleepRequested = false;
            Wake(gentle: false, 0);
            return;
        }
        if (g.Streak >= 10 && g.Streak % 10 == 0) { SwitchTo(new CelebrateActivity()); return; }
        if (_activity is CelebrateActivity) return;
        if (_activity is TagActivity)
        {
            Say("You got me!");
            SwitchTo(new HappyActivity(special: true));
            return;
        }
        if (_activity is ExcitedActivity excited) { excited.Boost(g.Streak); return; }
        if (g.Streak >= 3) { SwitchTo(new ExcitedActivity(g.Streak)); return; }
        SwitchTo(new HappyActivity(wink: Rng.NextDouble() < 0.12 + Personality.Mischievous * 0.12));
    }

    void OnGrab(SKPoint cursor)
    {
        Memory.TimesCarried++;
        Airborne = false; // catching Mochi mid-air works too
        _grabOffset = Position - cursor;
        _holdCursor = cursor;
        _swing = _swingVel = 0;
        _holdHasPrev = false;
        SleepRequested = false;
        SwitchTo(new HeldActivity());
    }

    void OnRelease(SKPoint velocity)
    {
        if (!IsHeld) return;
        float max = 3200f * Floor.DpiScale;
        var v = new SKPoint(Math.Clamp(velocity.X, -max, max), Math.Clamp(velocity.Y, -max, max));
        float speed = MathF.Sqrt(v.X * v.X + v.Y * v.Y) / Scale;
        Velocity = v;
        Airborne = true;
        _peakFallSpeed = 0;
        SwitchTo(new FallActivity(thrown: speed > 1100f));
    }

    void OnStroke(Gesture g)
    {
        if (IsHeld || Airborne) return;
        bool lying = Animator.Output.Lie > 0.5f;
        if (g.Region != HitRegion.Head && !lying) return;
        switch (_activity)
        {
            case SleepActivity sleep:
                sleep.Petted();
                return;
            case PettedActivity petted:
                petted.Stroke(g.Strength);
                return;
            case HeldActivity or FallActivity or LandActivity:
                return;
        }
        SwitchTo(new PettedActivity(g.Strength));
    }

    // ---- commands from the menu / tray ---------------------------------------------------------

    public void CommandSleep()
    {
        SleepRequested = true;
        if (!IsAsleep && !IsHeld && !Airborne) SwitchTo(new SleepActivity());
    }

    public void CommandWake()
    {
        SleepRequested = false;
        if (IsAsleep || _activity is DrowsyActivity) Wake(gentle: true, 0);
    }

    public void CommandPet()
    {
        if (IsHeld || Airborne) return;
        if (IsAsleep) { if (_activity is SleepActivity s) s.Petted(); return; }
        SwitchTo(new PettedActivity(0.8f, auto: true));
    }

    public void SetFollowCursor(bool on)
    {
        FollowCursor = on;
        if (IsHeld || Airborne) return;
        if (on)
        {
            if (IsAsleep) Wake(gentle: true, 0);
            else SwitchTo(new FollowActivity());
        }
        else if (_activity is FollowActivity) SwitchTo(new SitActivity());
    }

    public void CommandComeHere()
    {
        if (IsHeld || Airborne || !Senses.CursorKnown) return;
        SleepRequested = false;
        SwitchTo(new WalkActivity(WalkPurpose.ComeHere, Senses.Cursor.X));
    }

    public void CommandCelebrate()
    {
        if (!IsHeld && !Airborne) SwitchTo(new CelebrateActivity());
    }

    /// <summary>Says hello: a "Hi!" with a wave (start-up, waking the laptop, coming back).</summary>
    public void SayHi()
    {
        if (IsHeld || Airborne) return;
        SleepRequested = false;
        SwitchTo(new GreetingActivity(GreetingKind.Hello));
    }

    /// <summary>Puts down a saucer of milk or a bowl of treats.</summary>
    public void CommandFeed(PropKind food)
    {
        if (IsHeld || Airborne || food is not (PropKind.Milk or PropKind.Treats)) return;
        SleepRequested = false;
        _lastSnackAt = Now;
        SwitchTo(new SnackActivity(food, fromYou: true));
    }

    public void CommandWatchTv()
    {
        if (IsHeld || Airborne) return;
        SleepRequested = false;
        _lastTvAt = Now;
        SwitchTo(new WatchTvActivity());
    }

    /// <summary>Glasses on, laptop open: Mochi works alongside you for a while.</summary>
    public void CommandWork()
    {
        if (IsHeld || Airborne) return;
        SleepRequested = false;
        _lastWorkAt = Now;
        SwitchTo(new WorkActivity());
    }

    /// <summary>A speech bubble above Mochi's head.</summary>
    public void Say(string text)
    {
        var h = HeadModelUnflipped;
        Effects.Say(text, h.X + 12f * Facing, h.Y - 46f);
    }

    internal Prop ShowProp(PropKind kind, float x) => Prop = new Prop(kind, x, Rng.Next());

    internal void HideProp() => Prop = null;

    // ---- the yarn ball -------------------------------------------------------------------------------

    /// <summary>Brings out the yarn ball, if it isn't out already, and throws it for Mochi to catch.</summary>
    public void BringOutBall() => ThrowBall();

    /// <summary>
    /// Throws the ball in a high arc (from your pointer when it is above this floor) to come down a little way
    /// off on the roomier side of Mochi, who runs to catch it.
    /// </summary>
    public void ThrowBall()
    {
        if (Ball is { State: BallState.HeldByUser }) return;
        if (Ball is { State: BallState.InMouth }) DropBall();
        bool fromPointer = Senses.CursorKnown && Senses.Cursor.X > Floor.Left && Senses.Cursor.X < Floor.Right && Senses.Cursor.Y < Floor.Y;
        var from = fromPointer ? Senses.Cursor : new SKPoint(Position.X, Position.Y - 150f * Scale);
        from = new SKPoint(Math.Clamp(from.X, Floor.Left + 20f * Scale, Floor.Right - 20f * Scale),
            Math.Clamp(from.Y, Floor.Top + 30f * Scale, Floor.Y - 30f * Scale));
        int dir = Position.X - Floor.Left > Floor.Right - Position.X ? -1 : 1;
        float land = Math.Clamp(Position.X + dir * (150f + 110f * (float)Rng.NextDouble()) * Scale, Floor.Left + 30f * Scale, Floor.Right - 30f * Scale);
        const float flight = 1.25f;
        float g = YarnBall.Gravity * Scale, restY = Floor.Y - YarnBall.Radius * Scale;
        var v = new SKPoint((land - from.X) / flight, (restY - from.Y - 0.5f * g * flight * flight) / flight);
        Ball ??= new YarnBall(from, Scale, _env);
        Ball.Position = from;
        Ball.State = BallState.Free;
        Ball.Velocity = v;
        Ball.Spin = v.X / Ball.R;
        BallThrown();
        if ((IsAsleep || _activity is DrowsyActivity) && !IsHeld && !Airborne)
        {
            SleepRequested = false;
            SwitchTo(new BallPlayActivity());
        }
    }

    public void RemoveBall()
    {
        Ball = null;
        if (_activity is BallPlayActivity) SwitchTo(new SitActivity(facing: true));
    }

    public void GrabBall(SKPoint cursor)
    {
        if (Ball is not { } b) return;
        b.State = BallState.HeldByUser;
        _ballGrabOffset = b.Position - cursor;
    }

    public void DragBall(SKPoint cursor)
    {
        if (Ball is { State: BallState.HeldByUser } b) b.Position = cursor + _ballGrabOffset;
    }

    /// <summary>Lets go of the ball with the pointer's velocity (physical pixels per second).</summary>
    public void ReleaseBall(SKPoint velocity)
    {
        if (Ball is not { State: BallState.HeldByUser } b) return;
        float max = 4200f * Floor.DpiScale;
        b.Velocity = new SKPoint(Math.Clamp(velocity.X, -max, max), Math.Clamp(velocity.Y, -max, max));
        b.Spin = b.Velocity.X / b.R;
        b.State = BallState.Free;
        BallThrown();
    }

    void BallThrown()
    {
        _ballThrownAt = Now;
        _ballCooldown = 0f;
        Memory.TimesPlayedBall++;
        Personality.Nudge(Personality.Trait.Playful, 0.004f);
    }

    bool WantsBall(YarnBall ball, float dt)
    {
        if (_activity is PettedActivity or GreetingActivity or CelebrateActivity or WakeActivity or SleepActivity or DrowsyActivity
            or LandActivity or StartleActivity) return false;
        if (ball.Floor.Id != Floor.Id) return false; // on another monitor, out of reach
        if (Now - _ballThrownAt < 2.5) return true;
        // A ball just lying about: now and then Mochi goes and bats it around on its own.
        if (!_activity.IsCalm || _ballCooldown > 0) return false;
        return Rng.NextDouble() < (0.01f + 0.05f * Personality.Playful) * dt;
    }

    /// <summary>A swat (units per second, screen direction) or, with zero, trapping the ball under a paw.</summary>
    internal void HitBall(SKPoint unitsPerSecond)
    {
        if (Ball is not { State: BallState.Free } b) return;
        b.Velocity = Shapes.Scale(unitsPerSecond, Scale);
        b.Spin = b.Velocity.X / b.R;
        BallHits++;
    }

    internal void PickUpBall()
    {
        if (Ball is { State: BallState.Free } b) b.State = BallState.InMouth;
    }

    /// <summary>Snatched out of the air!</summary>
    internal void CatchBall()
    {
        if (Ball is not { State: BallState.Free } b) return;
        b.State = BallState.InMouth;
        BallCatches++;
        BurstHead(EffectKind.Sparkle, 5, 36f, 0.9f, 0.9f, 10f);
        if (Rng.NextDouble() < 0.4) Say("Got it!");
    }

    /// <summary>Closest the ball came to Mochi's mouth during the last frame, in model units (fast balls can't tunnel).</summary>
    internal float MouthDistanceToBall()
    {
        if (Ball == null) return float.MaxValue;
        var m = MouthWorld();
        var path = Ball.Position - _ballPrevPos;
        float len2 = Shapes.Dot(path, path);
        float k = len2 < 1e-3f ? 1f : Math.Clamp(Shapes.Dot(m - _ballPrevPos, path) / len2, 0f, 1f);
        return Shapes.Length(m - (_ballPrevPos + Shapes.Scale(path, k))) / Scale;
    }

    internal void DropBall()
    {
        if (Ball is not { State: BallState.InMouth } b) return;
        b.State = BallState.Free;
        b.Velocity = new SKPoint(Facing * 50f * Scale, 0);
    }

    internal void BallPlayEnded() => _ballCooldown = 50f + (float)Rng.NextDouble() * 70f - Personality.Playful * 25f;

    /// <summary>Where to bring the ball back to: just in front of the cursor, if it is over this floor.</summary>
    internal float CarryTarget()
    {
        if (!Senses.CursorKnown || Senses.Cursor.X < Floor.Left || Senses.Cursor.X > Floor.Right) return Position.X;
        float side = Senses.Cursor.X >= Position.X ? 1f : -1f;
        return Math.Clamp(Senses.Cursor.X - side * 55f * Scale, MinX, MaxX);
    }

    SKPoint MouthWorld()
    {
        var o = Animator.Output;
        var s = Skeleton.Compute(o);
        var local = Shapes.Rotate(new SKPoint(Math.Clamp(o.HeadTurn, -1f, 1f) * 8.5f + 2f, 12f), s.HeadAngle * MathF.PI / 180f);
        var m = s.Head + local;
        return new SKPoint(Position.X + m.X * Facing * Scale, Position.Y + m.Y * Scale);
    }

    // ---- effects helpers ---------------------------------------------------------------------------

    /// <summary>Emits a particle relative to the head; dx/vx are relative to facing (positive = in front).</summary>
    public void EmitHead(EffectKind kind, float dx, float dy, float vx, float vy, float size, float life)
    {
        var h = HeadModelUnflipped;
        Effects.Emit(kind, h.X + dx * Facing, h.Y + dy, vx * Facing, vy, size, life);
    }

    public void BurstHead(EffectKind kind, int count, float speed, float size, float life, float dy)
    {
        var h = HeadModelUnflipped;
        Effects.Burst(kind, h.X, h.Y + dy, count, speed, size, life);
    }

    /// <summary>A little "mew!" speech bubble above the head.</summary>
    public void Mew() => EmitHead(EffectKind.Mew, 16f, -40f, 3f, -10f, 1f, 1.5f);

    /// <summary>Emits a particle relative to the body centre (purring rumbles come from the chest).</summary>
    public void EmitBody(EffectKind kind, float dx, float dy, float vx, float vy, float size, float life)
    {
        var s = Skeleton.Compute(Animator.Output);
        Effects.Emit(kind, (s.BodyCenter.X + dx) * Facing, s.BodyCenter.Y + dy, vx * Facing, vy, size, life);
    }

    public void EmitFeet(int puffs)
    {
        for (int i = 0; i < puffs; i++)
        {
            float side = i % 2 == 0 ? 1f : -1f;
            Effects.Emit(EffectKind.Dust, side * (14f + 9f * i), -3f, side * (26f + 10f * (float)Rng.NextDouble()), -8f, 1f + 0.4f * (float)Rng.NextDouble(), 0.55f);
        }
    }

    /// <summary>Snapshot of where Mochi is, for persistence.</summary>
    public void Remember()
    {
        Memory.MonitorId = Floor.Id;
        Memory.FloorFraction = Floor.Width > 1 ? Math.Clamp((Position.X - Floor.Left) / Floor.Width, 0, 1) : 0.5;
        Memory.Facing = Facing;
        Memory.LastSeenUtc = DateTime.UtcNow;
        Memory.TraitDrift = (float[])Personality.Drift.Clone();
    }
}

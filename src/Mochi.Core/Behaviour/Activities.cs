using Mochi.Core.Effects;
using Mochi.Core.Rig;

namespace Mochi.Core.Behaviour;

// ============================================================================================
//  Calm, autonomous activities
// ============================================================================================

sealed class IdleActivity(bool perk = false) : Activity
{
    float _duration;
    Fidget _fidget = null!;
    public override PetState State => PetState.Idle;
    public override bool IsCalm => true;
    public override bool GazeSuppressed => _fidget.SteersGaze;

    public override void Enter(PetBrain b)
    {
        _duration = 3.5f + (float)b.Rng.NextDouble() * 6f;
        _fidget = new Fidget(b.Rng);
    }

    public override void Tick(PetBrain b, PetPose t, float dt)
    {
        t.TailRaise = 0.75f;
        t.TailCurl = 0.55f;
        if (perk)
        {
            // "Hm? You're back?" — ears and whiskers perk up.
            float k = Pulse(T, 0, 1.0f);
            t.EarLift += 0.9f * k;
            t.EyeSize += 0.08f * k;
            t.PupilSize += 0.3f * k;
            t.Whisker += 0.7f * k;
        }
        _fidget.Apply(t, dt, standing: true, b.Personality.Curious);
        if (T > _duration) Done = true;
    }
}

sealed class SitActivity(bool? facing = null, bool pawsUp = false) : Activity
{
    float _duration, _pawsUpAt = -1f, _pawsUpFor;
    bool _front;
    Fidget _fidget = null!;
    public override PetState State => PetState.Sitting;
    public override bool IsCalm => true;
    public override bool GazeSuppressed => _fidget.SteersGaze;

    public override void Enter(PetBrain b)
    {
        _duration = 7f + (float)b.Rng.NextDouble() * 12f + b.Personality.Lazy * 10f;
        _fidget = new Fidget(b.Rng);
        // Usually turns to sit square-on, facing you; now and then holds its paws up to its chest.
        _front = facing ?? b.Rng.NextDouble() < 0.75;
        if (_front && (pawsUp || b.Rng.NextDouble() < 0.4))
        {
            _pawsUpAt = pawsUp ? 0.3f : 2f + (float)b.Rng.NextDouble() * 4f;
            _pawsUpFor = 3f + (float)b.Rng.NextDouble() * 4f;
        }
    }

    public override void Tick(PetBrain b, PetPose t, float dt)
    {
        t.Sit = 1;
        t.Front = _front ? Ramp(T, 0.35f, 0.3f) : 0f;
        if (_pawsUpAt >= 0) t.PawsUp = Ramp(T, _pawsUpAt, 0.35f) * (1f - Ramp(T, _pawsUpAt + _pawsUpFor, 0.35f));
        t.TailWrap = Ramp(T, 0.4f, 0.8f); // tail tucked neatly around the front paws
        t.TailSway = 5f;
        t.TailSwaySpeed = 0.7f;
        _fidget.Apply(t, dt, standing: false, b.Personality.Curious);
        if (T > _duration) Done = true;
    }
}

/// <summary>The cat loaf: paws tucked under, tail wrapped, eyes half-closed in contentment.</summary>
sealed class LoafActivity : Activity
{
    float _duration, _restAt, _restFor, _nextBlink;
    Fidget _fidget = null!;
    public override PetState State => PetState.LyingDown;
    public override bool IsCalm => true;
    public override bool GazeSuppressed => _fidget.SteersGaze || Resting;
    bool Resting => T > _restAt && T < _restAt + _restFor;

    public override void Enter(PetBrain b)
    {
        _duration = 14f + (float)b.Rng.NextDouble() * 18f + b.Personality.Lazy * 14f;
        _restAt = 3f + (float)b.Rng.NextDouble() * 5f;
        _restFor = 6f + (float)b.Rng.NextDouble() * 8f;
        _nextBlink = 2f + (float)b.Rng.NextDouble() * 3f;
        _fidget = new Fidget(b.Rng);
    }

    public override void Tick(PetBrain b, PetPose t, float dt)
    {
        t.Lie = 1;
        t.TailWrap = Ramp(T, 0.5f, 0.8f);
        t.TailSway = 3f;
        float rest = Ramp(T, _restAt, 0.8f) * (1f - Ramp(T, _restAt + _restFor, 0.8f));
        t.EyeOpen = Lerp(1f, 0f, rest); // a contented loaf, eyes peacefully closed
        t.HeadLift = -0.25f * rest;
        t.HeadDy += 2f * rest;
        t.EarLift = -0.15f * rest;
        t.Smile = 0.55f;
        if (rest < 0.05f)
        {
            _fidget.Apply(t, dt, standing: false, b.Personality.Curious);
            if (T > _nextBlink)
            {
                t.EyeOpen *= SlowBlink(T, _nextBlink);
                if (T > _nextBlink + 1.6f) _nextBlink = T + 4f + (float)b.Rng.NextDouble() * 6f;
            }
        }
        if (T > _duration) Done = true;
    }
}

enum WalkPurpose { Wander, Zoomies, Approach, Corner, ComeHere }

sealed class WalkActivity(WalkPurpose purpose, float? targetX = null) : Activity
{
    float _target, _speed, _nextGlance, _chase;
    bool _run;
    int _legs;
    public override PetState State => _run ? PetState.Running : PetState.Walking;
    public override bool IsCalm => purpose is WalkPurpose.Wander or WalkPurpose.Corner;
    public override bool Interruptible => purpose is not WalkPurpose.ComeHere;
    public override Gaze Gaze => Gaze.Eyes;

    public override void Enter(PetBrain b)
    {
        float s = b.Scale;
        float x = b.Position.X;
        _speed = 38f + b.Personality.Playful * 8f - b.Personality.Lazy * 8f;
        _nextGlance = 1.5f + (float)b.Rng.NextDouble() * 2f;
        switch (purpose)
        {
            case WalkPurpose.Wander:
            {
                float dist = (100f + (float)b.Rng.NextDouble() * 340f) * s;
                float center = (b.MinX + b.MaxX) / 2f;
                int dir = b.Rng.NextDouble() < (x < center ? 0.68 : 0.32) ? 1 : -1;
                _target = x + dir * dist;
                break;
            }
            case WalkPurpose.Zoomies:
                _run = true;
                _speed = 170f;
                _legs = 1 + b.Rng.Next(3);
                _target = ZoomieLeg(b, b.Rng.NextDouble() < 0.5 ? 1 : -1);
                break;
            case WalkPurpose.Approach:
            case WalkPurpose.ComeHere:
            {
                float cx = targetX ?? b.Senses.Cursor.X;
                _target = cx - MathF.Sign(cx - x) * 65f * s;
                _run = purpose == WalkPurpose.ComeHere || MathF.Abs(cx - x) > 520f * s;
                if (_run) _speed = 150f;
                break;
            }
            case WalkPurpose.Corner:
            {
                bool left = x - b.MinX < b.MaxX - x;
                _target = (left ? b.MinX : b.MaxX) + (b.Rng.NextDouble() < 0.5 ? 10f : 30f) * s * (left ? 1 : -1);
                break;
            }
        }
        _target = Math.Clamp(_target, b.MinX, b.MaxX);
    }

    public override void Tick(PetBrain b, PetPose t, float dt)
    {
        // Cursor teasing: if the cursor darts away while Mochi approaches, give playful chase.
        if (purpose == WalkPurpose.Approach && b.Senses.CursorKnown && _chase < 3f)
        {
            float away = MathF.Abs(b.Senses.Cursor.X - b.Position.X) / b.Scale;
            if (b.CursorSpeed > 900f && away > 160f)
            {
                _run = true;
                _speed = 155f;
                _chase += dt;
                _target = Math.Clamp(b.Senses.Cursor.X - MathF.Sign(b.Senses.Cursor.X - b.Position.X) * 60f * b.Scale, b.MinX, b.MaxX);
                b.Personality.Nudge(Personality.Trait.Mischievous, 0.0005f);
            }
        }

        bool arrived = b.MoveToward(_target, _speed, dt, t, _run ? 2f : 1f);
        t.HeadTurn = _run ? 0.6f : 0.42f;
        // Tail held high with a curl at the tip: a happy, confident cat.
        t.TailRaise = _run ? 1f : 0.9f;
        t.TailCurl = _run ? 0.3f : 0.65f;
        t.TailSway = _run ? 6f : 7f;
        t.TailSwaySpeed = 1.1f;
        t.Smile = 0.5f;
        if (_run)
        {
            t.PupilSize = 0.95f;
            t.EarLift = -0.15f;
            t.Puff = purpose == WalkPurpose.Zoomies ? 0.3f : 0f;
            t.Whisker = -0.3f;
        }

        if (T > _nextGlance)
        {
            float g = Pulse(T, _nextGlance, 1.1f);
            t.HeadTurn = Lerp(t.HeadTurn, 0.05f, g);
            t.LookX = Lerp(t.LookX, 0f, g);
            if (T > _nextGlance + 1.1f) _nextGlance = T + 2.5f + (float)b.Rng.NextDouble() * 4f;
        }

        if (arrived)
        {
            if (purpose == WalkPurpose.Zoomies && --_legs > 0)
            {
                _target = ZoomieLeg(b, -b.Facing);
                return;
            }
            Next = purpose switch
            {
                WalkPurpose.Approach or WalkPurpose.ComeHere => new SitActivity(facing: true),
                WalkPurpose.Corner => b.Rng.NextDouble() < 0.5 ? new SitActivity() : new LoafActivity(),
                WalkPurpose.Zoomies => new TrickActivity(Trick.Groom), // acting as if nothing happened
                _ => null,
            };
            Done = true;
        }
        if (T > 30f) Done = true;
    }

    /// <summary>A short burst (250–600 units) in a direction, bouncing off the screen edge.</summary>
    static float ZoomieLeg(PetBrain b, int dir)
    {
        float dist = (250f + (float)b.Rng.NextDouble() * 350f) * b.Scale;
        float x = b.Position.X + dir * dist;
        if (x < b.MinX || x > b.MaxX) x = b.Position.X - dir * dist;
        return Math.Clamp(x, b.MinX, b.MaxX);
    }
}

sealed class FollowActivity : Activity
{
    bool _settled;
    public override PetState State => PetState.FollowingCursor;
    public override bool IsCalm => _settled;

    public override void Tick(PetBrain b, PetPose t, float dt)
    {
        if (!b.Senses.CursorKnown) { t.Sit = 1; t.TailWrap = 1; _settled = true; return; }
        float dx = b.Senses.Cursor.X - b.Position.X;
        float units = MathF.Abs(dx) / b.Scale;
        if (units > (_settled ? 90f : 45f))
        {
            _settled = false;
            bool run = units > 380f;
            b.MoveToward(b.Senses.Cursor.X - MathF.Sign(dx) * 40f * b.Scale, run ? 155f : 50f, dt, t, run ? 2f : 1f);
            t.HeadTurn = 0.5f;
            t.TailRaise = 0.95f;
            t.TailCurl = 0.7f;
            if (run) t.PupilSize = 0.9f;
        }
        else
        {
            if (!_settled && MathF.Sign(dx) != b.Facing && units > 12f) b.Turn(MathF.Sign(dx) >= 0 ? 1 : -1);
            _settled = true;
            t.Sit = 1;
            t.Front = 1;
            t.TailWrap = 1;
            t.Smile = 0.7f;
        }
    }
}

// ============================================================================================
//  Sleep cycle
// ============================================================================================

sealed class DrowsyActivity : Activity
{
    float _nextYawn = 2.5f;
    public override PetState State => PetState.Drowsy;
    public override Gaze Gaze => Gaze.Eyes;

    public override void Tick(PetBrain b, PetPose t, float dt)
    {
        b.Animator.BlinkGap = 2.2f;
        b.Animator.BreathRate = 0.3f;
        t.Lie = Ramp(T, 0.2f, 1f); // settles into a loaf
        t.TailWrap = 1f;
        t.EarLift = -0.3f;
        t.TailSway = 2f;
        t.Smile = 0.3f;

        // Fighting sleep: the lids sink, close for a moment while the head drops, then bob awake again.
        float cyc = T < 1f ? 0f : (T - 1f) % 9f;
        float closed = Ramp(cyc, 5f, 0.6f) * (1f - Ramp(cyc, 8.4f, 0.25f));
        t.EyeOpen = Lerp(0.8f, 0.45f, Ramp(cyc, 0f, 5f)) * (1f - closed);
        float nod = Ramp(cyc, 5.4f, 2.8f) * (1f - Ramp(cyc, 8.4f, 0.25f));
        t.HeadLift = -0.3f - 0.45f * nod;

        if (T > _nextYawn)
        {
            // A big kitten yawn: tiny fangs, curled tongue, ears back.
            float y = Pulse(T, _nextYawn, 1.6f);
            t.MouthOpen = y;
            t.Tongue = 0.25f * y;
            t.EyeOpen *= 1f - y;
            t.HeadLift += 0.8f * y;
            t.EarLift -= 0.5f * y;
            t.Whisker = -0.6f * y;
            if (T > _nextYawn + 1.6f) _nextYawn = T + 14f + (float)b.Rng.NextDouble() * 14f;
        }
        if (T > 900f) Done = true;
    }
}

sealed class SleepActivity(bool nap = false) : Activity
{
    float _circleUntil, _nextZ = 1.5f, _nextTwitch, _dreamAt, _dreamFor, _napFor, _pettedAt = -10f, _nextPurr;
    int _twitch, _turns;
    bool _circling;
    public bool Dreaming => T > _dreamAt && T < _dreamAt + _dreamFor;
    public override PetState State => Dreaming ? PetState.Dreaming : PetState.Sleeping;
    public override Gaze Gaze => Gaze.None;

    public override void Enter(PetBrain b)
    {
        var o = b.Animator.Output;
        _circling = o.Lie < 0.5f && b.Rng.NextDouble() < 0.75;
        _circleUntil = _circling ? 1.4f : 0f;
        _nextTwitch = 12f + (float)b.Rng.NextDouble() * 18f;
        _dreamAt = 35f + (float)b.Rng.NextDouble() * 35f;
        _dreamFor = 12f + (float)b.Rng.NextDouble() * 8f;
        _napFor = 40f + (float)b.Rng.NextDouble() * 50f;
        b.Effects.DreamIcon = b.Rng.Next(2);
    }

    /// <summary>Gentle petting while asleep: Mochi purrs in its sleep instead of waking.</summary>
    public void Petted() => _pettedAt = T;

    public override void Tick(PetBrain b, PetPose t, float dt)
    {
        b.Animator.AllowBlink = false;
        if (_circling && T < _circleUntil)
        {
            // Turns around a couple of times to find the comfy spot.
            if ((_turns == 0 && T > 0.35f) || (_turns == 1 && T > 0.85f)) { _turns++; b.TurnAround(); }
            t.GaitAmount = 1f;
            b.AdvanceGait(dt * 6f);
            t.HeadTurn = 0.4f;
            t.EyeOpen = 0.6f;
            t.TailRaise = 0.7f;
            return;
        }

        float s = T - _circleUntil;
        t.Lie = 1f;
        t.Curl = Ramp(s, 0.4f, 1.6f); // a fluffy ball with the tail over the nose
        t.TailWrap = 1f;
        t.EyeOpen = 1f - Ramp(s, 0.2f, 1.4f);
        t.EarLift = -0.25f;
        t.TailSway = 0f;
        t.Smile = 0.3f;
        t.HeadTurn = 0.1f;
        t.Blush = 0.5f;
        b.Animator.BreathRate = 0.22f;
        b.Animator.BreathDepth = 1.5f;

        if (s > _nextZ)
        {
            b.EmitHead(EffectKind.Zzz, 14f, -24f, 7f, -11f, 1f, 3.2f);
            _nextZ = s + 1.7f + (float)b.Rng.NextDouble() * 0.6f;
        }

        // Little twitches: an ear flick, a paw twitch, a whisker twitch.
        if (s > _nextTwitch)
        {
            float k = Pulse(s, _nextTwitch, 0.6f);
            switch (_twitch % 3)
            {
                case 0: t.EarFlapL = 20f * k; break;
                case 1: t.PawPaddle = 0.6f * k; break;
                default: t.Whisker = 0.8f * k; break;
            }
            if (s > _nextTwitch + 0.6f) { _twitch++; _nextTwitch = s + 14f + (float)b.Rng.NextDouble() * 20f; }
        }

        float dream = Ramp(s, _dreamAt, 1f) * (1f - Ramp(s, _dreamAt + _dreamFor - 1f, 1f));
        b.Effects.DreamBubble = dream;
        if (dream > 0.01f)
        {
            // Dreaming of chasing a fish or a ball of yarn.
            t.PawPaddle = MathF.Max(t.PawPaddle, 0.7f * dream);
            t.Whisker = 0.6f * MathF.Sin(s * 9f) * dream;
            t.TailSway = 6f * dream;
            t.TailSwaySpeed = 2.5f;
            t.MouthOpen = 0.12f * Pulse(s % 2.1f, 0.6f, 0.3f) * dream;
            t.Smile = 0.7f;
        }
        else if (s > _dreamAt + _dreamFor)
        {
            _dreamAt = s + 40f + (float)b.Rng.NextDouble() * 50f;
            b.Effects.DreamIcon = b.Rng.Next(2);
        }

        float pet = Pulse(T, _pettedAt, 2f);
        if (pet > 0)
        {
            t.Smile = 1f;
            t.Blush = 0.9f;
            t.Purr = pet;
            if (T > _nextPurr) { b.EmitBody(EffectKind.Purr, 16f, -26f, 4f, -6f, 0.8f, 0.9f); _nextPurr = T + 0.45f; }
        }

        if (nap && s > _napFor)
        {
            Next = new WakeActivity(gentle: true, pretend: false, then: null);
            Done = true;
        }
    }

    public override void Exit(PetBrain b) => b.Effects.DreamBubble = 0;
}

sealed class WakeActivity(bool gentle, bool pretend, Activity? then) : Activity
{
    float _offset;
    public override PetState State => PetState.Stretching;
    public override Gaze Gaze => T > _offset + 1f ? Gaze.Eyes : Gaze.None;

    public override void Enter(PetBrain b)
    {
        _offset = pretend ? 1.9f : 0f;
        if (!gentle) b.EmitHead(EffectKind.Exclaim, 6f, -40f, 0, -12f, 1f, 1f);
    }

    public override void Tick(PetBrain b, PetPose t, float dt)
    {
        b.Animator.AllowBlink = T > _offset + 1.2f;
        if (pretend && T < _offset)
        {
            // Pretending to sleep… and peeking with one eye.
            t.Lie = 1; t.Curl = 1; t.TailWrap = 1; t.EarLift = -0.2f;
            t.EyeOpen = 1f;
            t.EyeOpenL = 0f;
            t.EyeOpenR = 0.55f * Ramp(T, 0.3f, 0.3f) * (1f - Ramp(T, 1.2f, 0.2f));
            t.LookX = 0.4f; t.LookY = -0.3f;
            t.Smile = 0.6f;
            if (T > 1.6f) { t.HappyEyes = 1f; t.EarLift = 0.6f; }
            return;
        }

        float s = T - _offset;
        if (!gentle)
        {
            // Startled awake: a quick puffed-up arch, then composure.
            float up = Ramp(s, 0, 0.25f);
            t.Lie = 1f - up;
            t.Arch = Pulse(s, 0.05f, 0.9f);
            t.Puff = Pulse(s, 0f, 1f);
            t.EyeSize = 1.2f - 0.2f * Ramp(s, 0.4f, 0.5f);
            t.PupilSize = 1f;
            t.EarLift = -0.6f * (1f - Ramp(s, 0.6f, 0.6f));
            t.TailRaise = 1f;
            t.Hop = Hop(s, 0f, 0.3f, 7f);
            if (s > 1.3f) Finish(new TrickActivity(Trick.Groom));
            return;
        }

        // Gentle wake: flutter → uncurl → yawn → stand → stretch → head shake.
        float flutter = 1f - Ramp(s, 0.9f, 0.3f);
        t.Lie = 1f - Ramp(s, 2.0f, 0.5f);
        t.Curl = 1f - Ramp(s, 0.5f, 0.6f);
        t.TailWrap = 1f - Ramp(s, 1.6f, 0.6f);
        t.EyeOpen = Lerp(0.15f + 0.35f * MathF.Abs(MathF.Sin(s * 6.5f)), 1f, 1f - flutter);
        t.EarLift = Lerp(-0.4f, 0.1f, Ramp(s, 0.3f, 1f));
        float yawn = Pulse(s, 1.0f, 1.3f);
        t.MouthOpen = yawn;
        t.Tongue = 0.25f * yawn;
        t.EyeOpen *= 1f - yawn * 0.9f;
        t.HeadLift = 0.7f * yawn;
        t.EarLift -= 0.5f * yawn;
        float bow = Pulse(s, 2.5f, 1.4f);
        t.Bow = bow;
        t.TailRaise = 0.4f + 0.6f * bow;
        t.EyeOpen *= 1f - 0.7f * bow;
        if (s > 3.9f)
        {
            float shake = 1f - Ramp(s, 3.9f, 0.6f);
            t.HeadTilt = MathF.Sin(s * 48f) * 14f * shake;
            t.EarFlapL = t.EarFlapR = MathF.Sin(s * 48f + 1f) * 18f * shake;
        }
        if (s > 4.6f) Finish(then);
    }

    void Finish(Activity? next)
    {
        Next = next;
        Done = true;
    }
}

// ============================================================================================
//  Social and emotional reactions
// ============================================================================================

enum GreetingKind { Hello, Return, Morning, LongAbsence }

sealed class GreetingActivity(GreetingKind kind) : Activity
{
    float _start = -1f, _nextBump, _hiAt;
    int _bumps;
    bool _saidHi;
    public override PetState State => PetState.Greeting;
    public override Gaze Gaze => Gaze.Eyes;

    public override void Enter(PetBrain b)
    {
        b.Memory.TimesGreeted++;
        _bumps = 2 + (int)MathF.Round(b.Memory.Bond * 2f) + (kind == GreetingKind.LongAbsence ? 2 : 0);
    }

    public override void Tick(PetBrain b, PetPose t, float dt)
    {
        // Trot over to where you are first (cursor), tail high.
        if (_start < 0)
        {
            bool far = b.Senses.CursorKnown && MathF.Abs(b.Senses.Cursor.X - b.Position.X) / b.Scale > 260f;
            if (far && T < 4f && kind != GreetingKind.Hello)
            {
                b.MoveToward(b.Senses.Cursor.X - MathF.Sign(b.Senses.Cursor.X - b.Position.X) * 75f * b.Scale, 150f, dt, t, 2f);
                t.HeadTurn = 0.6f; t.TailRaise = 1f; t.TailCurl = 0.5f; t.PupilSize = 0.9f;
                return;
            }
            _start = T;
            _hiAt = kind == GreetingKind.Morning ? 1.3f : 0f; // after the morning stretch
            _nextBump = T + _hiAt + 2.3f;                     // head bumps once the wave is done
            if (kind == GreetingKind.Morning) b.BurstHead(EffectKind.Sparkle, 6, 40f, 1.1f, 1.2f, -30f);
        }

        float s = T - _start;
        t.HeadTurn = 0f;
        t.Smile = 0.9f;
        t.Blush = 0.75f;
        t.TailRaise = 1f;
        t.TailCurl = 0.8f;           // the "question mark" tail
        t.TailSway = 4f;             // with a happy little quiver
        t.TailSwaySpeed = 6f;
        t.EarLift = 0.4f;
        t.PupilSize = 0.8f;
        t.Sparkle = 1f;

        if (kind == GreetingKind.Morning && s < 1.3f)
        {
            // Morning stretch before saying hello.
            t.Bow = Pulse(s, 0f, 1.3f);
            t.MouthOpen = Pulse(s, 0.2f, 1f);
            t.EyeOpen = 1f - 0.8f * Pulse(s, 0.2f, 1f);
            return;
        }

        // Sits up facing you, waves a paw and says "Hi!".
        float hi = s - _hiAt;
        if (!_saidHi && hi >= 0.15f)
        {
            _saidHi = true;
            b.Say("Hi!");
        }
        float up = Ramp(hi, 0f, 0.3f);
        t.Sit = up;
        t.Front = up;
        t.Wave = Ramp(hi, 0.1f, 0.25f) * (1f - Ramp(hi, 1.9f, 0.3f));

        // Affectionate head bumps ("bunting") toward you, then a slow blink.
        if (_bumps > 0 && T >= _nextBump)
        {
            _bumps--;
            _nextBump = T + 0.55f;
            if (b.Rng.NextDouble() < 0.5) b.EmitHead(EffectKind.Heart, (float)(b.Rng.NextDouble() - 0.5) * 26f, -36f, 0, -20f, 0.9f, 1.5f);
        }
        float bump = Pulse(T, _nextBump - 0.55f, 0.45f);
        t.HeadDx = 4f * bump;
        t.HeadDy = 2f * bump;
        t.HeadTilt = -10f * bump;
        t.EyeOpen = 1f - 0.7f * bump;
        if (_bumps == 0 && T > _nextBump)
        {
            t.EyeOpen *= SlowBlink(T, _nextBump);
            if (T > _nextBump + 1.6f) { Next = new SitActivity(facing: true, pawsUp: true); Done = true; }
        }
    }
}

sealed class HappyActivity(bool special = false, bool wink = false) : Activity
{
    public override PetState State => PetState.Happy;
    public override Gaze Gaze => Gaze.None;

    public override void Enter(PetBrain b)
    {
        if (special) b.BurstHead(EffectKind.Heart, 5, 35f, 1f, 1.5f, -30f);
        else if (b.Rng.NextDouble() < 0.45) b.Mew();
        else if (b.Rng.NextDouble() < 0.5) b.EmitHead(EffectKind.Heart, 10f, -36f, 4f, -22f, 1f, 1.5f);
    }

    public override void Tick(PetBrain b, PetPose t, float dt)
    {
        t.HeadTurn = 0f;
        t.LookX = 0f;
        t.LookY = 0f;
        t.Smile = 0.9f;
        t.Blush = 0.8f;
        t.TailRaise = 1f;
        t.TailCurl = 0.8f;
        t.TailSway = 4f;
        t.TailSwaySpeed = 6f;
        t.EarLift = 0.45f;
        t.PupilSize = 0.85f;

        if (special)
        {
            // A twisting kitten leap with a squishy landing.
            t.Hop = Hop(T, 0.05f, 0.62f, 28f);
            t.Leap = Pulse(T, 0.05f, 0.62f);
            t.Flip = -360f * Ramp(T, 0.12f, 0.45f);
            t.Squash = LandSquash(T, 0.67f, 0.45f);
            t.HappyEyes = 1f;
            if (T > 1.5f) Done = true;
            return;
        }

        if (wink)
        {
            t.EyeOpenL = 1f - Pulse(T, 0.15f, 0.55f);
            t.Sparkle = 1f;
        }
        else t.EyeOpen *= SlowBlink(T, 0.15f);
        // A happy head bump toward you.
        float bump = Pulse(T, 0.05f, 0.5f);
        t.HeadDx = 3.5f * bump;
        t.HeadTilt = -8f * bump;
        t.Hop = Hop(T, 0.08f, 0.3f, 4f);
        t.Squash = LandSquash(T, 0.38f, 0.25f);
        if (T > 1.7f) Done = true;
    }
}

sealed class ExcitedActivity(int streak) : Activity
{
    float _duration = 2.4f, _nextHop, _turnAt = -1f;
    int _hopIndex, _streak = streak;
    public override PetState State => PetState.Excited;
    public override Gaze Gaze => Gaze.None;

    public void Boost(int streak)
    {
        _streak = Math.Max(_streak, streak);
        _duration = MathF.Min(T + 2.2f, 6f);
    }

    public override void Tick(PetBrain b, PetPose t, float dt)
    {
        // The kitten "crab hop": arched, puffed and bouncing sideways.
        float height = 8f + 1.4f * Math.Min(_streak, 8);
        if (T >= _nextHop)
        {
            _hopIndex++;
            _nextHop = T + 0.44f;
            if (_hopIndex % 3 == 0) _turnAt = T + 0.18f;
            b.BurstHead(EffectKind.Sparkle, 2, 30f, 0.9f, 0.9f, -20f);
        }
        if (_turnAt >= 0 && T >= _turnAt) { b.TurnAround(); _turnAt = -1f; }
        float local = 0.44f - (_nextHop - T);
        t.Hop = Hop(local, 0f, 0.36f, height);
        t.Squash = LandSquash(local, 0.36f, 0.3f);
        b.Nudge((_hopIndex % 2 == 0 ? -1f : 1f) * 26f * dt);
        t.Arch = 0.55f;
        t.Puff = 0.55f;
        t.HeadTurn = 0.05f;
        t.PupilSize = 1f;
        t.EyeSize = 1.08f;
        t.MouthOpen = 0.25f;
        t.Smile = 0.8f;
        t.Blush = 0.9f;
        t.Sparkle = 1f;
        t.TailRaise = 1f;
        t.TailCurl = 0.2f;
        t.EarLift = 0.3f;
        t.Whisker = 0.6f;
        if (T > _duration) Done = true;
    }
}

sealed class CelebrateActivity : Activity
{
    float _nextNote, _nextBeat = 2.5f;
    bool _burst;
    public override PetState State => PetState.Celebrating;
    public override Gaze Gaze => Gaze.None;

    public override void Enter(PetBrain b)
    {
        b.BurstHead(EffectKind.Sparkle, 10, 55f, 1.3f, 1.4f, -30f);
        b.Mew();
    }

    public override void Tick(PetBrain b, PetPose t, float dt)
    {
        t.Smile = 1f; t.Blush = 1f; t.Sparkle = 1f; t.TailRaise = 1f; t.TailCurl = 0.8f; t.HeadTurn = 0f; t.PupilSize = 0.9f;
        if (T >= _nextNote && T < 4.4f)
        {
            b.EmitHead(EffectKind.Note, (float)(b.Rng.NextDouble() - 0.5) * 40f, -40f, (float)(b.Rng.NextDouble() - 0.5) * 20f, -22f, 1f, 1.6f);
            _nextNote = T + 0.38f;
        }

        if (T < 1.6f)
        {
            // Sitting, kneading the air with a happy head bop.
            t.Sit = 1f;
            t.Front = 1f;
            t.PawsUp = 1f;
            t.Knead = 1f;
            t.Purr = 1f;
            t.HeadTilt = MathF.Sin(T * 9f) * 12f;
            t.HappyEyes = 1f;
        }
        else if (T < 2.5f)
        {
            // Backflip!
            float s = T - 1.6f;
            t.Hop = Hop(s, 0.05f, 0.75f, 36f);
            t.Leap = Pulse(s, 0.05f, 0.75f);
            t.Flip = 360f * Ramp(s, 0.12f, 0.58f);
            t.EyeSize = 1.15f;
            t.EarLift = 0.6f;
            t.Squash = LandSquash(s, 0.8f, 0.5f);
            if (s > 0.8f && !_burst)
            {
                _burst = true;
                b.BurstHead(EffectKind.Sparkle, 8, 60f, 1.2f, 1.2f, 0f);
                b.BurstHead(EffectKind.Heart, 4, 40f, 1f, 1.5f, -20f);
                b.EmitFeet(3);
            }
        }
        else
        {
            // Side-to-side crab-hop dance.
            float s = T - 2.5f;
            float beat = s % 0.5f;
            t.Hop = Hop(beat, 0f, 0.4f, 8f);
            t.Arch = 0.4f;
            t.Puff = 0.3f;
            if (T >= _nextBeat) { if (T > 2.6f) b.TurnAround(); _nextBeat += 0.5f; }
            t.HeadTilt = MathF.Sin(s * 12.5f) * 10f;
            t.HappyEyes = 1f;
        }
        if (T > 4.8f) { Next = new HappyActivity(); Done = true; }
    }
}

/// <summary>Being petted: purring, leaning into the hand, then kneading happily.</summary>
sealed class PettedActivity(float strength, bool auto = false) : Activity
{
    float _lastStroke, _nextHeart = 0.4f, _nextPurr, _intensity = strength;
    public override PetState State => PetState.BeingPetted;
    public override Gaze Gaze => Gaze.None;

    public void Stroke(float strength)
    {
        _lastStroke = T;
        _intensity = MathF.Min(1f, MathF.Max(_intensity, strength));
    }

    public override void Enter(PetBrain b)
    {
        b.Memory.TimesPetted++;
        b.Personality.Nudge(Personality.Trait.Affectionate, 0.004f);
        b.Personality.Nudge(Personality.Trait.Shy, -0.002f);
    }

    public override void Tick(PetBrain b, PetPose t, float dt)
    {
        if (auto && T < 3.4f) _lastStroke = T;
        var o = b.Animator.Output;
        bool lying = o.Lie > 0.5f;
        if (lying) t.Lie = 1f;
        else t.Sit = Ramp(T, 1.2f, 0.6f);
        t.Front = lying ? 0f : t.Sit; // sits up facing you to enjoy it
        t.TailWrap = lying ? 0.8f : 1f;

        float lean = MathF.Sin(T * 2.2f);
        t.EyeOpen = 0.08f;           // eyes blissfully closed
        t.Blush = 0.95f;
        t.Smile = 1f;
        t.EarLift = -0.2f;
        t.HeadTilt = 10f * lean;
        t.HeadDx = 1.8f * lean;
        t.HeadTurn = 0.05f;
        t.HeadLift = 0.25f;           // chin up for scritches
        t.Whisker = 0.3f;
        t.TailRaise = lying ? 0f : 1f;
        t.TailCurl = 0.7f;
        t.TailSway = 3f;
        t.TailSwaySpeed = 5f;
        t.Purr = 1f;
        t.Knead = Ramp(T, 2.2f, 0.6f) * (o.Sit > 0.5f || lying ? 1f : 0f);

        if (T > _nextPurr)
        {
            b.EmitBody(EffectKind.Purr, 18f + (float)b.Rng.NextDouble() * 6f, -22f, 6f, -8f, 0.8f + 0.3f * _intensity, 0.9f);
            _nextPurr = T + 0.35f;
        }
        if (T > _nextHeart)
        {
            b.EmitHead(EffectKind.Heart, (float)(b.Rng.NextDouble() - 0.5) * 34f, -34f, (float)(b.Rng.NextDouble() - 0.5) * 12f, -24f, 0.9f, 1.5f);
            _nextHeart = T + 0.7f + (float)b.Rng.NextDouble() * 0.4f;
        }
        if (T - _lastStroke > 1f) Done = true;
    }
}

sealed class CuriousActivity : Activity
{
    float _duration, _dir;
    public override PetState State => PetState.Curious;
    public override Gaze Gaze => Gaze.Eyes;

    public override void Enter(PetBrain b)
    {
        _duration = 2.4f + (float)b.Rng.NextDouble() * 1.8f;
        _dir = b.Rng.NextDouble() < 0.5 ? -1f : 1f;
        if (b.Rng.NextDouble() < 0.5) b.EmitHead(EffectKind.Question, 16f, -40f, 0, -10f, 1f, 1.4f);
    }

    public override void Tick(PetBrain b, PetPose t, float dt)
    {
        float k = Ramp(T, 0.1f, 0.35f);
        if (b.Animator.Output.Sit > 0.5f) { t.Sit = 1f; t.TailWrap = 1f; }
        t.HeadTilt = 16f * _dir * k;
        t.BrowRaise = 0.8f * k;
        t.EarLift = 0.7f * k;
        t.EarAsym = 0.5f * _dir * k;
        t.PupilSize = Lerp(0.55f, 0.92f, k);
        t.Whisker = 0.9f * k;
        t.HeadTurn = Math.Clamp(b.CursorRel.X / 200f, -0.4f, 0.6f);
        t.TailSway = 9f;
        t.TailSwaySpeed = 1.8f;
        // Tilts the other way half-way through, like a kitten trying to understand.
        if (T > _duration * 0.55f) t.HeadTilt = Lerp(t.HeadTilt, -16f * _dir, Ramp(T, _duration * 0.55f, 0.3f));
        if (T > _duration) Done = true;
    }
}

/// <summary>Startled: the classic arched, puffed-up kitten — never lasting fear.</summary>
sealed class StartleActivity : Activity
{
    public override PetState State => PetState.Surprised;
    public override Gaze Gaze => Gaze.Eyes;

    public override void Enter(PetBrain b) => b.EmitHead(EffectKind.Exclaim, 8f, -42f, 0, -14f, 1f, 1.1f);

    public override void Tick(PetBrain b, PetPose t, float dt)
    {
        float k = 1f - Ramp(T, 0.9f, 0.4f);
        t.Arch = k;
        t.Puff = k;
        t.Hop = Hop(T, 0f, 0.32f, 10f);
        if (T < 0.3f) b.Nudge(-45f * dt);
        t.Squash = LandSquash(T, 0.32f, 0.25f);
        t.EyeSize = 1f + 0.22f * k;
        t.PupilSize = 1f;
        t.EarLift = -0.8f * k;
        t.Whisker = -0.8f * k;
        t.TailRaise = 1f;
        t.TailCurl = 0.1f;
        t.HeadTurn = 0.15f;
        if (T > 1.3f) { Next = new TrickActivity(Trick.Groom); Done = true; } // "I meant to do that."
    }
}

/// <summary>Stalk, wiggle and pounce — on the cursor if it is close, otherwise on imaginary prey.</summary>
sealed class HuntActivity : Activity
{
    float _stalkFor, _wiggleAt, _pounceAt, _target;
    bool _pounced;
    public override PetState State => T < _pounceAt ? PetState.Hunting : PetState.Playful;
    public override Gaze Gaze => Gaze.Eyes;
    public override bool Interruptible => true;

    public override void Enter(PetBrain b)
    {
        _stalkFor = 1.6f + (float)b.Rng.NextDouble() * 1.4f;
        _wiggleAt = _stalkFor;
        _pounceAt = _wiggleAt + 0.9f;
        bool cursorPrey = b.Senses.CursorKnown && b.CursorInterest > 0.4f && MathF.Abs(b.CursorRel.X) < 420f;
        float preyX = cursorPrey ? b.Senses.Cursor.X : b.Position.X + b.Facing * (120f + (float)b.Rng.NextDouble() * 120f) * b.Scale;
        if (MathF.Sign(preyX - b.Position.X) != b.Facing) b.TurnAround();
        _target = Math.Clamp(preyX - b.Facing * 70f * b.Scale, b.MinX, b.MaxX);
    }

    public override void Tick(PetBrain b, PetPose t, float dt)
    {
        t.PupilSize = 1f;
        t.EarLift = 0.75f;
        t.Whisker = 1f;
        t.HeadTurn = 0.7f;
        t.LookX = 1f;
        t.LookY = 0.15f;

        if (T < _pounceAt)
        {
            // Low creep with the tail tip twitching…
            t.Crouch = 1f;
            t.TailRaise = -0.15f;
            t.TailCurl = 0.25f;
            t.TailSway = 12f;
            t.TailSwaySpeed = 2.4f;
            if (T < _stalkFor) b.MoveToward(_target, 18f, dt, t, 0.7f);
            else
            {
                // …then the butt wiggle.
                float w = (T - _wiggleAt) * 14f;
                t.BodyTilt = -3f * MathF.Sin(w);
                t.Hop = 0.6f + 0.6f * MathF.Sin(w * 2f);
            }
            return;
        }

        float s = T - _pounceAt;
        if (!_pounced)
        {
            _pounced = true;
            b.EmitHead(EffectKind.Exclaim, 10f, -40f, 0, -12f, 1f, 0.9f);
        }
        t.Hop = Hop(s, 0f, 0.46f, 26f);
        t.Leap = Pulse(s, 0f, 0.5f);
        if (s < 0.46f) b.Nudge(120f * dt);
        t.Squash = LandSquash(s, 0.46f, 0.4f);
        t.TailRaise = 0.3f;
        t.MouthOpen = 0.3f * Pulse(s, 0f, 0.5f);
        // Landed: a couple of excited swats at the "prey".
        if (s > 0.55f)
        {
            t.Crouch = 0.5f;
            t.FrontPawRaise = Pulse(s, 0.6f, 0.25f) + Pulse(s, 0.95f, 0.25f);
            t.HappyEyes = s > 1.3f ? 1f : 0f;
        }
        if (s > 1.7f) Done = true;
    }
}

// ============================================================================================
//  Physical states: carried, falling, landing
// ============================================================================================

/// <summary>Held under the arms like a kitten: long, dangly, paw beans showing.</summary>
sealed class HeldActivity : Activity
{
    public override PetState State => PetState.Held;
    public override Gaze Gaze => Gaze.None;

    public override void Enter(PetBrain b)
    {
        if (b.Rng.NextDouble() < 0.5) b.Mew();
    }

    public override void Tick(PetBrain b, PetPose t, float dt)
    {
        b.UpdateHold(t, dt);
        t.Held = 1f;
        float surprise = 1f - Ramp(T, 0.35f, 0.4f);
        t.EyeSize = 1f + 0.16f * surprise;
        t.PupilSize = 0.6f + 0.4f * surprise;
        t.EarLift = Lerp(0.2f, 0.5f, surprise);
        t.MouthOpen = 0.25f * surprise;
        t.Smile = Lerp(0.3f, 0.6f, 1f - surprise);
        t.TailRaise = -0.2f;
        t.TailSway = 6f;
        t.LookY = -0.55f;
        t.LookX = 0.1f;
        t.HeadTurn = 0f;
        t.Blush = 0.65f;
        t.PawPaddle = MathF.Min(1f, 0.15f + b.HoldSpeed / 900f);
    }
}

/// <summary>Falling: the righting reflex — always lands on its feet.</summary>
sealed class FallActivity(bool thrown) : Activity
{
    public override PetState State => PetState.Falling;
    public override Gaze Gaze => Gaze.None;
    public bool Thrown => thrown;

    public override void Tick(PetBrain b, PetPose t, float dt)
    {
        t.EyeSize = 1.12f;
        t.PupilSize = 1f;
        t.EarLift = -0.3f;
        t.Whisker = -0.4f;
        t.Puff = 0.35f;
        t.MouthOpen = thrown ? 0.3f : 0.15f;
        t.HappyEyes = thrown && T > 0.3f ? 1f : 0f;
        t.TailRaise = 0.9f;
        t.TailSway = 18f;
        t.TailSwaySpeed = 2f;
        t.HeadTurn = 0.1f;
        if (T > 12f) Done = true; // safety net; landing normally ends this
    }
}

sealed class LandActivity(float impact, bool thrown) : Activity
{
    public override PetState State => PetState.Landing;
    public override Gaze Gaze => Gaze.None;

    public override void Enter(PetBrain b) => b.EmitFeet(Math.Clamp((int)(impact / 300f), 2, 6));

    public override void Tick(PetBrain b, PetPose t, float dt)
    {
        float k = MathF.Min(1f, impact / 900f);
        t.Squash = 0.5f * k * (1f - Ramp(T, 0f, 0.35f));
        t.Crouch = 0.6f * k * (1f - Ramp(T, 0.1f, 0.3f));
        t.EyeOpen = 1f - 0.6f * k * Pulse(T, 0f, 0.3f);
        t.EarLift = -0.4f * Pulse(T, 0f, 0.35f);
        if (T > 0.42f)
        {
            Next = thrown ? new HuntActivity()
                : impact > 1300f ? new TrickActivity(Trick.Groom)
                : new TrickActivity(Trick.ShakeOff);
            Done = true;
        }
    }
}

// ============================================================================================
//  Little surprises
// ============================================================================================

enum Trick { Groom, Stretch, ChaseTail, SlowBlink, Blep, StareAtNothing, Knead, CrabHop, Doze, Sneeze, Chirp, PawSwat, ShakeOff, Wink }

sealed class TrickActivity(Trick trick) : Activity
{
    float _duration, _nextEvent;
    int _count;
    public Trick Kind => trick;

    public override PetState State => trick switch
    {
        Trick.Groom => PetState.Grooming,
        Trick.Knead => PetState.Kneading,
        Trick.ChaseTail or Trick.CrabHop or Trick.PawSwat => PetState.Playful,
        Trick.SlowBlink or Trick.Wink => PetState.Happy,
        Trick.Doze => PetState.LyingDown,
        Trick.Stretch => PetState.Stretching,
        Trick.StareAtNothing or Trick.Chirp => PetState.Curious,
        _ => PetState.Idle,
    };

    public override Gaze Gaze => trick is Trick.Doze or Trick.StareAtNothing or Trick.Wink or Trick.SlowBlink or Trick.Groom ? Gaze.None : Gaze.Eyes;
    public override bool IsCalm => trick is Trick.Doze or Trick.Blep;
    public override bool Interruptible => true;

    public override void Enter(PetBrain b)
    {
        _duration = trick switch
        {
            Trick.Groom => 3.2f,
            Trick.Stretch => 2.4f,
            Trick.ChaseTail => 2.4f,
            Trick.SlowBlink => 2f,
            Trick.Blep => 4f + (float)b.Rng.NextDouble() * 2f,
            Trick.StareAtNothing => 3.2f,
            Trick.Knead => 4f,
            Trick.CrabHop => 1.9f,
            Trick.Doze => 6f + (float)b.Rng.NextDouble() * 3f,
            Trick.Sneeze => 1.5f,
            Trick.Chirp => 1.8f,
            Trick.PawSwat => 1.5f,
            Trick.ShakeOff => 0.9f,
            _ => 1.2f,
        };
        if (trick == Trick.StareAtNothing && b.Facing != (b.Position.X > (b.MinX + b.MaxX) / 2 ? 1 : -1)) b.TurnAround();
        if (trick == Trick.Chirp) b.Mew();
    }

    public override void Tick(PetBrain b, PetPose t, float dt)
    {
        switch (trick)
        {
            case Trick.Groom:
            {
                // Lick the paw… then wash the face with it.
                t.Sit = 1f;
                t.TailWrap = 1f;
                b.Animator.AllowBlink = false;
                float lick = Ramp(T, 0.2f, 0.3f) * (1f - Ramp(T, 1.5f, 0.2f));
                float wash = Ramp(T, 1.5f, 0.25f) * (1f - Ramp(T, 2.8f, 0.3f));
                t.PawToFace = MathF.Max(lick, wash);
                t.HeadTurn = 0.3f;
                t.HeadLift = -0.3f * lick;
                t.Tongue = lick * (0.3f + 0.35f * MathF.Abs(MathF.Sin(T * 9f)));
                t.MouthOpen = 0.1f * lick;
                t.EyeOpen = 1f - 0.9f * MathF.Max(lick, wash);
                t.HeadTilt = 14f * wash * MathF.Sin(T * 7f);
                t.HeadDy = 2.5f * wash * MathF.Sin(T * 7f + 0.6f);
                t.EarLift = -0.3f * wash;
                break;
            }
            case Trick.Stretch:
            {
                float bow = Pulse(T, 0f, 1.7f);
                t.Bow = bow;
                t.EyeOpen = 1f - 0.85f * bow;
                t.MouthOpen = Pulse(T, 0.3f, 1.1f);
                t.Tongue = 0.25f * Pulse(T, 0.3f, 1.1f);
                t.TailRaise = 0.4f + 0.6f * bow;
                t.EarLift = -0.4f * bow;
                break;
            }
            case Trick.ChaseTail:
                t.GaitAmount = 2f;
                b.AdvanceGait(dt * 10f);
                t.Crouch = 0.3f;
                t.HeadTurn = -0.7f;
                t.LookX = -1f;
                t.PupilSize = 1f;
                t.TailRaise = 0.5f;
                t.TailSway = 25f;
                t.TailSwaySpeed = 3f;
                if (T > _nextEvent && T < 2.0f) { b.TurnAround(); _nextEvent = T + 0.26f; }
                t.Hop = Hop(T % 0.26f, 0f, 0.26f, 3f);
                if (T > 2.0f) { t.GaitAmount = 0f; t.HeadTurn = 0f; t.LookX = MathF.Sin(T * 12f) * 0.7f; }
                break;
            case Trick.SlowBlink:
                t.HeadTurn = 0f; t.LookX = 0f; t.LookY = 0f;
                t.EyeOpen *= Activity.SlowBlink(T, 0.3f);
                t.Smile = 0.8f;
                t.EarLift = -0.1f;
                if (T > 0.5f && _count == 0) { _count++; b.EmitHead(EffectKind.Heart, 14f, -34f, 4f, -16f, 0.8f, 1.4f); }
                break;
            case Trick.Blep:
                // Tongue tip poking out, totally unaware.
                t.Sit = 1f;
                t.TailWrap = 1f;
                t.Tongue = 0.16f * (1f - Ramp(T, _duration - 0.5f, 0.2f));
                t.EyeOpen = 0.92f;
                t.LookX = 0.1f;
                t.LookY = 0.1f;
                if (T > _duration - 0.5f) t.EarFlapR = 18f * Pulse(T, _duration - 0.5f, 0.3f);
                break;
            case Trick.StareAtNothing:
                t.HeadTurn = 0.7f;
                t.LookX = 1f;
                t.LookY = -0.4f;
                t.PupilSize = 1f;
                t.EarLift = 0.7f;
                t.Whisker = 0.8f;
                t.HeadTilt = 10f * Ramp(T, 1.8f, 0.3f);
                if (T > 2f && _count == 0) { _count++; b.EmitHead(EffectKind.Question, 18f, -40f, 0, -10f, 0.9f, 1.3f); }
                break;
            case Trick.Knead:
            {
                bool lying = b.Animator.Output.Lie > 0.5f;
                if (lying) t.Lie = 1f; else t.Sit = 1f;
                t.TailWrap = 0.8f;
                t.Knead = Ramp(T, 0.2f, 0.4f);
                t.Purr = 0.8f;
                t.EyeOpen = 0.05f; // blissfully closed while making biscuits
                t.Smile = 0.9f;
                t.Blush = 0.8f;
                if (T > _nextEvent) { b.EmitBody(EffectKind.Purr, 18f, -20f, 5f, -6f, 0.8f, 0.9f); _nextEvent = T + 0.5f; }
                break;
            }
            case Trick.CrabHop:
            {
                if (_count == 0) { _count++; b.EmitHead(EffectKind.Exclaim, 8f, -42f, 0, -14f, 1f, 1f); }
                t.Arch = 0.6f; t.Puff = 0.6f; t.PupilSize = 1f; t.EarLift = 0.2f;
                t.TailRaise = 1f; t.TailCurl = 0.1f;
                float local = T % 0.45f;
                t.Hop = Hop(local, 0f, 0.38f, 10f);
                b.Nudge((((int)(T / 0.45f)) % 2 == 0 ? -1f : 1f) * 30f * dt);
                break;
            }
            case Trick.Doze:
            {
                // Dozes off in a loaf… nods… and blinks awake.
                t.Lie = 1f;
                t.TailWrap = 1f;
                b.Animator.AllowBlink = false;
                float sleepy = Ramp(T, 0.5f, 2.5f) * (1f - Ramp(T, _duration - 0.6f, 0.15f));
                t.EyeOpen = 1f - sleepy;
                t.HeadLift = -0.6f * sleepy + 0.3f * Pulse(T % 2.2f, 1.6f, 0.3f) * sleepy;
                t.EarLift = -0.3f * sleepy;
                float jolt = Pulse(T, _duration - 0.6f, 0.6f);
                t.EyeSize = 1f + 0.18f * jolt;
                t.EarLift += 1f * jolt;
                break;
            }
            case Trick.Sneeze:
            {
                float ah = Ramp(T, 0f, 0.6f) * (1f - Ramp(T, 0.62f, 0.05f));
                float choo = Pulse(T, 0.62f, 0.3f);
                t.HeadLift = 0.5f * ah - 0.6f * choo;
                t.EyeOpen = 1f - 0.75f * ah - choo;
                t.MouthOpen = 0.2f * ah + 0.35f * choo;
                t.Whisker = 0.8f * ah - choo;
                t.Squash = 0.3f * choo;
                t.HeadTurn = 0f;
                if (T > 0.62f && _count == 0) { _count++; b.EmitHead(EffectKind.Dust, 2f, 10f, 0, 6f, 0.7f, 0.6f); }
                if (T > 0.9f) t.HeadTilt = MathF.Sin(T * 30f) * 8f * (1f - Ramp(T, 0.9f, 0.5f));
                break;
            }
            case Trick.Chirp:
                // Chattering at something only a cat can see.
                t.HeadTurn = 0.6f;
                t.LookY = -0.5f;
                t.PupilSize = 1f;
                t.EarLift = 0.8f;
                t.Whisker = 1f;
                t.MouthOpen = 0.3f * MathF.Max(0, MathF.Sin(T * 26f)) * (T < 1.2f ? 1f : 0f);
                t.TailSway = 16f;
                t.TailSwaySpeed = 3f;
                break;
            case Trick.PawSwat:
                t.Crouch = 0.4f;
                t.PupilSize = 1f;
                t.EarLift = 0.6f;
                t.HeadTurn = 0.6f;
                t.FrontPawRaise = Pulse(T, 0.3f, 0.25f) + Pulse(T, 0.75f, 0.25f);
                t.TailSway = 14f;
                t.TailSwaySpeed = 2.5f;
                break;
            case Trick.ShakeOff:
            {
                float k = 1f - Ramp(T, 0.1f, 0.8f);
                t.HeadTilt = MathF.Sin(T * 50f) * 14f * k;
                t.Wiggle = MathF.Sin(T * 50f) * 3f * k;
                t.EarFlapL = t.EarFlapR = MathF.Sin(T * 50f + 1f) * 18f * k;
                t.EyeOpen = 0.4f;
                break;
            }
            case Trick.Wink:
                t.HeadTurn = 0f; t.LookX = 0f; t.LookY = 0f;
                t.EyeOpenL = 1f - Pulse(T, 0.25f, 0.6f);
                t.Smile = 1f;
                t.Sparkle = 1f;
                t.HeadTilt = 6f * Pulse(T, 0.2f, 0.8f);
                if (T > 0.3f && _count == 0) { _count++; b.EmitHead(EffectKind.Sparkle, -16f, -6f, 0, -6f, 0.7f, 0.7f); }
                break;
        }
        if (T > _duration) Done = true;
    }
}

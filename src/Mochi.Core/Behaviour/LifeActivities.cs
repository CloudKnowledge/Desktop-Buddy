using Mochi.Core.Effects;
using Mochi.Core.Play;
using Mochi.Core.Rig;

namespace Mochi.Core.Behaviour;

/// <summary>A snack: laps up a saucer of milk or crunches through a bowl of treats, then licks its lips.</summary>
sealed class SnackActivity(PropKind kind, bool fromYou = false) : Activity
{
    const float DishX = 40f;
    float _eatFor;
    bool _yum;
    public override PetState State => PetState.Eating;
    public override Gaze Gaze => Gaze.None;

    public override void Enter(PetBrain b)
    {
        _eatFor = 7f + (float)b.Rng.NextDouble() * 5f;
        b.ShowProp(kind, DishX);
        if (fromYou) b.Say(kind == PropKind.Milk ? "Milk!" : "Treats!");
        else b.EmitHead(EffectKind.Heart, 6f, -36f, 0, -18f, 0.8f, 1.2f);
    }

    public override void Tick(PetBrain b, PetPose t, float dt)
    {
        if (b.Prop is not { } dish)
        {
            Done = true;
            return;
        }
        float end = 0.9f + _eatFor;
        dish.Alpha = Ramp(T, 0f, 0.3f) * (1f - Ramp(T, end + 1.9f, 0.35f));
        dish.Fill = 1f - 0.9f * Ramp(T, 0.9f, _eatFor);

        // Head right down into the dish, eyes closed in bliss, a little purr.
        float eat = Ramp(T, 0.7f, 0.4f) * (1f - Ramp(T, end, 0.45f));
        t.Crouch = 0.8f * eat;
        t.HeadLift = -eat;
        t.HeadDy = 8f * eat;
        t.HeadTurn = Lerp(0.3f, 0.75f, eat);
        t.EyeOpen = 1f - 0.85f * eat;
        t.Smile = 0.6f;
        t.TailRaise = 0.8f;
        t.TailCurl = 0.6f;
        t.TailSway = 3f;
        t.EarLift = 0.2f;
        t.Purr = 0.35f * eat;
        if (kind == PropKind.Milk)
        {
            // Quick little laps.
            t.Tongue = eat * (0.18f + 0.3f * MathF.Max(0f, MathF.Sin(T * 21f)));
            t.MouthOpen = 0.1f * eat;
        }
        else
        {
            // Crunch, crunch.
            t.MouthOpen = eat * 0.3f * MathF.Max(0f, MathF.Sin(T * 12f));
            t.HeadDy += eat * 1.2f * MathF.Sin(T * 12f);
        }

        // All gone: sits up and licks its lips, very pleased with itself.
        float after = Ramp(T, end, 0.4f);
        t.Sit = after;
        t.Tongue += 0.45f * (Pulse(T, end + 0.5f, 0.45f) + Pulse(T, end + 1.05f, 0.45f));
        t.HappyEyes = Pulse(T, end + 0.3f, 1.8f);
        if (!_yum && T > end + 0.4f)
        {
            _yum = true;
            b.Say("Yum!");
        }
        if (T > end + 2.3f) Done = true;
    }

    public override void Exit(PetBrain b) => b.HideProp();
}

/// <summary>Cat TV: a little set shows fish or birds; Mochi watches every move, chirps and paws at the screen.</summary>
sealed class WatchTvActivity : Activity
{
    const float TvX = 78f;
    float _duration, _nextSwat, _swatAt = -10f;
    bool _dozes;
    public override PetState State => PetState.WatchingTv;
    public override Gaze Gaze => Gaze.None;
    public override bool Interruptible => true;

    public override void Enter(PetBrain b)
    {
        _duration = 25f + (float)b.Rng.NextDouble() * 30f;
        _nextSwat = 5f + (float)b.Rng.NextDouble() * 6f;
        _dozes = b.Options.AutoSleep && b.Rng.NextDouble() < 0.2 + 0.4 * b.Personality.Lazy;
        // The TV goes on the side with room for it.
        float room = 125f * b.Scale;
        if (b.Facing > 0 ? b.Position.X + room > b.Floor.Right : b.Position.X - room < b.Floor.Left) b.TurnAround();
        b.ShowProp(PropKind.Tv, TvX);
    }

    public override void Tick(PetBrain b, PetPose t, float dt)
    {
        if (b.Prop is not { } tv)
        {
            Done = true;
            return;
        }
        tv.Alpha = Ramp(T, 0f, 0.5f) * (1f - Ramp(T, _duration, 0.5f));
        t.Sit = 1f;
        t.TailWrap = 0.7f;
        t.TailSway = 7f;
        t.TailSwaySpeed = 1.2f;
        t.EarLift = 0.65f;
        t.PupilSize = 0.95f;
        t.Whisker = 0.6f;

        // Eyes (and a little of the head) follow whatever is on the screen.
        var focus = PropRenderer.TvFocus(tv);
        t.LookX = Math.Clamp((TvX + focus.X - 14f) / 80f, -1f, 1f);
        t.LookY = Math.Clamp((focus.Y + 76f) / 110f, -1f, 1f);
        t.HeadTurn = 0.45f + 0.12f * Math.Clamp(focus.X / 12f, -1f, 1f);
        t.HeadTilt = 7f * MathF.Sin(T * 0.6f);

        // Now and then a swat at the screen, with a chirp.
        if (T > _nextSwat && T < _duration - 3f)
        {
            _swatAt = T;
            _nextSwat = T + 6f + (float)b.Rng.NextDouble() * 8f;
            if (b.Rng.NextDouble() < 0.6) b.Say(b.Rng.NextDouble() < 0.5 ? "mrrp!" : "ek-ek-ek!");
        }
        t.FrontPawRaise = Pulse(T, _swatAt, 0.35f) + Pulse(T, _swatAt + 0.4f, 0.3f);
        t.Crouch = 0.3f * Pulse(T, _swatAt - 0.2f, 1f);

        // A sleepy kitten nods off in front of the TV.
        if (_dozes)
        {
            float sleepy = Ramp(T, _duration - 8f, 6f);
            t.EyeOpen = 1f - sleepy;
            t.HeadLift = -0.5f * sleepy;
            t.EarLift -= 0.6f * sleepy;
            t.PupilSize = Lerp(0.95f, 0.5f, sleepy);
        }
        if (T > _duration + 0.6f)
        {
            if (_dozes) Next = new SleepActivity(nap: true);
            Done = true;
        }
    }

    public override void Exit(PetBrain b) => b.HideProp();
}

/// <summary>
/// "Catch me if you can!" When your pointer comes after Mochi along the floor it scampers away, glancing back,
/// then play-bows and waits for you to come again. Click it to catch it.
/// </summary>
sealed class TagActivity : Activity
{
    const int MaxRuns = 8;
    bool _fleeing, _leap;
    float _phaseAt, _target;
    int _runs;
    public override PetState State => PetState.Playful;
    public override Gaze Gaze => _fleeing ? Gaze.None : Gaze.EyesAndHead;

    public override void Enter(PetBrain b) => Flee(b);

    void Flee(PetBrain b)
    {
        _runs++;
        int away = b.Position.X >= b.Senses.Cursor.X ? 1 : -1;
        float room = away > 0 ? b.MaxX - b.Position.X : b.Position.X - b.MinX;
        float run = (190f + 130f * (float)b.Rng.NextDouble()) * b.Scale;
        _leap = room < 110f * b.Scale;
        if (_leap)
        {
            // Cornered: dart past you with a leap instead.
            away = -away;
            run = (260f + 80f * (float)b.Rng.NextDouble()) * b.Scale;
        }
        _target = Math.Clamp(b.Position.X + away * run, b.MinX, b.MaxX);
        if (b.Rng.NextDouble() < 0.35) b.Mew();
        _fleeing = true;
        _phaseAt = T;
    }

    public override void Tick(PetBrain b, PetPose t, float dt)
    {
        float s = T - _phaseAt;
        if (_fleeing)
        {
            bool there = b.MoveToward(_target, 210f, dt, t, 2f);
            t.TailRaise = 1f;
            t.TailCurl = 0.3f;
            t.EarLift = -0.2f;
            t.PupilSize = 1f;
            t.Smile = 0.8f;
            t.MouthOpen = 0.12f;
            t.HeadTurn = -0.35f; // glancing back over its shoulder at you
            if (_leap) t.Hop = Hop(s, 0.1f, 0.45f, 24f);
            if (there || s > 3.5f)
            {
                _fleeing = false;
                _phaseAt = T;
            }
            return;
        }

        // Play bow: front end down, bottom up, tail swishing. "Come on, catch me!"
        int toward = b.Senses.Cursor.X >= b.Position.X ? 1 : -1;
        if (toward != b.Facing && s < 0.2f) b.Turn(toward);
        t.Bow = 0.85f * Ramp(s, 0.1f, 0.25f);
        t.Wiggle = 2.5f * MathF.Sin(s * 15f) * Ramp(s, 0.4f, 0.2f);
        t.TailRaise = 1f;
        t.TailSway = 16f;
        t.TailSwaySpeed = 3f;
        t.EarLift = 0.6f;
        t.PupilSize = 1f;
        t.Smile = 0.75f;
        if (s > 0.35f && _runs < MaxRuns && b.CursorChasing)
        {
            Flee(b);
            return;
        }
        if (s > 5f || _runs >= MaxRuns)
        {
            if (b.Rng.NextDouble() < 0.6) b.Say(_runs >= MaxRuns ? "Phew!" : "mrrp?");
            Next = new SitActivity(facing: true);
            Done = true;
        }
    }
}

/// <summary>Busy at work, just like you: glasses on, a tiny laptop, paws pattering on the keys, the odd pause for thought.</summary>
sealed class WorkActivity : Activity
{
    static readonly string[] Quips = ["purr-fect!", "meow-ting?", "ship it!", "mrrp, a bug!", "brb, nap", "sent!"];
    float _duration, _nextThink, _thinkAt = -10f, _nextQuip;
    bool _signedOff;
    public override PetState State => PetState.Working;
    public override Gaze Gaze => Gaze.None;
    public override bool Interruptible => true;

    public override void Enter(PetBrain b)
    {
        _duration = 30f + (float)b.Rng.NextDouble() * 30f;
        _nextThink = 5f + (float)b.Rng.NextDouble() * 4f;
        _nextQuip = 6f + (float)b.Rng.NextDouble() * 8f;
        b.ShowProp(PropKind.Laptop, 0f);
    }

    public override void Tick(PetBrain b, PetPose t, float dt)
    {
        if (b.Prop is not { } laptop)
        {
            Done = true;
            return;
        }
        float packUp = Ramp(T, _duration, 0.8f);
        laptop.Alpha = Ramp(T, 0f, 0.3f) * (1f - Ramp(T, _duration + 1.1f, 0.3f));
        laptop.Fill = Ramp(T, 0.2f, 0.6f) * (1f - packUp); // the lid opens, and folds shut at the end

        if (T > _nextThink && T < _duration - 2f)
        {
            _thinkAt = T;
            _nextThink = T + 7f + (float)b.Rng.NextDouble() * 6f;
            if (b.Rng.NextDouble() < 0.4) b.EmitHead(EffectKind.Question, 12f, -40f, 0, -12f, 0.8f, 1.2f);
        }
        bool thinking = T > _thinkAt && T < _thinkAt + 2.2f;

        // Sitting square-on behind the laptop, paws pattering on the keys just above the lid.
        t.Sit = 1f;
        t.Front = 1f;
        t.TailWrap = 1f;
        t.TailSway = 4f;
        t.Glasses = Ramp(T, 0.3f, 0.4f) * (1f - Ramp(T, _duration + 0.4f, 0.4f));
        t.PawsUp = Ramp(T, 0.5f, 0.4f) * (1f - packUp);
        t.Knead = thinking ? 0f : Ramp(T, 0.9f, 0.3f) * (1f - packUp);
        // Eyes skim along the lines; now and then a pause to think, gazing up.
        t.HeadLift = thinking ? 0.25f : -0.3f;
        t.LookY = thinking ? -0.5f : 0.35f;
        t.LookX = thinking ? 0.3f : 0.35f * MathF.Sin(T * 1.7f);
        t.HeadTilt = thinking ? 12f * Ramp(T, _thinkAt, 0.3f) : 0f;
        t.EarLift = 0.3f;
        t.PupilSize = 0.6f;
        t.Smile = 0.4f;

        if (T > _nextQuip && T < _duration - 2f)
        {
            _nextQuip = T + 10f + (float)b.Rng.NextDouble() * 10f;
            if (b.Rng.NextDouble() < 0.6) b.Say(Quips[b.Rng.Next(Quips.Length)]);
        }
        if (!_signedOff && T > _duration)
        {
            _signedOff = true;
            b.Say(b.Rng.NextDouble() < 0.5 ? "Break time!" : "Done!");
        }
        if (T > _duration + 1.5f)
        {
            if (b.Rng.NextDouble() < 0.3) Next = new SnackActivity(PropKind.Milk); // a well-earned milk break
            Done = true;
        }
    }

    public override void Exit(PetBrain b) => b.HideProp();
}

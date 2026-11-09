using Mochi.Core.Effects;
using Mochi.Core.Play;
using Mochi.Core.Rig;
using SkiaSharp;

namespace Mochi.Core.Behaviour;

/// <summary>
/// Playing with the yarn ball. When you throw it, Mochi runs to where it will come down, springs up to catch
/// it in mid-air (or picks it up), and brings it back to you. A ball just lying about gets batted around,
/// stalked and pounced on instead.
/// </summary>
sealed class BallPlayActivity : Activity
{
    enum Phase { Watch, Chase, Bat, Stalk, Pounce, Jump, PickUp, Carry, Drop, Bored }

    const float Reach = 44f;      // ball distance in front of the anchor for a paw swat, model units
    const float MouthReach = 40f; // ...and for the mouth, standing or leaping
    const float LeapTime = 0.42f;
    const float JumpTime = 0.5f;

    Phase _phase = Phase.Watch;
    float _phaseAt, _pounceDist, _carryTo, _jumpHeight, _jumpNudge;
    int _bats, _rounds;
    bool _fetch, _fetchMode, _contact, _stalkNext, _willCatch, _caught;

    public override PetState State => _phase switch
    {
        Phase.PickUp or Phase.Carry or Phase.Drop => PetState.CarryingBall,
        Phase.Stalk or Phase.Pounce => PetState.Playful,
        _ => PetState.ChasingBall,
    };
    public override Gaze Gaze => Gaze.None; // the brain points the eyes at the ball instead

    public override void Enter(PetBrain b)
    {
        var p = b.Personality;
        _fetchMode = b.SinceBallThrown < 3f;
        if (_fetchMode)
        {
            // Your throw: go and catch it and bring it back (now and then a playful bat or two first).
            _rounds = b.Rng.NextDouble() < 0.25 ? 1 + b.Rng.Next(2) : 0;
            _fetch = b.Rng.NextDouble() < 0.9;
        }
        else
        {
            _rounds = 2 + b.Rng.Next(1 + (int)MathF.Round(p.Playful * 3f));
            _fetch = b.Rng.NextDouble() < 0.3 + 0.35 * p.Affectionate + 0.2 * p.Playful;
        }
        if (b.Rng.NextDouble() < 0.5) b.EmitHead(EffectKind.Exclaim, 8f, -42f, 0, -14f, 0.9f, 0.9f);
    }

    void Go(Phase phase)
    {
        _phase = phase;
        _phaseAt = T;
        _contact = false;
    }

    void StartChase(PetBrain b)
    {
        _stalkNext = b.Rng.NextDouble() < 0.35;
        Go(Phase.Chase);
    }

    /// <summary>Springs up to meet the ball if it will be within reach at the top of a jump.</summary>
    bool TryJump(PetBrain b, YarnBall ball)
    {
        var p = ball.PredictAt(JumpTime * 0.5f);
        if ((p.X >= b.Position.X ? 1 : -1) != b.Facing || ball.Velocity.Y < -250f * b.Scale) return false;
        float height = (ball.Floor.Y - p.Y) / b.Scale;
        float ahead = (p.X - (b.Position.X + b.Facing * MouthReach * b.Scale)) / b.Scale * b.Facing;
        if (height is < 30f or > 105f || ahead is < -12f or > 55f) return false;
        _jumpHeight = Math.Clamp(height - 44f, 4f, 62f);
        _jumpNudge = Math.Clamp(ahead, -6f, 50f);
        _willCatch = b.Rng.NextDouble() < 0.8;
        _caught = false;
        Go(Phase.Jump);
        return true;
    }

    public override void Tick(PetBrain b, PetPose t, float dt)
    {
        if (b.Ball is not { } ball)
        {
            Done = true;
            return;
        }
        float s = T - _phaseAt;
        float ahead = (ball.Position.X - b.Position.X) / b.Scale; // model units, screen direction
        float gap = MathF.Abs(ahead);
        int dir = ahead >= 0 ? 1 : -1;
        t.PupilSize = 1f;
        t.EarLift = 0.6f;
        t.Whisker = 0.7f;

        if (ball.State == BallState.HeldByUser)
        {
            // You've picked it up: sitting bolt upright, riveted, tail swishing.
            if (dir != b.Facing && gap > 10f) b.Turn(dir);
            t.Sit = 1f;
            t.EarLift = 0.85f;
            t.TailSway = 14f;
            t.TailSwaySpeed = 2.2f;
            _fetchMode = true;
            Go(Phase.Watch);
            return;
        }

        switch (_phase)
        {
            case Phase.Watch:
                // Crouched and riveted while it flies; off the moment it comes down.
                if (dir != b.Facing && gap > 10f) b.Turn(dir);
                t.Crouch = 0.6f;
                t.EarLift = 0.75f;
                t.Whisker = 0.9f;
                t.TailSway = 10f;
                t.TailSwaySpeed = 3f;
                t.Wiggle = 2.5f * MathF.Sin(T * 16f) * Ramp(s, 0.3f, 0.3f);
                if ((_fetchMode && s > 0.25f) || ball.OnFloor || (ball.Velocity.Y > 0 && ball.Position.Y > ball.Floor.Y - 70f * b.Scale) || s > 2.5f)
                    StartChase(b);
                break;

            case Phase.Chase:
            {
                bool airborne = !ball.OnFloor;
                if (airborne && _fetchMode && TryJump(b, ball)) break;
                bool rolling = airborne || MathF.Abs(ball.Velocity.X) / b.Scale > 150f;
                if (!_fetchMode && _stalkNext && ball.Resting && dir == b.Facing && gap is > 85f and < 150f)
                {
                    Go(Phase.Stalk);
                    break;
                }
                // Head for where it will come down (or where it is now).
                float aim = airborne ? ball.PredictLandingX() : ball.Position.X;
                int aimDir = aim >= b.Position.X ? 1 : -1;
                float stand = airborne || (_bats >= _rounds && _fetch) ? MouthReach : Reach;
                bool far = MathF.Abs(aim - b.Position.X) / b.Scale - stand > 140f || rolling;
                bool arrived = b.MoveToward(aim - aimDir * stand * b.Scale, far ? 175f : 85f, dt, t, far ? 2f : 1f);
                t.TailRaise = 0.85f;
                if (airborne)
                {
                    if (arrived)
                    {
                        // Waiting underneath, eyes up, ready to spring.
                        if (dir != b.Facing) b.Turn(dir);
                        t.Crouch = 0.5f;
                        t.Wiggle = 2f * MathF.Sin(T * 16f);
                    }
                }
                else if ((arrived || MathF.Abs(gap - stand) < 6f) && !rolling)
                {
                    if (dir != b.Facing) b.Turn(dir);
                    if (_bats >= _rounds) Go(_fetch ? Phase.PickUp : Phase.Bored);
                    else Go(Phase.Bat);
                }
                if (s > 12f) Go(Phase.Bored); // it's somewhere Mochi can't get to
                break;
            }

            case Phase.Jump:
                // A springing leap to snatch it out of the air.
                t.Leap = Pulse(s, 0f, JumpTime);
                t.Hop = Hop(s, 0f, JumpTime, _jumpHeight);
                t.MouthOpen = _caught ? 0f : 0.45f * Pulse(s, 0f, JumpTime);
                t.EarLift = 0.7f;
                t.Squash = LandSquash(s, JumpTime, 0.45f);
                if (s < JumpTime * 0.5f) b.Nudge(_jumpNudge * dt / (JumpTime * 0.5f));
                if (!_contact && ball.State == BallState.Free && s < JumpTime && b.MouthDistanceToBall() < YarnBall.Radius + 10f)
                {
                    _contact = true;
                    if (_willCatch)
                    {
                        b.CatchBall();
                        _caught = true;
                    }
                    else
                    {
                        // Bonk! Off the nose it goes.
                        b.HitBall(new SKPoint(-b.Facing * (60f + 80f * (float)b.Rng.NextDouble()), -(200f + 120f * (float)b.Rng.NextDouble())));
                        b.EmitHead(EffectKind.Question, 10f, -40f, 0, -12f, 0.9f, 1.1f);
                    }
                }
                if (s > JumpTime + 0.3f)
                {
                    if (_caught)
                    {
                        _carryTo = b.CarryTarget();
                        Go(Phase.Carry);
                    }
                    else StartChase(b);
                }
                break;

            case Phase.Bat:
                // A quick paw swat; the ball pops away at the moment of contact.
                if (dir != b.Facing && gap > 6f) b.Turn(dir);
                t.Crouch = 0.35f;
                t.FrontPawRaise = Pulse(s, 0.04f, 0.32f);
                t.EarLift = 0.7f;
                t.Whisker = 1f;
                if (!_contact && s > 0.26f)
                {
                    _contact = true;
                    if (MathF.Abs(gap - Reach) < 28f)
                    {
                        float power = (float)b.Rng.NextDouble();
                        b.HitBall(new SKPoint(b.Facing * (220f + 300f * power), -(100f + 320f * (float)b.Rng.NextDouble())));
                        _bats++;
                        if (b.Rng.NextDouble() < 0.2) b.Mew();
                    }
                }
                if (s > 0.5f) StartChase(b);
                break;

            case Phase.Stalk:
                // Low and still, bottom wiggling... then pounce.
                if (!ball.Resting)
                {
                    StartChase(b);
                    break;
                }
                t.Crouch = 1f;
                t.EarLift = 0.8f;
                t.Whisker = 1f;
                t.TailSway = 12f;
                t.TailSwaySpeed = 2.4f;
                t.Wiggle = 3f * MathF.Sin(s * 18f) * Ramp(s, 0.5f, 0.2f);
                if (s > 1.1f)
                {
                    _pounceDist = MathF.Max(0f, gap - 36f);
                    Go(Phase.Pounce);
                }
                break;

            case Phase.Pounce:
                // A springy leap that lands with both front paws on the ball.
                t.Leap = Pulse(s, 0f, LeapTime);
                t.Hop = Hop(s, 0f, LeapTime, 15f + _pounceDist * 0.12f);
                t.MouthOpen = 0.25f * Pulse(s, 0f, LeapTime);
                t.Squash = LandSquash(s, LeapTime, 0.4f);
                if (s < LeapTime) b.Nudge(_pounceDist * dt / LeapTime);
                if (!_contact && s >= LeapTime)
                {
                    _contact = true;
                    b.HitBall(default); // trapped!
                    _bats++;
                    b.EmitFeet(2);
                }
                if (s > LeapTime + 0.35f) Go(Phase.Bat);
                break;

            case Phase.PickUp:
                // Head right down to the floor, and up it comes, held gently in the mouth.
                if (dir != b.Facing && gap > 6f) b.Turn(dir);
                if (!_contact && MathF.Abs(gap - MouthReach) > 6f)
                    b.MoveToward(ball.Position.X - dir * MouthReach * b.Scale, 60f, dt, t, 1f);
                t.Crouch = 0.9f;
                t.HeadLift = -1f;
                t.HeadDy = 9f;
                t.HeadTurn = 0.8f;
                t.MouthOpen = 0.3f * (1f - Ramp(s, 0.45f, 0.1f));
                if (!_contact && s > 0.45f && MathF.Abs(gap - MouthReach) < 12f)
                {
                    _contact = true;
                    b.PickUpBall();
                    _carryTo = b.CarryTarget();
                }
                if (_contact && s > 0.75f) Go(Phase.Carry);
                else if (!_contact && s > 3f) Go(Phase.Bored);
                break;

            case Phase.Carry:
                // Trots back proudly with the prize, tail up like a flag.
                t.TailRaise = 1f;
                t.TailCurl = 0.75f;
                t.TailSway = 6f;
                t.Smile = 0.5f;
                t.HeadLift = 0.2f;
                t.HeadTurn = 0.45f;
                t.PupilSize = 0.8f;
                if (b.MoveToward(_carryTo, 95f, dt, t, 1f) || s > 10f) Go(Phase.Drop);
                break;

            case Phase.Drop:
                // Drops it right in front of you... and looks up, waiting for the next throw.
                t.Crouch = 0.6f * Pulse(s, 0f, 0.7f);
                t.HeadLift = -0.8f * Pulse(s, 0f, 0.7f);
                t.HeadDy = 6f * Pulse(s, 0f, 0.7f);
                t.Smile = 0.8f;
                if (!_contact && s > 0.32f)
                {
                    _contact = true;
                    b.DropBall();
                    if (_fetchMode && b.Rng.NextDouble() < 0.6) b.Say("Again!");
                    else b.Mew();
                }
                if (s > 0.8f)
                {
                    b.BallPlayEnded();
                    Next = new SitActivity(facing: true, pawsUp: _fetchMode || b.Rng.NextDouble() < 0.5);
                    Done = true;
                }
                break;

            case Phase.Bored:
                b.BallPlayEnded();
                Next = b.Rng.NextDouble() < 0.5 ? new TrickActivity(Trick.Groom) : new SitActivity();
                Done = true;
                break;
        }
    }

    public override void Exit(PetBrain b) => b.DropBall(); // interrupted mid-carry: it falls from the mouth
}

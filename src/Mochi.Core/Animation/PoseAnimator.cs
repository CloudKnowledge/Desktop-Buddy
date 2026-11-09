using Mochi.Core.Rig;
using SkiaSharp;

namespace Mochi.Core.Animation;

/// <summary>
/// Turns the behaviour's target pose into the pose that is drawn: eases every parameter, then layers
/// procedural life on top — breathing, blinking, tail sway, purring, paw paddling and ear flicks.
/// </summary>
public sealed class PoseAnimator
{
    readonly Random _rng;
    readonly PetPose _smoothed = new();
    float _breathPhase, _blinkTimer = 2.5f, _blinkClock = -1f, _purrPhase;
    bool _queuedSecondBlink, _suppressDouble;
    Spring _earL = new(620f, 22f), _earR = new(560f, 20f);
    SKPoint _prevHead;
    SKPoint _prevHeadVel;
    bool _hasPrev;

    /// <summary>The pose to render this frame.</summary>
    public PetPose Output { get; } = new();

    /// <summary>Breaths per second; the behaviour slows it for sleep and speeds it up for panting.</summary>
    public float BreathRate { get; set; } = 0.42f;
    public float BreathDepth { get; set; } = 1f;
    public bool AllowBlink { get; set; } = true;
    /// <summary>Multiplier on the gap between blinks (sleepy pups blink slowly).</summary>
    public float BlinkGap { get; set; } = 1f;

    public PoseAnimator(int seed = 1)
    {
        _rng = new Random(seed);
        _smoothed.ResetToNeutral();
        Output.CopyFrom(_smoothed);
    }

    /// <summary>Jump straight to a pose (used on start-up so Mochi does not "grow" into place).</summary>
    public void Snap(PetPose pose)
    {
        _smoothed.CopyFrom(pose);
        Output.CopyFrom(pose);
        _hasPrev = false;
    }

    /// <param name="gaitAdvance">radians to advance the gait cycle (distance travelled / stride).</param>
    /// <param name="externalAccel">acceleration of the whole pet in model units/s² (dragging, landing) to excite the ears.</param>
    public void Update(PetPose target, float dt, float gaitAdvance, SKPoint externalAccel)
    {
        if (dt <= 0) return;
        _smoothed.Approach(target, dt);
        _smoothed.GaitPhase = Wrap(_smoothed.GaitPhase + gaitAdvance);
        _smoothed.TailSwayPhase = Wrap(_smoothed.TailSwayPhase + MathF.Tau * _smoothed.TailSwaySpeed * dt);
        _smoothed.PaddlePhase = Wrap(_smoothed.PaddlePhase + MathF.Tau * 3.4f * dt);
        _smoothed.LightsPhase = Wrap(_smoothed.LightsPhase + MathF.Tau * 0.35f * dt);
        _breathPhase = Wrap(_breathPhase + MathF.Tau * BreathRate * dt);
        _purrPhase = Wrap(_purrPhase + MathF.Tau * 23f * dt);

        Output.CopyFrom(_smoothed);
        Output.Breath = MathF.Sin(_breathPhase) * BreathDepth;
        Output.EyeOpen *= BlinkFactor(dt, target);
        // A purr is a tiny, fast whole-body rumble.
        Output.Wiggle += 0.45f * Output.Purr * MathF.Sin(_purrPhase);
        Output.HeadDy += 0.25f * Output.Purr * MathF.Sin(_purrPhase * 1.3f);

        // Floppy ears lag behind head motion.
        var skeleton = Skeleton.Compute(Output);
        var head = skeleton.Head;
        SKPoint accel = externalAccel;
        if (_hasPrev)
        {
            var vel = new SKPoint((head.X - _prevHead.X) / dt, (head.Y - _prevHead.Y) / dt);
            accel += new SKPoint((vel.X - _prevHeadVel.X) / dt, (vel.Y - _prevHeadVel.Y) / dt);
            _prevHeadVel = vel;
        }
        _prevHead = head;
        _hasPrev = true;
        accel = new SKPoint(Math.Clamp(accel.X, -9000f, 9000f), Math.Clamp(accel.Y, -9000f, 9000f));

        // Cat ears are springy, not floppy: only a small flick on big jolts.
        const float gain = 0.00015f;
        _earL.Step(dt, (accel.Y + accel.X) * gain);
        _earR.Step(dt, (accel.Y - accel.X) * gain);
        Output.EarFlapL = target.EarFlapL + Math.Clamp(_earL.Value * 57.3f, -14f, 14f);
        Output.EarFlapR = target.EarFlapR + Math.Clamp(_earR.Value * 57.3f, -14f, 14f);
    }

    float BlinkFactor(float dt, PetPose target)
    {
        if (_blinkClock >= 0)
        {
            _blinkClock += dt;
            const float duration = 0.16f;
            if (_blinkClock >= duration)
            {
                _blinkClock = -1f;
                if (_queuedSecondBlink)
                {
                    _queuedSecondBlink = false;
                    _suppressDouble = true;
                    _blinkTimer = 0.1f;
                }
                return 1f;
            }
            return 1f - MathF.Sin(MathF.PI * _blinkClock / duration);
        }

        if (!AllowBlink || target.EyeOpen < 0.3f || target.HappyEyes > 0.6f) return 1f;
        _blinkTimer -= dt;
        if (_blinkTimer <= 0)
        {
            _blinkClock = 0f;
            _queuedSecondBlink = !_suppressDouble && _rng.NextDouble() < 0.18;
            _suppressDouble = false;
            _blinkTimer = (2.2f + (float)_rng.NextDouble() * 3.8f) * BlinkGap;
        }
        return 1f;
    }

    static float Wrap(float a) => a % MathF.Tau;

    /// <summary>Damped spring used for secondary motion (radians).</summary>
    struct Spring(float stiffness, float damping)
    {
        public float Value, Velocity;
        public void Step(float dt, float drive)
        {
            // Semi-implicit Euler is stable at the frame rates we run at; sub-step long frames.
            int steps = Math.Max(1, (int)MathF.Ceiling(dt / 0.008f));
            float h = dt / steps;
            for (int i = 0; i < steps; i++)
            {
                Velocity += (-stiffness * Value - damping * Velocity + drive * stiffness) * h;
                Value += Velocity * h;
            }
        }
    }
}

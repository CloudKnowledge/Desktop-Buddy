using Mochi.Core.Behaviour;
using Mochi.Core.Interaction;
using SkiaSharp;
using static Mochi.Core.Tests.Sim;

namespace Mochi.Core.Tests;

public class BrainTests
{
    static bool Asleep(PetBrain b) => b.State is PetState.Sleeping or PetState.Dreaming;

    [Fact]
    public void Gets_drowsy_then_sleeps_when_user_is_away_and_wakes_gradually_on_return()
    {
        var b = NewBrain();
        double drowsyAt = RunUntil(b, 200, t => S(idle: t), x => x.State == PetState.Drowsy);
        Assert.InRange(drowsyAt, 90, 150);

        double asleepAt = RunUntil(b, 200, t => S(idle: drowsyAt + t), Asleep);
        Assert.True(asleepAt >= 0, "should fall asleep once the sleep threshold passes");
        Assert.True(drowsyAt + asleepAt >= 240 - 0.1);

        Run(b, 0.2, _ => S(idle: 0.1));
        Assert.Equal(PetState.Stretching, b.State); // wakes gradually, not instantly

        double awakeAt = RunUntil(b, 15, _ => S(idle: 0.1), x => x.State != PetState.Stretching);
        Assert.True(awakeAt > 1.0, "waking up should take a moment (flutter, yawn, stretch)");
        Assert.False(b.IsAsleep);
    }

    [Fact]
    public void Normal_typing_pauses_never_count_as_being_away()
    {
        var b = NewBrain();
        bool everDrowsy = false;
        // Ten minutes of work with pauses up to 75 s between bursts of typing.
            // (A lazy kitten may still take a voluntary nap; that is personality, not away-detection.)
        for (double t = 0; t < 600; t += 1 / 30.0)
        {
            b.Update(1 / 30f, S(idle: t % 75));
            everDrowsy |= b.State is PetState.Drowsy;
        }
        Assert.False(everDrowsy);
    }

    [Fact]
    public void Auto_sleep_can_be_disabled()
    {
        var b = NewBrain(o => o.AutoSleep = false, PersonalityProfile.Lazy);
        bool everAsleep = false;
        for (double t = 0; t < 1800; t += 1 / 20.0)
        {
            b.Update(1 / 20f, S(idle: t));
            everAsleep |= b.State is PetState.Drowsy or PetState.Sleeping or PetState.Dreaming;
        }
        Assert.False(everAsleep);
    }

    [Fact]
    public void Requested_sleep_ignores_user_activity_but_a_click_wakes_mochi()
    {
        var b = NewBrain();
        b.CommandSleep();
        Run(b, 5, _ => S(idle: 0.05));
        Assert.True(Asleep(b));

        b.Handle(G(GestureKind.Click, b.HeadWorld, streak: 1));
        Run(b, 0.1, _ => S(idle: 0.05));
        Assert.Equal(PetState.Stretching, b.State);
    }

    [Fact]
    public void Single_click_makes_mochi_happy_and_rapid_clicks_escalate_to_a_celebration()
    {
        var b = NewBrain();
        Run(b, 1, _ => S(idle: 0.5));
        b.Handle(G(GestureKind.Click, b.HeadWorld, streak: 1));
        Run(b, 0.05, _ => S(idle: 0.05));
        Assert.Equal(PetState.Happy, b.State);

        for (int i = 2; i <= 10; i++)
        {
            b.Handle(G(i % 2 == 0 ? GestureKind.DoubleClick : GestureKind.Click, b.HeadWorld, streak: i));
            Run(b, 0.15, _ => S(idle: 0.05));
            if (i is >= 3 and < 10) Assert.Equal(PetState.Excited, b.State);
        }
        Assert.Equal(PetState.Celebrating, b.State);
        Assert.Equal(10, b.Memory.TimesClicked);
    }

    [Fact]
    public void Gentle_strokes_on_the_head_are_petting_and_stopping_ends_it()
    {
        var b = NewBrain();
        Run(b, 1, _ => S(idle: 0.5));
        for (int i = 0; i < 10; i++)
        {
            b.Handle(G(GestureKind.PetStroke, b.HeadWorld));
            Run(b, 0.3, _ => S(idle: 0.05));
            Assert.Equal(PetState.BeingPetted, b.State);
        }
        Assert.Equal(1, b.Memory.TimesPetted);
        Assert.True(b.Personality.Drift[(int)Personality.Trait.Affectionate] > 0);

        double ended = RunUntil(b, 3, _ => S(idle: 0.05), x => x.State != PetState.BeingPetted);
        Assert.InRange(ended, 0.3, 1.5);
    }

    [Fact]
    public void Body_strokes_do_not_count_as_petting_while_standing()
    {
        var b = NewBrain();
        Run(b, 1, _ => S(idle: 0.5));
        b.Handle(G(GestureKind.PetStroke, b.Position, region: HitRegion.Body));
        Run(b, 0.1, _ => S(idle: 0.05));
        Assert.NotEqual(PetState.BeingPetted, b.State);
    }

    [Fact]
    public void Can_be_picked_up_carried_thrown_and_lands_on_the_floor()
    {
        var env = new FakeEnv();
        var b = NewBrain(env: env);
        Run(b, 1, _ => S(idle: 0.5));
        var start = b.Position;

        b.Handle(G(GestureKind.GrabStart, new SKPoint(start.X, start.Y - 80)));
        Run(b, 0.05, _ => S(idle: 0));
        Assert.True(b.IsHeld);

        for (int i = 0; i < 40; i++)
        {
            b.Handle(G(GestureKind.DragMove, new SKPoint(start.X - i * 12, start.Y - 500)));
            Run(b, 1 / 60.0, _ => S(idle: 0));
        }
        Assert.True(b.Position.Y < env.Floor.Y - 300, "Mochi should be up in the air while carried");
        Assert.True(MathF.Abs(b.Animator.Output.SwingAngle) > 1f, "the body should swing like a pendulum when the hand moves");

        b.Handle(G(GestureKind.Release, b.Position, velocity: new SKPoint(-2600, -900)));
        Run(b, 0.02, _ => S(idle: 0));
        Assert.True(b.Airborne);

        double landed = RunUntil(b, 10, _ => S(idle: 0), x => !x.Airborne);
        Assert.True(landed > 0, "should land");
        Assert.Equal(env.Floor.Y, b.Position.Y, 0.01);
        Assert.InRange(b.Position.X, b.MinX, b.MaxX);
        Assert.Equal(1, b.Memory.TimesCarried);
    }

    [Fact]
    public void Follow_mode_walks_to_the_cursor_and_sits_nearby()
    {
        var b = NewBrain();
        b.SetFollowCursor(true);
        Run(b, 20, _ => S(idle: 0.2, cx: 300, cy: 900));
        Assert.Equal(PetState.FollowingCursor, b.State);
        Assert.InRange(MathF.Abs(b.Position.X - 300) / b.Scale, 0, 110);
    }

    [Theory]
    [InlineData(PersonalityProfile.Playful, 1)]
    [InlineData(PersonalityProfile.Lazy, 2)]
    [InlineData(PersonalityProfile.Curious, 3)]
    [InlineData(PersonalityProfile.Shy, 4)]
    [InlineData(PersonalityProfile.Affectionate, 5)]
    [InlineData(PersonalityProfile.Mischievous, 6)]
    public void Long_autonomous_life_stays_on_screen_with_sane_poses(PersonalityProfile profile, int seed)
    {
        var env = new FakeEnv();
        var b = NewBrain(o => o.Liveliness = 1f, profile, seed, env);
        var rng = new Random(seed);
        float cx = 800, cy = 600;
        var states = new HashSet<PetState>();
        for (double t = 0; t < 1200; t += 1 / 30.0)
        {
            if (rng.NextDouble() < 0.02) { cx = rng.Next(0, 1920); cy = rng.Next(0, 1200); }
            double idle = (t % 400) < 300 ? (t % 7) : (t % 400) - 300 + 7; // active stretches, then away for a bit
            b.Update(1 / 30f, S(idle, cx, cy));
            if (rng.NextDouble() < 0.002) b.Handle(G(GestureKind.Click, b.HeadWorld, streak: 1));

            Assert.InRange(b.Position.X, b.MinX - 0.5f, b.MaxX + 0.5f);
            if (!b.Airborne && !b.IsHeld) Assert.Equal(env.Floor.Y, b.Position.Y, 0.01);
            var p = b.Animator.Output;
            foreach (var v in new[] { p.Sit, p.Lie, p.Hop, p.HeadTurn, p.EyeOpen, p.GaitPhase, p.TailSwayPhase, p.EarFlapL, p.Squash, p.Purr, p.Arch, p.Crouch, p.Leap })
                Assert.True(float.IsFinite(v));
            Assert.InRange(p.EyeOpen, -0.01f, 1.01f);
            states.Add(b.State);
        }
        Assert.True(states.Count >= 5, $"expected a varied life, saw only: {string.Join(", ", states)}");
    }

    [Fact]
    public void A_pointer_wiggling_nearby_gets_stalked_and_pounced_on()
    {
        var b = NewBrain();
        double huntAt = RunUntil(b, 90, t =>
        {
            var head = b.HeadWorld;
            return S(idle: 0.05, head.X + 90 + 60 * MathF.Sin((float)t * 12f), head.Y + 10);
        }, x => x.State == PetState.Hunting);
        Assert.True(huntAt >= 0, "a wiggling pointer should trigger the prey instinct");
    }

    [Fact]
    public void Greets_after_a_long_absence()
    {
        var b = NewBrain(o => o.AutoSleep = false);
        Run(b, 1, _ => S(idle: 0.5));
        bool greeted = false;
        double away = 30 * 60;
        for (double t = 0; t < away + 20; t += 1 / 20.0)
        {
            double idle = t < away ? t : 0.1;
            b.Update(1 / 20f, S(idle));
            greeted |= t >= away && b.State == PetState.Greeting;
        }
        Assert.True(greeted);
    }
}

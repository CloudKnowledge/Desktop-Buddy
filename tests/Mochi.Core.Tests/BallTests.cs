using Mochi.Core.Behaviour;
using Mochi.Core.Play;
using SkiaSharp;
using static Mochi.Core.Tests.Sim;

namespace Mochi.Core.Tests;

public class BallTests
{
    static bool Playing(PetBrain b) => b.State is PetState.ChasingBall or PetState.CarryingBall or PetState.Playful;

    [Fact]
    public void Yarn_ball_bounces_rolls_and_comes_to_rest_on_the_floor_without_leaving_the_screen()
    {
        var env = new FakeEnv();
        var ball = new YarnBall(new SKPoint(500, 300), 1.25f, env) { Velocity = new SKPoint(1400, -900) };
        for (int i = 0; i < 60 * 20; i++)
        {
            ball.Step(1 / 60f, env);
            Assert.InRange(ball.Position.X, env.Floor.Left + ball.R - 0.01f, env.Floor.Right - ball.R + 0.01f);
            Assert.True(ball.Position.Y <= env.Floor.Y - ball.R + 0.01f, "ball sank into the floor");
            Assert.All(ball.Strand, p => Assert.True(p.Y <= env.Floor.Y + 0.01f, "strand sank into the floor"));
        }
        Assert.True(ball.Resting);
        Assert.Equal(env.Floor.Y - ball.R, ball.Position.Y, 0.01);
    }

    [Fact]
    public void Yarn_ball_bounces_back_off_the_edge_of_the_screen()
    {
        var env = new FakeEnv();
        var ball = new YarnBall(new SKPoint(1850, 1120), 1.25f, env) { Velocity = new SKPoint(2000, 0) };
        bool reversed = false;
        for (int i = 0; i < 60 && !reversed; i++)
        {
            ball.Step(1 / 60f, env);
            reversed = ball.Velocity.X < 0;
        }
        Assert.True(reversed);
    }

    [Fact]
    public void Mochi_runs_under_a_thrown_ball_and_catches_it_in_mid_air()
    {
        var b = NewBrain();
        var states = new HashSet<PetState>();
        for (int round = 0; round < 8 && b.BallCatches == 0; round++)
        {
            b.ThrowBall();
            for (double t = 0; t < 25 && Playing(b) | t < 0.5; t += 1 / 60.0)
            {
                b.Update(1 / 60f, S(idle: 0.5));
                states.Add(b.State);
                Assert.InRange(b.Position.X, b.MinX - 0.5f, b.MaxX + 0.5f);
            }
        }
        Assert.Contains(PetState.ChasingBall, states);
        Assert.Contains(PetState.CarryingBall, states);
        Assert.True(b.BallCatches >= 1, "Mochi should snatch a thrown ball out of the air now and then");
    }

    [Fact]
    public void A_ball_left_lying_about_gets_batted_around()
    {
        var b = NewBrain();
        b.ThrowBall();
        int hitsAfterFetch = -1;
        for (double t = 0; t < 600 && (hitsAfterFetch < 0 || b.BallHits <= hitsAfterFetch); t += 1 / 30.0)
        {
            b.Update(1 / 30f, S(idle: 0.5));
            if (hitsAfterFetch < 0 && t > 20 && !Playing(b)) hitsAfterFetch = b.BallHits;
        }
        Assert.True(hitsAfterFetch >= 0 && b.BallHits > hitsAfterFetch, "a playful kitten should bat a ball that's lying about");
    }

    [Fact]
    public void A_ball_you_throw_flies_and_Mochi_goes_after_it()
    {
        var b = NewBrain();
        b.BringOutBall();
        Run(b, 1, _ => S(idle: 0.5));
        var ball = b.Ball!;
        b.GrabBall(ball.Position);
        b.DragBall(new SKPoint(600, 700));
        Assert.Equal(new SKPoint(600, 700), ball.Position);
        Assert.Equal(BallState.HeldByUser, ball.State);

        b.ReleaseBall(new SKPoint(-1500, -800));
        Run(b, 0.2, _ => S(idle: 0.5));
        Assert.True(ball.Position.X < 600, "the ball should fly in the direction it was thrown");
        Assert.True(RunUntil(b, 5, _ => S(idle: 0.5), Playing) >= 0, "Mochi should go after a thrown ball");
    }

    [Fact]
    public void Sometimes_Mochi_brings_the_ball_back_and_drops_it_near_you()
    {
        var b = NewBrain(profile: PersonalityProfile.Affectionate, seed: 7);
        var cursor = new SKPoint(700, 1000);
        bool carried = false;
        for (int round = 0; round < 8 && !carried; round++)
        {
            b.ThrowBall();
            for (double t = 0; t < 30 && !carried; t += 1 / 60.0)
            {
                b.Update(1 / 60f, S(idle: 0.5, cursor.X, cursor.Y));
                carried = b.State == PetState.CarryingBall && b.Ball!.State == BallState.InMouth;
            }
        }
        Assert.True(carried, "an affectionate kitten should fetch now and then");

        double dropped = RunUntil(b, 20, _ => S(idle: 0.5, cursor.X, cursor.Y), x => x.Ball!.State == BallState.Free && x.State != PetState.CarryingBall);
        Assert.True(dropped >= 0);
        Run(b, 2, _ => S(idle: 0.5, cursor.X, cursor.Y));
        Assert.InRange(b.Ball!.Position.X, cursor.X - 220, cursor.X + 220);
    }

    [Fact]
    public void Putting_the_ball_away_ends_the_game()
    {
        var b = NewBrain();
        b.BringOutBall();
        Assert.True(RunUntil(b, 5, _ => S(idle: 0.5), Playing) >= 0);
        b.RemoveBall();
        Assert.Null(b.Ball);
        Run(b, 1, _ => S(idle: 0.5));
        Assert.False(Playing(b));
    }

    [Fact]
    public void Bringing_out_the_ball_wakes_a_sleeping_kitten_to_play()
    {
        var b = NewBrain();
        b.CommandSleep();
        Run(b, 3, _ => S(idle: 600));
        Assert.True(b.IsAsleep);
        b.BringOutBall();
        Assert.True(RunUntil(b, 3, _ => S(idle: 0.5), Playing) >= 0);
        Assert.False(b.IsAsleep);
    }
}

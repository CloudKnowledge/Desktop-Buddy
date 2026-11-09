using Mochi.Core.Behaviour;
using Mochi.Core.Play;
using static Mochi.Core.Tests.Sim;

namespace Mochi.Core.Tests;

public class LifeTests
{
    [Fact]
    public void Says_hi_with_a_wave_when_it_starts_and_again_when_the_laptop_wakes()
    {
        var b = NewBrain();
        b.Start(Afternoon, DateTime.UtcNow.AddHours(-1));
        bool waved = false;
        for (double t = 0; t < 3; t += 1 / 60.0)
        {
            b.Update(1 / 60f, S(idle: 0.5));
            waved |= b.Animator.Output.Wave > 0.5f;
        }
        Assert.Equal("Hi!", b.Effects.LastSaid);
        Assert.True(waved, "Mochi should wave a paw while saying hi");

        b.CommandSleep();
        Run(b, 3, _ => S(idle: 600));
        Assert.True(b.IsAsleep);
        b.SayHi(); // the laptop wakes up
        Assert.Equal(PetState.Greeting, b.State);
        Assert.False(b.IsAsleep);
    }

    [Theory]
    [InlineData(PropKind.Milk)]
    [InlineData(PropKind.Treats)]
    public void Feeding_puts_down_a_dish_that_gets_finished(PropKind food)
    {
        var b = NewBrain();
        b.CommandFeed(food);
        Assert.Equal(PetState.Eating, b.State);
        Assert.Equal(food, b.Prop!.Kind);
        Run(b, 5, _ => S(idle: 0.5));
        Assert.True(b.Prop!.Fill < 0.8f, "Mochi should be eating it");
        Assert.True(RunUntil(b, 20, _ => S(idle: 0.5), x => x.State != PetState.Eating) >= 0);
        Assert.Null(b.Prop);
        Assert.Equal("Yum!", b.Effects.LastSaid);
    }

    [Fact]
    public void Cat_tv_puts_on_a_show_that_Mochi_follows_with_its_eyes()
    {
        var b = NewBrain();
        b.CommandWatchTv();
        Assert.Equal(PetState.WatchingTv, b.State);
        Assert.Equal(PropKind.Tv, b.Prop!.Kind);
        var looks = new List<float>();
        for (double t = 0; t < 6; t += 1 / 30.0)
        {
            b.Update(1 / 30f, S(idle: 0.5));
            looks.Add(b.Animator.Output.LookX);
        }
        Assert.True(looks.Max() - looks.Min() > 0.1f, "eyes should follow the show");
        Assert.True(RunUntil(b, 70, _ => S(idle: 0.5), x => x.State != PetState.WatchingTv) >= 0);
        Assert.Null(b.Prop);
    }

    [Fact]
    public void At_sign_in_the_hello_waits_until_you_are_at_the_computer()
    {
        var b = NewBrain();
        b.Start(Afternoon, DateTime.UtcNow.AddHours(-1), greetNow: false);
        Run(b, 3, _ => S(idle: 30));
        Assert.True(b.GreetPending);
        Assert.NotEqual(PetState.Greeting, b.State);
        Assert.Null(b.Effects.LastSaid);

        b.GreetNow();
        Run(b, 1, _ => S(idle: 0.2));
        Assert.False(b.GreetPending);
        Assert.Equal(PetState.Greeting, b.State);
        Assert.Equal("Hi!", b.Effects.LastSaid);
    }

    [Fact]
    public void Work_mode_opens_a_laptop_puts_on_glasses_and_types()
    {
        var b = NewBrain();
        b.CommandWork();
        Assert.Equal(PetState.Working, b.State);
        Assert.Equal(PropKind.Laptop, b.Prop!.Kind);
        float maxKnead = 0;
        for (double t = 0; t < 4; t += 1 / 30.0)
        {
            b.Update(1 / 30f, S(idle: 0.5));
            maxKnead = MathF.Max(maxKnead, b.Animator.Output.Knead);
        }
        var o = b.Animator.Output;
        Assert.True(o.Glasses > 0.9f, "glasses on");
        Assert.True(o.Front > 0.9f && o.Sit > 0.9f, "sitting square-on behind the laptop");
        Assert.True(b.Prop!.Fill > 0.9f, "lid open");
        Assert.True(maxKnead > 0.5f, "paws pattering on the keys");

        Assert.True(RunUntil(b, 70, _ => S(idle: 0.5), x => x.State != PetState.Working) >= 0);
        Run(b, 1, _ => S(idle: 0.5));
        Assert.True(b.Animator.Output.Glasses < 0.2f, "glasses off after work");
    }

    [Fact]
    public void Snacks_tv_and_naps_happen_on_their_own_over_a_long_afternoon()
    {
        var b = NewBrain(o => o.Liveliness = 0.5f, PersonalityProfile.Lazy, seed: 3);
        var seen = new HashSet<PetState>();
        for (double t = 0; t < 3 * 3600 && !(seen.Contains(PetState.Eating) && seen.Contains(PetState.WatchingTv) && seen.Contains(PetState.Working)); t += 1 / 10.0)
        {
            b.Update(1 / 10f, S(idle: t % 60)); // working away: never idle long enough to doze off
            seen.Add(b.State);
        }
        Assert.Contains(PetState.Eating, seen);
        Assert.Contains(PetState.WatchingTv, seen);
        Assert.Contains(PetState.Working, seen);
    }

    [Fact]
    public void Chasing_Mochi_with_the_pointer_makes_it_run_off_playfully()
    {
        var b = NewBrain(o => o.Liveliness = 1f);
        Run(b, 6, _ => S(idle: 0.5));
        bool tag = false;
        float startX = 0, cursorX = 0;
        // Sweep the pointer along the floor towards Mochi a few times, like chasing it.
        for (int sweep = 0; sweep < 6 && !tag; sweep++)
        {
            startX = b.Position.X;
            cursorX = b.Position.X - 420f;
            for (double t = 0; t < 1.5 && !tag; t += 1 / 60.0)
            {
                cursorX = MathF.Min(cursorX + 650f / 60f, b.Position.X - 50f);
                b.Update(1 / 60f, S(idle: 0.1, cursorX, b.Position.Y - 30f));
                tag = b.Activity.GetType().Name == "TagActivity";
            }
            if (!tag) Run(b, 7, _ => S(idle: 0.5));
        }
        Assert.True(tag, "a pointer chasing Mochi along the floor should start a game of chase");

        Run(b, 2, _ => S(idle: 0.5, cursorX, b.Position.Y - 30f));
        Assert.True(b.Position.X > startX + 100f, "Mochi should run away from the pointer");
    }
}

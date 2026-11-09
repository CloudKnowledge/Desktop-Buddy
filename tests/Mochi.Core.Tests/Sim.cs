using Mochi.Core.Behaviour;
using Mochi.Core.Interaction;
using Mochi.Core.Persistence;
using SkiaSharp;

namespace Mochi.Core.Tests;

sealed class FakeEnv : IPetEnvironment
{
    public Floor Floor { get; set; } = new(0, 1920, 0, 1139, 1.25f, "test");
    public Floor FloorAt(SKPoint p) => Floor;
}

static class Sim
{
    public static readonly DateTime Afternoon = new(2026, 11, 8, 14, 0, 0);

    public static PetBrain NewBrain(Action<BrainOptions>? configure = null, PersonalityProfile profile = PersonalityProfile.Playful, int seed = 42, FakeEnv? env = null)
    {
        var options = new BrainOptions();
        configure?.Invoke(options);
        var brain = new PetBrain(options, new Personality(profile), new PetMemory(), env ?? new FakeEnv(), seed);
        brain.Place(new SKPoint(960, 1139), 1);
        return brain;
    }

    public static Senses S(double idle, float cx = 300, float cy = 300, DateTime? time = null) =>
        new() { Cursor = new SKPoint(cx, cy), CursorKnown = true, IdleSeconds = idle, LocalTime = time ?? Afternoon };

    public static void Run(PetBrain b, double seconds, Func<double, Senses> senses, float dt = 1f / 60f)
    {
        for (double t = 0; t < seconds; t += dt) b.Update(dt, senses(t));
    }

    /// <summary>Runs until the predicate holds; returns the elapsed time or -1.</summary>
    public static double RunUntil(PetBrain b, double maxSeconds, Func<double, Senses> senses, Func<PetBrain, bool> until, float dt = 1f / 60f)
    {
        for (double t = 0; t < maxSeconds; t += dt)
        {
            b.Update(dt, senses(t));
            if (until(b)) return t;
        }
        return -1;
    }

    public static Gesture G(GestureKind kind, SKPoint at, int streak = 0, HitRegion region = HitRegion.Head, float strength = 0.6f, SKPoint velocity = default) =>
        new(kind, at, velocity, streak, region, strength);
}

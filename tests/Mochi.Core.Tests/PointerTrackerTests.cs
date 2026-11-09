using Mochi.Core.Behaviour;
using Mochi.Core.Interaction;
using SkiaSharp;

namespace Mochi.Core.Tests;

public class PointerTrackerTests
{
    static readonly SKPoint P = new(500, 500);

    [Fact]
    public void Press_and_release_in_place_is_a_click()
    {
        var t = new PointerTracker();
        t.Down(P, 0, HitRegion.Head);
        t.Up(P, 0.12);
        var g = Assert.Single(t.Drain());
        Assert.Equal(GestureKind.Click, g.Kind);
        Assert.Equal(1, g.Streak);
        Assert.Equal(HitRegion.Head, g.Region);
    }

    [Fact]
    public void Moving_while_pressed_picks_up_and_release_reports_throw_velocity()
    {
        var t = new PointerTracker();
        t.Down(P, 0, HitRegion.Body);
        for (int i = 1; i <= 10; i++) t.Move(new SKPoint(P.X + i * 20, P.Y), i * 0.016, HitRegion.Body, 1.25f);
        t.Up(new SKPoint(P.X + 200, P.Y), 0.17);
        var gestures = t.Drain();
        Assert.Equal(GestureKind.GrabStart, gestures[0].Kind);
        Assert.Contains(gestures, g => g.Kind == GestureKind.DragMove);
        var release = gestures[^1];
        Assert.Equal(GestureKind.Release, release.Kind);
        Assert.InRange(release.Velocity.X, 900, 1400); // 20 px per 16 ms ≈ 1250 px/s
    }

    [Fact]
    public void A_still_long_press_picks_mochi_up()
    {
        var t = new PointerTracker();
        t.Down(P, 0, HitRegion.Body);
        t.Tick(0.2);
        Assert.Empty(t.Drain());
        t.Tick(0.5);
        Assert.Equal(GestureKind.GrabStart, Assert.Single(t.Drain()).Kind);
    }

    [Fact]
    public void Rapid_clicks_pair_into_double_clicks_and_count_a_streak()
    {
        var t = new PointerTracker();
        var kinds = new List<GestureKind>();
        var streaks = new List<int>();
        for (int i = 0; i < 4; i++)
        {
            t.Down(P, i * 0.2, HitRegion.Head);
            t.Up(P, i * 0.2 + 0.05);
            var g = Assert.Single(t.Drain());
            kinds.Add(g.Kind);
            streaks.Add(g.Streak);
        }
        Assert.Equal([GestureKind.Click, GestureKind.DoubleClick, GestureKind.Click, GestureKind.DoubleClick], kinds);
        Assert.Equal([1, 2, 3, 4], streaks);
    }

    [Fact]
    public void Gentle_back_and_forth_over_the_head_is_petting_but_a_fast_swipe_is_not()
    {
        var t = new PointerTracker();
        double time = 0;
        t.Move(P, time, HitRegion.Head, 1f);
        for (int pass = 0; pass < 4; pass++)
            for (int k = 0; k < 12; k++)
            {
                time += 0.02;
                float x = P.X + (pass % 2 == 0 ? k : 12 - k) * 3;
                t.Move(new SKPoint(x, P.Y), time, HitRegion.Head, 1f);
            }
        Assert.Contains(t.Drain(), g => g.Kind == GestureKind.PetStroke);

        var fast = new PointerTracker();
        fast.Move(P, 0, HitRegion.Head, 1f);
        for (int k = 1; k < 6; k++) fast.Move(new SKPoint(P.X + k * 80, P.Y), k * 0.01, HitRegion.Head, 1f);
        Assert.DoesNotContain(fast.Drain(), g => g.Kind == GestureKind.PetStroke);
    }
}

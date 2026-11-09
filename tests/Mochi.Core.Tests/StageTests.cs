using Mochi.Core.Behaviour;
using Mochi.Core.Persistence;
using Mochi.Core.Play;
using Mochi.Core.Stage;
using SkiaSharp;
using static Mochi.Core.Tests.Sim;

namespace Mochi.Core.Tests;

public class StageTests
{
    // An iPhone-sized screen in points: status bar on top, toolbar and home indicator at the bottom.
    static PetStage NewStage(int seed = 42, PetMemory? memory = null)
    {
        var stage = new PetStage(new MochiSettings(), memory ?? new PetMemory(), seed);
        stage.Resize(390, 844, 0, 59, 0, 83, Afternoon);
        return stage;
    }

    // Runs the stage at 60 fps from `from` for `seconds`; returns the time reached.
    static double Advance(PetStage s, double from, double seconds, DateTime? local = null)
    {
        double t = from;
        for (; t < from + seconds; t += 1 / 60.0) s.Tick(t, local ?? Afternoon);
        return t;
    }

    static double AdvanceUntil(PetStage s, double from, double maxSeconds, Func<PetStage, bool> until)
    {
        double t = from;
        for (; t < from + maxSeconds && !until(s); t += 1 / 60.0) s.Tick(t, Afternoon);
        return t;
    }

    static void Tap(PetStage s, SKPoint p, double t)
    {
        s.TouchBegan(p, t);
        s.TouchEnded(p, t + 0.08);
    }

    [Fact]
    public void Tapping_the_kitten_makes_it_happy_but_tapping_empty_space_does_not()
    {
        var s = NewStage();
        double t = Advance(s, 0, 6);
        var empty = new SKPoint(30, 200);
        Assert.False(s.IsOnPet(empty));
        Tap(s, empty, t);
        t = Advance(s, t + 0.1, 0.2);
        Assert.NotEqual(PetState.Happy, s.Brain.State);

        var head = s.Brain.HeadWorld;
        Assert.True(s.IsOnPet(head));
        Tap(s, head, t);
        Advance(s, t + 0.1, 0.2);
        Assert.Equal(PetState.Happy, s.Brain.State);
    }

    [Fact]
    public void Press_and_hold_picks_the_kitten_up_and_a_flick_throws_it()
    {
        var s = NewStage();
        double t = Advance(s, 0, 6);
        var head = s.Brain.HeadWorld;
        s.TouchBegan(head, t);
        t = Advance(s, t, 0.6);
        Assert.True(s.Brain.IsHeld);

        for (int i = 1; i <= 12; i++)
        {
            t += 1 / 60.0;
            s.TouchMoved(new SKPoint(head.X + i * 6f, head.Y - i * 14f), t);
            s.Tick(t, Afternoon);
        }
        t += 1 / 60.0;
        s.TouchEnded(new SKPoint(head.X + 78f, head.Y - 182f), t);
        Advance(s, t, 0.1);
        Assert.False(s.Brain.IsHeld);
        Assert.True(s.Brain.Airborne);
    }

    [Fact]
    public void Stroking_its_head_with_a_finger_pets_the_kitten()
    {
        var s = NewStage();
        double t = AdvanceUntil(s, 0, 60, x => x.Brain.Now > 5 && x.Brain.State is PetState.Sitting or PetState.Idle or PetState.LyingDown);
        var head = s.Brain.HeadWorld;
        s.TouchBegan(head, t);
        for (int i = 1; i <= 90; i++)
        {
            t += 1 / 60.0;
            s.TouchMoved(new SKPoint(head.X + 18f * MathF.Sin(i * 0.2f), head.Y), t);
            s.Tick(t, Afternoon);
        }
        Assert.Equal(PetState.BeingPetted, s.Brain.State);
        s.TouchEnded(head, t);
        Assert.False(s.Brain.IsHeld);
    }

    [Fact]
    public void Flicking_the_ball_sends_it_flying_and_tapping_it_throws_it_for_mochi()
    {
        var s = NewStage();
        double t = Advance(s, 0, 4);
        s.Brain.ThrowBall();
        t = Advance(s, t, 0.05);
        var ball = s.Brain.Ball!;
        var at = ball.Position;
        s.TouchBegan(at, t);
        Assert.Equal(BallState.HeldByUser, ball.State);
        for (int i = 1; i <= 6; i++)
        {
            t += 1 / 60.0;
            s.TouchMoved(new SKPoint(at.X - i * 15f, at.Y - i * 4f), t);
            s.Tick(t, Afternoon);
        }
        s.TouchEnded(new SKPoint(at.X - 105f, at.Y - 28f), t + 1 / 60.0);
        Assert.Equal(BallState.Free, ball.State);
        Assert.True(ball.Velocity.X < -500f, $"flicked to the left, vx = {ball.Velocity.X}");

        t = Advance(s, t + 0.1, 3);
        Tap(s, ball.Position, t);
        Assert.Equal(BallState.Free, ball.State);
        Assert.True(ball.Velocity.Y < -300f, $"thrown up in an arc, vy = {ball.Velocity.Y}");
    }

    [Fact]
    public void Coming_back_to_the_app_after_a_while_gets_a_hi()
    {
        var s = NewStage();
        double t = Advance(s, 0, 8);
        var utc = new DateTime(2026, 11, 8, 9, 0, 0, DateTimeKind.Utc);
        s.Pause(t, utc);
        s.Resume(t + 1, utc.AddSeconds(30));
        t = Advance(s, t + 1, 0.5);
        Assert.NotEqual(PetState.Greeting, s.Brain.State);

        s.Pause(t, utc.AddSeconds(40));
        s.Resume(t + 1, utc.AddMinutes(10));
        Advance(s, t + 1, 1.5);
        Assert.Equal(PetState.Greeting, s.Brain.State);
        Assert.Equal("Hi!", s.Brain.Effects.LastSaid);
    }

    [Fact]
    public void The_views_follow_the_kitten_on_whole_pixels_and_everything_draws()
    {
        var s = NewStage();
        Advance(s, 0, 2);
        s.Layout(3f);
        var f = s.PetFrame;
        Assert.True(f.Contains(s.Brain.HeadWorld));
        float left = f.Left * 3f;
        Assert.True(MathF.Abs(left - MathF.Round(left)) < 1e-3f, "snapped to device pixels");

        using var pet = SKSurface.Create(new SKImageInfo((int)MathF.Round(f.Width * 3f), (int)MathF.Round(f.Height * 3f)));
        s.DrawPet(pet.Canvas, 3f);
        using var petBmp = SKBitmap.FromImage(pet.Snapshot());
        int solid = 0;
        for (int y = 0; y < petBmp.Height; y += 4)
            for (int x = 0; x < petBmp.Width; x += 4)
                if (petBmp.GetPixel(x, y).Alpha > 200) solid++;
        Assert.True(solid > 600, $"the kitten should cover part of its view ({solid} samples)");

        using var room = SKSurface.Create(new SKImageInfo(390, 844));
        s.DrawRoom(room.Canvas, 1f);
        using var roomBmp = SKBitmap.FromImage(room.Snapshot());
        Assert.Equal(255, roomBmp.GetPixel(2, 2).Alpha);
        Assert.Equal(255, roomBmp.GetPixel(387, 841).Alpha);
    }

    [Fact]
    public void Turning_the_phone_keeps_the_kitten_on_the_floor_and_on_screen()
    {
        var s = NewStage();
        Advance(s, 0, 3);
        s.Resize(844, 390, 59, 0, 59, 70, Afternoon);
        Advance(s, 3, 2);
        Assert.Equal(s.Floor.Y, s.Brain.Position.Y, 1);
        Assert.InRange(s.Brain.Position.X, s.Brain.MinX - 0.5f, s.Brain.MaxX + 0.5f);

        s.Resize(390, 844, 0, 59, 0, 83, Afternoon);
        Advance(s, 5, 3);
        Assert.Equal(s.Floor.Y, s.Brain.Position.Y, 1);
    }

    [Fact]
    public void Sliding_a_finger_along_the_floor_towards_it_starts_a_game_of_chase()
    {
        var s = NewStage(5);
        double t = Advance(s, 0, 4);
        bool chased = false;
        for (int sweep = 0; sweep < 10 && !chased; sweep++)
        {
            float x = s.Brain.Position.X - 330f, y = s.Floor.Y - 30f, stop = s.Brain.Position.X - 45f;
            s.TouchBegan(new SKPoint(x, y), t);
            for (int i = 0; i < 180 && !chased; i++)
            {
                t += 1 / 60.0;
                if (i < 70) x = MathF.Min(x + 600f / 60f, stop);
                if (i == 70) s.TouchEnded(new SKPoint(x, y), t);
                else if (i < 70) s.TouchMoved(new SKPoint(x, y), t);
                s.Tick(t, Afternoon);
                chased = s.Brain.State == PetState.Playful;
            }
        }
        Assert.True(chased, "Mochi should run off and play chase");
    }

    [Fact]
    public void The_room_dims_at_night_and_mochi_remembers_where_it_was()
    {
        var memory = new PetMemory();
        var s = NewStage(7, memory);
        int version = s.RoomVersion;
        s.Tick(0, Afternoon);
        Assert.False(s.IsNightRoom);
        s.Tick(0.02, Afternoon.Date.AddHours(22));
        Assert.True(s.IsNightRoom);
        Assert.NotEqual(version, s.RoomVersion);

        s.Brain.Place(new SKPoint(300, s.Floor.Y), -1);
        s.Pause(1, DateTime.UtcNow);
        var again = NewStage(7, memory);
        Assert.Equal(300f, again.Brain.Position.X, 0);
        Assert.Equal(-1, again.Brain.Facing);
    }
}

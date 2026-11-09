using System.Diagnostics;
using Mochi.Core.Effects;
using Mochi.Core.Rig;
using SkiaSharp;

namespace Mochi.Core.Tests;

public class RenderingTests
{
    static PetPose Pose(Action<PetPose> f)
    {
        var p = new PetPose();
        p.ResetToNeutral();
        f(p);
        return p;
    }

    public static TheoryData<string> PoseNames => new()
    {
        "stand", "walk", "run", "sit", "loaf", "sleep", "stretch", "held", "flip", "happy", "wink", "yawn",
        "stalk", "pounce", "startled", "groom", "knead", "swat", "purr", "front", "paws up", "front turning",
    };

    static PetPose Named(string name) => name switch
    {
        "walk" => Pose(p => { p.GaitAmount = 1; p.GaitPhase = 1.1f; }),
        "run" => Pose(p => { p.GaitAmount = 2; p.GaitPhase = 2.2f; p.PupilSize = 1; }),
        "sit" => Pose(p => { p.Sit = 1; p.TailWrap = 1; }),
        "loaf" => Pose(p => { p.Lie = 1; p.TailWrap = 1; }),
        "sleep" => Pose(p => { p.Lie = 1; p.Curl = 1; p.TailWrap = 1; p.EyeOpen = 0; }),
        "stretch" => Pose(p => { p.Bow = 1; p.TailRaise = 1; }),
        "held" => Pose(p => { p.Held = 1; p.SwingAngle = 25; }),
        "flip" => Pose(p => { p.Hop = 30; p.Flip = 180; }),
        "happy" => Pose(p => { p.HappyEyes = 1; p.MouthOpen = 0.6f; p.TailRaise = 1; p.TailCurl = 1; }),
        "wink" => Pose(p => { p.EyeOpenL = 0; p.Sparkle = 1; }),
        "yawn" => Pose(p => { p.MouthOpen = 1; p.EyeOpen = 0.3f; p.EarLift = -1; }),
        "stalk" => Pose(p => { p.Crouch = 1; p.PupilSize = 1; p.Wiggle = 3; }),
        "pounce" => Pose(p => { p.Leap = 1; p.Hop = 22; p.MouthOpen = 0.3f; }),
        "startled" => Pose(p => { p.Arch = 1; p.Puff = 1; p.EyeSize = 1.3f; p.TailRaise = 1; p.EarLift = -1; }),
        "groom" => Pose(p => { p.Sit = 1; p.TailWrap = 1; p.PawToFace = 1; p.Tongue = 0.6f; }),
        "knead" => Pose(p => { p.Lie = 1; p.Knead = 1; p.PaddlePhase = 1.3f; p.Purr = 1; }),
        "swat" => Pose(p => { p.Crouch = 0.4f; p.FrontPawRaise = 1; }),
        "purr" => Pose(p => { p.Sit = 1; p.TailWrap = 1; p.EyeOpen = 0.05f; p.Blush = 1; p.Purr = 1; p.Wiggle = 0.4f; }),
        "front" => Pose(p => { p.Sit = 1; p.Front = 1; p.TailSway = 12; p.TailSwayPhase = 1.6f; }),
        "paws up" => Pose(p => { p.Sit = 1; p.Front = 1; p.PawsUp = 1; p.Knead = 1; p.PaddlePhase = 2f; }),
        "front turning" => Pose(p => { p.Sit = 1; p.Front = 0.5f; p.TailWrap = 1; }),
        _ => Pose(_ => { }),
    };

    [Theory]
    [MemberData(nameof(PoseNames))]
    public void Every_pose_renders_a_visible_kitten_inside_the_window_area(string name)
    {
        using var renderer = new PetRenderer(PetLook.Golden);
        var info = new SKImageInfo(250, 215, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);
        c.Translate(125, 185); // the same anchor as the desktop window
        renderer.Draw(c, Named(name));

        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        int opaque = 0;
        bool touchesEdge = false;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                if (bmp.GetPixel(x, y).Alpha <= 200) continue;
                opaque++;
                if (x == 0 || y == 0 || x == bmp.Width - 1) touchesEdge = true;
            }
        Assert.True(opaque > 3000, $"{name}: only {opaque} opaque pixels");
        Assert.False(touchesEdge, $"{name}: the kitten is clipped by the window edge");
    }

    [Fact]
    public void Paws_rest_on_the_ground_in_grounded_postures()
    {
        foreach (var pose in new[] { Pose(_ => { }), Pose(p => p.Sit = 1) })
        {
            var s = Skeleton.Compute(pose);
            Assert.Equal(Skeleton.PawGroundY, s.FrontPawNear.Y, 0.01);
            Assert.Equal(Skeleton.PawGroundY, s.HindPawNear.Y, 0.01);
        }
        // A loaf tucks its paws under, but never floats above the floor or sinks through it.
        var loaf = Skeleton.Compute(Pose(p => p.Lie = 1));
        Assert.InRange(loaf.FrontPawNear.Y, Skeleton.PawGroundY - 2f, 0f);
        Assert.InRange(loaf.HindPawNear.Y, Skeleton.PawGroundY - 2f, 0f);
    }

    [Fact]
    public void Coats_and_accessories_change_the_artwork()
    {
        static uint Hash(PetLook look)
        {
            using var r = new PetRenderer(look);
            using var surface = SKSurface.Create(new SKImageInfo(120, 120));
            surface.Canvas.Translate(60, 110);
            surface.Canvas.Scale(0.55f);
            r.Draw(surface.Canvas, Pose(_ => { }));
            using var img = surface.Snapshot();
            using var bmp = SKBitmap.FromImage(img);
            uint h = 2166136261;
            foreach (var px in bmp.Pixels) h = (h ^ (uint)px) * 16777619;
            return h;
        }
        var hashes = new[]
        {
            Hash(PetLook.Golden), Hash(PetLook.CreamCoat), Hash(PetLook.Ginger), Hash(PetLook.Silver),
            Hash(PetLook.Golden with { Accessory = AccessoryKind.Bow }), Hash(PetLook.Golden with { Accessory = AccessoryKind.None }),
            Hash(PetLook.Golden with { Accessory = AccessoryKind.Bell }), Hash(PetLook.Golden with { Stripes = 0f }),
        };
        Assert.Equal(hashes.Length, hashes.Distinct().Count());
    }

    [Fact]
    public void Rendering_is_cheap_enough_for_a_background_companion()
    {
        using var renderer = new PetRenderer(PetLook.Golden);
        using var effects = new EffectSystem();
        using var surface = SKSurface.Create(new SKImageInfo(313, 269, SKColorType.Bgra8888, SKAlphaType.Premul));
        var c = surface.Canvas;
        var pose = Pose(p => p.GaitAmount = 1);
        for (int i = 0; i < 5; i++) effects.Emit(EffectKind.Heart, i * 5, -100);
        void Frame(int i)
        {
            pose.GaitPhase = i * 0.1f;
            c.Clear(SKColors.Transparent);
            c.ResetMatrix();
            c.Translate(156, 231);
            c.Scale(1.25f);
            renderer.Draw(c, pose);
            effects.Draw(c, new SKPoint(20, -105));
        }
        for (int i = 0; i < 30; i++) Frame(i); // warm-up (sprite baking, JIT)
        var sw = Stopwatch.StartNew();
        const int frames = 300;
        for (int i = 0; i < frames; i++) Frame(i);
        double ms = sw.Elapsed.TotalMilliseconds / frames;
        Assert.True(ms < 6, $"average frame {ms:F2} ms");
    }
}

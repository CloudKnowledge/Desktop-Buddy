using System.Diagnostics;
using Mochi.Core.Behaviour;
using Mochi.Core.Persistence;
using Mochi.Core.Play;
using Mochi.Core.Rig;
using SkiaSharp;

// Renders contact sheets of Mochi's poses for visual QA, measures render cost and builds the app icon.
// Usage: Mochi.Preview [outDir] [--icon <path.ico>]
var iconArg = Array.IndexOf(args, "--icon");
if (iconArg >= 0 && iconArg + 1 < args.Length)
{
    var icoPath = Path.GetFullPath(args[iconArg + 1]);
    Directory.CreateDirectory(Path.GetDirectoryName(icoPath)!);
    File.WriteAllBytes(icoPath, IconArt.BuildIco(PetLook.Golden, 16, 20, 24, 32, 40, 48, 64, 128, 256));
    Console.WriteLine($"Wrote {icoPath}");
    // Strip of PNGs for checking legibility at tray sizes.
    var strip = Path.Combine(Path.GetTempPath(), "mochi-icon-strip.png");
    using (var surface = SKSurface.Create(new SKImageInfo(16 + 24 + 32 + 48 + 256 + 60, 256)))
    {
        surface.Canvas.Clear(new SKColor(0x20, 0x20, 0x20));
        float x = 0;
        foreach (int size in new[] { 16, 24, 32, 48, 256 })
        {
            using var bmp = IconArt.RenderFace(PetLook.Golden, size);
            using var img = SKImage.FromBitmap(bmp);
            surface.Canvas.DrawImage(img, x, 0, new SKSamplingOptions(SKFilterMode.Linear));
            x += size + 12;
        }
        Save(surface, strip);
    }
    Console.WriteLine($"Wrote {strip}");
    return;
}

var outDir = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : Path.Combine(Environment.CurrentDirectory, "preview-out");
Directory.CreateDirectory(outDir);

// --life: films everyday moments (hello, snacks, cat TV, chase, catching the ball) from the real brain.
if (args.Contains("--life"))
{
    var env = new FlatEnv(new Floor(0, 1400, 0, 600, 1f, "preview"));
    using var pr = new PetRenderer(PetLook.Golden);
    using var yr = new YarnBallRenderer();
    using var props = new PropRenderer();
    const int lifeCols = 4, lifeRows = 4;
    const float cellW = 440, cellH = 210, k = 1.15f;
    var info = new SKImageInfo((int)(lifeCols * cellW), (int)(lifeRows * cellH));
    using var surface = SKSurface.Create(info);
    var c = surface.Canvas;
    c.Clear(new SKColor(0x1E, 0x1F, 0x24));
    using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 13f);
    using var label = new SKPaint { IsAntialias = true, Color = SKColors.LightGray };
    using var floorPaint = new SKPaint { Color = new SKColor(0x3A, 0x3C, 0x44), StrokeWidth = 2 };
    int shot = 0;

    void Shot(PetBrain b, string name)
    {
        float cx = (shot % lifeCols) * cellW, cy = (shot / lifeCols) * cellH;
        shot++;
        float focus = b.Position.X + b.Facing * 35f;
        if (b.Ball is { } bb) focus = (focus + bb.Position.X) / 2f;
        c.Save();
        c.ClipRect(new SKRect(cx, cy, cx + cellW, cy + cellH));
        c.Translate(cx + cellW / 2f - focus * k, cy + cellH - 14f - 600f * k);
        c.Scale(k);
        c.DrawLine(0, 600, 1400, 600, floorPaint);
        c.Save();
        c.Translate(b.Position.X, b.Position.Y);
        c.Scale(b.Scale);
        c.Save();
        c.Scale(b.Facing, 1f);
        pr.Draw(c, b.Animator.Output);
        if (b.Prop is { } prop) props.Draw(c, prop);
        c.Restore();
        b.Effects.Draw(c, new SKPoint(b.HeadModelUnflipped.X, b.HeadModelUnflipped.Y - 30f));
        c.Restore();
        if (b.Ball is { } ball)
        {
            c.Save();
            c.Translate(ball.Position.X, ball.Position.Y);
            c.Scale(ball.Scale);
            yr.Draw(c, ball, PetLook.AccessoryColors[1].Color);
            c.Restore();
        }
        c.Restore();
        c.DrawText($"{name}  ({b.State})", cx + 8, cy + 16, SKTextAlign.Left, font, label);
    }

    PetBrain Kitten(int seed)
    {
        var b = new PetBrain(new BrainOptions(), new Personality(PersonalityProfile.Playful), new PetMemory(), env, seed);
        b.Place(new SKPoint(600, 600), 1);
        return b;
    }

    Senses At(float x, float y) => new() { Cursor = new SKPoint(x, y), CursorKnown = true, IdleSeconds = 0.5, LocalTime = DateTime.Today.AddHours(14) };

    // Runs a scene, taking each shot the first time its condition holds.
    void Scene(PetBrain b, double seconds, Func<PetBrain, double, Senses> senses, params (Func<PetBrain, double, bool> When, string Name)[] shots)
    {
        int next = 0;
        for (double t = 0; t < seconds && next < shots.Length; t += 1 / 60.0)
        {
            b.Update(1 / 60f, senses(b, t));
            if (shots[next].When(b, t)) Shot(b, shots[next++].Name);
        }
        while (next < shots.Length) Shot(b, shots[next++].Name + " (missed)");
    }

    var hi = Kitten(1);
    hi.Start(DateTime.Today.AddHours(14), DateTime.UtcNow.AddHours(-1));
    Scene(hi, 4, (_, _) => At(900, 300), ((_, t) => t >= 0.45, "Hi!"), ((b, _) => b.Animator.Output.Wave > 0.9f, "waving hello"));

    var milk = Kitten(2);
    milk.CommandFeed(PropKind.Milk);
    Scene(milk, 20, (_, _) => At(900, 300), ((_, t) => t >= 4, "lapping milk"), ((b, _) => b.Effects.LastSaid == "Yum!", "yum!"));

    var treats = Kitten(3);
    treats.CommandFeed(PropKind.Treats);
    Scene(treats, 10, (_, _) => At(900, 300), ((_, t) => t >= 4.1, "crunching treats"));

    var work = Kitten(30);
    work.CommandWork();
    Scene(work, 20, (_, _) => At(900, 300), ((b, t) => t >= 2 && b.Animator.Output.Knead > 0.7f, "working with you"));

    var tv = Kitten(4);
    tv.CommandWatchTv();
    Scene(tv, 30, (_, _) => At(900, 300), ((_, t) => t >= 3, "cat TV"), ((b, _) => b.Animator.Output.FrontPawRaise > 0.8f, "pawing at the screen"));

    var tag = Kitten(5);
    float cursorX = 0;
    Scene(tag, 30, (b, t) =>
        {
            // Warm up, then sweep the pointer along the floor towards Mochi, again and again.
            if (t < 4) return At(150, 300);
            double sweep = (t - 4) % 3.0;
            if (sweep < 0.02) cursorX = b.Position.X - 380f;
            if (sweep < 1.2) cursorX = MathF.Min(cursorX + 700f / 60f, b.Position.X - 45f);
            return At(cursorX, 570);
        },
        ((b, _) => b.State == PetState.Playful && b.Animator.Output.GaitAmount > 1.6f, "run, run!"),
        ((b, _) => b.Animator.Output.Bow > 0.75f, "play bow: catch me!"),
        ((b, t) => t > 9 && b.State == PetState.Playful && b.Animator.Output.GaitAmount > 1.6f, "off again"));

    for (int seed = 6; seed < 40; seed++)
    {
        var probe = Kitten(seed);
        probe.ThrowBall();
        for (double t = 0; t < 8; t += 1 / 60.0) probe.Update(1 / 60f, At(560, 420));
        if (probe.BallCatches == 0) continue;
        var ball = Kitten(seed);
        ball.ThrowBall();
        Scene(ball, 14, (_, _) => At(560, 420),
            ((b, t) => t >= 0.35, "ball thrown"),
            ((b, _) => b.Animator.Output.Leap > 0.5f && b.Animator.Output.Hop > 6f, "jump..."),
            ((b, _) => b.BallCatches > 0, "caught it!"),
            ((b, _) => b.State == PetState.CarryingBall && b.Animator.Output.GaitAmount > 0.5f, "bringing it back"));
        break;
    }
    Save(surface, Path.Combine(outDir, "life-film.png"));
    Console.WriteLine($"Wrote life-film ({shot} shots)");
    return;
}

// --ball: films a simulated play session with the yarn ball (the real brain, physics and renderers).
if (args.Contains("--ball"))
{
    var env = new FlatEnv(new Floor(0, 1400, 0, 600, 1f, "preview"));
    var brain = new PetBrain(new BrainOptions(), new Personality(PersonalityProfile.Affectionate), new PetMemory(), env, 11);
    brain.Place(new SKPoint(700, 600), 1);
    brain.BringOutBall();
    using var pr = new PetRenderer(PetLook.Golden);
    using var yr = new YarnBallRenderer();
    var yarn = PetLook.AccessoryColors[1].Color;
    const int filmCols = 3, filmRows = 10;
    const float cellW = 600, cellH = 190, k = 0.8f;
    var info = new SKImageInfo((int)(filmCols * cellW), (int)(filmRows * cellH));
    using var surface = SKSurface.Create(info);
    var c = surface.Canvas;
    c.Clear(new SKColor(0x1E, 0x1F, 0x24));
    using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 13f);
    using var label = new SKPaint { IsAntialias = true, Color = SKColors.LightGray };
    using var floorPaint = new SKPaint { Color = new SKColor(0x3A, 0x3C, 0x44), StrokeWidth = 2 };
    var senses = new Senses { Cursor = new SKPoint(300, 560), CursorKnown = true, IdleSeconds = 0.5, LocalTime = DateTime.Today.AddHours(14) };
    int frame = 0;
    for (int step = 0; frame < filmCols * filmRows; step++)
    {
        brain.Update(1 / 60f, senses);
        if (step % 30 != 29) continue;
        float cx = (frame % filmCols) * cellW, cy = (frame / filmCols) * cellH;
        var ball = brain.Ball!;
        float mid = Math.Clamp((brain.Position.X + ball.Position.X) / 2f, cellW / k / 2f, 1400 - cellW / k / 2f);
        c.Save();
        c.ClipRect(new SKRect(cx, cy, cx + cellW, cy + cellH));
        c.Translate(cx + cellW / 2f - mid * k, cy + cellH - 18f - 600f * k);
        c.Scale(k);
        c.DrawLine(0, 600, 1400, 600, floorPaint);
        c.Save();
        c.Translate(brain.Position.X, brain.Position.Y);
        c.Scale(brain.Scale);
        c.Save();
        c.Scale(brain.Facing, 1f);
        pr.Draw(c, brain.Animator.Output);
        c.Restore();
        brain.Effects.Draw(c, new SKPoint(brain.HeadModelUnflipped.X, brain.HeadModelUnflipped.Y - 30f));
        c.Restore();
        c.Save();
        c.Translate(ball.Position.X, ball.Position.Y);
        c.Scale(ball.Scale);
        yr.Draw(c, ball, yarn);
        c.Restore();
        c.Restore();
        c.DrawText($"{(step + 1) / 60f:F1}s  {brain.State}", cx + 8, cy + 16, SKTextAlign.Left, font, label);
        frame++;
    }
    Save(surface, Path.Combine(outDir, "ball-film.png"));
    Console.WriteLine($"Wrote ball-film ({brain.BallHits} hits)");
    return;
}

static PetPose P(Action<PetPose> f)
{
    var p = new PetPose();
    p.ResetToNeutral();
    f(p);
    return p;
}

var poses = new List<(string Name, PetPose Pose)>
{
    ("front", P(p => { p.Sit = 1; p.Front = 1; p.HeadTurn = 0; p.LookX = 0; p.Smile = 0.6f; })),
    ("paws up", P(p => { p.Sit = 1; p.Front = 1; p.PawsUp = 1; p.HeadTurn = 0; p.HeadTilt = -6; p.LookX = 0; p.LookY = -0.1f; p.Smile = 0.7f; p.Blush = 0.6f; p.TailSway = 0; })),
    ("front purr", P(p => { p.Sit = 1; p.Front = 1; p.EyeOpen = 0.05f; p.Blush = 0.9f; p.Smile = 1; p.HeadTilt = 10; p.HeadLift = 0.25f; })),
    ("stand", P(_ => { })),
    ("happy", P(p => { p.HappyEyes = 1; p.Blush = 0.8f; p.HeadTurn = 0; p.TailRaise = 1; p.TailCurl = 0.8f; p.EarLift = 0.4f; })),
    ("walk A", P(p => { p.GaitAmount = 1; p.GaitPhase = 0; p.HeadTurn = 0.42f; p.TailRaise = 0.9f; p.TailCurl = 0.65f; })),
    ("walk B", P(p => { p.GaitAmount = 1; p.GaitPhase = MathF.PI / 2; p.HeadTurn = 0.42f; p.TailRaise = 0.9f; p.TailCurl = 0.65f; })),
    ("run", P(p => { p.GaitAmount = 2; p.GaitPhase = 1.2f; p.HeadTurn = 0.6f; p.TailRaise = 1; p.PupilSize = 1; p.EarLift = -0.15f; })),
    ("sit", P(p => { p.Sit = 1; p.TailWrap = 1; })),
    ("curious", P(p => { p.Sit = 1; p.TailWrap = 1; p.HeadTilt = 16; p.BrowRaise = 0.8f; p.EarLift = 0.7f; p.EarAsym = 0.5f; p.PupilSize = 0.92f; p.Whisker = 0.9f; p.LookX = 0.5f; p.LookY = -0.3f; })),
    ("loaf", P(p => { p.Lie = 1; p.TailWrap = 1; p.HeadTurn = 0.15f; p.Smile = 0.55f; })),
    ("sleep", P(p => { p.Lie = 1; p.Curl = 1; p.TailWrap = 1; p.EyeOpen = 0; p.EarLift = -0.25f; p.TailSway = 0; p.HeadTurn = 0.1f; })),
    ("yawn", P(p => { p.Lie = 1; p.TailWrap = 1; p.EyeOpen = 0; p.MouthOpen = 1; p.Tongue = 0.25f; p.HeadLift = 0.6f; p.EarLift = -0.5f; p.Whisker = -0.6f; })),
    ("stretch", P(p => { p.Bow = 1; p.TailRaise = 1; p.EyeOpen = 0.15f; p.EarLift = -0.4f; })),
    ("held", P(p => { p.Held = 1; p.EyeSize = 1.1f; p.TailRaise = -0.2f; p.LookY = -0.55f; p.Blush = 0.65f; })),
    ("purring", P(p => { p.Sit = 1; p.TailWrap = 1; p.EyeOpen = 0.08f; p.Blush = 0.95f; p.HeadTilt = -10; p.HeadLift = 0.25f; p.Smile = 1; p.Knead = 1; p.PaddlePhase = 2f; })),
    ("startled", P(p => { p.Arch = 1; p.Puff = 1; p.EyeSize = 1.22f; p.PupilSize = 1; p.EarLift = -0.8f; p.Whisker = -0.8f; p.TailRaise = 1; p.TailCurl = 0.1f; p.HeadTurn = 0.15f; })),
    ("stalk", P(p => { p.Crouch = 1; p.PupilSize = 1; p.EarLift = 0.75f; p.Whisker = 1; p.HeadTurn = 0.7f; p.LookX = 1; p.TailRaise = -0.15f; p.TailCurl = 0.25f; })),
    ("pounce", P(p => { p.Leap = 1; p.Hop = 22; p.PupilSize = 1; p.EarLift = 0.75f; p.HeadTurn = 0.7f; p.LookX = 1; p.TailRaise = 0.3f; p.MouthOpen = 0.3f; })),
    ("groom", P(p => { p.Sit = 1; p.TailWrap = 1; p.PawToFace = 1; p.HeadTurn = 0.3f; p.HeadLift = -0.3f; p.Tongue = 0.55f; p.MouthOpen = 0.1f; p.EyeOpen = 0.1f; })),
    ("mew", P(p => { p.Sit = 1; p.TailWrap = 1; p.MouthOpen = 0.7f; p.HeadTurn = 0; p.EarLift = 0.4f; p.PupilSize = 0.8f; })),
    ("blep", P(p => { p.Sit = 1; p.TailWrap = 1; p.Tongue = 0.16f; p.HeadTurn = 0; p.LookX = 0; p.LookY = 0.1f; })),
    ("swat", P(p => { p.Crouch = 0.4f; p.FrontPawRaise = 1; p.PupilSize = 1; p.EarLift = 0.6f; p.HeadTurn = 0.6f; })),
    ("drowsy", P(p => { p.Lie = 1; p.TailWrap = 1; p.EyeOpen = 0.45f; p.HeadLift = -0.3f; p.EarLift = -0.3f; p.Smile = 0.3f; })),
};

// --pose <name>: one pose, large, for detail work.
var poseArg = Array.IndexOf(args, "--pose");
if (poseArg >= 0 && poseArg + 1 < args.Length)
{
    var (name, pose) = poses.First(x => x.Name == args[poseArg + 1]);
    var info = new SKImageInfo(720, 560);
    using var surface = SKSurface.Create(info);
    surface.Canvas.Clear(new SKColor(0xF4, 0xF6, 0xFA));
    using var r = new PetRenderer(PetLook.Golden);
    surface.Canvas.Translate(360, 520);
    surface.Canvas.Scale(5f);
    r.Draw(surface.Canvas, pose);
    Save(surface, Path.Combine(outDir, $"pose-{name.Replace(' ', '-')}.png"));
    Console.WriteLine($"Wrote pose-{name}");
    return;
}

const float scale = 2.2f;
const int cell = 230;
int cols = 4, rows = (poses.Count + cols - 1) / cols;
using var renderer = new PetRenderer(PetLook.Golden);
using var labelFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 15f, 1f, 0f);

foreach (var (bgName, bg) in new[] { ("light", new SKColor(0xF4, 0xF6, 0xFA)), ("dark", new SKColor(0x1E, 0x1F, 0x24)) })
{
    var info = new SKImageInfo((int)(cols * cell * scale / 1.6f), (int)(rows * cell * scale / 1.6f));
    using var surface = SKSurface.Create(info);
    var c = surface.Canvas;
    c.Clear(bg);
    using var label = new SKPaint { IsAntialias = true, Color = bgName == "light" ? SKColors.DimGray : SKColors.LightGray };
    float cw = info.Width / (float)cols, ch = info.Height / (float)rows;
    for (int i = 0; i < poses.Count; i++)
    {
        float x = (i % cols) * cw, y = (i / cols) * ch;
        c.Save();
        c.Translate(x + cw * 0.5f, y + ch * 0.86f);
        c.Scale(scale);
        renderer.Draw(c, poses[i].Pose);
        c.Restore();
        c.DrawText(poses[i].Name, x + 10, y + 22, SKTextAlign.Left, labelFont, label);
    }
    Save(surface, Path.Combine(outDir, $"sheet-{bgName}.png"));
}

// Close-ups of the face and of the sleeping curl for detail checks.
{
    var info = new SKImageInfo(1100, 520);
    using var surface = SKSurface.Create(info);
    var c = surface.Canvas;
    c.Clear(new SKColor(0xF4, 0xF6, 0xFA));
    c.Save();
    c.Translate(260, 250);
    c.Scale(6f);
    renderer.DrawHeadOnly(c, P(p => { p.HeadTurn = 0.1f; p.LookX = 0.2f; }));
    c.Restore();
    c.Save();
    c.Translate(800, 440);
    c.Scale(4.4f);
    renderer.Draw(c, P(p => { p.Lie = 1; p.Curl = 1; p.TailWrap = 1; p.EyeOpen = 0; p.EarLift = -0.25f; p.TailSway = 0; p.HeadTurn = 0.1f; }));
    c.Restore();
    Save(surface, Path.Combine(outDir, "closeup.png"));
}

// True on-screen size (125 % DPI) on a dark and a light background — what the user actually sees.
{
    var real = new (string Name, PetPose Pose)[]
    {
        ("sit", P(p => { p.Sit = 1; p.Front = 1; p.PawsUp = 1; p.TailWrap = 1; p.HeadTurn = 0; p.LookX = 0; })),
        ("loaf", P(p => { p.Lie = 1; p.TailWrap = 1; p.EyeOpen = 0; p.Smile = 0.55f; })),
        ("walk", P(p => { p.GaitAmount = 1; p.GaitPhase = 0.8f; p.HeadTurn = 0.42f; p.TailRaise = 0.9f; p.TailCurl = 0.65f; })),
        ("sleep", P(p => { p.Lie = 1; p.Curl = 1; p.TailWrap = 1; p.EyeOpen = 0; p.EarLift = -0.25f; p.TailSway = 0; })),
        ("happy", P(p => { p.HappyEyes = 1; p.Blush = 0.8f; p.HeadTurn = 0; p.TailRaise = 1; p.TailCurl = 0.8f; })),
    };
    var info = new SKImageInfo(real.Length * 170, 2 * 150);
    using var surface = SKSurface.Create(info);
    var c = surface.Canvas;
    c.Clear(new SKColor(0x1F, 0x1F, 0x1F));
    using (var light = new SKPaint { Color = new SKColor(0xF3, 0xF3, 0xF3) }) c.DrawRect(0, 150, info.Width, 150, light);
    for (int row = 0; row < 2; row++)
        for (int i = 0; i < real.Length; i++)
        {
            c.Save();
            c.Translate(85 + i * 170, 140 + row * 150);
            c.Scale(1.25f);
            renderer.Draw(c, real[i].Pose);
            c.Restore();
        }
    Save(surface, Path.Combine(outDir, "realsize.png"));
}

// Coat variants and accessories.
{
    var looks = new[]
    {
        PetLook.Golden, PetLook.CreamCoat, PetLook.Ginger, PetLook.Silver,
        PetLook.Golden with { Accessory = AccessoryKind.Bell }, PetLook.Golden with { Accessory = AccessoryKind.Bow },
    };
    var info = new SKImageInfo(1200, 300);
    using var surface = SKSurface.Create(info);
    var c = surface.Canvas;
    c.Clear(new SKColor(0xF4, 0xF6, 0xFA));
    for (int i = 0; i < looks.Length; i++)
    {
        using var r = new PetRenderer(looks[i]);
        c.Save();
        c.Translate(100 + i * 200, 270);
        c.Scale(2.1f);
        r.Draw(c, P(p => { p.Sit = 1; p.Front = 1; p.PawsUp = 1; p.TailWrap = 1; p.HeadTurn = 0; p.LookX = 0; }));
        c.Restore();
    }
    Save(surface, Path.Combine(outDir, "coats.png"));
}

// Render cost at 125 % DPI in a typical window.
{
    var info = new SKImageInfo(313, 269, SKColorType.Bgra8888, SKAlphaType.Premul);
    using var surface = SKSurface.Create(info);
    var c = surface.Canvas;
    var pose = P(p => { p.GaitAmount = 1; p.HeadTurn = 0.4f; });
    var sw = Stopwatch.StartNew();
    const int frames = 2000;
    for (int i = 0; i < frames; i++)
    {
        pose.GaitPhase = i * 0.1f;
        pose.TailSwayPhase = i * 0.2f;
        c.Clear(SKColors.Transparent);
        c.Save();
        c.Translate(156, 231);
        c.Scale(1.25f);
        renderer.Draw(c, pose);
        c.Restore();
    }
    sw.Stop();
    Console.WriteLine($"Average render: {sw.Elapsed.TotalMilliseconds / frames:F3} ms/frame");
}

Console.WriteLine($"Wrote previews to {outDir}");

static void Save(SKSurface surface, string path)
{
    using var img = surface.Snapshot();
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    using var fs = File.Create(path);
    data.SaveTo(fs);
}

sealed class FlatEnv(Floor floor) : IPetEnvironment
{
    public Floor FloorAt(SKPoint screenPoint) => floor;
}

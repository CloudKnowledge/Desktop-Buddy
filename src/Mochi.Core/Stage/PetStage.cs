using Mochi.Core.Behaviour;
using Mochi.Core.Interaction;
using Mochi.Core.Persistence;
using Mochi.Core.Play;
using Mochi.Core.Rig;
using SkiaSharp;

namespace Mochi.Core.Stage;

/// <summary>
/// Hosts Mochi on one touch screen (the iPhone / iPad app): a little room, touches mapped onto the same gestures
/// the desktop uses, the placement of the kitten's and the ball's views, and their drawing. Units are points.
/// </summary>
public sealed class PetStage : IPetEnvironment, IDisposable
{
    // Same drawing area around the kitten's ground point as the desktop window, in model units.
    const float ModelLeft = -125f, ModelTop = -185f, ModelWidth = 250f, ModelHeight = 215f;
    const float BallHalf = 42f;
    const float FloorGap = 26f;        // points between the kitten's paws and the toolbar
    const float TouchSlop = 8f;        // points a finger may wander before a press becomes a stroke
    const float TouchFuzz = 10f;       // points around the kitten that still count as touching it
    const float MaskScale = 0.5f;      // hit-mask pixels per point
    const double HiAfterSeconds = 120; // away at least this long and Mochi says hi again
    public const string FloorId = "touch";

    enum Touch { None, Room, Pet, Ball }

    readonly PetRenderer _renderer, _hitRenderer;
    readonly YarnBallRenderer _ballRenderer = new();
    readonly PropRenderer _propRenderer = new();
    readonly PointerTracker _tracker = new();
    readonly List<(double T, SKPoint P)> _ballTrail = [];
    Floor _floor = new(0, 390, 0, 760, 1.2f, FloorId);
    SKColor _ballColor;
    SKBitmap? _mask;
    SKRect _maskFrame;
    double _maskAt = double.NegativeInfinity;
    double _lastTick = -1, _lastRender = -1, _lastInputAt, _downTime;
    DateTime? _pausedUtc;
    SKPoint _cursor, _downAt;
    HitRegion _downRegion;
    Touch _touch;
    bool _cursorKnown, _stroking, _grabbed, _ballDragged, _night;
    float _px = 2f;

    public PetStage(MochiSettings settings, PetMemory memory, int seed = 0)
    {
        Settings = settings;
        Memory = memory;
        settings.Normalize();
        memory.Normalize();
        var options = new BrainOptions();
        options.ApplyFrom(settings);
        Brain = new PetBrain(options, new Personality(settings.Personality, memory.TraitDrift), memory, this, seed);
        _renderer = new PetRenderer(settings.BuildLook());
        _hitRenderer = new PetRenderer(settings.BuildLook());
        ApplySettings();
    }

    public PetBrain Brain { get; }
    public MochiSettings Settings { get; }
    public PetMemory Memory { get; }
    public bool Started { get; private set; }
    public SKSize Size { get; private set; }
    public Floor Floor => _floor;
    /// <summary>Evening and night: the lights in the room are low.</summary>
    public bool IsNightRoom => _night;
    /// <summary>Changes whenever the room needs repainting (resized, or day turned to night).</summary>
    public int RoomVersion { get; private set; }
    /// <summary>Where the kitten's view goes this frame (points, snapped to device pixels).</summary>
    public SKRect PetFrame { get; private set; }
    /// <summary>Where the ball's view goes this frame, while <see cref="HasBall"/>.</summary>
    public SKRect BallFrame { get; private set; }
    public bool HasBall => Brain.Ball != null;

    public Floor FloorAt(SKPoint screenPoint) => _floor;

    /// <summary>Pushes settings into the brain and renderers. Safe to call any time.</summary>
    public void ApplySettings()
    {
        Settings.Normalize();
        Brain.Options.ApplyFrom(Settings);
        if (Brain.Personality.Profile != Settings.Personality) Brain.Personality.SetProfile(Settings.Personality);
        var look = Settings.BuildLook();
        _renderer.SetLook(look);
        _hitRenderer.SetLook(look);
        Brain.Effects.Enabled = Settings.ShowEffects;
        if (!Settings.ShowEffects) Brain.Effects.Clear();
        _ballColor = PetLook.AccessoryColors[Settings.AccessoryColorIndex].Color;
    }

    /// <summary>
    /// The screen size and the edges the room must keep clear (status bar, toolbar, home indicator), in points.
    /// The first call puts Mochi in the room and starts it with a greeting.
    /// </summary>
    public void Resize(float width, float height, float left, float top, float right, float bottom, DateTime nowLocal)
    {
        float scale = Math.Clamp(MathF.Min(width, height) / 300f, 1f, 1.8f);
        float floorY = MathF.Max(top + 140f, height - bottom - FloorGap);
        _floor = new Floor(left, MathF.Max(left + 1f, width - right), top, floorY, scale, FloorId);
        Size = new SKSize(width, height);
        SetNight(nowLocal);
        RoomVersion++;
        if (Started) return; // the brain follows the new floor on its next update
        Started = true;
        double fraction = Memory.MonitorId == FloorId && Memory.FloorFraction >= 0 ? Memory.FloorFraction : 0.5;
        Brain.Place(new SKPoint(_floor.Left + (float)fraction * _floor.Width, _floor.Y), Memory.Facing);
        Brain.Start(nowLocal, Memory.LastSeenUtc);
    }

    /// <summary>Advances Mochi's life. <paramref name="now"/> is a monotonic clock in seconds.</summary>
    public void Tick(double now, DateTime nowLocal)
    {
        float dt = _lastTick < 0 ? 0f : (float)Math.Clamp(now - _lastTick, 0, 0.1);
        _lastTick = now;
        if (!Started) return;
        SetNight(nowLocal);

        if (_touch == Touch.Pet && !_stroking && !_grabbed && now - _downTime >= _tracker.LongPressSeconds)
        {
            // Press and hold picks Mochi up.
            _grabbed = true;
            _tracker.Down(_downAt, _downTime, _downRegion);
        }
        _tracker.Tick(now);
        foreach (var g in _tracker.Drain()) Brain.Handle(g);
        if (dt <= 0) return;
        Brain.Update(dt, new Senses
        {
            Cursor = _cursor,
            CursorKnown = _cursorKnown,
            IdleSeconds = Math.Max(0, now - _lastInputAt),
            LocalTime = nowLocal,
        });
    }

    /// <summary>Whether to redraw now: 60 fps while things move, less while Mochi rests or sleeps.</summary>
    public bool ShouldRender(double now)
    {
        if (_lastRender >= 0 && now - _lastRender < 1.0 / TargetFps() - 0.004) return false;
        _lastRender = now;
        return true;
    }

    int TargetFps()
    {
        if (Brain.IsHeld || Brain.Airborne || _touch != Touch.None) return 60;
        if (Brain.Ball is { } ball && (!ball.Resting || ball.State != BallState.Free)) return 60;
        return Brain.State switch
        {
            PetState.Sleeping or PetState.Dreaming => 15,
            PetState.Idle or PetState.Sitting or PetState.LyingDown or PetState.Drowsy or PetState.Eating or PetState.WatchingTv or PetState.Working => 30,
            _ => 60,
        };
    }

    /// <summary>The app went to the background or the screen locked.</summary>
    public void Pause(double now, DateTime utcNow)
    {
        TouchCancelled(now);
        _pausedUtc ??= utcNow;
        if (Started) Brain.Remember();
    }

    /// <summary>The app is back. After a couple of minutes away Mochi says hi again, as when the laptop wakes.</summary>
    public void Resume(double now, DateTime utcNow)
    {
        _lastTick = now;
        _lastInputAt = now;
        _lastRender = -1;
        if (Started && _pausedUtc is { } at && (utcNow - at).TotalSeconds >= HiAfterSeconds) Brain.SayHi();
        _pausedUtc = null;
    }

    void SetNight(DateTime local)
    {
        bool night = local.Hour >= 20 || local.Hour < 6;
        if (night == _night) return;
        _night = night;
        RoomVersion++;
    }

    // ---- touch -----------------------------------------------------------------------------------
    // Tap = click, double tap = double click, press and hold = pick up (then drag and fling),
    // moving a finger over Mochi = stroking (petting). Anywhere else the finger is the "cursor".

    public void TouchBegan(SKPoint p, double t)
    {
        if (!Started) return;
        if (_touch != Touch.None) TouchCancelled(t);
        _lastInputAt = t;
        _cursor = p;
        _cursorKnown = true;
        _downAt = p;
        _downTime = t;
        if (Brain.Ball is { } ball && SKPoint.Distance(p, ball.Position) < MathF.Max(ball.R * 1.8f, 26f))
        {
            _touch = Touch.Ball;
            _ballDragged = false;
            _ballTrail.Clear();
            _ballTrail.Add((t, p));
            Brain.GrabBall(p);
        }
        else if (IsOnPet(p))
        {
            _touch = Touch.Pet;
            _stroking = _grabbed = false;
            _downRegion = Brain.HitTest(p);
        }
        else _touch = Touch.Room;
    }

    public void TouchMoved(SKPoint p, double t)
    {
        if (_touch == Touch.None) return;
        _lastInputAt = t;
        _cursor = p;
        switch (_touch)
        {
            case Touch.Ball:
                _ballTrail.Add((t, p));
                if (_ballTrail.Count > 16) _ballTrail.RemoveAt(0);
                if (SKPoint.Distance(p, _downAt) > 4f) _ballDragged = true;
                Brain.DragBall(p);
                break;
            case Touch.Pet when _grabbed:
                _tracker.Move(p, t, Brain.HitTest(p), Brain.Scale);
                break;
            case Touch.Pet:
                if (!_stroking)
                {
                    if (SKPoint.Distance(p, _downAt) < TouchSlop) break;
                    // Moving before the long press: a stroke, not a pick-up.
                    _stroking = true;
                    _tracker.Move(_downAt, _downTime, _downRegion, Brain.Scale);
                }
                _tracker.Move(p, t, IsOnPet(p) ? Brain.HitTest(p) : HitRegion.None, Brain.Scale);
                break;
        }
    }

    public void TouchEnded(SKPoint p, double t)
    {
        if (_touch == Touch.None) return;
        _lastInputAt = t;
        _cursor = p;
        switch (_touch)
        {
            case Touch.Ball:
                ReleaseBall(p, t);
                break;
            case Touch.Pet when _grabbed:
                _tracker.Up(p, t);
                break;
            case Touch.Pet when _stroking:
                _tracker.Leave(t);
                break;
            case Touch.Pet:
                // A tap: the same click / double click / rapid-click streak as on the desktop.
                _tracker.Down(_downAt, _downTime, _downRegion);
                _tracker.Up(p, t);
                break;
        }
        _touch = Touch.None;
    }

    public void TouchCancelled(double t)
    {
        switch (_touch)
        {
            case Touch.Ball:
                Brain.ReleaseBall(default);
                break;
            case Touch.Pet when _grabbed:
                _tracker.Cancel(t);
                break;
            case Touch.Pet when _stroking:
                _tracker.Leave(t);
                break;
        }
        _touch = Touch.None;
    }

    void ReleaseBall(SKPoint p, double t)
    {
        _ballTrail.Add((t, p));
        if (!_ballDragged && t - _downTime < 0.4)
        {
            // A tap throws the ball for Mochi to catch.
            Brain.ReleaseBall(default);
            Brain.ThrowBall();
            return;
        }
        // Throw with the finger's velocity over the last ~80 ms.
        var last = _ballTrail[^1];
        var first = last;
        foreach (var s in _ballTrail)
        {
            if (last.T - s.T > 0.08) continue;
            first = s;
            break;
        }
        double span = last.T - first.T;
        Brain.ReleaseBall(span > 0.005 ? new SKPoint((float)((last.P.X - first.P.X) / span), (float)((last.P.Y - first.P.Y) / span)) : default);
    }

    /// <summary>Whether a point is on the kitten itself rather than the empty space around it.</summary>
    public bool IsOnPet(SKPoint p)
    {
        if (!Started) return false;
        var frame = PetBounds();
        if (!SKRect.Inflate(frame, TouchFuzz, TouchFuzz).Contains(p)) return false;
        UpdateMask(frame);
        var mask = _mask!;
        for (int i = 0; i <= 8; i++)
        {
            float r = i == 0 ? 0f : TouchFuzz, a = i * MathF.PI / 4f;
            int x = (int)((p.X + r * MathF.Cos(a) - _maskFrame.Left) * MaskScale);
            int y = (int)((p.Y + r * MathF.Sin(a) - _maskFrame.Top) * MaskScale);
            if (x >= 0 && y >= 0 && x < mask.Width && y < mask.Height && mask.GetPixel(x, y).Alpha > 60) return true;
        }
        return false;
    }

    // A small silhouette of the kitten, redrawn at most once per update, for per-pixel touch tests.
    void UpdateMask(SKRect frame)
    {
        if (_mask != null && _maskAt == Brain.Now) return;
        _maskAt = Brain.Now;
        _maskFrame = frame;
        int w = Math.Max(1, (int)MathF.Ceiling(frame.Width * MaskScale)), h = Math.Max(1, (int)MathF.Ceiling(frame.Height * MaskScale));
        if (_mask == null || _mask.Width != w || _mask.Height != h)
        {
            _mask?.Dispose();
            _mask = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        }
        using var c = new SKCanvas(_mask);
        c.Clear(SKColors.Transparent);
        c.Scale(MaskScale);
        DrawKitten(c, frame, extras: false);
    }

    // ---- layout and drawing ------------------------------------------------------------------------

    SKRect PetBounds()
    {
        float s = Brain.Scale;
        var pos = Brain.Position;
        return new SKRect(pos.X + ModelLeft * s, pos.Y + ModelTop * s, pos.X + (ModelLeft + ModelWidth) * s, pos.Y + (ModelTop + ModelHeight) * s);
    }

    /// <summary>Places the kitten's and the ball's views for this frame, snapped to whole device pixels.</summary>
    public void Layout(float pixelScale)
    {
        _px = MathF.Max(1f, pixelScale);
        PetFrame = Snap(PetBounds());
        if (Brain.Ball is { } b)
        {
            float h = BallHalf * b.Scale;
            BallFrame = Snap(new SKRect(b.Position.X - h, b.Position.Y - h, b.Position.X + h, b.Position.Y + h));
        }
    }

    SKRect Snap(SKRect r)
    {
        float l = MathF.Floor(r.Left * _px) / _px, t = MathF.Floor(r.Top * _px) / _px;
        return new SKRect(l, t, l + MathF.Ceiling(r.Width * _px) / _px, t + MathF.Ceiling(r.Height * _px) / _px);
    }

    /// <summary>Draws the kitten, its prop and effects into the cleared canvas of a view placed at <see cref="PetFrame"/>.</summary>
    public void DrawPet(SKCanvas c, float pixelScale)
    {
        c.Save();
        c.Scale(pixelScale);
        DrawKitten(c, PetFrame, extras: true);
        c.Restore();
    }

    /// <summary>Draws the yarn ball into the cleared canvas of a view placed at <see cref="BallFrame"/>.</summary>
    public void DrawBall(SKCanvas c, float pixelScale)
    {
        if (Brain.Ball is not { } ball) return;
        c.Save();
        c.Scale(pixelScale);
        c.Translate(ball.Position.X - BallFrame.Left, ball.Position.Y - BallFrame.Top);
        c.Scale(ball.Scale);
        _ballRenderer.Draw(c, ball, _ballColor);
        c.Restore();
    }

    // Origin of the canvas = top-left of `frame`, in points.
    void DrawKitten(SKCanvas c, SKRect frame, bool extras)
    {
        var pos = Brain.Position;
        c.Save();
        c.Translate(pos.X - frame.Left, pos.Y - frame.Top);
        c.Scale(Brain.Scale);
        c.Save();
        c.Scale(Brain.Facing, 1f);
        (extras ? _renderer : _hitRenderer).Draw(c, Brain.Animator.Output);
        if (extras && Brain.Prop is { } prop) _propRenderer.Draw(c, prop);
        c.Restore();
        if (extras)
        {
            var head = Brain.HeadModelUnflipped;
            Brain.Effects.Draw(c, new SKPoint(head.X, head.Y - 30f));
        }
        c.Restore();
    }

    /// <summary>Paints the room (wall, window, curtains, floor, rug). Repaint when <see cref="RoomVersion"/> changes.</summary>
    public void DrawRoom(SKCanvas c, float pixelScale)
    {
        float w = Size.Width, h = Size.Height;
        if (w <= 0 || h <= 0) return;
        bool night = _night;
        float k = _floor.DpiScale;
        float horizon = _floor.Y - 26f * k; // where the wall meets the floor, behind the kitten
        c.Save();
        c.Scale(pixelScale);

        Gradient(c, new SKRect(0, 0, w, horizon), night ? new SKColor(0x2B, 0x27, 0x42) : new SKColor(0xFF, 0xF6, 0xEA),
            night ? new SKColor(0x3C, 0x35, 0x56) : new SKColor(0xFB, 0xE2, 0xCB));
        DrawWindow(c, w, horizon, night);

        using var paint = new SKPaint { IsAntialias = true };
        paint.Color = night ? new SKColor(0x4B, 0x44, 0x68) : new SKColor(0xFF, 0xFD, 0xF8);
        c.DrawRect(0, horizon - 9f, w, 9f, paint);
        paint.Color = night ? new SKColor(0x22, 0x1F, 0x35) : new SKColor(0xE9, 0xCF, 0xB6);
        c.DrawRect(0, horizon - 10f, w, 1.2f, paint);

        Gradient(c, new SKRect(0, horizon, w, h), night ? new SKColor(0x5B, 0x46, 0x35) : new SKColor(0xE8, 0xB9, 0x87),
            night ? new SKColor(0x3F, 0x30, 0x26) : new SKColor(0xCD, 0x93, 0x5F));
        // Floorboards, a little wider apart nearer the viewer.
        paint.Color = new SKColor(0x5A, 0x34, 0x18, night ? (byte)50 : (byte)36);
        float y = horizon, gap = 9f;
        for (int row = 0; y < h; row++)
        {
            c.DrawRect(0, y, w, 1f, paint);
            for (float x = (row * 97 % 160) - 160f; x < w; x += 160f) c.DrawRect(x, y, 1f, gap, paint);
            y += gap;
            gap *= 1.22f;
        }

        // A soft round rug in the middle of the floor.
        float rx = MathF.Min(w * 0.36f, 230f), ry = 12f * k;
        paint.Color = night ? new SKColor(0x6E, 0x5E, 0x99, 210) : new SKColor(0xF2, 0xB4, 0xAE, 225);
        c.DrawOval(new SKPoint(w / 2f, _floor.Y + 3f), new SKSize(rx, ry), paint);
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 1.5f;
        paint.Color = new SKColor(0xFF, 0xFF, 0xFF, night ? (byte)60 : (byte)140);
        c.DrawOval(new SKPoint(w / 2f, _floor.Y + 3f), new SKSize(rx - 9f, ry - 4f), paint);
        c.Restore();
    }

    void DrawWindow(SKCanvas c, float w, float horizon, bool night)
    {
        float wallTop = _floor.Top, space = horizon - wallTop;
        float ww = MathF.Min(w * 0.52f, 300f), wh = ww * 0.72f;
        if (wh > space * 0.42f) { wh = space * 0.42f; ww = wh / 0.72f; }
        float wx = (w - ww) / 2f, wy = wallTop + space * 0.2f + 10f;
        var frameColor = night ? new SKColor(0x8E, 0x86, 0xB5) : SKColors.White;
        using var paint = new SKPaint { IsAntialias = true, Color = frameColor };

        // Rod and curtains behind the frame's edges.
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 4f;
        paint.StrokeCap = SKStrokeCap.Round;
        paint.Color = night ? new SKColor(0x7A, 0x6F, 0xA8) : new SKColor(0xC9, 0xA2, 0x7E);
        c.DrawLine(wx - 34f, wy - 18f, wx + ww + 34f, wy - 18f, paint);
        paint.Style = SKPaintStyle.Fill;

        c.DrawRoundRect(new SKRect(wx - 7f, wy - 7f, wx + ww + 7f, wy + wh + 7f), 10f, 10f, Paint(paint, frameColor));
        var glass = new SKRect(wx, wy, wx + ww, wy + wh);
        c.Save();
        c.ClipRoundRect(new SKRoundRect(glass, 5f), antialias: true);
        Gradient(c, glass, night ? new SKColor(0x14, 0x1C, 0x3A) : new SKColor(0x9F, 0xD6, 0xFF),
            night ? new SKColor(0x2E, 0x3B, 0x6B) : new SKColor(0xE3, 0xF4, 0xFF));
        if (night)
        {
            var rng = new Random(7);
            for (int i = 0; i < 22; i++)
            {
                paint.Color = new SKColor(0xFF, 0xFF, 0xFF, (byte)rng.Next(120, 255));
                c.DrawCircle(wx + (float)rng.NextDouble() * ww, wy + (float)rng.NextDouble() * wh * 0.8f, 0.7f + (float)rng.NextDouble(), paint);
            }
            var moon = new SKPoint(wx + ww * 0.74f, wy + wh * 0.3f);
            float mr = wh * 0.13f;
            c.DrawCircle(moon, mr, Paint(paint, new SKColor(0xFF, 0xF2, 0xC6)));
            c.DrawCircle(moon.X + mr * 0.45f, moon.Y - mr * 0.25f, mr * 0.85f, Paint(paint, new SKColor(0x1D, 0x27, 0x4C)));
        }
        else
        {
            c.DrawCircle(wx + ww * 0.78f, wy + wh * 0.26f, wh * 0.2f, Paint(paint, new SKColor(0xFF, 0xF4, 0xC2, 150)));
            c.DrawCircle(wx + ww * 0.78f, wy + wh * 0.26f, wh * 0.11f, Paint(paint, new SKColor(0xFF, 0xF4, 0xC2)));
            paint.Color = SKColors.White;
            foreach (var (cx, cy, s) in new[] { (0.25f, 0.42f, 1f), (0.55f, 0.7f, 0.8f) })
            {
                float x = wx + ww * cx, y = wy + wh * cy, r = wh * 0.09f * s;
                c.DrawCircle(x, y, r, paint);
                c.DrawCircle(x + r * 1.1f, y - r * 0.4f, r * 1.25f, paint);
                c.DrawCircle(x + r * 2.3f, y, r * 0.95f, paint);
                c.DrawRect(x, y, r * 2.3f, r * 0.95f, paint);
            }
        }
        c.Restore();
        paint.Color = frameColor;
        c.DrawRect(wx + ww / 2f - 2.5f, wy, 5f, wh, paint);
        c.DrawRect(wx, wy + wh / 2f - 2.5f, ww, 5f, paint);
        c.DrawRoundRect(new SKRect(wx - 16f, wy + wh + 5f, wx + ww + 16f, wy + wh + 14f), 4f, 4f,
            Paint(paint, night ? new SKColor(0x7C, 0x74, 0xA3) : new SKColor(0xF3, 0xE9, 0xDF)));

        // Curtains tied back on both sides.
        var cloth = night ? new SKColor(0x5D, 0x4E, 0x86) : new SKColor(0xF4, 0xB8, 0xA6);
        foreach (int side in new[] { -1, 1 })
        {
            float edge = side < 0 ? wx : wx + ww;
            using var b = new SKPathBuilder();
            b.MoveTo(edge - side * 30f, wy - 18f);
            b.LineTo(edge + side * ww * 0.17f, wy - 18f);
            b.QuadTo(edge + side * ww * 0.09f, wy + wh * 0.45f, edge - side * 2f, wy + wh * 0.7f);
            b.QuadTo(edge + side * 6f, wy + wh * 0.92f, edge + side * ww * 0.05f, wy + wh + 36f);
            b.LineTo(edge - side * 30f, wy + wh + 36f);
            b.Close();
            using var path = b.Detach();
            c.DrawPath(path, Paint(paint, cloth));
            c.DrawOval(new SKPoint(edge - side * 2f, wy + wh * 0.7f), new SKSize(7f, 4f), Paint(paint, Rig.PetRenderer.Mix(cloth, SKColors.Black, 0.25f)));
        }
    }

    static SKPaint Paint(SKPaint p, SKColor color)
    {
        p.Color = color;
        return p;
    }

    static void Gradient(SKCanvas c, SKRect r, SKColor top, SKColor bottom)
    {
        using var shader = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom), [top, bottom], null, SKShaderTileMode.Clamp);
        using var p = new SKPaint { Shader = shader };
        c.DrawRect(r, p);
    }

    public void Dispose()
    {
        _renderer.Dispose();
        _hitRenderer.Dispose();
        _ballRenderer.Dispose();
        _propRenderer.Dispose();
        _mask?.Dispose();
    }
}

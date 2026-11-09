using Mochi.Core.Behaviour;
using SkiaSharp;

namespace Mochi.Core.Interaction;

public enum GestureKind { Click, DoubleClick, GrabStart, DragMove, Release, PetStroke, HoverEnter, HoverLeave }

/// <param name="Screen">Pointer position in physical screen pixels.</param>
/// <param name="Velocity">Pointer velocity in px/s (for releases / throws).</param>
/// <param name="Streak">Number of clicks in the current rapid-click streak.</param>
/// <param name="Strength">0..1 intensity of a petting stroke.</param>
public readonly record struct Gesture(GestureKind Kind, SKPoint Screen, SKPoint Velocity, int Streak, HitRegion Region, float Strength);

/// <summary>
/// Converts raw pointer events over the pet into gestures. Distinguishes a click from a drag, a long press
/// (pick up) from a click, a double click from a rapid-click streak, and a gentle stroke (petting) from a
/// cursor merely passing by.
/// </summary>
public sealed class PointerTracker
{
    public double DoubleClickSeconds { get; set; } = 0.45;
    public double StreakGapSeconds { get; set; } = 0.75;
    public double LongPressSeconds { get; set; } = 0.42;
    /// <summary>Movement (in model units) before a press becomes a drag.</summary>
    public float DragThreshold { get; set; } = 5f;

    readonly List<Gesture> _out = [];
    readonly Queue<(double T, SKPoint P)> _samples = new();
    readonly Queue<(double T, float Dist, int Dir)> _strokes = new();

    bool _pressed, _grabbing, _hovering, _pairOpen;
    SKPoint _downPos;
    double _downTime;
    HitRegion _downRegion;
    double _lastClickTime = double.NegativeInfinity;
    SKPoint _lastClickPos;
    int _streak;
    SKPoint _lastHoverPos;
    double _lastHoverTime;
    double _lastStrokeEmit;

    public bool IsPressed => _pressed;
    public bool IsGrabbing => _grabbing;

    /// <summary>Collects gestures produced since the last call.</summary>
    public IReadOnlyList<Gesture> Drain()
    {
        var copy = _out.ToArray();
        _out.Clear();
        return copy;
    }

    public void Down(SKPoint p, double t, HitRegion region)
    {
        _pressed = true;
        _grabbing = false;
        _downPos = p;
        _downTime = t;
        _downRegion = region;
        _samples.Clear();
        _samples.Enqueue((t, p));
    }

    /// <param name="scale">Physical pixels per model unit, so thresholds feel the same at every size/DPI.</param>
    public void Move(SKPoint p, double t, HitRegion region, float scale)
    {
        if (_pressed)
        {
            AddSample(t, p);
            if (!_grabbing && Len(p - _downPos) > DragThreshold * scale)
            {
                _grabbing = true;
                Emit(GestureKind.GrabStart, _downPos, default, _downRegion);
            }
            if (_grabbing) Emit(GestureKind.DragMove, p, default, region);
            return;
        }

        if (!_hovering)
        {
            _hovering = true;
            _lastHoverPos = p;
            _lastHoverTime = t;
            Emit(GestureKind.HoverEnter, p, default, region);
            return;
        }

        TrackStroke(p, t, region, scale);
    }

    public void Up(SKPoint p, double t)
    {
        if (!_pressed) return;
        _pressed = false;
        AddSample(t, p);
        if (_grabbing)
        {
            _grabbing = false;
            Emit(GestureKind.Release, p, Velocity(t), _downRegion);
            return;
        }

        // Only the second click of a pair is a double click; a third rapid click starts a new pair.
        bool isDouble = _pairOpen && t - _lastClickTime <= DoubleClickSeconds && Len(p - _lastClickPos) < 10f;
        _pairOpen = !isDouble;
        _streak = t - _lastClickTime <= StreakGapSeconds ? _streak + 1 : 1;
        _lastClickTime = t;
        _lastClickPos = p;
        Emit(isDouble ? GestureKind.DoubleClick : GestureKind.Click, p, default, _downRegion, streak: _streak);
    }

    /// <summary>Pointer left the pet (or capture was lost).</summary>
    public void Leave(double t)
    {
        if (_hovering) Emit(GestureKind.HoverLeave, _lastHoverPos, default, HitRegion.None);
        _hovering = false;
        _strokes.Clear();
    }

    /// <summary>Call every frame: turns a still press into a pick-up after <see cref="LongPressSeconds"/>.</summary>
    public void Tick(double t)
    {
        if (_pressed && !_grabbing && t - _downTime >= LongPressSeconds)
        {
            _grabbing = true;
            Emit(GestureKind.GrabStart, _downPos, default, _downRegion);
        }
    }

    /// <summary>Abort an in-flight press (e.g. focus lost) — releases a held pet in place.</summary>
    public void Cancel(double t)
    {
        if (_pressed && _grabbing) Emit(GestureKind.Release, _downPos, default, _downRegion);
        _pressed = _grabbing = false;
    }

    void TrackStroke(SKPoint p, double t, HitRegion region, float scale)
    {
        var d = p - _lastHoverPos;
        double dt = Math.Max(1e-3, t - _lastHoverTime);
        _lastHoverPos = p;
        _lastHoverTime = t;
        if (region == HitRegion.None) return;

        float dist = Len(d) / Math.Max(0.1f, scale);         // model units
        float speed = (float)(dist / dt);                       // model units per second
        if (dist < 0.3f || speed > 1400f) { if (speed > 1400f) _strokes.Clear(); return; }

        int dir = MathF.Abs(d.X) >= MathF.Abs(d.Y) ? Math.Sign(d.X) : 2 * Math.Sign(d.Y);
        _strokes.Enqueue((t, dist, dir));
        while (_strokes.Count > 0 && t - _strokes.Peek().T > 1.4) _strokes.Dequeue();

        float total = 0;
        int reversals = 0, prev = 0;
        foreach (var s in _strokes)
        {
            total += s.Dist;
            if (prev != 0 && s.Dir == -prev) reversals++;
            if (s.Dir != 0) prev = s.Dir;
        }

        // A stroke is gentle back-and-forth movement over Mochi, not a cursor zooming past.
        if (total > 45f && reversals >= 1 && t - _lastStrokeEmit > 0.25)
        {
            _lastStrokeEmit = t;
            float strength = Math.Clamp(total / 160f, 0.2f, 1f);
            Emit(GestureKind.PetStroke, p, default, region, strength: strength);
        }
    }

    void AddSample(double t, SKPoint p)
    {
        _samples.Enqueue((t, p));
        while (_samples.Count > 2 && t - _samples.Peek().T > 0.09) _samples.Dequeue();
    }

    SKPoint Velocity(double now)
    {
        if (_samples.Count < 2) return default;
        var first = _samples.Peek();
        var last = _samples.Last();
        double dt = last.T - first.T;
        if (dt < 0.008 || now - last.T > 0.12) return default;
        return new SKPoint((float)((last.P.X - first.P.X) / dt), (float)((last.P.Y - first.P.Y) / dt));
    }

    void Emit(GestureKind kind, SKPoint p, SKPoint v, HitRegion region, int streak = 0, float strength = 0) =>
        _out.Add(new Gesture(kind, p, v, streak, region, strength));

    static float Len(SKPoint v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y);
}

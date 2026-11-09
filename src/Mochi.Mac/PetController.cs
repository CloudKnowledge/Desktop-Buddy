using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using AppKit;
using Foundation;
using Mochi.Core.Behaviour;
using Mochi.Core.Interaction;
using Mochi.Core.Persistence;
using Mochi.Core.Play;
using Mochi.Core.Rig;
using Mochi.Mac.Platform;
using SkiaSharp;

namespace Mochi.Mac;

/// <summary>
/// Runs Mochi on the Mac desktop: one frame loop that reads the pointer and the idle timer, updates the brain,
/// and renders into the floating windows — only as often as the current behaviour needs.
/// </summary>
internal sealed class PetController : IDisposable
{
    // Area around the anchor (ground point) that the window covers, in model units.
    const float ModelLeft = -125f, ModelTop = -185f, ModelWidth = 250f, ModelHeight = 215f;
    // Half-size of the yarn ball's window around its centre (ball, loose strand and shadow), in model units.
    const float BallHalf = 42f;

    readonly MochiSettings _settings;
    readonly PetMemory _memory;
    readonly string _memoryPath;
    readonly Screens _screens;
    readonly PetRenderer _renderer;
    readonly PetWindow _window;
    readonly PointerTracker _tracker = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly List<(NSNotificationCenter Center, NSObject Token)> _observers = [];
    NSTimer? _timer;
    double _interval;
    double _lastTick, _lastRender = -1, _lastTopmost, _lastSlowCheck, _lastSave;
    bool _userHidden, _locked, _displayOff, _disposed, _hovering, _pressed;
    SKPoint _lastCursor;
    int _errors;
    bool? _loginApplied;

    // The yarn ball lives in its own small window so it can fly anywhere on the screen.
    PetWindow? _ballWindow;
    readonly YarnBallRenderer _ballRenderer = new();
    readonly PropRenderer _propRenderer = new();
    readonly List<(double T, SKPoint P)> _ballTrail = [];
    SKColor _ballColor;
    SKPoint _ballDownAt;
    double _ballDownTime;
    bool _ballPressed, _ballDragged;

    // Saying "Hi!" when the Mac wakes up or you come back to it.
    bool _sayHi;
    double _lockedAt = -1, _displayOffAt = -1;

    public event Action<SKPoint>? ContextMenuRequested;
    public PetBrain Brain { get; }
    public bool IsHidden => _userHidden;

    public PetController(MochiSettings settings, PetMemory memory, string memoryPath, Screens screens)
    {
        _settings = settings;
        _memory = memory;
        _memoryPath = memoryPath;
        _screens = screens;

        var options = new BrainOptions();
        options.ApplyFrom(settings);
        Brain = new PetBrain(options, new Personality(settings.Personality, memory.TraitDrift), memory, screens);
        _renderer = new PetRenderer(settings.BuildLook());
        _window = new PetWindow("Mochi");

        _window.LeftDown += p =>
        {
            _pressed = true;
            _tracker.Down(p, Now, Brain.HitTest(p));
        };
        _window.LeftUp += p =>
        {
            if (!_pressed) return;
            _pressed = false;
            _tracker.Up(p, Now);
        };
        _window.RightDown += p => ContextMenuRequested?.Invoke(p);
        _tracker.DoubleClickSeconds = NSEvent.DoubleClickInterval;

        var floor = screens.ById(memory.MonitorId) ?? screens.Primary;
        float fraction = memory.FloorFraction >= 0 ? (float)memory.FloorFraction : 0.78f;
        Brain.Place(new SKPoint(floor.Left + fraction * floor.Width, floor.Y), memory.Facing);
        // The Mac may still be signing in (Mochi can open at login): save the "Hi!" until you're at the computer.
        Brain.Start(DateTime.Now, memory.LastSeenUtc, greetNow: false);

        ApplySettings();
        Observe(NSWorkspace.SharedWorkspace.NotificationCenter, NSWorkspace.WillSleepNotification, _ => SaveMemory());
        Observe(NSWorkspace.SharedWorkspace.NotificationCenter, NSWorkspace.DidWakeNotification, _ =>
        {
            _lastTick = Now;
            _sayHi = true; // the Mac just woke up
        });
        Observe(NSWorkspace.SharedWorkspace.NotificationCenter, NSWorkspace.ScreensDidSleepNotification, _ => SetDisplayOn(false));
        Observe(NSWorkspace.SharedWorkspace.NotificationCenter, NSWorkspace.ScreensDidWakeNotification, _ => SetDisplayOn(true));
        Observe(NSDistributedNotificationCenter.DefaultCenter, new NSString("com.apple.screenIsLocked"), _ => SetLocked(true));
        Observe(NSDistributedNotificationCenter.DefaultCenter, new NSString("com.apple.screenIsUnlocked"), _ => SetLocked(false));
        Observe(NSNotificationCenter.DefaultCenter, NSApplication.DidChangeScreenParametersNotification, _ =>
        {
            _screens.Refresh();
            _lastRender = -1;
        });
    }

    double Now => _clock.Elapsed.TotalSeconds;

    void Observe(NSNotificationCenter center, NSString name, Action<NSNotification> handler) =>
        _observers.Add((center, center.AddObserver(name, handler)));

    public void Start()
    {
        _lastTick = Now;
        UpdateVisibility();
        Tick();
        Log.Info("Mochi started.");
    }

    /// <summary>Pushes settings into the brain, renderer and windows. Safe to call any time.</summary>
    public void ApplySettings()
    {
        _settings.Normalize();
        Brain.Options.ApplyFrom(_settings);
        if (Brain.Personality.Profile != _settings.Personality) Brain.Personality.SetProfile(_settings.Personality);
        _renderer.SetLook(_settings.BuildLook());
        Brain.Effects.Enabled = _settings.ShowEffects;
        if (!_settings.ShowEffects) Brain.Effects.Clear();
        foreach (var w in new[] { _window, _ballWindow })
        {
            w?.SetClickThrough(_settings.ClickThrough);
            w?.SetExcludedFromCapture(_settings.HideFromScreenCapture);
            w?.SetFullScreenSpaces(!_settings.HideInFullscreen);
        }
        _ballColor = PetLook.AccessoryColors[_settings.AccessoryColorIndex].Color;

        if (_loginApplied != _settings.StartWithWindows)
        {
            try
            {
                if (LoginItem.IsEnabled() != _settings.StartWithWindows) LoginItem.Set(_settings.StartWithWindows);
                _loginApplied = _settings.StartWithWindows;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjCRuntime.ObjCException)
            {
                Log.Error("Could not update the login item", ex);
            }
        }
        _lastRender = -1;
    }

    public void SetHidden(bool hidden)
    {
        _userHidden = hidden;
        UpdateVisibility();
    }

    void SetLocked(bool locked)
    {
        if (locked) _lockedAt = Now;
        else if (_locked && _lockedAt >= 0 && Now - _lockedAt > 120) _sayHi = true;
        _locked = locked;
        UpdateVisibility();
    }

    void SetDisplayOn(bool on)
    {
        if (!on) _displayOffAt = Now;
        else if (_displayOff && _displayOffAt >= 0 && Now - _displayOffAt > 120) _sayHi = true;
        _displayOff = !on;
        UpdateVisibility();
    }

    void UpdateVisibility()
    {
        bool visible = !_userHidden && !_locked && !_displayOff;
        if (visible && !_window.IsVisible)
        {
            _lastTick = Now;
            _lastRender = -1;
            Render();
            _window.Show();
            _ballWindow?.Show();
        }
        else if (!visible && _window.IsVisible)
        {
            CancelPointer();
            _window.Hide();
            _ballWindow?.Hide();
        }
        Schedule(visible ? 1 / 60.0 : 0.5);
    }

    void CancelPointer()
    {
        _pressed = false;
        _hovering = false;
        _tracker.Cancel(Now);
        if (_ballPressed) ReleaseBall(null);
        _window.SetInteractive(false);
        _ballWindow?.SetInteractive(false);
    }

    // Common modes keep Mochi moving while a menu is open.
    void Schedule(double seconds)
    {
        if (_disposed || (_timer != null && Math.Abs(_interval - seconds) < 1e-6)) return;
        _timer?.Invalidate();
        _interval = seconds;
        _timer = NSTimer.CreateRepeatingTimer(seconds, _ => Tick());
        _timer.Tolerance = seconds * 0.1;
        NSRunLoop.Main.AddTimer(_timer, NSRunLoopMode.Common);
    }

    void Tick()
    {
        double now = Now;
        float dt = (float)Math.Min(0.1, now - _lastTick);
        _lastTick = now;
        try
        {
            if (now - _lastSlowCheck > 1.0)
            {
                _lastSlowCheck = now;
                _screens.RefreshIfStale();
            }
            if (!_window.IsVisible)
            {
                Schedule(0.5); // hidden: almost no work
                return;
            }

            var cursor = Screens.Cursor();
            UpdatePointer(cursor, now);
            var senses = new Senses
            {
                Cursor = cursor,
                CursorKnown = true,
                IdleSeconds = SystemSignals.IdleSeconds(),
                LocalTime = DateTime.Now,
            };
            _tracker.Tick(now);
            foreach (var g in _tracker.Drain()) Brain.Handle(g);
            if (_sayHi)
            {
                _sayHi = false;
                Brain.SayHi();
            }
            if (Brain.GreetPending && ((now > 4 && senses.IdleSeconds < 1.5) || now > 45)) Brain.GreetNow();
            Brain.Update(dt, senses);
            SyncBallWindow();

            int fps = TargetFps();
            Schedule(fps >= 60 ? 1 / 60.0 : fps >= 30 ? 1 / 30.0 : 1 / 16.0);
            if (_lastRender < 0 || now - _lastRender >= 1.0 / fps - 0.006)
            {
                _lastRender = now;
                Render();
            }
            if (now - _lastTopmost > 2.5)
            {
                _lastTopmost = now;
                _window.KeepOnTop();
                _ballWindow?.KeepOnTop(); // the ball stays in front of the kitten (it's carried in the mouth)
            }
            if (now - _lastSave > 60)
            {
                _lastSave = now;
                SaveMemory();
            }
            _errors = 0;
        }
        catch (Exception ex)
        {
            Log.Error("Frame failed", ex);
            // Something is persistently wrong; slow down rather than spin.
            if (++_errors > 30) Schedule(1.0);
        }
    }

    // AppKit only sends mouse moves to the active app, so the pointer is polled: hovering over Mochi is petting,
    // and the windows take clicks only where Mochi is drawn.
    void UpdatePointer(SKPoint cursor, double now)
    {
        bool moved = cursor != _lastCursor;
        _lastCursor = cursor;
        bool overPet = !_settings.ClickThrough && _window.Covers(cursor);
        _window.SetInteractive(overPet || _pressed);
        _ballWindow?.SetInteractive(_ballPressed || (!_settings.ClickThrough && _ballWindow.Covers(cursor)));

        if (overPet || _pressed)
        {
            if (moved || !_hovering) _tracker.Move(cursor, now, Brain.HitTest(cursor), Brain.Scale);
            _hovering = true;
        }
        else if (_hovering)
        {
            _hovering = false;
            _tracker.Leave(now);
        }

        if (_ballPressed && moved)
        {
            _ballTrail.Add((now, cursor));
            if (_ballTrail.Count > 16) _ballTrail.RemoveAt(0);
            if (SKPoint.Distance(cursor, _ballDownAt) > 4f) _ballDragged = true;
            Brain.DragBall(cursor);
        }
    }

    int TargetFps()
    {
        int max = _settings.MaxFps;
        if (Brain.IsHeld || Brain.Airborne || _hovering || _tracker.IsPressed || _ballPressed) return max;
        if (Brain.Ball is { } ball && (!ball.Resting || ball.State != BallState.Free)) return max;
        return Brain.State switch
        {
            PetState.Sleeping or PetState.Dreaming => 16,
            PetState.Idle or PetState.Sitting or PetState.LyingDown or PetState.Drowsy or PetState.Eating or PetState.WatchingTv or PetState.Working => Math.Min(max, 30),
            _ => max,
        };
    }

    void Render()
    {
        float scale = Brain.Scale;
        var pos = Brain.Position;
        float px = _window.BackingScale;
        var frame = Snap(new SKRect(pos.X + ModelLeft * scale, pos.Y + ModelTop * scale,
            pos.X + (ModelLeft + ModelWidth) * scale, pos.Y + (ModelTop + ModelHeight) * scale), px);
        var c = _window.BeginFrame(frame, px);
        // Sub-pixel placement keeps slow walking perfectly smooth.
        c.Translate(pos.X - frame.Left, pos.Y - frame.Top);
        c.Scale(scale);
        c.Save();
        c.Scale(Brain.Facing, 1f);
        _renderer.Draw(c, Brain.Animator.Output);
        if (Brain.Prop is { } prop) _propRenderer.Draw(c, prop);
        c.Restore();
        var head = Brain.HeadModelUnflipped;
        Brain.Effects.Draw(c, new SKPoint(head.X, head.Y - 30f));
        _window.EndFrame();
        RenderBall();
    }

    static SKRect Snap(SKRect r, float px)
    {
        float l = MathF.Floor(r.Left * px) / px, t = MathF.Floor(r.Top * px) / px;
        return new SKRect(l, t, l + MathF.Ceiling(r.Width * px) / px, t + MathF.Ceiling(r.Height * px) / px);
    }

    // ---- the yarn ball -------------------------------------------------------------------------

    /// <summary>Creates the ball's window when a ball comes out and destroys it when it's put away.</summary>
    void SyncBallWindow()
    {
        if (Brain.Ball == null)
        {
            if (_ballWindow == null) return;
            _ballPressed = false;
            _ballWindow.Dispose();
            _ballWindow = null;
            return;
        }
        if (_ballWindow != null) return;

        var w = new PetWindow("Mochi's yarn ball");
        w.LeftDown += p =>
        {
            _ballPressed = true;
            _ballDragged = false;
            _ballDownAt = p;
            _ballDownTime = Now;
            _ballTrail.Clear();
            _ballTrail.Add((Now, p));
            Brain.GrabBall(p);
        };
        w.LeftUp += p => ReleaseBall(p);
        w.RightDown += p => ContextMenuRequested?.Invoke(p);
        w.SetClickThrough(_settings.ClickThrough);
        w.SetExcludedFromCapture(_settings.HideFromScreenCapture);
        w.SetFullScreenSpaces(!_settings.HideInFullscreen);
        _ballWindow = w;
        if (_window.IsVisible)
        {
            RenderBall();
            w.Show();
        }
    }

    void ReleaseBall(SKPoint? at)
    {
        if (!_ballPressed) return;
        _ballPressed = false;
        if (at is { } p) _ballTrail.Add((Now, p));
        if (!_ballDragged && Now - _ballDownTime < 0.4)
        {
            // A click throws the ball for Mochi to catch.
            Brain.ReleaseBall(default);
            Brain.ThrowBall();
            return;
        }
        // Throw with the pointer's velocity over the last ~80 ms.
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

    void RenderBall()
    {
        if (_ballWindow == null || Brain.Ball is not { } ball) return;
        float scale = ball.Scale, half = BallHalf * scale, px = _ballWindow.BackingScale;
        var frame = Snap(new SKRect(ball.Position.X - half, ball.Position.Y - half, ball.Position.X + half, ball.Position.Y + half), px);
        var c = _ballWindow.BeginFrame(frame, px);
        c.Translate(ball.Position.X - frame.Left, ball.Position.Y - frame.Top);
        c.Scale(scale);
        _ballRenderer.Draw(c, ball, _ballColor);
        _ballWindow.EndFrame();
    }

    public void SaveMemory()
    {
        try
        {
            Brain.Remember();
            JsonStore.Save(_memoryPath, _memory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Could not save memory", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Invalidate();
        foreach (var (center, token) in _observers) center.RemoveObserver(token);
        SaveMemory();
        _ballWindow?.Dispose();
        _ballRenderer.Dispose();
        _propRenderer.Dispose();
        _window.Dispose();
        _renderer.Dispose();
        Brain.Effects.Dispose();
    }
}

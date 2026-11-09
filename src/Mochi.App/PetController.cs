using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Mochi.App.Platform;
using Mochi.Core.Behaviour;
using Mochi.Core.Interaction;
using Mochi.Core.Persistence;
using Mochi.Core.Play;
using Mochi.Core.Rig;
using SkiaSharp;

namespace Mochi.App;

/// <summary>
/// Runs Mochi on the desktop: one frame loop that reads the cursor and the OS idle timer, updates the
/// brain, and renders into the layered window — only as often as the current behaviour needs.
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
    readonly Displays _displays;
    readonly PetRenderer _renderer;
    readonly LayeredWindow _window;
    readonly PointerTracker _tracker = new();
    readonly System.Windows.Forms.Timer _timer = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _lastTick, _lastRender = -1, _lastTopmost, _lastSlowCheck, _lastSave;
    bool _userHidden, _fullscreenHidden, _locked, _displayOff, _disposed, _hovering;
    int _errors;
    bool? _startupApplied;

    // The yarn ball lives in its own small window so it can fly anywhere on the screen.
    LayeredWindow? _ballWindow;
    readonly YarnBallRenderer _ballRenderer = new();
    readonly PropRenderer _propRenderer = new();
    readonly List<(double T, SKPoint P)> _ballTrail = [];
    SKColor _ballColor;
    SKPoint _ballDownAt;
    double _ballDownTime;
    bool _ballPressed, _ballDragged;

    // Saying "Hi!" when the laptop wakes up or you come back to it.
    bool _sayHi;
    double _lockedAt = -1, _displayOffAt = -1;

    public event Action<SKPoint>? ContextMenuRequested;
    public PetBrain Brain { get; }
    public bool IsHidden => _userHidden;
    /// <summary>Measured render cost, for the settings window.</summary>
    public double LastFrameMs { get; private set; }
    public double CurrentFps { get; private set; }

    public PetController(MochiSettings settings, PetMemory memory, string memoryPath, Displays displays, bool atSignIn = false)
    {
        _settings = settings;
        _memory = memory;
        _memoryPath = memoryPath;
        _displays = displays;

        var options = new BrainOptions();
        options.ApplyFrom(settings);
        Brain = new PetBrain(options, new Personality(settings.Personality, memory.TraitDrift), memory, displays);
        _renderer = new PetRenderer(settings.BuildLook());
        _window = new LayeredWindow("Mochi");

        _window.LeftDown += p => _tracker.Down(p, Now, Brain.HitTest(p));
        _window.MouseMoved += (p, _) => { _hovering = true; _tracker.Move(p, Now, Brain.HitTest(p), Brain.Scale); };
        _window.LeftUp += p => _tracker.Up(p, Now);
        _window.MouseLeft += () => { _hovering = false; _tracker.Leave(Now); };
        _window.CaptureLost += () => _tracker.Cancel(Now);
        _window.RightUp += p => ContextMenuRequested?.Invoke(p);
        _window.DisplayPowerChanged += on =>
        {
            if (!on) _displayOffAt = Now;
            else if (_displayOff && _displayOffAt >= 0 && Now - _displayOffAt > 120) _sayHi = true;
            _displayOff = !on;
            UpdateVisibility();
        };
        _tracker.DoubleClickSeconds = Native.GetDoubleClickTime() / 1000.0;

        var floor = displays.ById(memory.MonitorId) ?? displays.Primary;
        float fraction = memory.FloorFraction >= 0 ? (float)memory.FloorFraction : 0.78f;
        Brain.Place(new SKPoint(floor.Left + fraction * floor.Width, floor.Y), memory.Facing);
        // At sign-in the desktop is still loading: save the "Hi!" until you're at the computer.
        Brain.Start(DateTime.Now, memory.LastSeenUtc, greetNow: !atSignIn);

        ApplySettings();
        _timer.Interval = 15;
        _timer.Tick += (_, _) => Tick();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    double Now => _clock.Elapsed.TotalSeconds;

    public void Start()
    {
        _lastTick = Now;
        UpdateVisibility();
        Tick();
        _timer.Start();
        Log.Info("Mochi started.");
    }

    /// <summary>Pushes settings into the brain, renderer and window. Safe to call any time.</summary>
    public void ApplySettings()
    {
        _settings.Normalize();
        Brain.Options.ApplyFrom(_settings);
        if (Brain.Personality.Profile != _settings.Personality) Brain.Personality.SetProfile(_settings.Personality);
        _renderer.SetLook(_settings.BuildLook());
        Brain.Effects.Enabled = _settings.ShowEffects;
        if (!_settings.ShowEffects) Brain.Effects.Clear();
        _window.SetClickThrough(_settings.ClickThrough);
        if (!_window.SetExcludedFromCapture(_settings.HideFromScreenCapture) && _settings.HideFromScreenCapture)
            Log.Info("Screen-capture exclusion is not supported on this Windows build.");
        _ballWindow?.SetClickThrough(_settings.ClickThrough);
        _ballWindow?.SetExcludedFromCapture(_settings.HideFromScreenCapture);
        _ballColor = PetLook.AccessoryColors[_settings.AccessoryColorIndex].Color;

        if (_startupApplied != _settings.StartWithWindows)
        {
            try
            {
                if (StartupRegistration.IsEnabled() != _settings.StartWithWindows) StartupRegistration.Set(_settings.StartWithWindows);
                _startupApplied = _settings.StartWithWindows;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                Log.Error("Could not update the startup registration", ex);
            }
        }
        _lastRender = -1;
    }

    public void SetHidden(bool hidden)
    {
        _userHidden = hidden;
        UpdateVisibility();
    }

    void UpdateVisibility()
    {
        bool visible = !_userHidden && !_fullscreenHidden && !_locked && !_displayOff;
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
            _tracker.Cancel(Now);
            if (_ballPressed) ReleaseBall(null);
            _window.Hide();
            _ballWindow?.Hide();
        }
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
                _displays.RefreshIfStale();
                bool full = _settings.HideInFullscreen && SystemSignals.IsFullscreenBusy();
                bool locked = SystemSignals.IsSessionLocked();
                if (full != _fullscreenHidden || locked != _locked)
                {
                    _fullscreenHidden = full;
                    _locked = locked;
                    UpdateVisibility();
                }
            }
            if (!_window.IsVisible)
            {
                _timer.Interval = 500; // asleep in the tray: almost no work
                return;
            }

            var senses = new Senses
            {
                Cursor = LayeredWindow.CursorPos(),
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
            _timer.Interval = fps >= 60 ? 15 : fps >= 30 ? 31 : 62;
            if (_lastRender < 0 || now - _lastRender >= 1.0 / fps - 0.006)
            {
                CurrentFps = _lastRender < 0 ? fps : 1.0 / Math.Max(1e-3, now - _lastRender);
                _lastRender = now;
                Render();
            }
            if (now - _lastTopmost > 2.5)
            {
                _lastTopmost = now;
                _window.KeepOnTop();
                _ballWindow?.KeepOnTop(); // the ball stays in front of the kitten (it's carried in the mouth)
            }
            if (now - _lastSave > 60) { _lastSave = now; SaveMemory(); }
            _errors = 0;
        }
        catch (Exception ex)
        {
            Log.Error("Frame failed", ex);
            if (++_errors > 30)
            {
                // Something is persistently wrong; slow down rather than spin.
                _timer.Interval = 1000;
            }
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
        var sw = Stopwatch.StartNew();
        float scale = Brain.Scale;
        int w = (int)MathF.Ceiling(ModelWidth * scale);
        int h = (int)MathF.Ceiling(ModelHeight * scale);
        var pos = Brain.Position;
        int ox = (int)MathF.Floor(pos.X + ModelLeft * scale);
        int oy = (int)MathF.Floor(pos.Y + ModelTop * scale);

        var c = _window.BeginFrame(w, h);
        // Sub-pixel placement keeps slow walking perfectly smooth.
        c.Translate(pos.X - ox, pos.Y - oy);
        c.Scale(scale);
        c.Save();
        c.Scale(Brain.Facing, 1f);
        _renderer.Draw(c, Brain.Animator.Output);
        if (Brain.Prop is { } prop) _propRenderer.Draw(c, prop);
        c.Restore();
        var head = Brain.HeadModelUnflipped;
        Brain.Effects.Draw(c, new SKPoint(head.X, head.Y - 30f));
        _window.EndFrame(ox, oy);
        RenderBall();
        LastFrameMs = sw.Elapsed.TotalMilliseconds;
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

        var w = new LayeredWindow("Mochi's yarn ball");
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
        w.MouseMoved += (p, _) =>
        {
            if (!_ballPressed) return;
            _ballTrail.Add((Now, p));
            if (_ballTrail.Count > 16) _ballTrail.RemoveAt(0);
            if (SKPoint.Distance(p, _ballDownAt) > 4f) _ballDragged = true;
            Brain.DragBall(p);
        };
        w.LeftUp += p => ReleaseBall(p);
        w.CaptureLost += () => ReleaseBall(null);
        w.RightUp += p => ContextMenuRequested?.Invoke(p);
        w.SetClickThrough(_settings.ClickThrough);
        w.SetExcludedFromCapture(_settings.HideFromScreenCapture);
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
        float scale = ball.Scale;
        int size = (int)MathF.Ceiling(2f * BallHalf * scale);
        int ox = (int)MathF.Floor(ball.Position.X - BallHalf * scale);
        int oy = (int)MathF.Floor(ball.Position.Y - BallHalf * scale);
        var c = _ballWindow.BeginFrame(size, size);
        c.Translate(ball.Position.X - ox, ball.Position.Y - oy);
        c.Scale(scale);
        _ballRenderer.Draw(c, ball, _ballColor);
        _ballWindow.EndFrame(ox, oy);
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

    void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        _locked = e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect;
        if (_locked) _lockedAt = Now;
        else if (e.Reason == SessionSwitchReason.SessionUnlock && _lockedAt >= 0 && Now - _lockedAt > 120) _sayHi = true;
        UpdateVisibility();
    }

    void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        _displays.Refresh();
        _lastRender = -1;
    }

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            _lastTick = Now;
            _sayHi = true; // the laptop just woke up
        }
        if (e.Mode == PowerModes.Suspend) SaveMemory();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SaveMemory();
        _ballWindow?.Dispose();
        _ballRenderer.Dispose();
        _propRenderer.Dispose();
        _window.Dispose();
        _renderer.Dispose();
        Brain.Effects.Dispose();
    }
}

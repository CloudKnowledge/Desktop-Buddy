using System;
using System.Diagnostics;
using System.IO;
using CoreAnimation;
using CoreGraphics;
using Foundation;
using Mochi.Core.Persistence;
using Mochi.Core.Play;
using Mochi.Core.Rig;
using Mochi.Core.Stage;
using SkiaSharp;
using SkiaSharp.Views.iOS;
using UIKit;

namespace Mochi.iOS;

/// <summary>
/// The app's one screen: the room, Mochi and the yarn ball as three Skia views, a toolbar of things to do, and a
/// display-link frame loop. Behaviour, touch handling, layout and drawing live in the shared <see cref="PetStage"/>.
/// </summary>
public sealed class PetViewController : UIViewController
{
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly string _settingsPath, _memoryPath;
    readonly PetStage _stage;
    readonly SKCanvasView _room = NewCanvas(), _pet = NewCanvas(), _ball = NewCanvas();
    readonly UIToolbar _toolbar = new();
    readonly UIBarButtonItem _more = new() { Image = Symbol("ellipsis.circle"), Title = "More" };
    CADisplayLink? _link;
    (double W, double H, double L, double T, double R, double B) _layout;
    int _roomVersion = -1;
    bool _running;

    public PetViewController()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mochi");
        _settingsPath = Path.Combine(dir, "settings.json");
        _memoryPath = Path.Combine(dir, "memory.json");
        _stage = new PetStage(JsonStore.Load<MochiSettings>(_settingsPath), JsonStore.Load<PetMemory>(_memoryPath));
    }

    double Now => _clock.Elapsed.TotalSeconds;

    static SKCanvasView NewCanvas() => new() { Opaque = false, BackgroundColor = UIColor.Clear, UserInteractionEnabled = false };

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        View!.BackgroundColor = UIColor.FromRGB(0xFB, 0xE2, 0xCB);
        View.MultipleTouchEnabled = false;
        _room.PaintSurface += (_, e) => Paint(_room, e, _stage.DrawRoom);
        _pet.PaintSurface += (_, e) => Paint(_pet, e, _stage.DrawPet);
        _ball.PaintSurface += (_, e) => Paint(_ball, e, _stage.DrawBall);
        _ball.Hidden = true;
        View.AddSubviews(_room, _pet, _ball, _toolbar);
        BuildToolbar();
    }

    static void Paint(SKCanvasView view, SKPaintSurfaceEventArgs e, Action<SKCanvas, float> draw)
    {
        e.Surface.Canvas.Clear(SKColors.Transparent);
        double width = view.Bounds.Width;
        if (width > 0) draw(e.Surface.Canvas, (float)(e.Info.Width / width));
    }

    public override void ViewDidLayoutSubviews()
    {
        base.ViewDidLayoutSubviews();
        var bounds = View!.Bounds;
        var safe = View.SafeAreaInsets;
        double width = bounds.Width, height = bounds.Height;
        double bar = _toolbar.SizeThatFits(new CGSize(width, 0)).Height;
        double barTop = height - safe.Bottom - bar;
        _toolbar.Frame = new CGRect(0, barTop, width, bar);
        _room.Frame = bounds;
        var layout = (width, height, (double)safe.Left, (double)safe.Top, (double)safe.Right, height - barTop);
        if (layout != _layout)
        {
            _layout = layout;
            _stage.Resize((float)width, (float)height, (float)safe.Left, (float)safe.Top, (float)safe.Right, (float)(height - barTop), DateTime.Now);
            _roomVersion = _stage.RoomVersion;
            _room.SetNeedsDisplay();
        }
        Render();
    }

    public override void ViewDidAppear(bool animated)
    {
        base.ViewDidAppear(animated);
        Resume();
    }

    /// <summary>The app is on screen again: start the frame loop (and say hi after a while away).</summary>
    public void Resume()
    {
        if (_running || !IsViewLoaded) return;
        _running = true;
        _stage.Resume(Now, DateTime.UtcNow);
        if (_link == null)
        {
            _link = CADisplayLink.Create(OnFrame);
            _link.AddToRunLoop(NSRunLoop.Main, NSRunLoopMode.Common);
        }
        _link.Paused = false;
    }

    /// <summary>The app is leaving the screen: stop drawing and save what Mochi remembers.</summary>
    public void Pause()
    {
        if (!_running) return;
        _running = false;
        if (_link != null) _link.Paused = true;
        _stage.Pause(Now, DateTime.UtcNow);
        Save();
    }

    void OnFrame()
    {
        double now = Now;
        _stage.Tick(now, DateTime.Now);
        if (_stage.RoomVersion != _roomVersion)
        {
            _roomVersion = _stage.RoomVersion;
            _room.SetNeedsDisplay();
            SetNeedsStatusBarAppearanceUpdate();
        }
        if (_stage.ShouldRender(now)) Render();
    }

    void Render()
    {
        double scale = View!.TraitCollection.DisplayScale;
        _stage.Layout(scale > 0 ? (float)scale : 2f);
        _pet.Frame = ToRect(_stage.PetFrame);
        _pet.SetNeedsDisplay();
        _ball.Hidden = !_stage.HasBall;
        if (!_stage.HasBall) return;
        _ball.Frame = ToRect(_stage.BallFrame);
        _ball.SetNeedsDisplay();
    }

    static CGRect ToRect(SKRect r) => new((double)r.Left, (double)r.Top, (double)r.Width, (double)r.Height);

    public override UIStatusBarStyle PreferredStatusBarStyle() =>
        _stage.IsNightRoom ? UIStatusBarStyle.LightContent : UIStatusBarStyle.DarkContent;

    // ---- touch: the stage turns these into taps, petting strokes, pick-ups and ball throws ----------------

    public override void TouchesBegan(NSSet touches, UIEvent? evt)
    {
        base.TouchesBegan(touches, evt);
        if (Point(touches) is { } p) _stage.TouchBegan(p, Now);
    }

    public override void TouchesMoved(NSSet touches, UIEvent? evt)
    {
        base.TouchesMoved(touches, evt);
        if (Point(touches) is { } p) _stage.TouchMoved(p, Now);
    }

    public override void TouchesEnded(NSSet touches, UIEvent? evt)
    {
        base.TouchesEnded(touches, evt);
        if (Point(touches) is { } p) _stage.TouchEnded(p, Now);
    }

    public override void TouchesCancelled(NSSet touches, UIEvent? evt)
    {
        base.TouchesCancelled(touches, evt);
        _stage.TouchCancelled(Now);
    }

    SKPoint? Point(NSSet touches)
    {
        if (touches.AnyObject is not UITouch touch) return null;
        var p = touch.LocationInView(View);
        return new SKPoint((float)p.X, (float)p.Y);
    }

    // ---- toolbar ----------------------------------------------------------------------------------------

    void BuildToolbar()
    {
        var brain = _stage.Brain;
        // Tap throws the ball; press and hold for more.
        var ball = new UIBarButtonItem(Symbol("tennisball.fill"), UIBarButtonItemStyle.Plain, (_, _) => brain.ThrowBall()) { Title = "Play ball" };
        ball.Menu = UIMenu.Create("", [Act("Throw the ball", "arrow.up.forward", brain.ThrowBall), Act("Put the ball away", "archivebox", brain.RemoveBall)]);
        var feed = new UIBarButtonItem { Image = Symbol("cup.and.saucer.fill"), Title = "Feed" };
        feed.Menu = UIMenu.Create("", [Act("A saucer of milk", "drop.fill", () => brain.CommandFeed(PropKind.Milk)), Act("Some treats", "sparkles", () => brain.CommandFeed(PropKind.Treats))]);
        var tv = new UIBarButtonItem(Symbol("tv.fill"), UIBarButtonItemStyle.Plain, (_, _) => brain.CommandWatchTv()) { Title = "Watch cat TV" };
        var work = new UIBarButtonItem(Symbol("laptopcomputer"), UIBarButtonItemStyle.Plain, (_, _) => brain.CommandWork()) { Title = "Work with me" };
        var nap = new UIBarButtonItem(Symbol("moon.zzz.fill"), UIBarButtonItemStyle.Plain, (_, _) =>
        {
            if (brain.IsAsleep) brain.CommandWake();
            else brain.CommandSleep();
        }) { Title = "Nap or wake up" };
        RefreshMoreMenu();
        _toolbar.Items = [ball, Flexible(), feed, Flexible(), tv, Flexible(), work, Flexible(), nap, Flexible(), _more];
    }

    void RefreshMoreMenu()
    {
        var s = _stage.Settings;
        var brain = _stage.Brain;
        var coats = new[] { ("Golden", CoatPreset.Golden), ("Cream", CoatPreset.Cream), ("Ginger", CoatPreset.Ginger), ("Silver", CoatPreset.Silver) };
        var accessories = new[] { ("Fairy lights", AccessoryKind.FairyLights), ("Bell collar", AccessoryKind.Bell), ("Hair bow", AccessoryKind.Bow), ("None", AccessoryKind.None) };
        var coatItems = new UIMenuElement[coats.Length];
        for (int i = 0; i < coats.Length; i++)
        {
            var (name, coat) = coats[i];
            coatItems[i] = Choice(name, s.Coat == coat, () => s.Coat = coat);
        }
        var accessoryItems = new UIMenuElement[accessories.Length];
        for (int i = 0; i < accessories.Length; i++)
        {
            var (name, kind) = accessories[i];
            accessoryItems[i] = Choice(name, s.Accessory == kind, () => s.Accessory = kind);
        }
        _more.Menu = UIMenu.Create("", [
            Act($"Pet {s.PetName}", "heart.fill", brain.CommandPet),
            Act("Say hi", "hand.wave", brain.SayHi),
            UIMenu.Create("Coat", coatItems),
            UIMenu.Create("Accessory", accessoryItems),
        ]);
    }

    UIAction Choice(string title, bool selected, Action apply)
    {
        var action = Act(title, null, () =>
        {
            apply();
            _stage.ApplySettings();
            Save();
            RefreshMoreMenu();
        });
        if (selected) action.State = UIMenuElementState.On;
        return action;
    }

    static UIAction Act(string title, string? symbol, Action run) =>
        UIAction.Create(title, symbol == null ? null : Symbol(symbol), null, _ => run());

    static UIImage Symbol(string name) => UIImage.GetSystemImage(name) ?? new UIImage();

    static UIBarButtonItem Flexible() => new(UIBarButtonSystemItem.FlexibleSpace);

    void Save()
    {
        try
        {
            JsonStore.Save(_settingsPath, _stage.Settings);
            JsonStore.Save(_memoryPath, _stage.Memory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Mochi could not save: {ex.Message}");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _link?.Invalidate();
            _stage.Dispose();
        }
        base.Dispose(disposing);
    }
}

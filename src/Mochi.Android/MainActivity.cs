using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Android.App;
using Android.Content.PM;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using Mochi.Core.Persistence;
using Mochi.Core.Play;
using Mochi.Core.Rig;
using Mochi.Core.Stage;
using SkiaSharp;
using SkiaSharp.Views.Android;
using Activity = Android.App.Activity;
using Color = Android.Graphics.Color;
using View = Android.Views.View;

namespace Mochi.Droid;

/// <summary>
/// The app's one screen: the room, Mochi and the yarn ball as three Skia views, a bar of things to do, and a
/// Choreographer frame loop. Behaviour, touch handling, layout and drawing live in the shared <see cref="PetStage"/>.
/// </summary>
[Activity(Label = "Mochi", MainLauncher = true, Exported = true, Theme = "@android:style/Theme.Material.Light.NoActionBar",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.SmallestScreenSize | ConfigChanges.ScreenLayout |
                           ConfigChanges.UiMode | ConfigChanges.Density | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation)]
public sealed class MainActivity : Activity, Choreographer.IFrameCallback, View.IOnApplyWindowInsetsListener
{
    const int BarDp = 64, BarMarginDp = 10;
    static readonly (string Name, CoatPreset Coat)[] Coats = [("Golden", CoatPreset.Golden), ("Cream", CoatPreset.Cream), ("Ginger", CoatPreset.Ginger), ("Silver", CoatPreset.Silver)];
    static readonly (string Name, AccessoryKind Kind)[] Accessories = [("Fairy lights", AccessoryKind.FairyLights), ("Bell collar", AccessoryKind.Bell), ("Hair bow", AccessoryKind.Bow), ("None", AccessoryKind.None)];

    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly List<TextView> _barLabels = [];
    string _settingsPath = "", _memoryPath = "";
    PetStage _stage = null!;
    FrameLayout _root = null!;
    SKCanvasView _room = null!, _pet = null!, _ball = null!;
    LinearLayout _bar = null!;
    (int L, int T, int R, int B) _insets;
    (int W, int H, int L, int T, int R, int B) _layout;
    int _roomVersion = -1;
    float _density = 1f;
    bool _running;

    double Now => _clock.Elapsed.TotalSeconds;

    int Px(float dp) => (int)MathF.Round(dp * _density);

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        string dir = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Mochi");
        _settingsPath = Path.Combine(dir, "settings.json");
        _memoryPath = Path.Combine(dir, "memory.json");
        _stage = new PetStage(JsonStore.Load<MochiSettings>(_settingsPath), JsonStore.Load<PetMemory>(_memoryPath));
        _density = Resources!.DisplayMetrics!.Density;

        // Draw under the status and navigation bars (Android 15+ always does); the stage keeps clear of them.
        if (!OperatingSystem.IsAndroidVersionAtLeast(35))
        {
            Window!.SetDecorFitsSystemWindows(false);
            Window.SetStatusBarColor(Color.Transparent);
            Window.SetNavigationBarColor(Color.Transparent);
        }
        Window!.NavigationBarContrastEnforced = false;

        _root = new FrameLayout(this);
        _root.SetBackgroundColor(Color.Rgb(0xFB, 0xE2, 0xCB));
        _room = NewCanvas();
        _pet = NewCanvas();
        _ball = NewCanvas();
        _room.PaintSurface += (_, e) => Paint(_room, e, _stage.DrawRoom);
        _pet.PaintSurface += (_, e) => Paint(_pet, e, _stage.DrawPet);
        _ball.PaintSurface += (_, e) => Paint(_ball, e, _stage.DrawBall);
        _ball.Visibility = ViewStates.Gone;
        _root.AddView(_room, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _root.AddView(_pet, new FrameLayout.LayoutParams(1, 1));
        _root.AddView(_ball, new FrameLayout.LayoutParams(1, 1));
        _bar = BuildBar();
        _root.AddView(_bar, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Px(BarDp), GravityFlags.Bottom));
        _root.Touch += OnTouch;
        _root.SetOnApplyWindowInsetsListener(this);
        _root.LayoutChange += (_, _) => Relayout();
        SetContentView(_root);
        UpdateBars();
    }

    SKCanvasView NewCanvas() => new(this) { Clickable = false, Focusable = false };

    void Paint(SKCanvasView view, SKPaintSurfaceEventArgs e, Action<SKCanvas, float> draw)
    {
        e.Surface.Canvas.Clear(SKColors.Transparent);
        if (view.Width > 0) draw(e.Surface.Canvas, e.Info.Width * _density / view.Width);
    }

    public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
    {
        var bars = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout());
        _insets = (bars.Left, bars.Top, bars.Right, bars.Bottom);
        var lp = (FrameLayout.LayoutParams)_bar.LayoutParameters!;
        lp.LeftMargin = bars.Left + Px(BarMarginDp);
        lp.RightMargin = bars.Right + Px(BarMarginDp);
        lp.BottomMargin = bars.Bottom + Px(BarMarginDp);
        _bar.LayoutParameters = lp;
        Relayout();
        return WindowInsets.Consumed!;
    }

    void Relayout()
    {
        int width = _root.Width, height = _root.Height;
        if (width <= 0 || height <= 0) return;
        var (l, t, r, b) = _insets;
        int bottom = b + Px(BarDp + 2 * BarMarginDp);
        var layout = (width, height, l, t, r, bottom);
        if (layout == _layout) return;
        _layout = layout;
        _stage.Resize(width / _density, height / _density, l / _density, t / _density, r / _density, bottom / _density, DateTime.Now);
        _roomVersion = _stage.RoomVersion;
        _room.Invalidate();
        UpdateBars();
        Render();
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (_running) return;
        _running = true;
        _stage.Resume(Now, DateTime.UtcNow);
        Choreographer.Instance!.PostFrameCallback(this);
    }

    // Switching apps, locking the phone or opening the notification shade over the app.
    protected override void OnPause()
    {
        if (_running)
        {
            _running = false;
            Choreographer.Instance!.RemoveFrameCallback(this);
            _stage.Pause(Now, DateTime.UtcNow);
            Save();
        }
        base.OnPause();
    }

    public void DoFrame(long frameTimeNanos)
    {
        if (!_running) return;
        Choreographer.Instance!.PostFrameCallback(this);
        double now = Now;
        _stage.Tick(now, DateTime.Now);
        if (_stage.RoomVersion != _roomVersion)
        {
            _roomVersion = _stage.RoomVersion;
            _room.Invalidate();
            UpdateBars();
        }
        if (_stage.ShouldRender(now)) Render();
    }

    void Render()
    {
        _stage.Layout(_density);
        Place(_pet, _stage.PetFrame);
        _pet.Invalidate();
        _ball.Visibility = _stage.HasBall ? ViewStates.Visible : ViewStates.Gone;
        if (!_stage.HasBall) return;
        Place(_ball, _stage.BallFrame);
        _ball.Invalidate();
    }

    // Moving by translation each frame avoids a layout pass; the size only changes with the kitten's scale.
    void Place(View view, SKRect frame)
    {
        int w = Math.Max(1, (int)MathF.Round(frame.Width * _density)), h = Math.Max(1, (int)MathF.Round(frame.Height * _density));
        var lp = view.LayoutParameters!;
        if (lp.Width != w || lp.Height != h)
        {
            lp.Width = w;
            lp.Height = h;
            view.LayoutParameters = lp;
        }
        view.TranslationX = frame.Left * _density;
        view.TranslationY = frame.Top * _density;
    }

    // ---- touch: the stage turns these into taps, petting strokes, pick-ups and ball throws ----------------

    void OnTouch(object? sender, View.TouchEventArgs e)
    {
        var ev = e.Event!;
        var p = new SKPoint(ev.GetX() / _density, ev.GetY() / _density);
        switch (ev.ActionMasked)
        {
            case MotionEventActions.Down: _stage.TouchBegan(p, Now); break;
            case MotionEventActions.Move: _stage.TouchMoved(p, Now); break;
            case MotionEventActions.Up: _stage.TouchEnded(p, Now); break;
            case MotionEventActions.Cancel: _stage.TouchCancelled(Now); break;
        }
        e.Handled = true;
    }

    // ---- the bar of things to do ----------------------------------------------------------------------

    LinearLayout BuildBar()
    {
        var bar = new LinearLayout(this) { Orientation = Orientation.Horizontal, Elevation = Px(6) };
        bar.SetGravity(GravityFlags.Center);
        var brain = _stage.Brain;
        // Tap throws the ball; press and hold for more.
        var ball = AddButton(bar, "\U0001F9F6", "Ball", _ => brain.ThrowBall());
        ball.LongClick += (_, e) =>
        {
            e.Handled = true;
            ShowMenu(ball, ("Throw the ball", brain.ThrowBall), ("Put the ball away", brain.RemoveBall));
        };
        AddButton(bar, "\U0001F95B", "Feed", v => ShowMenu(v, ("A saucer of milk", () => brain.CommandFeed(PropKind.Milk)), ("Some treats", () => brain.CommandFeed(PropKind.Treats))));
        AddButton(bar, "\U0001F4FA", "TV", _ => brain.CommandWatchTv());
        AddButton(bar, "\U0001F4BB", "Work", _ => brain.CommandWork());
        AddButton(bar, "\U0001F4A4", "Nap", _ =>
        {
            if (brain.IsAsleep) brain.CommandWake();
            else brain.CommandSleep();
        });
        AddButton(bar, "\u22EF", "More", ShowMore);
        return bar;
    }

    View AddButton(LinearLayout bar, string symbol, string label, Action<View> tap)
    {
        var button = new LinearLayout(this) { Orientation = Orientation.Vertical, Clickable = true, Focusable = true, ContentDescription = label };
        button.SetGravity(GravityFlags.Center);
        var ripple = new TypedValue();
        Theme!.ResolveAttribute(Android.Resource.Attribute.SelectableItemBackgroundBorderless, ripple, true);
        button.SetBackgroundResource(ripple.ResourceId);
        var icon = new TextView(this) { Text = symbol, Gravity = GravityFlags.Center };
        icon.SetTextSize(ComplexUnitType.Sp, 22);
        var text = new TextView(this) { Text = label, Gravity = GravityFlags.Center };
        text.SetTextSize(ComplexUnitType.Sp, 11);
        _barLabels.Add(icon);
        _barLabels.Add(text);
        button.AddView(icon);
        button.AddView(text);
        button.Click += (_, _) => tap(button);
        bar.AddView(button, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1f));
        return button;
    }

    void ShowMenu(View anchor, params (string Title, Action Run)[] items)
    {
        var popup = new PopupMenu(this, anchor);
        for (int i = 0; i < items.Length; i++) popup.Menu!.Add(0, i, i, items[i].Title);
        popup.MenuItemClick += (_, e) => items[e.Item!.ItemId].Run();
        popup.Show();
    }

    void ShowMore(View anchor)
    {
        var s = _stage.Settings;
        var brain = _stage.Brain;
        var popup = new PopupMenu(this, anchor);
        var root = popup.Menu!;
        var actions = new List<Action>();
        var chosen = new List<IMenuItem>();
        IMenuItem Add(IMenu menu, int group, string title, Action run)
        {
            var item = menu.Add(group, actions.Count, actions.Count, title)!;
            actions.Add(run);
            return item;
        }

        Add(root, 0, $"Pet {s.PetName}", brain.CommandPet);
        Add(root, 0, "Say hi", brain.SayHi);
        var coats = root.AddSubMenu("Coat")!;
        foreach (var (name, coat) in Coats)
        {
            var item = Add(coats, 1, name, () => Choose(() => s.Coat = coat));
            if (s.Coat == coat) chosen.Add(item);
        }
        coats.SetGroupCheckable(1, true, true);
        var accessories = root.AddSubMenu("Accessory")!;
        foreach (var (name, kind) in Accessories)
        {
            var item = Add(accessories, 2, name, () => Choose(() => s.Accessory = kind));
            if (s.Accessory == kind) chosen.Add(item);
        }
        accessories.SetGroupCheckable(2, true, true);
        foreach (var item in chosen) item.SetChecked(true);
        popup.MenuItemClick += (_, e) =>
        {
            if (e.Item is { HasSubMenu: false } item) actions[item.ItemId]();
        };
        popup.Show();
    }

    void Choose(Action apply)
    {
        apply();
        _stage.ApplySettings();
        Save();
    }

    void UpdateBars()
    {
        bool night = _stage.IsNightRoom;
        var background = new GradientDrawable();
        background.SetCornerRadius(Px(22));
        background.SetColor(night ? Color.Argb(235, 0x2C, 0x28, 0x40) : Color.Argb(235, 0xFF, 0xFC, 0xF7));
        _bar.Background = background;
        var ink = night ? Color.Rgb(0xEC, 0xE6, 0xFF) : Color.Rgb(0x6B, 0x45, 0x2A);
        foreach (var label in _barLabels) label.SetTextColor(ink);
        int light = (int)(WindowInsetsControllerAppearance.LightStatusBars | WindowInsetsControllerAppearance.LightNavigationBars);
        Window!.InsetsController?.SetSystemBarsAppearance(night ? 0 : light, light);
    }

    void Save()
    {
        try
        {
            JsonStore.Save(_settingsPath, _stage.Settings);
            JsonStore.Save(_memoryPath, _stage.Memory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Mochi", $"Mochi could not save: {ex.Message}");
        }
    }

    protected override void OnDestroy()
    {
        _stage.Dispose();
        base.OnDestroy();
    }
}

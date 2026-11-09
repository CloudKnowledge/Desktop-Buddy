using System;
using AppKit;
using CoreAnimation;
using CoreGraphics;
using Foundation;
using SkiaSharp;

namespace Mochi.Mac.Platform;

/// <summary>
/// A small borderless, transparent panel that floats above other apps without ever taking focus, showing one
/// Skia frame at a time. Clicks land only where the frame has visible pixels; elsewhere they fall through to
/// the apps underneath. Positions are top-left-origin points (see <see cref="Screens"/>).
/// </summary>
internal sealed class PetWindow : IDisposable
{
    static readonly CGColorSpace ColorSpace = CGColorSpace.CreateSrgb()!;

    readonly NSPanel _panel;
    readonly FrameView _view;
    SKBitmap? _bitmap;
    SKCanvas? _canvas;
    CGBitmapContext? _context;
    SKRect _frame;
    float _pixelScale = 2f;
    bool _interactive, _clickThrough, _disposed;

    public event Action<SKPoint>? LeftDown, LeftUp, RightDown;

    public PetWindow(string title)
    {
        _panel = new NSPanel(new CGRect(0, 0, 1, 1), NSWindowStyle.Borderless | NSWindowStyle.NonactivatingPanel, NSBackingStore.Buffered, false)
        {
            Title = title,
            BackgroundColor = NSColor.Clear,
            IsOpaque = false,
            HasShadow = false,
            // Above the Dock, so Mochi can be carried over it.
            Level = NSWindowLevel.Status,
            FloatingPanel = true,
            BecomesKeyOnlyIfNeeded = true,
            HidesOnDeactivate = false,
            IgnoresMouseEvents = true,
        };
        _panel.ReleaseWhenClosed(false);
        _view = new FrameView(this) { WantsLayer = true };
        _panel.ContentView = _view;
        SetFullScreenSpaces(false);
    }

    public bool IsVisible => _panel.IsVisible;

    /// <summary>Device pixels per point on the screen the window is on (2 on Retina).</summary>
    public float BackingScale => (float)Math.Max(1.0, (double)_panel.BackingScaleFactor);

    public void Show() => _panel.OrderFrontRegardless();

    public void Hide() => _panel.OrderOut(null);

    public void KeepOnTop()
    {
        if (_panel.IsVisible) _panel.OrderFrontRegardless();
    }

    /// <summary>Ghost mode: every click passes through.</summary>
    public void SetClickThrough(bool on)
    {
        _clickThrough = on;
        UpdateMouse();
    }

    /// <summary>Whether the pointer is over Mochi (or Mochi is held), so the window should take the clicks.</summary>
    public void SetInteractive(bool on)
    {
        if (on == _interactive) return;
        _interactive = on;
        UpdateMouse();
    }

    void UpdateMouse()
    {
        bool ignore = _clickThrough || !_interactive;
        if (_panel.IgnoresMouseEvents != ignore) _panel.IgnoresMouseEvents = ignore;
    }

    /// <summary>Leaves Mochi out of screenshots, recordings and screen sharing (apps that honour it).</summary>
    public void SetExcludedFromCapture(bool on) => _panel.SharingType = on ? NSWindowSharingType.None : NSWindowSharingType.ReadOnly;

    /// <summary>Whether Mochi also shows over full-screen apps, or stays on the desktop's own spaces.</summary>
    public void SetFullScreenSpaces(bool on)
    {
        var behaviour = NSWindowCollectionBehavior.CanJoinAllSpaces | NSWindowCollectionBehavior.Stationary | NSWindowCollectionBehavior.IgnoresCycle;
        if (on) behaviour |= NSWindowCollectionBehavior.FullScreenAuxiliary;
        _panel.CollectionBehavior = behaviour;
    }

    /// <summary>A cleared canvas for the next frame, in points from the top-left of <paramref name="frame"/>.</summary>
    public SKCanvas BeginFrame(SKRect frame, float pixelScale)
    {
        int w = Math.Max(1, (int)MathF.Ceiling(frame.Width * pixelScale)), h = Math.Max(1, (int)MathF.Ceiling(frame.Height * pixelScale));
        if (_bitmap == null || _bitmap.Width != w || _bitmap.Height != h)
        {
            _canvas?.Dispose();
            _context?.Dispose();
            _bitmap?.Dispose();
            _bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
            // Core Graphics reads the same pixels Skia draws into.
            _context = new CGBitmapContext(_bitmap.GetPixels(), w, h, 8, _bitmap.RowBytes, ColorSpace,
                CGBitmapFlags.PremultipliedFirst | CGBitmapFlags.ByteOrder32Little);
            _canvas = new SKCanvas(_bitmap);
        }
        _frame = frame;
        _pixelScale = pixelScale;
        var c = _canvas!;
        c.ResetMatrix();
        c.Clear(SKColors.Transparent);
        c.Scale(pixelScale);
        return c;
    }

    /// <summary>Moves the window and shows the new pixels in one step, so nothing tears or lags.</summary>
    public void EndFrame()
    {
        _canvas!.Flush();
        using var image = _context!.ToImage();
        CATransaction.Begin();
        CATransaction.DisableActions = true;
        _panel.SetFrame(Screens.ToCocoa(_frame), false);
        var layer = _view.Layer!;
        layer.ContentsScale = _pixelScale;
        layer.Contents = image;
        CATransaction.Commit();
    }

    /// <summary>Whether a screen point is on a visible pixel of the last frame.</summary>
    public bool Covers(SKPoint p)
    {
        if (_bitmap == null || !_panel.IsVisible) return false;
        int x = (int)((p.X - _frame.Left) * _pixelScale), y = (int)((p.Y - _frame.Top) * _pixelScale);
        return x >= 0 && y >= 0 && x < _bitmap.Width && y < _bitmap.Height && _bitmap.GetPixel(x, y).Alpha > 24;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _panel.OrderOut(null);
        _panel.Close();
        _panel.Dispose();
        _canvas?.Dispose();
        _context?.Dispose();
        _bitmap?.Dispose();
    }

    sealed class FrameView(PetWindow owner) : NSView
    {
        bool _leftDown;

        // The first click on Mochi counts even though Mochi's app is never the active one.
        public override bool AcceptsFirstMouse(NSEvent? theEvent) => true;

        public override void MouseDown(NSEvent theEvent)
        {
            // Control-click is the trackpad's right-click.
            _leftDown = (theEvent.ModifierFlags & NSEventModifierMask.ControlKeyMask) == 0;
            if (_leftDown) owner.LeftDown?.Invoke(Screens.Cursor());
            else owner.RightDown?.Invoke(Screens.Cursor());
        }

        public override void MouseUp(NSEvent theEvent)
        {
            if (!_leftDown) return;
            _leftDown = false;
            owner.LeftUp?.Invoke(Screens.Cursor());
        }

        public override void RightMouseDown(NSEvent theEvent) => owner.RightDown?.Invoke(Screens.Cursor());
    }
}

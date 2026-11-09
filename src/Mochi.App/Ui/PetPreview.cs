using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mochi.Core.Animation;
using Mochi.Core.Rig;
using SkiaSharp;

namespace Mochi.App.Ui;

/// <summary>Animated portrait of Mochi for the settings and welcome windows.</summary>
internal sealed class PetPreview : IDisposable
{
    readonly Image _image;
    readonly PetRenderer _renderer;
    readonly PoseAnimator _animator = new(11);
    readonly PetPose _target = new();
    readonly DispatcherTimer _timer;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    WriteableBitmap? _bitmap;
    double _last;

    public PetPreview(Image image, PetLook look)
    {
        _image = image;
        _renderer = new PetRenderer(look);
        _target.ResetToNeutral();
        _target.Sit = 1;
        _target.Front = 1;
        _target.PawsUp = 1;
        _target.TailWrap = 1;
        _animator.Snap(_target);
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Render();
        _timer.Start();
    }

    public void SetLook(PetLook look) => _renderer.SetLook(look);

    void Render()
    {
        if (!_image.IsVisible || _image.ActualWidth < 4) return;
        if (Window.GetWindow(_image) is { WindowState: WindowState.Minimized }) return;
        var dpi = VisualTreeHelper.GetDpi(_image);
        int w = (int)Math.Round(_image.ActualWidth * dpi.DpiScaleX), h = (int)Math.Round(_image.ActualHeight * dpi.DpiScaleY);
        if (_bitmap == null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
        {
            _bitmap = new WriteableBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32, null);
            _image.Source = _bitmap;
        }

        double now = _clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Min(0.1, now - _last);
        _last = now;
        float t = (float)now;
        _target.ResetToNeutral();
        _target.Sit = 1;
        _target.Front = 1;
        _target.PawsUp = 1;
        _target.TailWrap = 1;
        _target.HeadTurn = 0.06f * MathF.Sin(t * 0.7f);
        _target.HeadTilt = -4f + 6f * MathF.Sin(t * 0.45f);
        _target.TailSway = 6f;
        _target.Smile = 0.6f;
        _target.Blush = 0.55f;
        // Every few seconds: a slow "cat kiss" blink at you, then an ear flick.
        float cyc = t % 7f;
        _target.EyeOpen = Mochi.Core.Behaviour.Activity.SlowBlink(cyc, 5.2f);
        _target.EarFlapR = 14f * Mochi.Core.Behaviour.Activity.Pulse(cyc, 2.4f, 0.25f);
        _animator.Update(_target, dt, 0, default);

        _bitmap.Lock();
        try
        {
            using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul), _bitmap.BackBuffer, _bitmap.BackBufferStride);
            var c = surface.Canvas;
            c.Clear(SKColors.Transparent);
            float scale = MathF.Min(w / 110f, h / 140f);
            c.Translate(w / 2f - 4f * scale, h - 10f * scale);
            c.Scale(scale);
            _renderer.Draw(c, _animator.Output);
            c.Flush();
        }
        finally
        {
            _bitmap.AddDirtyRect(new Int32Rect(0, 0, w, h));
            _bitmap.Unlock();
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _renderer.Dispose();
    }
}

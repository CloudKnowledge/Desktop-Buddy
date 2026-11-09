using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SkiaSharp;
using static Mochi.App.Platform.Native;

namespace Mochi.App.Platform;

/// <summary>
/// A borderless, per-pixel-alpha, always-on-top window that never takes focus and never appears in the
/// taskbar or Alt+Tab. Skia renders straight into a DIB section which is pushed with
/// <c>UpdateLayeredWindow</c> — position and pixels update atomically, and fully transparent pixels
/// let clicks fall through to whatever is underneath.
/// </summary>
internal sealed class LayeredWindow : NativeWindow, IDisposable
{
    static readonly IntPtr HandCursor = LoadCursor(IntPtr.Zero, IDC_HAND);

    readonly IntPtr _screenDc, _memDc;
    IntPtr _dib, _oldBitmap, _bits;
    int _width, _height, _originX = -32000, _originY = -32000;
    SKSurface? _surface;
    bool _trackingLeave, _disposed;

    public event Action<SKPoint>? LeftDown, LeftUp, RightUp;
    public event Action<SKPoint, bool>? MouseMoved;
    public event Action? MouseLeft, CaptureLost;
    /// <summary>Raised when the monitor turns off (false) or on (true).</summary>
    public event Action<bool>? DisplayPowerChanged;

    readonly IntPtr _powerNotification;

    public bool IsVisible { get; private set; }

    public LayeredWindow(string title)
    {
        CreateHandle(new CreateParams
        {
            Caption = title,
            Style = WS_POPUP,
            ExStyle = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE,
            X = -32000, Y = -32000, Width = 1, Height = 1,
        });
        _screenDc = GetDC(IntPtr.Zero);
        _memDc = CreateCompatibleDC(_screenDc);
        var displayState = GUID_CONSOLE_DISPLAY_STATE;
        _powerNotification = RegisterPowerSettingNotification(Handle, ref displayState, 0);
    }

    public void SetClickThrough(bool on)
    {
        int ex = GetWindowLong(Handle, GWL_EXSTYLE);
        SetWindowLong(Handle, GWL_EXSTYLE, on ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT);
    }

    /// <summary>Hides the window from screen sharing, recording and screenshots while keeping it visible locally.</summary>
    public bool SetExcludedFromCapture(bool on) => SetWindowDisplayAffinity(Handle, on ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);

    public void Show()
    {
        ShowWindow(Handle, SW_SHOWNOACTIVATE);
        IsVisible = true;
    }

    public void Hide()
    {
        ShowWindow(Handle, SW_HIDE);
        IsVisible = false;
    }

    public void KeepOnTop() => SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    public SKCanvas BeginFrame(int width, int height)
    {
        if (_surface == null || width != _width || height != _height) Allocate(width, height);
        var c = _surface!.Canvas;
        c.ResetMatrix();
        c.Clear(SKColors.Transparent);
        return c;
    }

    public void EndFrame(int x, int y)
    {
        _surface!.Flush();
        GdiFlush();
        var dst = new POINT { X = x, Y = y };
        var size = new SIZE { cx = _width, cy = _height };
        var src = new POINT();
        var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
        UpdateLayeredWindow(Handle, _screenDc, ref dst, ref size, _memDc, ref src, 0, ref blend, ULW_ALPHA);
        _originX = x;
        _originY = y;
    }

    void Allocate(int width, int height)
    {
        FreeBitmap();
        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        _dib = CreateDIBSection(_memDc, ref bmi, 0, out _bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection failed.");
        _oldBitmap = SelectObject(_memDc, _dib);
        _width = width;
        _height = height;
        _surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), _bits, width * 4)
                   ?? throw new InvalidOperationException("Could not create a Skia surface over the DIB.");
    }

    void FreeBitmap()
    {
        _surface?.Dispose();
        _surface = null;
        if (_dib != IntPtr.Zero)
        {
            SelectObject(_memDc, _oldBitmap);
            DeleteObject(_dib);
            _dib = IntPtr.Zero;
        }
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_MOUSEACTIVATE:
                m.Result = MA_NOACTIVATE;
                return;
            case WM_SETCURSOR:
                SetCursor(HandCursor);
                m.Result = 1;
                return;
            case WM_LBUTTONDOWN:
            case WM_LBUTTONDBLCLK:
                SetCapture(Handle);
                LeftDown?.Invoke(PointFrom(m.LParam));
                return;
            case WM_LBUTTONUP:
                // Report the release before dropping capture so a throw keeps its velocity.
                LeftUp?.Invoke(PointFrom(m.LParam));
                ReleaseCapture();
                return;
            case WM_RBUTTONUP:
                RightUp?.Invoke(PointFrom(m.LParam));
                return;
            case WM_MOUSEMOVE:
                if (!_trackingLeave)
                {
                    var tme = new TRACKMOUSEEVENT { cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = TME_LEAVE, hwndTrack = Handle };
                    _trackingLeave = TrackMouseEvent(ref tme);
                }
                MouseMoved?.Invoke(PointFrom(m.LParam), (m.WParam.ToInt64() & 0x1) != 0);
                return;
            case WM_MOUSELEAVE:
                _trackingLeave = false;
                MouseLeft?.Invoke();
                return;
            case WM_CAPTURECHANGED:
                CaptureLost?.Invoke();
                break;
            case WM_POWERBROADCAST when m.WParam == PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero:
                // POWERBROADCAST_SETTING: GUID (16 bytes), DWORD length, then the data (0 = off, 1 = on, 2 = dimmed).
                DisplayPowerChanged?.Invoke(Marshal.ReadInt32(m.LParam, 20) != 0);
                m.Result = 1;
                return;
        }
        base.WndProc(ref m);
    }

    /// <summary>Client coordinates (signed, may be outside the window while captured) to screen pixels.</summary>
    SKPoint PointFrom(IntPtr lParam)
    {
        long v = lParam.ToInt64();
        return new SKPoint(_originX + (short)(v & 0xFFFF), _originY + (short)((v >> 16) & 0xFFFF));
    }

    public static SKPoint CursorPos()
    {
        GetCursorPos(out var p);
        return new SKPoint(p.X, p.Y);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_powerNotification != IntPtr.Zero) UnregisterPowerSettingNotification(_powerNotification);
        FreeBitmap();
        DeleteDC(_memDc);
        ReleaseDC(IntPtr.Zero, _screenDc);
        DestroyHandle();
    }
}

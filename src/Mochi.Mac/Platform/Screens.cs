using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AppKit;
using CoreGraphics;
using Foundation;
using Mochi.Core.Behaviour;
using ServiceManagement;
using SkiaSharp;

namespace Mochi.Mac.Platform;

/// <summary>
/// The displays and the "floor" Mochi walks on: the bottom of each screen's visible area (the top of the Dock).
/// Mochi works in points with the origin at the top-left of the main screen and y pointing down, like on Windows;
/// AppKit's origin is bottom-left, so everything that crosses over goes through <see cref="ToCocoa"/> / <see cref="Cursor"/>.
/// </summary>
internal sealed class Screens : IPetEnvironment
{
    sealed record Screen(string Id, CGRect Frame, CGRect Visible, bool Main);

    static double _primaryHeight = 900;
    List<Screen> _screens = [];
    long _refreshedAt;

    public Screens() => Refresh();

    public void Refresh()
    {
        var list = new List<Screen>();
        var all = NSScreen.Screens;
        for (int i = 0; i < all.Length; i++)
        {
            var s = all[i];
            string id = s.DeviceDescription["NSScreenNumber"] is NSNumber n ? $"display-{n.UInt32Value}" : $"screen-{i}";
            list.Add(new Screen(id, s.Frame, s.VisibleFrame, i == 0));
        }
        if (list.Count > 0)
        {
            _screens = list;
            _primaryHeight = (double)list[0].Frame.Height;
        }
        _refreshedAt = Stopwatch.GetTimestamp();
    }

    /// <summary>Re-reads the layout every few seconds so Dock or resolution changes are picked up.</summary>
    public void RefreshIfStale()
    {
        if (Stopwatch.GetElapsedTime(_refreshedAt).TotalSeconds > 3) Refresh();
    }

    public Floor FloorAt(SKPoint p)
    {
        var s = _screens.FirstOrDefault(s => ToTopLeft(s.Frame).Contains(p))
                ?? _screens.MinBy(s => Distance(ToTopLeft(s.Frame), p));
        return s == null ? new Floor(0, 1440, 25, 860, 1f, "default") : ToFloor(s);
    }

    public Floor Primary => ToFloor(_screens.FirstOrDefault(s => s.Main) ?? _screens[0]);

    public Floor? ById(string? id) => _screens.FirstOrDefault(s => s.Id == id) is { } s ? ToFloor(s) : null;

    // Points are already scaled for Retina, so the kitten's size needs no extra DPI factor.
    static Floor ToFloor(Screen s)
    {
        var v = ToTopLeft(s.Visible);
        return new Floor(v.Left, v.Right, v.Top, v.Bottom - 1, 1f, s.Id);
    }

    static SKRect ToTopLeft(CGRect r) =>
        SKRect.Create((float)r.X, (float)(_primaryHeight - (double)r.Y - (double)r.Height), (float)r.Width, (float)r.Height);

    /// <summary>A top-left-origin rectangle as an AppKit screen rectangle.</summary>
    public static CGRect ToCocoa(SKRect r) => new(r.Left, _primaryHeight - r.Bottom, r.Width, r.Height);

    /// <summary>The mouse pointer in top-left-origin points.</summary>
    public static SKPoint Cursor()
    {
        var p = NSEvent.CurrentMouseLocation;
        return new SKPoint((float)p.X, (float)(_primaryHeight - (double)p.Y));
    }

    static float Distance(SKRect r, SKPoint p)
    {
        float dx = MathF.Max(MathF.Max(r.Left - p.X, 0), p.X - r.Right);
        float dy = MathF.Max(MathF.Max(r.Top - p.Y, 0), p.Y - r.Bottom);
        return dx * dx + dy * dy;
    }
}

/// <summary>Presence signals. Only a timestamp — never input contents.</summary>
internal static class SystemSignals
{
    // kCGAnyInputEventType: keyboard, mouse or trackpad.
    const CGEventType AnyInput = (CGEventType)uint.MaxValue;

    /// <summary>Seconds since the last keyboard, mouse or trackpad input in this session.</summary>
    public static double IdleSeconds() => CGEventSource.GetSecondsSinceLastEventType(CGEventSourceStateID.CombinedSession, AnyInput);
}

/// <summary>Opt-in "open at login" through the system's Login Items (System Settings → General → Login Items).</summary>
internal static class LoginItem
{
    public static bool IsEnabled() => SMAppService.MainApp.Status == SMAppServiceStatus.Enabled;

    public static void Set(bool enabled)
    {
        NSError? error;
        bool ok = enabled ? SMAppService.MainApp.Register(out error) : SMAppService.MainApp.Unregister(out error);
        if (!ok) throw new InvalidOperationException(error?.LocalizedDescription ?? "Login item change failed.");
    }
}

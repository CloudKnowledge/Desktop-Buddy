using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Win32;
using Mochi.Core.Behaviour;
using SkiaSharp;
using static Mochi.App.Platform.Native;

namespace Mochi.App.Platform;

/// <summary>Monitor layout (physical pixels) and the "floor" Mochi walks on: the top of each taskbar.</summary>
internal sealed class Displays : IPetEnvironment
{
    sealed record Monitor(string Id, RECT Bounds, RECT Work, float Scale, bool Primary);

    List<Monitor> _monitors = [];
    long _refreshedAt;

    public Displays() => Refresh();

    public void Refresh()
    {
        var list = new List<Monitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFOEX { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(h, ref info))
            {
                float scale = GetDpiForMonitor(h, 0, out uint dpi, out _) == 0 ? dpi / 96f : 1f;
                list.Add(new Monitor(info.szDevice, info.rcMonitor, info.rcWork, scale, (info.dwFlags & 1) != 0));
            }
            return true;
        }, IntPtr.Zero);
        if (list.Count > 0) _monitors = list;
        _refreshedAt = Stopwatch.GetTimestamp();
    }

    /// <summary>Re-reads the layout every few seconds so taskbar or resolution changes are picked up.</summary>
    public void RefreshIfStale()
    {
        if (Stopwatch.GetElapsedTime(_refreshedAt).TotalSeconds > 3) Refresh();
    }

    public Floor FloorAt(SKPoint p)
    {
        var m = _monitors.FirstOrDefault(m => p.X >= m.Bounds.Left && p.X < m.Bounds.Right && p.Y >= m.Bounds.Top && p.Y < m.Bounds.Bottom)
                ?? _monitors.MinBy(m => Distance(m.Bounds, p))
                ?? new Monitor("default", new RECT { Right = 1920, Bottom = 1080 }, new RECT { Right = 1920, Bottom = 1040 }, 1f, true);
        return ToFloor(m);
    }

    public Floor Primary => ToFloor(_monitors.FirstOrDefault(m => m.Primary) ?? _monitors[0]);

    public Floor? ById(string? id) => _monitors.FirstOrDefault(m => m.Id == id) is { } m ? ToFloor(m) : null;

    static Floor ToFloor(Monitor m) => new(m.Work.Left, m.Work.Right, m.Work.Top, m.Work.Bottom - 1, m.Scale, m.Id);

    static float Distance(RECT r, SKPoint p)
    {
        float dx = MathF.Max(MathF.Max(r.Left - p.X, 0), p.X - r.Right);
        float dy = MathF.Max(MathF.Max(r.Top - p.Y, 0), p.Y - r.Bottom);
        return dx * dx + dy * dy;
    }
}

/// <summary>Presence and context signals. Only timestamps and state flags — never input contents.</summary>
internal static class SystemSignals
{
    /// <summary>Seconds since the last keyboard or mouse input in this session (GetLastInputInfo).</summary>
    public static double IdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        uint now = unchecked((uint)Environment.TickCount);
        return unchecked(now - info.dwTime) / 1000.0;
    }

    /// <summary>
    /// True while a presentation, game, video or other full-screen app is in the foreground. A full-screen
    /// remote desktop or VM window is where you work, so Mochi stays with you there.
    /// </summary>
    public static bool IsFullscreenBusy()
    {
        if (SHQueryUserNotificationState(out int state) != 0) return false;
        if (state is 3 /* D3D full screen */ or 4 /* presentation mode */) return true;
        return state == 2 /* busy: some full-screen window */ && !IsWorkspaceClient(GetForegroundWindow());
    }

    static readonly string[] WorkspaceClients = ["mstsc", "msrdc", "msrdcw", "vmconnect", "vmware-vmx", "vmware", "VirtualBoxVM", "Windows365", "WindowsApp"];
    static uint _lastPid;
    static bool _lastWasWorkspace;

    static bool IsWorkspaceClient(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == _lastPid) return _lastWasWorkspace;
        _lastPid = pid;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            _lastWasWorkspace = WorkspaceClients.Any(n => p.ProcessName.Equals(n, StringComparison.OrdinalIgnoreCase));
        }
        catch (ArgumentException) { _lastWasWorkspace = false; }
        catch (InvalidOperationException) { _lastWasWorkspace = false; }
        return _lastWasWorkspace;
    }

    /// <summary>
    /// True when the workstation is locked (or on the secure desktop). Polled rather than event-driven so it
    /// is also correct when Mochi was started while the session was already locked.
    /// </summary>
    public static bool IsSessionLocked()
    {
        const uint DESKTOP_READOBJECTS = 0x0001;
        IntPtr desk = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (desk == IntPtr.Zero) return true; // the Winlogon desktop is not accessible to us
        try
        {
            var name = new char[64];
            if (!GetUserObjectInformation(desk, 2 /* UOI_NAME */, name, name.Length * 2, out _)) return false;
            return !new string(name).TrimEnd('\0').Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseDesktop(desk);
        }
    }
}

/// <summary>Opt-in "start when I sign in" via the per-user Run key — transparent and removable, no admin rights.</summary>
internal static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Mochi";

    static string Command => $"\"{Environment.ProcessPath}\" --startup";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string s && s.Equals(Command, StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

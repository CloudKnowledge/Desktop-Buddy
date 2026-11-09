using SkiaSharp;

namespace Mochi.Core.Behaviour;

/// <summary>Every behaviour state Mochi can be in.</summary>
public enum PetState
{
    Idle, Walking, Running, Sleeping, Drowsy, Happy, Excited, Curious, Playful, Scared, Surprised, Sad,
    Stretching, Sitting, LyingDown, FollowingCursor, ChasingBall, CarryingBall, BeingPetted, Dreaming,
    Greeting, Celebrating, Held, Falling, Landing, Grooming, Kneading, Hunting, Eating, WatchingTv, Working,
}

/// <summary>How much of Mochi's gaze an activity lets the cursor-tracking overlay control.</summary>
public enum Gaze { None, Eyes, EyesAndHead }

/// <summary>Part of the pet under the pointer.</summary>
public enum HitRegion { None, Head, Body }

/// <summary>
/// The walkable floor at a point on the desktop: the bottom of a monitor's work area (top of the taskbar),
/// in physical screen pixels, plus that monitor's DPI scale.
/// </summary>
public readonly record struct Floor(float Left, float Right, float Top, float Y, float DpiScale, string Id)
{
    public float Width => Right - Left;
}

/// <summary>Host-provided information about the desktop.</summary>
public interface IPetEnvironment
{
    /// <summary>The floor of the monitor containing (or nearest to) <paramref name="screenPoint"/>.</summary>
    Floor FloorAt(SKPoint screenPoint);
}

/// <summary>What Mochi can perceive this frame. Nothing here is stored or transmitted.</summary>
public struct Senses
{
    /// <summary>Cursor position in physical screen pixels.</summary>
    public SKPoint Cursor;
    public bool CursorKnown;
    /// <summary>Seconds since the last keyboard/mouse input anywhere (from the OS idle timer).</summary>
    public double IdleSeconds;
    public DateTime LocalTime;
}

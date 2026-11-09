namespace Mochi.Core.Play;

public enum PropKind { None, Milk, Treats, Tv, Laptop }

/// <summary>
/// Something Mochi is using right now — a saucer of milk, a bowl of treats or a little TV. Positioned in
/// model units in front of Mochi (facing space, ground at y = 0) and drawn in Mochi's own window.
/// </summary>
public sealed class Prop(PropKind kind, float x, int seed)
{
    public PropKind Kind { get; } = kind;
    /// <summary>Model units in front of Mochi's anchor.</summary>
    public float X { get; } = x;
    public int Seed { get; } = seed;
    /// <summary>0..1 fade in/out.</summary>
    public float Alpha { get; set; }
    /// <summary>Milk level, treats left, or how far the laptop lid is open, 0..1.</summary>
    public float Fill { get; set; } = 1f;
    /// <summary>Seconds since it appeared (drives the TV show).</summary>
    public float Time { get; set; }
}

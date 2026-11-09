using SkiaSharp;

namespace Mochi.Core.Rig;

public enum CoatPreset { Golden, Cream, Ginger, Silver }

public enum AccessoryKind { None, Bell, Bow, FairyLights }

/// <summary>Colour palette and accessories. Everything visual that a user may customise lives here.</summary>
public sealed record PetLook
{
    public required SKColor Fur { get; init; }
    public required SKColor FurLight { get; init; }
    public required SKColor FurShade { get; init; }
    /// <summary>Darker tipping on the back, forehead and tail – the "shaded golden" look.</summary>
    public required SKColor FurDeep { get; init; }
    /// <summary>Ruff, chest, whisker pads and belly.</summary>
    public required SKColor Cream { get; init; }
    public required SKColor Outline { get; init; }
    public required SKColor IrisLight { get; init; }
    public required SKColor Iris { get; init; }
    public required SKColor IrisDark { get; init; }
    public SKColor Pupil { get; init; } = new(0x14, 0x10, 0x0E);
    public SKColor EyeRim { get; init; } = new(0x2A, 0x1C, 0x14);
    public SKColor Nose { get; init; } = new(0xDE, 0x7F, 0x8C);
    public SKColor EarInner { get; init; } = new(0xF5, 0xB4, 0xBE);
    public SKColor Tongue { get; init; } = new(0xF2, 0x8C, 0x9E);
    public SKColor MouthInside { get; init; } = new(0x8E, 0x39, 0x46);
    public SKColor Blush { get; init; } = new(0xFF, 0x9B, 0xB0);
    public SKColor PawPad { get; init; } = new(0xF2, 0x9F, 0xB2);
    public SKColor Whisker { get; init; } = new(0xFF, 0xFD, 0xF6);
    /// <summary>Tabby markings and how strong they are (0 = plain coat).</summary>
    public SKColor Stripe { get; init; } = new(0xC0, 0x5C, 0x18);
    public float Stripes { get; init; } = 1f;

    public AccessoryKind Accessory { get; init; } = AccessoryKind.FairyLights;
    public SKColor AccessoryColor { get; init; } = new(0xE8, 0x6A, 0x92);

    /// <summary>Golden-orange tabby with big teal eyes.</summary>
    public static PetLook Golden { get; } = new()
    {
        Fur = new(0xF2, 0xA2, 0x4A),
        FurLight = new(0xFF, 0xD3, 0x8E),
        FurShade = new(0xD9, 0x7E, 0x30),
        FurDeep = new(0xB8, 0x5E, 0x1E),
        Cream = new(0xFF, 0xF1, 0xD8),
        Outline = new(0x7C, 0x3E, 0x12),
        Stripe = new(0xC4, 0x5E, 0x1A),
        IrisLight = new(0x9C, 0xF2, 0xE4),
        Iris = new(0x2C, 0xB0, 0xA8),
        IrisDark = new(0x0C, 0x5A, 0x62),
    };

    public static PetLook CreamCoat { get; } = new()
    {
        Fur = new(0xF4, 0xDA, 0xB0),
        FurLight = new(0xFF, 0xF1, 0xDA),
        FurShade = new(0xDE, 0xB9, 0x86),
        FurDeep = new(0xC4, 0x98, 0x60),
        Cream = new(0xFF, 0xF9, 0xEE),
        Outline = new(0x8C, 0x6B, 0x44),
        Stripe = new(0xD2, 0xA0, 0x66),
        Stripes = 0.75f,
        IrisLight = new(0xF9, 0xC2, 0x5A),
        Iris = new(0xE0, 0x8A, 0x2E),
        IrisDark = new(0x95, 0x4E, 0x15),
        AccessoryColor = new(0x5B, 0x9B, 0xE6),
    };

    public static PetLook Ginger { get; } = new()
    {
        Fur = new(0xE2, 0x7C, 0x36),
        FurLight = new(0xF6, 0xB7, 0x76),
        FurShade = new(0xC0, 0x62, 0x24),
        FurDeep = new(0x9C, 0x48, 0x16),
        Cream = new(0xFC, 0xE6, 0xC8),
        Outline = new(0x6A, 0x32, 0x0E),
        Stripe = new(0xA2, 0x42, 0x10),
        IrisLight = new(0xE8, 0xEA, 0x7C),
        Iris = new(0x9E, 0xBA, 0x40),
        IrisDark = new(0x4E, 0x6A, 0x1C),
        AccessoryColor = new(0x2E, 0x9E, 0x7A),
    };

    /// <summary>Silver tabby with sea-green eyes.</summary>
    public static PetLook Silver { get; } = new()
    {
        Fur = new(0xD9, 0xDC, 0xE3),
        FurLight = new(0xF7, 0xF8, 0xFB),
        FurShade = new(0xB4, 0xB9, 0xC4),
        FurDeep = new(0x8E, 0x94, 0xA2),
        Cream = new(0xFF, 0xFF, 0xFF),
        Outline = new(0x55, 0x5B, 0x68),
        Stripe = new(0x5C, 0x62, 0x70),
        IrisLight = new(0xB5, 0xEC, 0xC4),
        Iris = new(0x43, 0xB0, 0x88),
        IrisDark = new(0x1D, 0x6B, 0x55),
        Nose = new(0xE5, 0x8C, 0x98),
        AccessoryColor = new(0x8E, 0x6C, 0xD8),
    };

    public static PetLook FromPreset(CoatPreset preset) => preset switch
    {
        CoatPreset.Cream => CreamCoat,
        CoatPreset.Ginger => Ginger,
        CoatPreset.Silver => Silver,
        _ => Golden,
    };

    /// <summary>Named accessory colours offered in the menu.</summary>
    public static IReadOnlyList<(string Name, SKColor Color)> AccessoryColors { get; } =
    [
        ("Rose pink", new SKColor(0xE8, 0x6A, 0x92)),
        ("Sky blue", new SKColor(0x5B, 0x9B, 0xE6)),
        ("Mint", new SKColor(0x2E, 0x9E, 0x7A)),
        ("Lavender", new SKColor(0x8E, 0x6C, 0xD8)),
        ("Cherry red", new SKColor(0xE2, 0x4B, 0x55)),
        ("Sunflower", new SKColor(0xF2, 0xB7, 0x2C)),
    ];
}

namespace Mochi.Core.Behaviour;

public enum PersonalityProfile { Playful, Lazy, Curious, Shy, Affectionate, Mischievous }

/// <summary>
/// Six traits in 0..1. A chosen profile sets the baseline; everyday interactions nudge the traits a little
/// (bounded drift), so Mochi slowly reflects how you spend time together without ever guilt-tripping you.
/// </summary>
public sealed class Personality
{
    public const float MaxDrift = 0.25f;

    public PersonalityProfile Profile { get; private set; }
    public float Playful => Clamp(_base[0] + Drift[0]);
    public float Lazy => Clamp(_base[1] + Drift[1]);
    public float Curious => Clamp(_base[2] + Drift[2]);
    public float Shy => Clamp(_base[3] + Drift[3]);
    public float Affectionate => Clamp(_base[4] + Drift[4]);
    public float Mischievous => Clamp(_base[5] + Drift[5]);

    /// <summary>Learned offsets per trait (persisted). Order: playful, lazy, curious, shy, affectionate, mischievous.</summary>
    public float[] Drift { get; } = new float[6];

    float[] _base = new float[6];

    public Personality(PersonalityProfile profile, float[]? drift = null)
    {
        SetProfile(profile);
        if (drift is { Length: 6 })
            for (int i = 0; i < 6; i++) Drift[i] = Math.Clamp(drift[i], -MaxDrift, MaxDrift);
    }

    public void SetProfile(PersonalityProfile profile)
    {
        Profile = profile;
        _base = profile switch
        {
            PersonalityProfile.Lazy => [0.3f, 0.9f, 0.3f, 0.3f, 0.5f, 0.1f],
            PersonalityProfile.Curious => [0.5f, 0.3f, 0.9f, 0.2f, 0.5f, 0.3f],
            PersonalityProfile.Shy => [0.3f, 0.5f, 0.5f, 0.9f, 0.4f, 0.1f],
            PersonalityProfile.Affectionate => [0.5f, 0.4f, 0.5f, 0.1f, 0.95f, 0.2f],
            PersonalityProfile.Mischievous => [0.7f, 0.3f, 0.6f, 0.1f, 0.5f, 0.9f],
            _ => [0.9f, 0.2f, 0.6f, 0.1f, 0.6f, 0.4f],
        };
    }

    public enum Trait { Playful, Lazy, Curious, Shy, Affectionate, Mischievous }

    /// <summary>Nudges a learned trait; tiny steps so change happens over days, not minutes.</summary>
    public void Nudge(Trait trait, float amount)
    {
        int i = (int)trait;
        Drift[i] = Math.Clamp(Drift[i] + amount, -MaxDrift, MaxDrift);
    }

    static float Clamp(float v) => Math.Clamp(v, 0f, 1f);

    public static string Describe(PersonalityProfile p) => p switch
    {
        PersonalityProfile.Playful => "Pounces, stalks your cursor and gets the zoomies",
        PersonalityProfile.Lazy => "Loafs and naps a lot, very chill",
        PersonalityProfile.Curious => "Watches your cursor and explores the screen",
        PersonalityProfile.Shy => "Keeps to the corners, warms up slowly",
        PersonalityProfile.Affectionate => "Purrs, slow-blinks and loves head pats",
        PersonalityProfile.Mischievous => "Swats at your cursor and pulls little pranks",
        _ => "",
    };
}

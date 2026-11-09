using Mochi.Core.Behaviour;
using Mochi.Core.Persistence;
using Mochi.Core.Rig;

namespace Mochi.Core.Tests;

public sealed class PersistenceTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "mochi-tests-" + Guid.NewGuid().ToString("N"));

    public PersistenceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Settings_round_trip()
    {
        var path = Path.Combine(_dir, "settings.json");
        var s = new MochiSettings { PetName = "Biscuit", Personality = PersonalityProfile.Lazy, Coat = CoatPreset.Ginger, Accessory = AccessoryKind.FairyLights, Size = 1.4f, AutoSleep = false };
        JsonStore.Save(path, s);
        var loaded = JsonStore.Load<MochiSettings>(path);
        Assert.Equal("Biscuit", loaded.PetName);
        Assert.Equal(PersonalityProfile.Lazy, loaded.Personality);
        Assert.Equal(CoatPreset.Ginger, loaded.Coat);
        Assert.Equal(AccessoryKind.FairyLights, loaded.Accessory);
        Assert.Equal(1.4f, loaded.Size);
        Assert.False(loaded.AutoSleep);
        Assert.Contains("\"Lazy\"", File.ReadAllText(path)); // human-readable enums
    }

    [Theory]
    [InlineData("Red", "Bandana", CoatPreset.Ginger, AccessoryKind.Bow)]
    [InlineData("Cream", "Collar", CoatPreset.Cream, AccessoryKind.Bell)]
    [InlineData("Sparkly", "Tiara", CoatPreset.Golden, AccessoryKind.None)]
    public void Settings_saved_by_the_puppy_version_still_load(string coat, string accessory, CoatPreset expectedCoat, AccessoryKind expectedAccessory)
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, $$"""{ "PetName": "Biscuit", "Coat": "{{coat}}", "Accessory": "{{accessory}}", "Size": 1.2 }""");
        string? logged = null;
        var s = JsonStore.Load<MochiSettings>(path, msg => logged = msg);
        Assert.Null(logged);
        Assert.Equal("Biscuit", s.PetName);
        Assert.Equal(expectedCoat, s.Coat);
        Assert.Equal(expectedAccessory, s.Accessory);
        Assert.Equal(1.2f, s.Size);
    }

    [Fact]
    public void Corrupt_files_fall_back_to_defaults_and_are_kept_for_inspection()
    {
        var path = Path.Combine(_dir, "memory.json");
        File.WriteAllText(path, "{ this is not json");
        string? logged = null;
        var m = JsonStore.Load<PetMemory>(path, msg => logged = msg);
        Assert.NotNull(m);
        Assert.Equal(0, m.TimesPetted);
        Assert.NotNull(logged);
        Assert.True(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void Missing_files_give_defaults()
    {
        var s = JsonStore.Load<MochiSettings>(Path.Combine(_dir, "nope.json"));
        Assert.Equal("Mochi", s.PetName);
        Assert.True(s.HideFromScreenCapture);
        Assert.False(s.StartWithWindows); // start-up is opt-in
    }

    [Fact]
    public void Normalize_repairs_out_of_range_values()
    {
        var s = new MochiSettings { PetName = "  ", Size = 99f, Liveliness = float.NaN, DrowsyAfterSeconds = 300, SleepAfterSeconds = 100, MaxFps = 144, AccessoryColorIndex = 77 };
        s.Normalize();
        Assert.Equal("Mochi", s.PetName);
        Assert.Equal(2.5f, s.Size);
        Assert.Equal(0.5f, s.Liveliness);
        Assert.True(s.SleepAfterSeconds > s.DrowsyAfterSeconds);
        Assert.Equal(60, s.MaxFps);
        Assert.InRange(s.AccessoryColorIndex, 0, PetLook.AccessoryColors.Count - 1);

        var m = new PetMemory { TraitDrift = [5f, -5f, float.NaN], FloorFraction = 7 };
        m.Normalize();
        Assert.Equal(6, m.TraitDrift.Length);
        Assert.All(m.TraitDrift, d => Assert.InRange(d, -Personality.MaxDrift, Personality.MaxDrift));
        Assert.Equal(-1, m.FloorFraction);
    }

    [Fact]
    public void Personality_drift_is_bounded()
    {
        var p = new Personality(PersonalityProfile.Shy);
        for (int i = 0; i < 10_000; i++) p.Nudge(Personality.Trait.Affectionate, 0.01f);
        Assert.Equal(Personality.MaxDrift, p.Drift[(int)Personality.Trait.Affectionate]);
        Assert.InRange(p.Affectionate, 0f, 1f);
    }
}

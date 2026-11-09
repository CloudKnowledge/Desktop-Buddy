using System.Text.Json;
using System.Text.Json.Serialization;
using Mochi.Core.Behaviour;
using Mochi.Core.Rig;

namespace Mochi.Core.Persistence;

/// <summary>User preferences. Persisted to settings.json.</summary>
public sealed class MochiSettings
{
    public int Version { get; set; } = 1;
    public string PetName { get; set; } = "Mochi";
    public PersonalityProfile Personality { get; set; } = PersonalityProfile.Playful;
    public CoatPreset Coat { get; set; } = CoatPreset.Golden;
    public AccessoryKind Accessory { get; set; } = AccessoryKind.FairyLights;
    public int AccessoryColorIndex { get; set; }
    /// <summary>Size multiplier on top of the monitor DPI scale.</summary>
    public float Size { get; set; } = 1.0f;
    /// <summary>0 = calm, 1 = lively. Scales how often Mochi wanders.</summary>
    public float Liveliness { get; set; } = 0.5f;

    public bool AutoSleep { get; set; } = true;
    public int DrowsyAfterSeconds { get; set; } = 90;
    public int SleepAfterSeconds { get; set; } = 240;

    public bool ShowEffects { get; set; } = true;
    public bool ClickThrough { get; set; }
    public bool HideFromScreenCapture { get; set; } = true;
    public bool HideInFullscreen { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public int MaxFps { get; set; } = 60;
    public bool FirstRunDone { get; set; }

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(PetName)) PetName = "Mochi";
        PetName = PetName.Trim();
        if (PetName.Length > 24) PetName = PetName[..24];
        Size = Math.Clamp(float.IsFinite(Size) ? Size : 1f, 0.5f, 2.5f);
        Liveliness = Math.Clamp(float.IsFinite(Liveliness) ? Liveliness : 0.5f, 0f, 1f);
        DrowsyAfterSeconds = Math.Clamp(DrowsyAfterSeconds, 20, 3600);
        SleepAfterSeconds = Math.Clamp(SleepAfterSeconds, DrowsyAfterSeconds + 10, 7200);
        MaxFps = MaxFps is 30 or 60 ? MaxFps : 60;
        if (!Enum.IsDefined(Personality)) Personality = PersonalityProfile.Playful;
        if (!Enum.IsDefined(Coat)) Coat = CoatPreset.Golden;
        if (!Enum.IsDefined(Accessory)) Accessory = AccessoryKind.FairyLights;
        AccessoryColorIndex = Math.Clamp(AccessoryColorIndex, 0, PetLook.AccessoryColors.Count - 1);
    }

    public PetLook BuildLook()
    {
        var look = PetLook.FromPreset(Coat);
        return look with { Accessory = Accessory, AccessoryColor = PetLook.AccessoryColors[AccessoryColorIndex].Color };
    }
}

/// <summary>What Mochi remembers between runs. Persisted to memory.json, never leaves the machine.</summary>
public sealed class PetMemory
{
    public int Version { get; set; } = 1;
    /// <summary>Monitor the pet was last on and the fraction (0..1) across its floor.</summary>
    public string? MonitorId { get; set; }
    public double FloorFraction { get; set; } = -1;
    public int Facing { get; set; } = 1;

    public float[] TraitDrift { get; set; } = new float[6];
    public int TimesPetted { get; set; }
    public int TimesClicked { get; set; }
    public int TimesCarried { get; set; }
    public int TimesGreeted { get; set; }
    public int TimesPlayedBall { get; set; }
    public DateTime FirstMetUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public string? LastMorningGreeting { get; set; }

    /// <summary>0..1 bond that grows slowly with interactions; scales greeting enthusiasm.</summary>
    [JsonIgnore]
    public float Bond => Math.Clamp((TimesPetted * 2 + TimesClicked + TimesCarried + TimesGreeted * 3) / 400f, 0f, 1f);

    public void Normalize()
    {
        if (TraitDrift is not { Length: 6 }) TraitDrift = new float[6];
        for (int i = 0; i < 6; i++)
            TraitDrift[i] = float.IsFinite(TraitDrift[i]) ? Math.Clamp(TraitDrift[i], -Personality.MaxDrift, Personality.MaxDrift) : 0f;
        if (FloorFraction is not (>= 0 and <= 1)) FloorFraction = -1;
        Facing = Facing >= 0 ? 1 : -1;
    }
}

/// <summary>Small JSON persistence helper with atomic writes and corrupt-file recovery.</summary>
public static class JsonStore
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new LenientEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static T Load<T>(string path, Action<string>? log = null) where T : new()
    {
        try
        {
            if (!File.Exists(path)) return new T();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, Options) ?? new T();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            log?.Invoke($"Could not read {Path.GetFileName(path)} ({ex.Message}); starting fresh and keeping a backup.");
            try { File.Copy(path, path + ".corrupt", overwrite: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return new T();
        }
    }

    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>
/// Writes enums as names and reads them forgivingly: unknown values fall back to the default instead of
/// failing the whole file, and names from older versions (e.g. the puppy's "Bandana") are migrated.
/// </summary>
sealed class LenientEnumConverter : JsonConverterFactory
{
    static readonly Dictionary<string, string> Legacy = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bandana"] = nameof(AccessoryKind.Bow),
        ["Collar"] = nameof(AccessoryKind.Bell),
        ["Red"] = nameof(CoatPreset.Ginger),
    };

    public override bool CanConvert(Type t) => t.IsEnum;

    public override JsonConverter CreateConverter(Type t, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(t))!;

    sealed class Converter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int n) && Enum.IsDefined(typeof(T), n))
                return (T)Enum.ToObject(typeof(T), n);
            if (reader.TokenType != JsonTokenType.String) return default;
            var name = reader.GetString() ?? "";
            if (Enum.TryParse<T>(name, ignoreCase: true, out var value) && Enum.IsDefined(value)) return value;
            return Legacy.TryGetValue(name, out var mapped) && Enum.TryParse<T>(mapped, out var migrated) ? migrated : default;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }
}

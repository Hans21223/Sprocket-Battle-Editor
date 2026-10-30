namespace SprocketBattles;

/// Named, fixed lighting choices shared by battle files, menu controls and the runtime lighting owner.
/// Map default deliberately has no preset: old battles retain the map author's lighting.
public static class BattleTimeOfDay
{
    public const string MapDefault = "Map default";

    public readonly record struct Rgb(float R, float G, float B);

    /// Angles are degrees, the directional light's brightness is lux, and sky colours are linear RGB.
    /// At Night the directional light is a dim blue moon rather than a daytime sun.
    public sealed record Preset(string Name, float Hour, float SunElevation, float SunAzimuth, float SunLux,
        Rgb SunColor, Rgb SkyTop, Rgb SkyMiddle, Rgb SkyBottom)
    {
        public float CycleFraction => ToCycleFraction(Hour);
    }

    static readonly Preset[] presets =
    {
        new("Dawn", 6, 4, 80, 6000, new(1, .56f, .30f),
            new(.20f, .28f, .46f), new(.68f, .33f, .22f), new(.08f, .09f, .12f)),
        new("Morning", 9, 28, 110, 45000, new(1, .90f, .76f),
            new(.30f, .50f, .88f), new(.62f, .72f, .86f), new(.16f, .19f, .23f)),
        new("Noon", 12, 70, 165, 110000, new(1, .98f, .94f),
            new(.30f, .52f, .92f), new(.65f, .76f, .90f), new(.20f, .22f, .25f)),
        new("Afternoon", 15, 38, 230, 65000, new(1, .87f, .69f),
            new(.26f, .43f, .76f), new(.64f, .66f, .71f), new(.17f, .18f, .20f)),
        new("Sunset", 17.75f, 3, 275, 4000, new(1, .40f, .16f),
            new(.16f, .19f, .34f), new(.76f, .27f, .12f), new(.045f, .05f, .07f)),
        new("Night", 0, 30, 310, .4f, new(.58f, .68f, 1),
            new(.004f, .006f, .018f), new(.012f, .018f, .038f), new(.003f, .004f, .01f)),
    };

    public static IReadOnlyList<string> Names { get; } =
        Array.AsReadOnly(new[] { MapDefault }.Concat(presets.Select(p => p.Name)).ToArray());

    public static bool IsKnown(string? name) => name == MapDefault || presets.Any(p => p.Name == name);

    /// Unknown or explicit null values are refused, rather than silently changing a shared battle's lighting.
    public static string? Check(string? name) => IsKnown(name) ? null
        : "the time of day is not supported. Choose Map default, Dawn, Morning, Noon, Afternoon, Sunset or Night.";

    public static bool TryGet(string? name, out Preset preset)
    {
        preset = presets.FirstOrDefault(p => p.Name == name)!;
        return preset != null;
    }

    /// The native cycle begins at dawn (06:00), rather than midnight.
    public static float ToCycleFraction(float hour)
    {
        if (!float.IsFinite(hour)) throw new ArgumentOutOfRangeException(nameof(hour), "The time must be finite.");
        double shifted = ((double)hour - 6) % 24;
        if (shifted < 0) shifted += 24;
        // A float just before dawn can round a fraction just below one up to one.
        return MathF.Min((float)(shifted / 24), MathF.BitDecrement(1f));
    }

    /// UI arrows may wrap in either direction. A malformed in-memory choice returns to the safe map default.
    public static string Next(string? current, int direction = 1)
    {
        int index = -1;
        for (int i = 0; i < Names.Count; i++) if (Names[i] == current) { index = i; break; }
        if (index < 0) return MapDefault;
        int step = direction % Names.Count;
        return Names[(index + step + Names.Count) % Names.Count];
    }
}

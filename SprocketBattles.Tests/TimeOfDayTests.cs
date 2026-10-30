using SprocketBattles;

internal static class TimeOfDayTests
{
    static int checks;
    static void Check(bool ok, string why)
    {
        checks++;
        if (!ok) throw new Exception("time of day: " + why);
    }

    internal static void Run()
    {
        checks = 0;
        Check(BattleTimeOfDay.Names.SequenceEqual(new[]
            { "Map default", "Dawn", "Morning", "Noon", "Afternoon", "Sunset", "Night" }),
            "the menu offers a map-preserving default and the expected named choices");
        Check(BattleTimeOfDay.Names.Distinct(StringComparer.Ordinal).Count() == BattleTimeOfDay.Names.Count,
            "each saved choice has one unambiguous canonical name");
        Check(BattleTimeOfDay.Names is not string[], "callers cannot mutate the shared menu choice array");
        Check(new BattleFile().TimeOfDay == BattleTimeOfDay.MapDefault
            && BattleFile.FromJson("{\"name\":\"Old battle\",\"map\":\"Fields\"}").TimeOfDay == BattleTimeOfDay.MapDefault,
            "new and legacy missing-field battles preserve authored lighting");
        Check(BattleTimeOfDay.Check(BattleTimeOfDay.MapDefault) == null
            && !BattleTimeOfDay.TryGet(BattleTimeOfDay.MapDefault, out _),
            "map default is valid without producing a lighting override");
        Check(!BattleTimeOfDay.TryGet(null, out _) && !BattleTimeOfDay.TryGet("invalid", out _),
            "no preset is generated for a malformed choice");
        foreach (string name in BattleTimeOfDay.Names)
        {
            var original = new BattleFile { Name = "Lighting", Map = "Fields", TimeOfDay = name };
            var roundTrip = BattleFile.FromJson(original.ToJson());
            Check(roundTrip.TimeOfDay == name && roundTrip.CheckData() == null,
                name + " survives save/read with valid battle data");
            var withPick = new BattleFile { TimeOfDay = name, Units = {
                new BattleUnit { Id = "u1", Pick = true, Control = "player", Blueprint = "Stand-in" } } };
            Check(withPick.WithPicks(new[] { "Chosen" }).TimeOfDay == name,
                name + " survives the clone used when substituting the player's tank");
            Check(BattleTimeOfDay.IsKnown(name), name + " is recognized by validation");
            Check(BattleTimeOfDay.Next(BattleTimeOfDay.Next(name), -1) == name,
                name + " arrows reverse without changing the saved choice");
        }
        foreach (string name in BattleTimeOfDay.Names.Skip(1))
        {
            Check(BattleTimeOfDay.TryGet(name, out var preset) && preset.Name == name,
                name + " resolves to its named immutable preset");
            Check(float.IsFinite(preset.Hour) && preset.Hour >= 0 && preset.Hour < 24
                && float.IsFinite(preset.SunElevation) && preset.SunElevation is >= -90 and <= 90
                && float.IsFinite(preset.SunAzimuth) && preset.SunAzimuth is >= 0 and < 360
                && float.IsFinite(preset.SunLux) && preset.SunLux > 0,
                name + " has usable finite angles, clock hour and light intensity");
            foreach (var color in new[] { preset.SunColor, preset.SkyTop, preset.SkyMiddle, preset.SkyBottom })
                Check(float.IsFinite(color.R) && color.R is >= 0 and <= 1
                    && float.IsFinite(color.G) && color.G is >= 0 and <= 1
                    && float.IsFinite(color.B) && color.B is >= 0 and <= 1,
                    name + " colour channels remain finite and bounded");
        }
        foreach (var expected in new[] { ("Dawn", 0f), ("Morning", .125f), ("Noon", .25f),
            ("Afternoon", .375f), ("Sunset", (17.75f - 6) / 24), ("Night", .75f) })
        {
            BattleTimeOfDay.TryGet(expected.Item1, out var preset);
            Check(Math.Abs(preset.CycleFraction - expected.Item2) < .000001f,
                expected.Item1 + " maps to the native cycle that starts at 06:00");
        }
        Check(BattleTimeOfDay.ToCycleFraction(24) == .75f
            && BattleTimeOfDay.ToCycleFraction(30) == 0
            && BattleTimeOfDay.ToCycleFraction(-6) == .5f,
            "clock hours wrap across midnight and previous days");
        foreach (float hour in new[] { -100f, -24f, -.5f, 0, 5.5f, MathF.BitDecrement(6f), 23.9999f, 100f, float.MinValue, float.MaxValue })
        {
            float fraction = BattleTimeOfDay.ToCycleFraction(hour);
            Check(float.IsFinite(fraction) && fraction >= 0 && fraction < 1,
                "every finite clock value yields a bounded native cycle fraction: " + hour);
        }
        foreach (float hour in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            bool rejected = false;
            try { BattleTimeOfDay.ToCycleFraction(hour); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected, "nonfinite clock hours never reach the native environment");
        }
        BattleTimeOfDay.TryGet("Noon", out var noon);
        BattleTimeOfDay.TryGet("Night", out var night);
        BattleTimeOfDay.TryGet("Sunset", out var sunset);
        Check(sunset.CycleFraction > .45f && sunset.CycleFraction < .5f && night.CycleFraction >= .5f,
            "sunset remains on the native sun side of the half-cycle transition; night uses the moon");
        Check(noon.SunLux > night.SunLux * 10000 && noon.SkyTop.B > night.SkyTop.B * 10,
            "night has distinct moon/ambient settings instead of a renamed daytime preset");
        Check(BattleTimeOfDay.Next("Night") == BattleTimeOfDay.MapDefault
            && BattleTimeOfDay.Next(BattleTimeOfDay.MapDefault, -1) == "Night",
            "arrows wrap at both ends of the menu");
        Check(BattleTimeOfDay.Next("Noon", 0) == "Noon"
            && BattleTimeOfDay.Next("Noon", int.MaxValue) == BattleTimeOfDay.Names[(3 + int.MaxValue % 7) % 7]
            && BattleTimeOfDay.Next("Noon", int.MinValue) == BattleTimeOfDay.Names[(3 + int.MinValue % 7 + 7) % 7],
            "zero and extreme direction steps cannot overflow an index");
        Check(BattleTimeOfDay.Next(null) == BattleTimeOfDay.MapDefault
            && BattleTimeOfDay.Next("invalid", -1) == BattleTimeOfDay.MapDefault,
            "invalid in-memory UI choices return safely to authored lighting");
        foreach (string json in new[]
            { "{\"timeOfDay\":null}", "{\"timeOfDay\":\"\"}", "{\"timeOfDay\":\"Storm\"}", "{\"timeOfDay\":\"noon\"}" })
        {
            bool rejected = false;
            try { BattleFile.FromJson(json); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "unsupported explicit saved values are refused: " + json);
        }
        Check(new BattleFile { TimeOfDay = null! }.CheckData() != null
            && new BattleFile { TimeOfDay = "invalid" }.CheckData(forPlay: true) != null,
            "invalid live data is caught before runtime lighting can consume it");
        GauntletSaveData RunWithTime(string choice) => new()
        {
            Id = "time-test", Settings = new GauntletSettings { Map = "Fields", TimeOfDay = choice },
            Credits = 100000, Capacity = 8
        };
        foreach (string choice in BattleTimeOfDay.Names)
        {
            var saved = RunWithTime(choice);
            Check(saved.Settings.Check() == null && GauntletSaveData.FromJson(saved.ToJson())?.Settings.TimeOfDay == choice,
                choice + " survives saved Gauntlet settings for subsequent rounds and resume");
        }
        Check(new GauntletSettings { Map = "Fields" }.TimeOfDay == BattleTimeOfDay.MapDefault,
            "new Gauntlet runs retain map lighting until a time is chosen");
        var legacyRun = System.Text.Json.Nodes.JsonNode.Parse(RunWithTime("Night").ToJson())!.AsObject();
        legacyRun["Settings"]!.AsObject().Remove("TimeOfDay");
        Check(GauntletSaveData.FromJson(legacyRun.ToJsonString())?.Settings.TimeOfDay == BattleTimeOfDay.MapDefault,
            "older saved Gauntlet settings with no time field remain resumable with map lighting");
        foreach (string choice in new[] { null!, "", "invalid" })
        {
            var invalidRun = RunWithTime(choice);
            Check(invalidRun.Settings.Check() != null && GauntletSaveData.FromJson(invalidRun.ToJson()) == null,
                "malformed Gauntlet lighting cannot reach the next native round");
        }
        Console.WriteLine($"TIME_OF_DAY_TESTS_OK: {checks} checks");
    }
}

using SprocketBattles;

static class DesignPoolRulesTests
{
    static void Check(bool value, string why)
    { if (!value) throw new Exception("design pool: " + why); }

    internal static void Run()
    {
        var designs = new[]
        {
            (Name: "Player light", Faction: "PMC", BaseGame: false),
            (Name: "Enemy heavy", Faction: "Red Army", BaseGame: false),
            (Name: "Another PMC", Faction: "PMC Reserve", BaseGame: false),
            (Name: "Native tank", Faction: "Base game", BaseGame: true),
            (Name: "Custom name collision", Faction: "Base game", BaseGame: false),
        };
        string[] Select(int pool, string? faction) => designs.Where(d => DesignPoolRules.Matches(pool, d.BaseGame, d.Faction, faction))
            .Select(d => d.Name).ToArray();
        Check(Select(0, null).Length == 4 && Select(1, null).SequenceEqual(new[] { "Native tank" })
            && Select(2, null).Length == 5, "source-only choices preserve your, native and combined pools");
        Check(Select(0, "pmc").SequenceEqual(new[] { "Player light" }), "folder faction matching is exact and ignores case");
        Check(Select(0, "Red Army").SequenceEqual(new[] { "Enemy heavy" }), "enemy selection has its own pool independent of player selection");
        Check(Select(0, "Missing faction").Length == 0, "an empty selected faction cannot silently use another faction's tanks");
        Check(Select(1, "PMC").Length == 0, "a faction restriction cannot bypass the selected source");
        Check(Select(0, "Base game").SequenceEqual(new[] { "Custom name collision" }), "a custom faction called Base game does not select shipped tanks");
        Check(Select(-1, null).Length == 0 && Select(3, null).Length == 0, "an invalid pool is never an unrestricted pool");
        Check(new GauntletSettings().EnemyFaction == null, "Gauntlet keeps the existing unrestricted default source");
        Console.WriteLine("DESIGN_POOL_TESTS_OK: separate sides, exact faction, source intersection, empty faction, defaults");
    }
}

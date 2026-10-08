using SprocketBattles;

internal static class FreeForAllRulesTests
{
    internal static void Run()
    {
        var masks = Enumerable.Range(0, BattleFile.MaxFreeForAllTanks).Select(FreeForAllRules.TeamMask).ToArray();
        for (int i = 0; i < masks.Length; i++)
        {
            Check(masks[i] != 0 && (masks[i] & (masks[i] - 1)) == 0, "one independent team bit per contender");
            for (int j = i + 1; j < masks.Length; j++) Check((masks[i] & masks[j]) == 0, "contenders are never allied");
        }
        Throws(() => FreeForAllRules.TeamMask(-1));
        Throws(() => FreeForAllRules.TeamMask(BattleFile.MaxFreeForAllTanks));
        var player = new FreeForAllRules.Contender("player", true, false, true);
        var enemy = new FreeForAllRules.Contender("enemy", true, false, false);
        Check(FreeForAllRules.Outcome(new[] { player, enemy }) == null, "two survivors keep fighting");
        Check(FreeForAllRules.Outcome(new[] { player, enemy with { Mobile = false } }) is { Won: true, Winner: "player" }, "last player wins");
        Check(FreeForAllRules.Outcome(new[] { player with { Mobile = false }, enemy }) is { Won: false, Winner: "enemy", Draw: false }, "last enemy wins");
        Check(FreeForAllRules.Outcome(new[] { player with { Mobile = false }, enemy with { Mobile = false } }) is { Draw: true }, "no surviving tank draws");
        Check(FreeForAllRules.Outcome(new[] { player, enemy with { Waiting = true, Mobile = false } }) == null, "hidden reserve prevents early victory");
        Check(FreeForAllRules.Outcome(Array.Empty<FreeForAllRules.Contender>()) == null, "loading or empty battle never declares a winner");
        var targets = new[] { new FreeForAllRules.Target("self", 0), new FreeForAllRules.Target("old", 100), new FreeForAllRules.Target("near", 25) };
        Check(FreeForAllRules.SelectTarget("self", null, targets, false) == "near", "idle AI picks the nearest opponent, never itself");
        Check(FreeForAllRules.SelectTarget("self", "old", targets, false) == "old", "retarget cooldown preserves the native attack task");
        Check(FreeForAllRules.SelectTarget("self", "old", targets, true) == "near", "clearly nearer opponent replaces the old goal after cooldown");
        Check(FreeForAllRules.SelectTarget("self", "gone", targets, false) == "near", "destroyed goal is replaced without waiting for cooldown");
        var close = new[] { new FreeForAllRules.Target("old", 100), new FreeForAllRules.Target("near", 81) };
        Check(FreeForAllRules.SelectTarget("self", "old", close, true) == "old", "small distance changes do not constantly restart pathfinding");
        var unavailable = new[] { new FreeForAllRules.Target("reserve", 1, false), new FreeForAllRules.Target("invalid", float.NaN), new FreeForAllRules.Target("outside", float.PositiveInfinity), new FreeForAllRules.Target("self", 0) };
        Check(FreeForAllRules.SelectTarget("self", null, unavailable, true) == null, "waiting reserves and invalid positions never become goals");
        var tied = new[] { new FreeForAllRules.Target("b", 100), new FreeForAllRules.Target("a", 100) };
        Check(FreeForAllRules.SelectTarget("self", null, tied, true) == "a", "equal-distance selection is deterministic");
        Check(FreeForAllRules.SelectTarget("self", "b", tied, true) == "b", "equal-distance opponent does not replace a current goal");
        Check(!FreeForAllRules.RefreshApproach(4.9f, 10000, 0, false), "approach updates never exceed the five-second limit");
        Check(!FreeForAllRules.RefreshApproach(6, 399, 1000, true), "small target movements keep the existing path");
        Check(FreeForAllRules.RefreshApproach(6, 400, 1000, true), "moving opponent gets an updated path destination");
        Check(FreeForAllRules.RefreshApproach(6, 0, 225, true), "reaching an old goal allows a new approach");
        Check(FreeForAllRules.RefreshApproach(6, 0, 1000, false), "a dropped path is restarted after the cooldown");
        Console.WriteLine("FREE_FOR_ALL_RULES_TESTS_OK");
    }

    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Throws(Action action)
    {
        try { action(); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new Exception("unsupported contender index must be rejected");
    }
}

using SprocketBattles;

static class SpawnTeamMappingTests
{
    static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("spawn teams: " + description);
    }

    static void Refuses(Action action, string description)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("spawn teams: accepted " + description);
    }

    public static void Run()
    {
        int[] nativeRoles = { 1, 2 };
        Check(SpawnTeamMapping.Resolve(new[] { 1, 2 }, nativeRoles).SequenceEqual(new[] { 0, 1 }),
            "normal attacker/defender roles retain their native locators");
        Check(SpawnTeamMapping.Resolve(new[] { 2, 1 }, nativeRoles).SequenceEqual(new[] { 1, 0 }),
            "swapping player and AI roles swaps locators before first load");
        Check(SpawnTeamMapping.Resolve(new[] { 2, 1 }, nativeRoles, new[] { 1, 0 }).SequenceEqual(new[] { 1, 0 }),
            "retry preserves the initialized swapped-role map");

        // The native dictionary is keyed by GameTeamDefinition.ID: attacker 1 and defender 2, never indexes 0/1.
        var nativeMap = new Dictionary<int, int> { [1] = 0, [2] = 1 };
        var queried = new List<int>();
        int? Lookup(int id)
        {
            queried.Add(id);
            return nativeMap.TryGetValue(id, out int index) ? index : null;
        }
        var normalLiveMap = SpawnTeamMapping.ReadLiveMap(new[] { 1, 2 }, Lookup);
        Check(queried.SequenceEqual(new[] { 1, 2 }) && normalLiveMap.SequenceEqual(new[] { 0, 1 }),
            "retry looks up native role IDs rather than zero-based custom team indexes");
        Check(SpawnTeamMapping.Resolve(new[] { 1, 2 }, nativeRoles, normalLiveMap).SequenceEqual(new[] { 0, 1 }),
            "normal native IDs resolve to the expected spawn locators");
        queried.Clear();
        var swappedLiveMap = SpawnTeamMapping.ReadLiveMap(new[] { 2, 1 }, Lookup);
        Check(queried.SequenceEqual(new[] { 2, 1 }) && swappedLiveMap.SequenceEqual(new[] { 1, 0 }),
            "swapped sides look up IDs in the custom team order");
        Check(SpawnTeamMapping.Resolve(new[] { 2, 1 }, nativeRoles, swappedLiveMap).SequenceEqual(new[] { 1, 0 }),
            "swapped native IDs retain each side's authored spawn locators");
        Refuses(() => SpawnTeamMapping.ReadLiveMap(new[] { 0, 1 }, Lookup), "array indexes used as native IDs");
        Refuses(() => SpawnTeamMapping.ReadLiveMap(new[] { 1, 1 }, Lookup), "duplicate native IDs");
        Refuses(() => SpawnTeamMapping.ReadLiveMap(new[] { 1, 3 }, Lookup), "a missing native ID");

        // Authored Team 1 positions must follow the player team, even when native team 0 is the enemy.
        var battle = new BattleFile();
        battle.Units.Add(new BattleUnit { Id = "player", Team = 0, Blueprint = "A", Position = new[] { 12f, 0, 24 } });
        battle.Units.Add(new BattleUnit { Id = "enemy", Team = 1, Blueprint = "B", Position = new[] { 80f, 0, 90 } });
        var mapping = SpawnTeamMapping.Resolve(new[] { 2, 1 }, nativeRoles);
        var locators = Enumerable.Range(0, 2).Select(_ => new List<BattleUnit>()).ToArray();
        for (int team = 0; team < 2; team++)
            locators[mapping[team]].AddRange(battle.SpawnGroups(team).SelectMany(g => g));
        Check(locators[1].Single().Id == "player" && locators[0].Single().Id == "enemy",
            "swapped roles keep each team's authored units and positions together");

        Refuses(() => SpawnTeamMapping.Resolve(new[] { 2, 1 }, nativeRoles, new[] { 0, 1 }), "a stale retry role map");
        Refuses(() => SpawnTeamMapping.Resolve(new[] { 1, 2 }, nativeRoles, new[] { 0 }), "an incomplete retry map");
        Refuses(() => SpawnTeamMapping.Resolve(new[] { 1, 1 }, nativeRoles), "two teams sharing one locator");
        Refuses(() => SpawnTeamMapping.Resolve(new[] { 1, 2 }, new[] { 1, 1 }), "a missing native defender role");
        Refuses(() => SpawnTeamMapping.Resolve(new[] { 1, 2 }, new[] { 3, 2 }), "ambiguous native roles");
        Refuses(() => SpawnTeamMapping.Resolve(new[] { 0, 2 }, nativeRoles), "an unset battle role");
        Console.WriteLine("SPAWN_TEAM_TESTS_OK: native role IDs, normal roles, swapped roles, authored units, retries, invalid mappings");
    }
}

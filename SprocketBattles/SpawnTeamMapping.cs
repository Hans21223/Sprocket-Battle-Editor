namespace SprocketBattles;

/// Role bits are normalized to 1 = attacker, 2 = defender. The native game resolves roles before spawning units.
internal static class SpawnTeamMapping
{
    /// Native team IDs are external dictionary keys; custom array positions are only display/order indexes.
    internal static int[] ReadLiveMap(IReadOnlyList<int> ids, Func<int, int?> lookup)
    {
        var result = new int[ids.Count];
        var known = new HashSet<int>();
        for (int team = 0; team < ids.Count; team++)
        {
            int id = ids[team];
            if (!known.Add(id))
                throw new InvalidOperationException($"Two battle teams have the same native ID {id}");
            result[team] = lookup(id)
                ?? throw new InvalidOperationException($"Team {team + 1} (native ID {id}) has no native team mapping");
        }
        return result;
    }

    internal static int[] Resolve(IReadOnlyList<int> teams, IReadOnlyList<int> nativeTeams, IReadOnlyList<int>? liveMap = null)
    {
        if (liveMap != null && liveMap.Count != teams.Count)
            throw new InvalidOperationException("The battle's native team map has the wrong number of teams");
        var result = new int[teams.Count];
        var occupied = new HashSet<int>();
        for (int team = 0; team < teams.Count; team++)
        {
            int role = teams[team];
            if (role != 1 && role != 2)
                throw new InvalidOperationException($"Team {team + 1} must be an attacker or defender");
            int found = -1;
            for (int candidate = 0; candidate < nativeTeams.Count; candidate++)
            {
                if ((nativeTeams[candidate] & role) == 0) continue;
                if (found >= 0)
                    throw new InvalidOperationException($"Team {team + 1} matches more than one native mission role");
                found = candidate;
            }
            if (found < 0) throw new InvalidOperationException($"Team {team + 1} has no matching native mission role");
            if (!occupied.Add(found))
                throw new InvalidOperationException("Two battle teams resolve to the same native team");
            if (liveMap != null && liveMap[team] != found)
                throw new InvalidOperationException($"Team {team + 1}'s native mission mapping doesn't match its role; start the battle again from the menu");
            result[team] = found;
        }
        return result;
    }
}

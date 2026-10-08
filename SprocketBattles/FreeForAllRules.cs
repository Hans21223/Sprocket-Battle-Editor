namespace SprocketBattles;

/// Native TeamID uses eight independent bits. A contender gets one bit, never a composite team mask.
internal static class FreeForAllRules
{
    // Authored teams locate spawn groups. Once FFA has assigned contender IDs, those groups are not alliances.
    internal static bool AreEnemies(bool freeForAll, int? authoredSelf, int? authoredOther, byte selfMask, byte otherMask) =>
        !freeForAll && authoredSelf.HasValue && authoredOther.HasValue
            ? authoredSelf != authoredOther : selfMask != 0 && otherMask != 0 && (selfMask & otherMask) == 0;

    internal static byte TeamMask(int contender)
    {
        if (contender < 0 || contender >= BattleFile.MaxFreeForAllTanks)
            throw new ArgumentOutOfRangeException(nameof(contender));
        return (byte)(1 << contender);
    }

    internal readonly record struct Contender(string Id, bool Mobile, bool Waiting, bool Player);
    internal readonly record struct Result(string? Winner, bool Won, bool Draw);

    internal readonly record struct Target(string Id, float DistanceSquared, bool Eligible = true);

    // A moving target gets a refreshed destination at most once in five seconds. Reaching an old destination or
    // losing the native path also permits a refresh; small target movements leave the existing path untouched.
    internal static bool RefreshApproach(float elapsed, float movedSquared, float remainingSquared, bool hasTask) =>
        elapsed >= 5 && (movedSquared >= 400 || remainingSquared <= 225 || !hasTask);

    /// Keep a live goal until a new opponent is meaningfully nearer. This avoids restarting the native approach
    /// every sight tick as equally distant tanks exchange places. An eliminated target is replaced immediately.
    internal static string? SelectTarget(string self, string? current, IReadOnlyList<Target> targets, bool mayRetarget)
    {
        var eligible = targets.Where(t => t.Eligible && t.Id != self && float.IsFinite(t.DistanceSquared)
            && t.DistanceSquared >= 0).OrderBy(t => t.DistanceSquared).ThenBy(t => t.Id, StringComparer.Ordinal).ToArray();
        if (eligible.Length == 0) return null;
        var old = eligible.FirstOrDefault(t => t.Id == current);
        if (old.Id == null) return eligible[0].Id;
        var nearest = eligible[0];
        return mayRetarget && nearest.DistanceSquared < old.DistanceSquared * 0.64f ? nearest.Id : old.Id;
    }

    internal static Result? Outcome(IReadOnlyList<Contender> contenders)
    {
        // A reinforcement remains a contender until it arrives, even while hidden and unable to move.
        if (contenders.Count == 0 || contenders.Any(c => c.Waiting)) return null;
        var survivors = contenders.Where(c => c.Mobile).Take(2).ToArray();
        if (survivors.Length > 1) return null;
        return survivors.Length == 0 ? new Result(null, false, true)
            : new Result(survivors[0].Id, survivors[0].Player, false);
    }
}

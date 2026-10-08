namespace SprocketBattles;

/// Source and faction restrictions intersect. An empty selection never borrows tanks from another faction.
internal static class DesignPoolRules
{
    internal static bool Matches(int pool, bool baseGame, string actualFaction, string? selectedFaction) =>
        (pool switch { 0 => !baseGame, 1 => baseGame, 2 => true, _ => false })
        && (string.IsNullOrEmpty(selectedFaction)
            || string.Equals(actualFaction, selectedFaction, StringComparison.OrdinalIgnoreCase));
}

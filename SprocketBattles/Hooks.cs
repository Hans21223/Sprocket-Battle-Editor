using HarmonyLib;
using Sprocket.ArtificialIntelligence;
using Sprocket.Orders;

namespace SprocketBattles;

/// Two of the game's AI methods, for the tanks you command only (see Battle.OverrideGame): an order from anywhere but the
/// Battle Editor (a mission's scripted orders, the command wheel) is dropped, and so are formation tasks (a formation
/// leader drags its followers to their places). Every other tank, and every tank with Override off, is left alone.
/// Neither takes a CancellationToken by value (a hook on such a method crashed the game).
[HarmonyPatch(typeof(CommanderAI))]
internal static class Hooks
{
    [HarmonyPrefix, HarmonyPatch(nameof(CommanderAI.Order))]
    static bool OnlyOurs(CommanderAI __instance, Order __0)
    {
        try { return Battle.Accept(__instance, __0); }
        catch (Exception) { return true; }
    }

    [HarmonyPrefix, HarmonyPatch(nameof(CommanderAI.UpdateFormationTasks))]
    static bool NoFormation(CommanderAI __instance)
    {
        try { return !Battle.Holds(__instance); }
        catch (Exception) { return true; }
    }
}

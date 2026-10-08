using HarmonyLib;
using Sprocket.ArtificialIntelligence;

namespace SprocketBattles;

/// Driver Step has only float/enum arguments: no async or CancellationToken ABI is patched.
[HarmonyPatch(typeof(DriverAI), nameof(DriverAI.Step))]
internal static class MovementHoldHooks
{
    [HarmonyPrefix]
    static bool Driver(DriverAI __instance)
    {
        try { return !Battle.StopDriver(__instance); }
        catch (Exception ex) { Trace.Write($"force stop: driver hold failed: {ex.Message}"); return true; }
    }
}

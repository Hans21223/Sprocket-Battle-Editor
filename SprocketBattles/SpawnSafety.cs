using HarmonyLib;
using Sprocket;
using Sprocket.Vehicles;
using Sprocket.Vehicles.Tracks;

namespace SprocketBattles;

/// Observe the reference-only track setup seam. SpawnPlan supplies poses before any component is enabled;
/// changing them here could invalidate state already created by another component in the same enable loop.
[HarmonyPatch(typeof(TrackAssembly), nameof(TrackAssembly.EnableBehaviour))]
internal static class SpawnSafety
{
    static int entries;

    internal static void Clear() { entries = 0; }

    // Generated framework staging points are deliberately on terrain. Native map points may be on bridges.
    internal static bool TerrainStaging(DeathmatchGameMode? mode) => mode != null && mode.gameObject.name == "Deathmatch (Map Framework)";

    [HarmonyPrefix]
    static void Before(IBehaviourEnableContext __0)
    {
        try
        {
            if (!Battle.PendingPlacement || entries >= 4) return;
            var body = __0?.Body;
            entries++;
            Trace.Write($"spawn seam: native track setup at {body?.position.ToString() ?? "unknown"}, main body {(body == null ? "missing" : body.isKinematic ? "kinematic" : "dynamic")}; pose unchanged");
        }
        catch (Exception ex) { Trace.Write($"spawn seam: couldn't observe track setup: {ex.Message}"); }
    }
}

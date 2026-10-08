using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace SprocketBattles;

/// Battle Editor: battles made on the custom battle maps (tanks, a mission, a cinematic) and played as made, from the
/// main menu's Battle Editor button; F10 commands the battle's tanks (and takes control of one).
[BepInPlugin("local.sprocket.battles", "Battle Editor", Version)]
public sealed class Plugin : BasePlugin
{
    const string Version = "0.17.30";

    internal static ManualLogSource ModLog = null!;

    public override void Load()
    {
        ModLog = Log;
        AddComponent<BattleEditor>();
        var harmony = new Harmony("local.sprocket.battles");
        try { harmony.PatchAll(typeof(Menu)); }
        catch (Exception ex) { Trace.Write($"No Battle Editor button on the main menu, could not attach to it: {ex}"); }
        try { harmony.PatchAll(typeof(BattleScenarioDisplay)); }
        catch (Exception ex) { Trace.Write($"Using the fallback battle limits display: {ex}"); }
        try { harmony.PatchAll(typeof(SpawnSafety)); }
        catch (Exception ex) { Trace.Write($"Native track setup diagnostics couldn't attach: {ex}"); }
        try { harmony.PatchAll(typeof(SpawnPlan)); }
        catch (Exception ex) { Trace.Write($"Native authored spawn locations couldn't attach: {ex}"); }
        try { harmony.PatchAll(typeof(Hooks)); }
        catch (Exception ex) { Battle.OverrideGame = false; Trace.Write($"Your orders can't override the game's, could not attach to its AI: {ex}"); }
        try { harmony.PatchAll(typeof(PuppetHooks)); }
        catch (Exception ex) { Trace.Write($"Force control can't hold the AI back (it may fight your controls), could not attach to it: {ex}"); }
        try { harmony.PatchAll(typeof(MovementHoldHooks)); }
        catch (Exception ex) { Trace.Write($"Force stop couldn't attach to the AI driver: {ex}"); }
        try { harmony.PatchAll(typeof(ReplayAudioHooks)); harmony.PatchAll(typeof(ReplayAudioStartHooks)); ReplayAudioHooks.Attached = true; }
        catch (Exception ex) { Trace.Write($"Replay one-shot sound capture couldn't attach: {ex}"); }
        try { FreeForAll.Attach(harmony); }
        catch (Exception ex) { Trace.Write($"Free-for-All is unavailable, couldn't attach to native mission logic: {ex}"); }
        try { Gauntlet.Attach(harmony); }
        catch (Exception ex) { Trace.Write($"Gauntlet is unavailable, couldn't attach to native mission logic: {ex}"); }
        Trace.Write($"Battle Editor {Version} loaded ({harmony.GetPatchedMethods().Count()} game methods attached): open Edit battle from the main menu, F10 for the command view");
    }
}

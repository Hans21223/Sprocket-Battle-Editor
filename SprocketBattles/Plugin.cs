using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace SprocketBattles;

/// Battle Editor: battles made on the custom battle maps (tanks, a mission, a cinematic) and played as made, from the
/// main menu's Battle Editor button, or F9 in a custom battle; F10 commands the battle's tanks (and takes control of one).
[BepInPlugin("local.sprocket.battles", "Battle Editor", Version)]
public sealed class Plugin : BasePlugin
{
    const string Version = "0.15.3";

    internal static ManualLogSource ModLog = null!;

    public override void Load()
    {
        ModLog = Log;
        AddComponent<BattleEditor>();
        var harmony = new Harmony("local.sprocket.battles");
        try { harmony.PatchAll(typeof(Menu)); }
        catch (Exception ex) { Trace.Write($"No Battle Editor button on the main menu, could not attach to it: {ex}"); }
        try { harmony.PatchAll(typeof(Hooks)); }
        catch (Exception ex) { Battle.OverrideGame = false; Trace.Write($"Your orders can't override the game's, could not attach to its AI: {ex}"); }
        try { harmony.PatchAll(typeof(PuppetHooks)); }
        catch (Exception ex) { Trace.Write($"Force control can't hold the AI back (it may fight your controls), could not attach to it: {ex}"); }
        Trace.Write($"Battle Editor {Version} loaded ({harmony.GetPatchedMethods().Count()} game methods attached): the main menu's Battle Editor button or F9 in a custom battle, F10 for the command view");
    }
}

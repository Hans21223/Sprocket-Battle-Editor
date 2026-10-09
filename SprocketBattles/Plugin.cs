using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine.InputSystem;

namespace SprocketBattles;

/// Battle Editor: battles made on the custom battle maps (tanks, a mission, a cinematic) and played as made, from the
/// main menu's Battle Editor button; F10 commands the battle's tanks (and takes control of one).
[BepInPlugin("local.sprocket.battles", "Battle Editor", Version)]
[BepInDependency(ModApi.Guid, BepInDependency.DependencyFlags.SoftDependency)] // Sprocket Mod API, if installed (ModApi.cs)
public sealed class Plugin : BasePlugin
{
    const string Version = "0.17.31";

    internal static ManualLogSource ModLog = null!;

    // The editor's key and the command view's: F9 and F10, or as rebound in the Sprocket Mod API's keybinding window.
    static object? editorKey, commandKey;
    internal static bool EditorKeyDown(Keyboard keys) => editorKey != null ? ModApi.KeyPressed(editorKey) : keys.f9Key.wasPressedThisFrame;
    internal static bool CommandKeyDown(Keyboard keys) => commandKey != null ? ModApi.KeyPressed(commandKey) : keys.f10Key.wasPressedThisFrame;
    internal static string EditorKey => editorKey != null ? ModApi.KeyShown(editorKey) : "F9";
    internal static string CommandKey => commandKey != null ? ModApi.KeyShown(commandKey) : "F10";

    public override void Load()
    {
        ModLog = Log;
        editorKey = ModApi.RegisterKey(Log, "editor", "Battle editor: open, leave, stop a recording or preview", "Battle Editor", "F9", ModApi.Gameplay);
        commandKey = ModApi.RegisterKey(Log, "command-view", "Command view: open and close", "Battle Editor", "F10", ModApi.Gameplay);
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
        Trace.Write($"Battle Editor {Version} loaded ({harmony.GetPatchedMethods().Count()} game methods attached): open Edit battle from the main menu, {CommandKey} for the command view");
    }
}

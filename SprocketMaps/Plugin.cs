using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace SprocketMaps;

/// Map Framework: more of Sprocket's maps made ready for custom battles. The game's own Custom Battle screen (and the
/// Battle Editor) then offer them. A map's custom battle setup is repaired as it loads (Ambush's), and maps the game
/// only made for its scenarios get one built. The two Sandboxes, which the game doesn't list at all, are listed too.
[BepInPlugin("local.sprocket.maps", "Map Framework", Version)]
public sealed class Plugin : BasePlugin
{
    const string Version = "0.4.1";

    internal static ManualLogSource ModLog = null!;

    /// Lets a battle launcher reject unsupported bundle scenes before leaving the menu, without a hard dependency.
    public static string? UnsupportedMapReason(string? map) => Maps.UnavailableReason(map);
    public static string? CustomMapLoadName(string? map) => Maps.CustomLoadName(map);
    public static string? TakeLoadFailure() => CustomMapBridge.TakeLoadFailure();
    public static string? MapNotReadyReason(string? map) => CustomMapBridge.NotReadyReason(map);

    public override void Load()
    {
        ModLog = Log;
        AutoMapImport.Configure(Config);
        Maps.LoadCustomBundles();
        AddComponent<CustomMapRunner>();
        var harmony = new Harmony("local.sprocket.maps");
        try { harmony.PatchAll(typeof(MapList)); }
        catch (Exception ex) { Log.LogError($"Couldn't add maps to the game's custom battle list: {ex}"); }
        try { harmony.PatchAll(typeof(Builder)); }
        catch (Exception ex) { Log.LogError($"Couldn't attach to the game's battle start, so maps without a custom battle setup won't get one: {ex}"); }
        try { harmony.PatchAll(typeof(Repair)); }
        catch (Exception ex) { Log.LogError($"Couldn't attach to the game's custom battle start, so broken setups stay broken: {ex}"); }
        try
        {
            harmony.PatchAll(typeof(CustomSceneLookupPatch));
            NativeSceneLoadHook.Install();
            harmony.PatchAll(typeof(CustomMapInitiatePatch));
        }
        catch (Exception ex)
        {
            CustomMapBridge.Disable("Custom maps could not be started. Update Map Framework and restart the game.");
            Log.LogError($"Custom map loading couldn't attach: {ex}");
        }
        Log.LogInfo($"Map Framework {Version} loaded: {string.Join(", ", Maps.All.Select(m => m.Identifier))} offered for custom battles");
    }
}

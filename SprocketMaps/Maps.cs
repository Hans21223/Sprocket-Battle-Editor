using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket;
using Sprocket.MissionFramework;
using Sprocket.UI;
using UnityEngine;

namespace SprocketMaps;

/// The maps this framework makes ready for custom battles, by the game's identifier (Scenarios\Configs\*.json), and
/// how many tanks a side each takes.
internal static class Maps
{
    /// `Scene`: the map's scene, for maps with no custom battle setup of their own (built by Builder); null if it has one.
    /// `Attackers` / `Defenders`: where each side starts (x, z), for a map with no scenario spawn points to go by.
    /// `Description`: for a map the game doesn't list at all, which the framework lists itself, with its picture
    /// (`<Identifier>.png` next to the DLL).
    internal sealed record Map(string Identifier, ushort Spawns, string Why, string? Scene = null,
        Vector2? Attackers = null, Vector2? Defenders = null, string? Description = null, string? DisplayName = null);

    internal static readonly Map[] Known =
    {
        // Has a custom battle setup in its scene, but its two team missions are flagged wrong (Repair).
        new("Ambush", 6, "its setup repaired"),
        // Only made for their scenarios: a setup is built as the battle starts (Builder).
        new("TheCrossroad", 16, "a setup built for it", "The Crossroad"),
        new("SilentBorder", 16, "a setup built for it", "Silent Border"),
        // Not in the game's map list at all: listed by the framework, its setup built on the open ground along the north
        // edge (the sea to the west, the stream to the south, the world's wall 30 m behind the rows).
        new("Sandbox", 16, "listed and a setup built for it", "Sandbox", new(-55, 265), new(415, 265),
            "The Sandbox's own ground, between the sea and the eastern hills, with its stream and lakes. Both sides start along the open north edge, 470 m apart."),
        // The light Sandbox the designer's test drive uses: flat ground 750 m by 2 km, with test blocks and suspension
        // bumps in its south half; both sides start in the open north half, 700 m apart.
        new("Sandbox (Low performance)", 16, "listed and a setup built for it", "Sandbox (Low performance)", new(0, 400), new(0, 1100),
            "The test drive's flat ground: no hills, no cover, nothing in the way. Both sides start in its open north half, 700 m apart."),
    };

    internal static IEnumerable<Map> All => Known.Concat(CustomMapBridge.ListedMaps);
    internal static string? CustomLoadName(string? identifier) => CustomMapBridge.LoadName(identifier);

    // Scene bundles supply scenery; CustomMapBridge starts them through an authentic native controller.
    // Discovery never modifies an authored bundle or any built-in scene definition.
    static readonly HashSet<string> unavailableBundles = new(StringComparer.OrdinalIgnoreCase);

    internal static string? UnavailableReason(string? identifier) => CustomMapBridge.FailureFor(identifier)
        ?? (MapOwnership.Quarantined(identifier, unavailableBundles) && CustomLoadName(identifier) == null
            ? $"Map '{identifier}' could not be loaded. Update its map file or choose another map."
            : null);

    internal static void LoadCustomBundles()
    {
        var dir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
        var searchPaths = new List<string>();
        if (!string.IsNullOrEmpty(dir))
        {
            searchPaths.Add(Path.Combine(dir, "CustomMaps"));
            searchPaths.Add(dir);
        }
        try
        {
            if (!string.IsNullOrEmpty(BepInEx.Paths.PluginPath))
            {
                searchPaths.Add(Path.Combine(BepInEx.Paths.PluginPath, "CustomMaps"));
                searchPaths.Add(Path.Combine(BepInEx.Paths.PluginPath, "SprocketMaps", "CustomMaps"));
            }
        }
        catch (Exception ex) { Plugin.ModLog.LogWarning($"Could not locate the plugin maps folder: {ex.Message}"); }

        foreach (var customDir in searchPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(customDir)) continue;
                foreach (var file in Directory.EnumerateFiles(customDir))
                {
                    if (MapOwnership.BundleName(file) is not { } bundleName) continue;
                    if (MapOwnership.IsNativeMap(bundleName))
                    {
                        Plugin.ModLog.LogWarning($"Ignored bundle '{file}': its name belongs to a built-in map. Native scene resolution is unchanged.");
                        continue;
                    }
                    if (!unavailableBundles.Add(bundleName)) continue;
                    try { CustomMapBridge.Register(file); }
                    catch (Exception ex) { Plugin.ModLog.LogWarning($"Couldn't load custom map '{file}': {ex.Message}. The map file was left untouched."); }
                }
            }
            catch (Exception ex) { Plugin.ModLog.LogWarning($"Could not inspect custom map folder '{customDir}': {ex.Message}"); }
        }
    }

    /// By its config's identifier ("TheCrossroad") or its scene's name ("The Crossroad"), whichever the game goes by.
    internal static Map? Of(string? identifier) => All.FirstOrDefault(m =>
        string.Equals(m.Identifier, identifier, StringComparison.OrdinalIgnoreCase) || string.Equals(m.Scene, identifier, StringComparison.OrdinalIgnoreCase));

    internal static Map? Built(string scene) => MapOwnership.CanBuildNativeScene(scene)
        ? Known.FirstOrDefault(m => m.Scene != null && string.Equals(m.Scene, scene, StringComparison.OrdinalIgnoreCase))
        : null;
}

/// The game's map list (read from Scenarios\Configs at start and on reload): the framework's maps marked as custom
/// battle maps, with their spawn count and a picture if the config has none. The files themselves are left alone.
[HarmonyPatch]
internal static class MapList
{
    [HarmonyPostfix, HarmonyPatch(typeof(ScenarioLoader), nameof(ScenarioLoader.GetScenarioDefinitions))]
    static void Listed(ref Il2CppReferenceArray<MapInfo> __result)
    {
        if (__result == null) return;
        var supported = new List<MapInfo>();
        for (int i = 0; i < __result.Length; i++)
        {
            var entry = __result[i];
            if (Maps.UnavailableReason(entry?.Config?.Identifier) is { } reason)
                Plugin.ModLog.LogWarning(reason);
            else supported.Add(entry!); // Preserve any null slots already supplied by the native list.
        }
        if (supported.Count != __result.Length) __result = new Il2CppReferenceArray<MapInfo>(supported.ToArray());
        var unknown = new List<string>();
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < __result.Length; i++)
        {
            var config = __result[i]?.Config;
            if (config != null) listed.Add(config.Identifier);
            if (config == null || config.Deathmatch) continue;
            if (Maps.Of(config.Identifier) is not { } map) { unknown.Add(config.Identifier); continue; }
            config.Deathmatch = true;
            if (config.TeamSpawnsSupported == null || config.TeamSpawnsSupported.Length < 2 || config.TeamSpawnsSupported[0] == 0)
                config.TeamSpawnsSupported = new Il2CppStructArray<ushort>(new[] { map.Spawns, map.Spawns });
            if (string.IsNullOrEmpty(config.CustomBattleSplashPath)) config.CustomBattleSplashPath = config.ScenarioSplashPath;
            Plugin.ModLog.LogInfo($"{config.Identifier}: offered for custom battles ({map.Why}, {map.Spawns} tanks a side)");
        }
        if (unknown.Count > 0) Plugin.ModLog.LogInfo($"scenario-only maps the framework doesn't know: {string.Join(", ", unknown)}");

        var added = Maps.All.Where(m => m.Description != null && !listed.Contains(m.Identifier)).ToList();
        if (added.Count == 0) return;
        var all = new Il2CppReferenceArray<MapInfo>(__result.Length + added.Count);
        for (int i = 0; i < __result.Length; i++) all[i] = __result[i];
        for (int i = 0; i < added.Count; i++) all[__result.Length + i] = Listing(added[i]);
        __result = all;
    }

    /// A list entry for a map the game has no files for. Its picture's path is absolute: the game joins it to its
    /// StreamingAssets folder, which leaves an absolute path as it is.
    static MapInfo Listing(Maps.Map map)
    {
        var splash = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? "", map.Identifier + ".png");
        if (!File.Exists(splash))
        {
            var fallback = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? "", "Sandbox.png");
            splash = File.Exists(fallback) ? fallback : "";
        }
        var localization = new MapLocalization
        {
            Identifier = map.Identifier, Name = map.DisplayName ?? map.Identifier, Description = map.Description,
            Objectives = new Il2CppStringArray(0), FailConditions = new Il2CppStringArray(0), Tags = new Il2CppStringArray(0),
        };
        var config = new MapConfig
        {
            Identifier = map.Identifier, Order = 1000, ScenarioSplashPath = splash, CustomBattleSplashPath = splash, TeamCount = 2,
            TeamSpawnsSupported = new Il2CppStructArray<ushort>(new[] { map.Spawns, map.Spawns }), Deathmatch = true, Scenario = false,
        };
        Plugin.ModLog.LogInfo($"{map.Identifier}: offered for custom battles ({map.Why}, {map.Spawns} tanks a side)");
        return new MapInfo { Localization = localization, Config = config };
    }
}

/// A map's custom battle setup put right as its game mode starts (InitiateInternal, which the game calls through its
/// virtual table; LoadGameSetup, which reads the flags, is a small method the compiler folds into its caller, so a hook
/// on it never runs). The game gives your attacking team the team whose mission is flagged Attackers and the defending
/// team the one flagged Defenders, the first match each; Ambush's missions are flagged "all" and "none", so both teams
/// went to the first and the second team was never placed (the battle stuck on its loading screen).
[HarmonyPatch]
internal static class Repair
{
    [HarmonyPrefix, HarmonyPatch(typeof(DeathmatchGameMode), nameof(DeathmatchGameMode.InitiateInternal))]
    static void Setup(DeathmatchGameMode __instance)
    {
        try
        {
            var teams = __instance.teams;
            if (teams == null || teams.Length != 2) { Plugin.ModLog.LogInfo($"custom battle setup: {teams?.Length ?? 0} teams, left as it is"); return; }
            var attackers = teams[0]?.mission; var defenders = teams[1]?.mission;
            if (attackers == null || defenders == null) { Plugin.ModLog.LogWarning("custom battle setup: a team has no mission, left as it is"); return; }
            Plugin.ModLog.LogInfo($"custom battle setup: team missions flagged {attackers.flags} and {defenders.flags}");
            bool right = attackers.flags.HasFlag(MissionFlags.Attackers) && !attackers.flags.HasFlag(MissionFlags.Defenders)
                      && defenders.flags.HasFlag(MissionFlags.Defenders) && !defenders.flags.HasFlag(MissionFlags.Attackers);
            if (right) return;
            Plugin.ModLog.LogInfo($"custom battle setup repaired: team missions were flagged {attackers.flags} and {defenders.flags}, now Attackers and Defenders");
            attackers.flags = MissionFlags.Attackers;
            defenders.flags = MissionFlags.Defenders;
        }
        catch (Exception ex) { Plugin.ModLog.LogError($"repairing the custom battle setup failed: {ex}"); }
    }
}

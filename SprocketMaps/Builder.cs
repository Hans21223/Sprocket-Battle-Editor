using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket;
using Sprocket.GameControl;
using Sprocket.MissionFramework;
using Sprocket.Orders;
using Sprocket.Spawning;
using Sprocket.Vehicles.Missions;
using Sprocket.Vehicles.Spawning;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SprocketMaps;

/// A custom battle setup built for a map that has none (The Crossroad, Silent Border), as the battle starts: the
/// same parts the game's own custom battle maps carry in their scenes (Defence's, Fields'), added to the map's game
/// controller as one more game mode it can run. Per team: a mission (Attackers or Defenders) with one stage that
/// moves its tanks up to a line towards the enemy and then attacks them, done once every enemy tank is out of the
/// fight; a vehicle spawner (its team number is the tanks' team); and 16 spawn points in rows where the map's own
/// scenario starts that side. The game then spawns, orders and ends the battle itself.
[HarmonyPatch]
internal static class Builder
{
    [HarmonyPrefix, HarmonyPatch(typeof(GameController), nameof(GameController.Initiate))]
    static void Initiate(GameController __instance, Il2CppReferenceArray<Il2CppSystem.Object> __1)
    {
        try
        {
            if (CustomMapBridge.Waiting(__instance)) return;
            var scene = __instance.gameObject.scene;
            if ((CustomMapBridge.BuildMap(__instance) ?? Maps.Built(scene.name)) is not { } map) return;
            bool deathmatch = false;
            if (__1 != null)
                for (int i = 0; i < __1.Length; i++)
                    if (__1[i]?.TryCast<GameSetupContext>() is { } setup && setup.Mode == SupportedGameModes.Deathmatch) deathmatch = true;
            if (!deathmatch) return; // its own scenario: left alone
            var modes = __instance.sceneGameModes;
            if (modes != null)
                for (int i = 0; i < modes.Length; i++)
                    if (modes[i]?.TryCast<DeathmatchGameMode>() != null) return; // has one already
            var mode = Build(scene, map);
            var all = new Il2CppReferenceArray<GameMode>((modes?.Length ?? 0) + 1);
            for (int i = 0; i < all.Length - 1; i++) all[i] = modes![i];
            all[all.Length - 1] = mode;
            __instance.sceneGameModes = all;
        }
        catch (Exception ex) { Plugin.ModLog.LogError($"building the custom battle setup failed: {ex}"); }
    }

    static DeathmatchGameMode Build(Scene scene, Maps.Map map)
    {
        var (attackers, defenders) = map is { Attackers: { } a, Defenders: { } d }
            ? (Ground(new Vector3(a.x, 0, a.y)), Ground(new Vector3(d.x, 0, d.y)))
            : Anchors(scene);
        // Inactive, as the game keeps its own: the game controller turns on the mode it runs.
        var root = new GameObject("Deathmatch (Map Framework)");
        root.SetActive(false);
        SceneManager.MoveGameObjectToScene(root, scene);
        var mode = root.AddComponent<DeathmatchGameMode>();
        var teams = new Il2CppReferenceArray<DeathmatchGameMode.TeamDeathmatchConfig>(2);
        float spacing = CustomMapBridge.LoadName(map.Identifier) != null ? CustomMapBridge.SpawnSpacing : 15;
        teams[0] = Team(root, "Attackers", MissionFlags.Attackers, 1, 2, attackers, defenders, 120, spacing, map.Spawns);
        teams[1] = Team(root, "Defenders", MissionFlags.Defenders, 2, 1, defenders, attackers, 64, spacing, map.Spawns);
        mode.teams = teams;
        Plugin.ModLog.LogInfo($"{map.Identifier}: custom battle setup built (attackers at {attackers}, defenders at {defenders})");
        return mode;
    }

    static DeathmatchGameMode.TeamDeathmatchConfig Team(GameObject root, string name, MissionFlags flags, byte own, byte enemy, Vector3 at, Vector3 toward, byte movePriority, float spacing, int count)
    {
        var team = Child(root.transform, name, Vector3.zero, Quaternion.identity);
        var facing = Quaternion.LookRotation(Flat(toward - at));
        var mission = team.AddComponent<Mission>();

        // Done once every enemy tank is out of the fight (no mobile one left).
        var kill = Child(team.transform, "Kill Objective", at, facing).AddComponent<ImmobilizeObjective>();
        kill.minMobileCount = 1;
        kill.filter = new ObjectiveEntityFilter { TeamID = (TeamID)enemy, GroupID = 0, BuildID = -1, SpawnerID = -1 };

        // Up to a line 60% of the way across, facing the enemy, then attack.
        var move = Child(team.transform, "Move Objective", Vector3.Lerp(at, toward, 0.6f), facing).AddComponent<MoveToLineVehicleOrderStageEvent>();
        Orders(move, own, movePriority);
        move.width = 2000;
        move.faceDirection = false;
        var attack = Child(team.transform, "Attack Order", toward, facing).AddComponent<AttackOrderStageEvent>();
        Orders(attack, own, 128);
        attack.attackType = (AttackApproachType)3;
        attack.engageMode = (AttackEngageMode)1;
        attack.accuracyModifier = 1;
        attack.TargetTeamID = (TeamID)enemy;
        attack.TargetGroupID = 0;
        attack.TargetSpawnPointID = -1;

        var stage = Child(team.transform, "Stage", at, facing).AddComponent<Stage>();
        var branch = new Stage.StageBranch { nextStage = null, objectives = new Il2CppReferenceArray<Objective>(new Objective[] { kill }) };
        stage.progression = new Il2CppReferenceArray<Stage.StageBranch>(1);
        stage.progression[0] = branch;
        stage.events = new Il2CppReferenceArray<StageEvent>(new StageEvent[] { move, attack });
        mission.firstStage = stage;
        mission.flags = flags;

        var spawnerObject = Child(team.transform, "Spawner", at, facing);
        var spawner = spawnerObject.AddComponent<VehicleSpawner>();
        spawner.teamID = (TeamID)own;
        spawner.groupID = (GroupID)1;
        spawner.SpawnerID = -1;
        spawner.appendNameID = true;
        spawnerObject.AddComponent<KillObjective>().minFractionKilled = 1;

        // Native maps keep their 4x4 rows; custom maps use the capacity checked by their author.
        var points = Child(team.transform, "Spawn Points", at, facing);
        var spawnPoints = points.AddComponent<SpawnPoints>();
        spawnPoints.random = false;
        spawnPoints.randomOnConnectingLines = false;
        var side = facing * Vector3.right; var back = facing * Vector3.back;
        var off = new List<string>();
        for (int i = 0; i < count; i++)
        {
            var offset = SpawnFormation.Offset(count, i, spacing);
            var spot = at + side * offset.Side + back * offset.Back;
            var ground = SpawnSpot(spot, out var on);
            if (on != null) off.Add($"{i} on {on} at {ground.y:0.0}");
            Child(points.transform, i.ToString(), ground, facing);
        }
        if (off.Count > 0) Plugin.ModLog.LogInfo($"{name}: spawn points not on the terrain: {string.Join(", ", off)}");
        return new DeathmatchGameMode.TeamDeathmatchConfig { mission = mission, spawner = spawner, spawnPoints = spawnPoints };
    }

    static void Orders(VehicleOrderStageEvent order, byte team, byte priority)
    {
        order.priority = priority;
        order.cancellationType = (OrderCancellationType)1;
        order.cancelOnStageEnd = false;
        order.delay = 0;
        order.TeamID = (TeamID)team;
        order.GroupID = 0;
        order.SpawnPointID = -1;
        order.vehiclesInRange = false;
    }

    /// Where each side starts: the middle of the map's own scenario spawn points for its allies and its enemies.
    static (Vector3 Attackers, Vector3 Defenders) Anchors(Scene scene)
    {
        Vector3? ally = null, enemy = null;
        foreach (var root in scene.GetRootGameObjects())
            foreach (var points in root.GetComponentsInChildren<SpawnPoints>(true))
            {
                string path = "";
                for (var t = points.transform; t != null; t = t.parent) path = t.name + "/" + path;
                var t0 = points.transform;
                var middle = t0.position;
                if (t0.childCount > 0)
                {
                    var sum = Vector3.zero;
                    for (int i = 0; i < t0.childCount; i++) sum += t0.GetChild(i).position;
                    middle = sum / t0.childCount;
                }
                if (path.Contains("Ally") || path.Contains("Allies")) ally ??= middle;
                else if (path.Contains("Enemy")) enemy ??= middle;
            }
        var a = ally ?? Vector3.zero; var e = enemy ?? a + Vector3.forward * 600;
        if ((e - a).magnitude < 100) e = a + Flat(e - a).normalized * 600;
        return (Ground(a), Ground(e));
    }

    static Vector3 Flat(Vector3 v) { v.y = 0; return v.sqrMagnitude < 0.01f ? Vector3.forward : v; }

    /// The ground under a point: the map's terrain where it has one. The first solid surface down from 1000 m up could be
    /// something high (Sandbox spawn points ended up about 250 m in the air, and tanks fell from there).
    static Vector3 Ground(Vector3 at) => Ground(at, out _);

    /// Conservative staging clearance before the vehicle's geometry is available. SpawnPoints.Get preserves this
    /// child height; ground snapping is a separate authoring operation, not part of the runtime picker.
    static Vector3 SpawnSpot(Vector3 at, out string? on)
    {
        var middle = Ground(at, out on);
        float top = middle.y;
        for (int x = -2; x <= 2; x++)
            for (int z = -2; z <= 2; z++)
                if (x != 0 || z != 0) top = Math.Max(top, Ground(new Vector3(at.x + x * 4, at.y, at.z + z * 4), out _).y);
        return new Vector3(middle.x, top + 2f, middle.z); // Ground's 0.5 m and 2 more
    }

    /// `on`: what it stands on when that isn't terrain (null on terrain).
    static Vector3 Ground(Vector3 at, out string? on)
    {
        var hits = Physics.RaycastAll(new Vector3(at.x, at.y + 1000, at.z), Vector3.down, 3000, ~0, QueryTriggerInteraction.Ignore)
            .Where(h => h.collider != null).OrderBy(h => h.distance).ToList();
        on = null;
        foreach (var hit in hits)
            if (hit.collider.TryCast<TerrainCollider>() != null) return hit.point + Vector3.up * 0.5f;
        if (hits.Count == 0) return at;
        on = hits[0].collider.name;
        return hits[0].point + Vector3.up * 0.5f;
    }

    static GameObject Child(Transform parent, string name, Vector3 position, Quaternion rotation)
    {
        var o = new GameObject(name);
        o.transform.SetParent(parent, false);
        o.transform.SetPositionAndRotation(position, rotation);
        return o;
    }
}

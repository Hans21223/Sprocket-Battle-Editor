using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket;
using Sprocket.CustomBattles;
using Sprocket.MissionFramework;
using Sprocket.Spawning;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SprocketBattles;

/// Give the native spawner the authored poses before it builds component physics state. Never teleport a live track.
[HarmonyPatch(typeof(DeathmatchGameMode), nameof(DeathmatchGameMode.InitiateInternal))]
internal static class SpawnPlan
{
    sealed record Saved(DeathmatchGameMode Mode, int Team, SpawnLocator Original, SpawnLocator Replacement);
    static readonly List<Saved> saved = new();
    static GameObject? root;
    static DeathmatchGameMode? mode;
    static bool ready;
    internal static string? Failure { get; private set; }

    internal static bool OwnsScene(Transform vehicle) => root != null && mode != null
        && vehicle.gameObject.scene.handle == root.scene.handle;

    internal static bool ReadyFor(DeathmatchGameMode? target) => ready && root != null && mode != null
        && target != null && mode.Pointer == target.Pointer && saved.Count > 0;

    internal static void Clear()
    {
        ready = false;
        Failure = null;
        foreach (var entry in saved)
        {
            if (entry.Mode == null || entry.Original == null || entry.Mode.teams == null
                || entry.Team >= entry.Mode.teams.Length) continue;
            var config = entry.Mode.teams[entry.Team];
            if (config?.spawnPoints?.Pointer != entry.Replacement.Pointer) continue;
            config.spawnPoints = entry.Original;
            // TeamDeathmatchConfig is a native value type: the indexer returns a boxed copy.
            entry.Mode.teams[entry.Team] = config;
        }
        saved.Clear();
        if (root != null) UnityEngine.Object.Destroy(root);
        root = null; mode = null;
    }

    // Map Framework repairs native mission roles in its default-priority prefix. The native team map is built next.
    [HarmonyPrefix, HarmonyPriority(Priority.Last)]
    static void Starting(DeathmatchGameMode __instance, GameModeContext __0)
    {
        try
        {
            var setup = __0?.SetupContext;
            if (!Battle.PendingPlacement || setup?.Mode != SupportedGameModes.Deathmatch) return;
            Apply(__instance, setup);
        }
        catch (Exception ex)
        {
            Failure = ex.Message;
            Trace.Write($"spawn plan: couldn't prepare native spawn points: {ex}");
        }
    }

    internal static void Apply(DeathmatchGameMode target, GameSetupContext? initialSetup = null)
    {
        var units = Battle.PendingUnits;
        Clear();
        if (units == null || units.Count == 0) return;
        if (target.teams == null) throw new InvalidOperationException("This battle has no native team setup");
        Battle.OwnScene(target);
        mode = target;
        root = new GameObject("Battle Editor spawn plan");
        SceneManager.MoveGameObjectToScene(root, target.gameObject.scene);
        try
        {
            var setup = initialSetup ?? target.context?.SetupContext;
            var definitions = setup?.SetupConfig?.TryCast<BattleConfig>()?.Teams;
            if (definitions == null) throw new InvalidOperationException("This battle has no custom battle team definitions");
            var roles = new int[definitions.Length];
            for (int team = 0; team < roles.Length; team++)
            {
                var definition = definitions[team] ?? throw new InvalidOperationException($"Team {team + 1} has no definition");
                roles[team] = ((definition.Flags & TeamDefinitionFlags.Attacker) != 0 ? 1 : 0)
                    | ((definition.Flags & TeamDefinitionFlags.Defender) != 0 ? 2 : 0);
            }
            var nativeRoles = new int[target.teams.Length];
            for (int team = 0; team < nativeRoles.Length; team++)
            {
                var mission = target.teams[team]?.mission
                    ?? throw new InvalidOperationException($"Native team {team + 1} has no mission role");
                nativeRoles[team] = ((mission.flags & MissionFlags.Attackers) != 0 ? 1 : 0)
                    | ((mission.flags & MissionFlags.Defenders) != 0 ? 2 : 0);
            }
            int[]? liveMap = null;
            if (initialSetup == null)
            {
                // Retry retains the native map keyed by GameTeamDefinition.ID, not the custom team array index.
                // The custom battle getter assigns attacker ID 1 and defender ID 2, including after swapping sides.
                var map = target.teamIDMap ?? throw new InvalidOperationException("The battle's native team map isn't ready");
                var gameDefinitions = setup?.SetupConfig?.Teams
                    ?? throw new InvalidOperationException("This battle has no native game team definitions");
                if (gameDefinitions.Length != roles.Length)
                    throw new InvalidOperationException("The battle's native and custom team definitions don't match");
                var ids = new int[gameDefinitions.Length];
                for (int team = 0; team < ids.Length; team++) ids[team] = gameDefinitions[team].ID;
                liveMap = SpawnTeamMapping.ReadLiveMap(ids, id => map.TryGetValue(id, out int index) ? index : null);
            }
            var mapping = SpawnTeamMapping.Resolve(roles, nativeRoles, liveMap);
            if (units.Any(u => u.Team < 0 || u.Team >= mapping.Length))
                throw new InvalidOperationException("A tank refers to a team that isn't in this battle");
            var randomPoses = RandomSpawnPoints.Prepare(target, units, mapping);
            Vector3? playerSpawn = null;
            for (int team = 0; team < mapping.Length; team++)
            {
                int nativeTeam = mapping[team];
                var config = target.teams[nativeTeam];
                var original = config?.spawnPoints;
                if (config == null || original == null) throw new InvalidOperationException($"Team {team + 1} has no native spawn locator");
                var ordered = units.Where(u => u.Team == team).ToList();
                if (ordered.Count == 0) continue;
                var group = new GameObject($"Team {team + 1} (native team {nativeTeam + 1})");
                group.transform.SetParent(root.transform, false);
                var points = group.AddComponent<SpawnPoints>();
                points.random = false; points.randomOnConnectingLines = false; points.lastSpawnIndex = -1;
                for (int i = 0; i < ordered.Count; i++)
                {
                    var unit = ordered[i];
                    Vector3 at;
                    Quaternion turn;
                    float halfFootprint = 8;
                    if (randomPoses != null && randomPoses.TryGetValue(unit.Id, out var randomPose))
                    { at = randomPose.Position; turn = randomPose.Rotation; halfFootprint = randomPose.HalfFootprint; }
                    else if (unit.AtSpawn)
                    {
                        bool stagedGauntlet = false;
                        at = Vector3.zero;
                        turn = Quaternion.identity;
                        if (Battle.Playing?.Gauntlet != null && team == 1 && playerSpawn.HasValue)
                        {
                            original.Get(0, out var enemyNative);
                            Vector3 diff = enemyNative.Position - playerSpawn.Value;
                            diff.y = 0;
                            float totalDist = diff.magnitude;
                            if (totalDist > 10f)
                            {
                                Vector3 forwardDir = diff.normalized;
                                Vector3 rightDir = Vector3.Cross(Vector3.up, forwardDir).normalized;
                                float combatDist = Math.Clamp(totalDist * 0.15f, 90f, 160f);
                                Vector3 center = playerSpawn.Value + forwardDir * combatDist;
                                float offset = (i - (ordered.Count - 1) * 0.5f) * 16f;
                                Vector3 candidate = center + rightDir * offset;
                                candidate.y = playerSpawn.Value.y;
                                var testHeight = SpawnClearance.RootHeight(candidate.x - halfFootprint, candidate.x + halfFootprint,
                                    candidate.z - halfFootprint, candidate.z + halfFootprint, 0,
                                    (x, z) => SpawnGround.Height(x, z, candidate.y, SpawnSafety.TerrainStaging(target)));
                                if (testHeight == null)
                                {
                                    var directH = SpawnGround.Height(candidate.x, candidate.z, candidate.y, false);
                                    if (directH != null) testHeight = directH;
                                    else
                                    {
                                        var centerH = SpawnGround.Height(center.x, center.z, center.y, false);
                                        if (centerH != null) { candidate = center; testHeight = centerH; }
                                    }
                                }
                                if (testHeight != null)
                                {
                                    at = candidate;
                                    turn = Quaternion.LookRotation(-forwardDir, Vector3.up);
                                    stagedGauntlet = true;
                                }
                            }
                        }
                        if (!stagedGauntlet)
                        {
                            original.Get(i, out var native);
                            at = native.Position;
                            turn = native.Rotation;
                        }
                    }
                    else
                    { at = Files.Vector(unit.Position); turn = Quaternion.Euler(0, unit.Yaw, 0); }
                    if (!SpawnGround.Finite(at) || !SpawnGround.Finite(turn))
                        throw new InvalidOperationException($"{unit.Id} has an invalid spawn pose");
                    var height = SpawnClearance.RootHeight(at.x - halfFootprint, at.x + halfFootprint, at.z - halfFootprint, at.z + halfFootprint, 0,
                        (x, z) => SpawnGround.Height(x, z, at.y, randomPoses == null && unit.AtSpawn && SpawnSafety.TerrainStaging(target)));
                    if (height == null) throw new InvalidOperationException($"No ground under {unit.Id} at {at}");
                    at.y = height.Value + 2.5f - SpawnClearance.Gap;
                    if (team == 0 && i == 0) playerSpawn = at;
                    var point = new GameObject(unit.Id);
                    point.transform.SetParent(group.transform, false);
                    point.transform.SetPositionAndRotation(at, turn);
                    Trace.Write($"spawn plan: Team {team + 1}, native team {nativeTeam + 1}, index {i} = {unit.Id} at {at}, yaw {turn.eulerAngles.y:0.0}");
                }
                saved.Add(new Saved(target, nativeTeam, original, points));
                config.spawnPoints = points;
                target.teams[nativeTeam] = config;
                if (target.teams[nativeTeam]?.spawnPoints?.Pointer != points.Pointer)
                    throw new InvalidOperationException($"Team {team + 1} didn't retain its native spawn locator");
            }
            ready = saved.Count > 0;
            Trace.Write("spawn plan: authored positions assigned before native vehicle construction");
        }
        catch
        { Clear(); throw; }
    }
}

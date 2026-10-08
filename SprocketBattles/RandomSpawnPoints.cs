using Sprocket;
using UnityEngine;
using UnityEngine.AI;

namespace SprocketBattles;

/// Builds poses once, before native vehicles exist. The native locators keep their authored child order.
internal static class RandomSpawnPoints
{
    internal readonly record struct Pose(Vector3 Position, Quaternion Rotation, float HalfFootprint);
    const float HalfFootprint = 8;
    const float MaxHeightDifference = 2.4f;

    internal static Dictionary<string, Pose>? Prepare(DeathmatchGameMode target, IReadOnlyList<BattleUnit> units, int[] mapping)
    {
        var battle = Battle.Playing;
        // Editor battles have authored positions. Random placement requires explicit launcher settings.
        var settings = battle?.FreeForAllSpawns;
        if (settings is not { Randomize: true }) return null;
        if (settings.Check() is { } problem) throw new InvalidOperationException(problem);
        float halfFootprint = HalfFootprint;
        foreach (var unit in units)
        {
            // Shapes from previously spawned designs retain a conservative whole-tank footprint, including
            // rotated turrets and guns. An unseen design keeps the existing 16 m default footprint.
            var shape = Shapes.Of(unit.Blueprint);
            if (shape == null) continue;
            var bounds = shape.bounds;
            if (!SpawnGround.Finite(bounds.min) || !SpawnGround.Finite(bounds.max)) continue;
            double x = Math.Max(Math.Abs((double)bounds.min.x), Math.Abs((double)bounds.max.x));
            double z = Math.Max(Math.Abs((double)bounds.min.z), Math.Abs((double)bounds.max.z));
            float radius = (float)Math.Sqrt(x * x + z * z);
            if (float.IsFinite(radius)) halfFootprint = Math.Max(halfFootprint, radius + 1);
        }
        float minimum = Math.Max(settings.MinSpacing, (float)(2 * Math.Sqrt(2) * halfFootprint + 2));
        if (minimum > settings.MaxSpacing)
            throw new InvalidOperationException($"These tanks need at least {Math.Ceiling(minimum):0} m of spawn spacing to clear their footprints. Increase the maximum spacing or choose smaller tanks.");
        if (units.Select(u => u.Id).Distinct(StringComparer.Ordinal).Count() != units.Count)
            throw new InvalidOperationException("Random Free-for-All spawns need a different ID for every tank. Re-add the duplicated tank in the editor.");

        // Native starting anchors identify the intended battle area. Never invoke the native random-lines
        // mode or change the ordering which pairs tank definitions with their loading group's locators.
        var anchors = new List<Vector3>();
        for (int team = 0; team < mapping.Length; team++)
        {
            var locator = target.teams[mapping[team]]?.spawnPoints;
            if (locator == null) throw new InvalidOperationException($"Spawn group {team + 1} has no native starting points.");
            int count = Math.Max(1, units.Count(u => u.Team == team));
            for (int i = 0; i < count; i++)
            {
                locator.Get(i, out var point);
                if (SpawnGround.Finite(point.Position)) anchors.Add(point.Position);
            }
        }
        if (anchors.Count == 0) throw new InvalidOperationException("The map has no usable native starting points for random Free-for-All spawns.");
        float reference = anchors.Average(p => p.y);
        double padding = Math.Min(4000, Math.Max(100, settings.MaxSpacing * 2d));
        var area = new RandomSpawnPlan.Area((float)(anchors.Min(p => p.x) - padding), (float)(anchors.Max(p => p.x) + padding),
            (float)(anchors.Min(p => p.z) - padding), (float)(anchors.Max(p => p.z) + padding));

        SpawnSurface? navigation = null;
        try
        {
            NavMesh.Triangulate(out var vertices, out var indices);
            if (vertices != null && indices != null && vertices.Length > 0 && indices.Length >= 3)
                navigation = new SpawnSurface(vertices.Select(p => new SpawnSurface.Vertex(p.x, p.y, p.z)).ToArray(), indices.ToArray());
        }
        catch (Exception ex) { Trace.Write($"random spawns: navigation coverage unavailable; validating scene terrain instead: {ex.Message}"); }
        if (navigation?.Any == true) area = Intersect(area, navigation.Bounds);
        else
        {
            // Some custom maps build navigation later. Their loaded terrain still gives strict finite
            // playable boundaries; candidate footprint, slope and obstacle checks remain mandatory.
            var terrainBounds = SceneTerrainBounds(target.gameObject.scene.handle);
            if (terrainBounds is { } bounds) area = Intersect(area, bounds);
        }
        area = new(area.MinX + halfFootprint, area.MaxX - halfFootprint, area.MinZ + halfFootprint, area.MaxZ - halfFootprint);
        if (!area.Valid) throw new InvalidOperationException("This map has no clear area large enough for random tank spawns. Use placed spawn points or a different map.");
        int seed = System.Random.Shared.Next();
        var ground = new Dictionary<RandomSpawnPlan.Point, float>();
        bool Usable(RandomSpawnPlan.Point point)
        {
            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            for (int x = -1; x <= 1; x++) for (int z = -1; z <= 1; z++)
            {
                float px = point.X + x * halfFootprint, pz = point.Z + z * halfFootprint;
                var nav = navigation?.Any == true ? navigation.Height(px, pz, reference) : null;
                if (navigation?.Any == true && nav == null) return false;
                var height = SpawnGround.Height(px, pz, nav ?? reference, nav == null && SpawnSafety.TerrainStaging(target));
                if (height is not { } support || !float.IsFinite(support)) return false;
                // A navigation layer on an elevated bridge must agree with the collider queried as ground.
                if (nav is { } navigable && Math.Abs((double)navigable - support) > 1.5) return false;
                min = Math.Min(min, support); max = Math.Max(max, support);
                if (max - min > MaxHeightDifference) return false;
            }
            var center = new Vector3(point.X, max + 2, point.Z);
            foreach (var collider in Physics.OverlapBox(center, new Vector3(halfFootprint, 1.8f, halfFootprint), Quaternion.identity,
                                                        ~0, QueryTriggerInteraction.Ignore))
            {
                if (collider == null || collider.gameObject.scene.handle != target.gameObject.scene.handle) continue;
                if (collider.TryCast<TerrainCollider>() != null) continue;
                if (collider.transform.root.name.StartsWith("Battle Editor", StringComparison.Ordinal)) continue;
                // The box begins above the highest footprint ground sample: static solids here are trees,
                // buildings, rocks, fences or overhangs, rather than the supporting road/bridge surface.
                return false;
            }
            ground[point] = max; return true;
        }
        if (!RandomSpawnPlan.TryPlan(units.Count, minimum, settings.MaxSpacing, area, seed, Usable,
                                     out var points, out int candidates))
            throw new InvalidOperationException($"Couldn't fit {units.Count} random spawns with {settings.MinSpacing:0.#}–{settings.MaxSpacing:0.#} m spacing on clear ground. Try a smaller minimum, larger maximum, another map, or placed spawn points.");
        var poses = new Dictionary<string, Pose>(StringComparer.Ordinal);
        for (int i = 0; i < units.Count; i++)
        {
            var point = points[i];
            poses.Add(units[i].Id, new(new Vector3(point.X, ground[point], point.Z), Quaternion.Euler(0, point.Yaw, 0), halfFootprint));
        }
        Trace.Write($"random spawns: {units.Count} global Free-for-All points, seed {seed}, {minimum:0.#}–{settings.MaxSpacing:0.#} m nearest-opponent spacing, {halfFootprint * 2:0.#} m conservative footprint; {candidates} candidates, navigation {(navigation?.Any == true ? "validated" : "not baked; terrain validated")}");
        return poses;
    }

    static RandomSpawnPlan.Area Intersect(RandomSpawnPlan.Area a, RandomSpawnPlan.Area b)
        => new(Math.Max(a.MinX, b.MinX), Math.Min(a.MaxX, b.MaxX), Math.Max(a.MinZ, b.MinZ), Math.Min(a.MaxZ, b.MaxZ));

    static RandomSpawnPlan.Area? SceneTerrainBounds(int scene)
    {
        RandomSpawnPlan.Area? result = null;
        foreach (var terrain in Terrain.activeTerrains)
        {
            if (terrain == null || terrain.gameObject.scene.handle != scene || terrain.terrainData == null) continue;
            var at = terrain.transform.position; var size = terrain.terrainData.size;
            if (!SpawnGround.Finite(at) || !SpawnGround.Finite(size) || size.x <= 0 || size.z <= 0) continue;
            var bounds = new RandomSpawnPlan.Area(at.x, at.x + size.x, at.z, at.z + size.z);
            result = result is { } old ? new(Math.Min(old.MinX, bounds.MinX), Math.Max(old.MaxX, bounds.MaxX),
                Math.Min(old.MinZ, bounds.MinZ), Math.Max(old.MaxZ, bounds.MaxZ)) : bounds;
        }
        return result;
    }
}

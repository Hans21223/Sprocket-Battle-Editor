using Sprocket.Vehicles;
using UnityEngine;

namespace SprocketBattles;

/// Placement queries never use another tank, a trigger or an editor marker as ground.
internal static class SpawnGround
{
    internal static float? Height(float x, float z, float referenceY, bool terrainFirst = false)
    {
        if (!float.IsFinite(x) || !float.IsFinite(z) || !float.IsFinite(referenceY)) return null;
        float? terrain = null, surface = null;
        foreach (var hit in Physics.RaycastAll(new Vector3(x, referenceY + 1000, z), Vector3.down, 5000, ~0, QueryTriggerInteraction.Ignore))
        {
            var collider = hit.collider;
            if (collider == null || !float.IsFinite(hit.point.y)) continue;
            if (collider.TryCast<TerrainCollider>() != null)
                terrain = terrain is { } old ? Math.Max(old, hit.point.y) : hit.point.y;
            else if (collider.attachedRigidbody == null && collider.GetComponentInParent<VehicleObject>() == null
                     && collider.GetComponentInParent<VehicleBehaviour>() == null
                     && !collider.transform.root.name.StartsWith("Battle Editor", StringComparison.Ordinal)
                     && hit.point.y <= referenceY + 60)
                // Keep authored bridge/road placement, without selecting a distant overhead scene collider.
                surface = surface is { } old ? Math.Max(old, hit.point.y) : hit.point.y;
        }
        if (terrainFirst && terrain != null) return terrain;
        return terrain is { } floor && surface is { } solid ? Math.Max(floor, solid) : terrain ?? surface;
    }

    internal static Vector3? Position(SpawnAssembly assembly, Vector3 target, Quaternion turn, bool terrainFirst = false)
    {
        if (!Finite(target) || !Finite(turn) || !assembly.TryGetBounds(out var bounds)) return null;
        return Position(assembly.Root, bounds, target, turn, terrainFirst);
    }

    internal static Vector3? Position(Transform root, Bounds bounds, Vector3 target, Quaternion turn, bool terrainFirst)
    {
        if (!Finite(target) || !Finite(turn)) return null;
        if (!Finite(root.position) || !Finite(root.rotation)) return null;
        var inverse = Quaternion.Inverse(root.rotation);
        var min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        // Transform the conservative collider bounds before sampling, so a requested yaw is included.
        for (int x = 0; x < 2; x++)
            for (int y = 0; y < 2; y++)
                for (int z = 0; z < 2; z++)
                {
                    var world = new Vector3(x == 0 ? bounds.min.x : bounds.max.x,
                                            y == 0 ? bounds.min.y : bounds.max.y,
                                            z == 0 ? bounds.min.z : bounds.max.z);
                    var offset = turn * (inverse * (world - root.position));
                    min = Vector3.Min(min, offset);
                    max = Vector3.Max(max, offset);
                }
        var height = SpawnClearance.RootHeight(target.x + min.x, target.x + max.x, target.z + min.z, target.z + max.z,
                                              min.y, (x, z) => Height(x, z, target.y, terrainFirst));
        return height is { } at ? new Vector3(target.x, at, target.z) : null;
    }

    internal static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
    internal static bool Finite(Quaternion q) => float.IsFinite(q.x) && float.IsFinite(q.y) && float.IsFinite(q.z)
        && float.IsFinite(q.w) && q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w > 0.0001f;
}

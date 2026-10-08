using UnityEngine;
using UnityEngine.AI;

namespace SprocketMaps;

/// Use the native path API present in Sprocket, without stripped triangulation or sampling wrappers.
internal sealed class CustomMapPathQuery : IDisposable
{
    readonly NavMeshPath path = new();
    int remaining = 4096;
    bool disposed;

    internal bool Contains(Vector3 point) => Connects(point, point);

    internal bool Connects(Vector3 from, Vector3 to)
    {
        if (disposed) throw new ObjectDisposedException(nameof(CustomMapPathQuery));
        static bool Finite(Vector3 p) => float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z);
        if (!Finite(from) || !Finite(to)) return false;
        if (remaining-- <= 0)
            throw new InvalidOperationException("Spawn validation reached its query limit. Add two clear, connected spawn-area markers to this map.");
        if (!NavMesh.CalculatePath(from, to, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
            return false;
        // corners is a compiled native getter. GetCornersNonAlloc and Triangulate are stripped wrappers.
        var corners = path.corners;
        if (corners == null || corners.Length == 0) return false;
        static CustomMapPathRules.Point Point(Vector3 p) => new(p.x, p.y, p.z);
        return CustomMapPathRules.Reaches(Point(from), Point(to), Point(corners[0]),
            Point(corners[corners.Length - 1]), corners.Length, true);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        var pointer = path.m_Ptr;
        path.m_Ptr = IntPtr.Zero;
        if (pointer != IntPtr.Zero) NavMeshPath.DestroyNavMeshPath(pointer);
    }
}

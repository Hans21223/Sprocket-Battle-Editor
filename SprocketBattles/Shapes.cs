using System.Security.Cryptography;
using System.Text;
using Sprocket.Vehicles.PlateStructures;
using UnityEngine;
using UnityEngine.Rendering;

namespace SprocketBattles;

/// What each design looks like, for its markers in the editor: a spawned tank of it copied into one mesh, in the tank's
/// own space with its lowest point at 0. The plates are taken as the physics has them (they draw themselves without a
/// mesh object), the other parts (wheels, gun, fittings) from their meshes. Kept in BepInEx\cache\SprocketBattles-shapes
/// until the design's file changes, so a design has its shape from its first battle on.
internal static class Shapes
{
    // ponytail: every readable part goes in, inside ones too; cap it if big designs make the files or markers heavy.
    const int MaxPoints = 250_000;

    static readonly string Dir = Path.Combine(BepInEx.Paths.CachePath, "SprocketBattles-shapes");
    static readonly Dictionary<string, (Mesh? Mesh, long Stamp)> known = new(StringComparer.OrdinalIgnoreCase);

    static long StampOf(string path) { try { return File.GetLastWriteTimeUtc(path).Ticks; } catch (Exception) { return 0; } }

    static string FileFor(string path) =>
        Path.Combine(Dir, Path.GetFileNameWithoutExtension(path) + "-" +
                          Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..10] + ".shape");

    /// The design's shape, if a tank of it has been seen since its file last changed.
    internal static Mesh? Of(string blueprint)
    {
        var path = Files.Absolute(blueprint);
        long stamp = StampOf(path);
        // A known miss (no shape) stays one until the file changes; a known shape is read again only if it's gone.
        if (known.TryGetValue(path, out var k) && k.Stamp == stamp && (k.Mesh is null || k.Mesh)) return k.Mesh;
        Mesh? mesh = null;
        try
        {
            var file = FileFor(path);
            if (File.Exists(file))
            {
                using var r = new BinaryReader(File.OpenRead(file));
                if (r.ReadInt64() == stamp)
                {
                    var points = new Vector3[r.ReadInt32()];
                    for (int i = 0; i < points.Length; i++) points[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    var triangles = new int[r.ReadInt32()];
                    for (int i = 0; i < triangles.Length; i++) triangles[i] = r.ReadInt32();
                    mesh = MeshOf(Path.GetFileNameWithoutExtension(path), points, triangles);
                }
            }
        }
        catch (Exception ex) { Trace.Write($"shapes: couldn't read {Path.GetFileNameWithoutExtension(path)}'s: {ex.Message}"); }
        known[path] = (mesh, stamp);
        return mesh;
    }

    /// A spawned tank of a design: its shape taken and kept, unless it's known already.
    internal static void Learn(string blueprint, Transform tank)
    {
        if (Of(blueprint) != null) return;
        var path = Files.Absolute(blueprint);
        string name = Path.GetFileNameWithoutExtension(path);
        var toTank = tank.worldToLocalMatrix;
        var points = new List<Vector3>();
        var triangles = new List<int>();
        int parts = 0, unreadable = 0, left = 0;
        void Add(Mesh? mesh, Transform part)
        {
            if (mesh == null) return;
            if (!mesh.isReadable) { unreadable++; return; }
            var v = mesh.vertices;
            if (points.Count + v.Length > MaxPoints) { left++; return; }
            var t = mesh.triangles;
            var m = toTank * part.localToWorldMatrix;
            int start = points.Count;
            for (int i = 0; i < v.Length; i++) points.Add(m.MultiplyPoint3x4(v[i]));
            for (int i = 0; i < t.Length; i++) triangles.Add(start + t[i]);
            parts++;
        }
        foreach (var plates in tank.GetComponentsInChildren<PlateStructure>())
            foreach (var collider in new[] { plates.physicsCollider, plates.mirrorPhysicsCollider })
                if (collider != null && collider.enabled) Add(collider.sharedMesh, collider.transform);
        foreach (var filter in tank.GetComponentsInChildren<MeshFilter>())
            if (filter.gameObject.activeInHierarchy && filter.GetComponent<MeshRenderer>() is { enabled: true }) Add(filter.sharedMesh, filter.transform);
        if (triangles.Count == 0) { Trace.Write($"shapes: nothing to take from {name} ({unreadable} meshes unreadable)"); return; }
        float bottom = points.Min(p => p.y);
        for (int i = 0; i < points.Count; i++) points[i] -= new Vector3(0, bottom, 0);
        long stamp = StampOf(path);
        var mesh = MeshOf(name, points.ToArray(), triangles.ToArray());
        known[path] = (mesh, stamp);
        try
        {
            Directory.CreateDirectory(Dir);
            using var w = new BinaryWriter(File.Create(FileFor(path)));
            w.Write(stamp);
            w.Write(points.Count);
            foreach (var p in points) { w.Write(p.x); w.Write(p.y); w.Write(p.z); }
            w.Write(triangles.Count);
            foreach (var t in triangles) w.Write(t);
        }
        catch (Exception ex) { Trace.Write($"shapes: couldn't keep {name}'s: {ex.Message}"); }
        var size = mesh.bounds.size;
        Trace.Write($"shapes: {name} taken from {parts} parts, {points.Count} points, {size.x:0.0} x {size.y:0.0} x {size.z:0.0} m" +
                    (unreadable > 0 ? $", {unreadable} unreadable" : "") + (left > 0 ? $", {left} left out (over {MaxPoints} points)" : ""));
    }

    static Mesh MeshOf(string name, Vector3[] points, int[] triangles)
    {
        // Kept by this class, which Unity can't see: without the flag, a scene change's clean-up would destroy it.
        var mesh = new Mesh { name = "Battle Editor shape " + name, hideFlags = HideFlags.DontUnloadUnusedAsset };
        if (points.Length > 65535) mesh.indexFormat = IndexFormat.UInt32;
        mesh.vertices = points;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}

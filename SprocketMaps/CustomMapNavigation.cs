namespace SprocketMaps;

/// A managed spatial lookup for the native triangulation. The game's stripped Unity wrapper has no
/// SamplePosition, and scanning native arrays for every formation footprint would block the main thread.
internal sealed class CustomMapNavigation
{
    internal readonly record struct Vertex(float X, float Y, float Z);
    internal readonly record struct Pair(int First, int Second, float DistanceSquared);
    readonly record struct Triangle(Vertex A, Vertex B, Vertex C);
    const float Cell = 32;
    readonly Dictionary<(int X, int Z), List<Triangle>> cells = new();
    readonly List<Triangle> large = new();
    internal bool Any { get; private set; }

    /// Retain only the pairs that spawn validation can visit. Sorting every pair of a 31 x 31 grid
    /// allocated hundreds of thousands of entries on the Unity update thread.
    internal static IReadOnlyList<Pair> FarthestPairs(IReadOnlyList<Vertex> points, int limit, float minimumDistanceSquared)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        if (!float.IsFinite(minimumDistanceSquared) || minimumDistanceSquared < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumDistanceSquared));
        var retained = new PriorityQueue<Pair, (float Distance, long Order)>();
        for (int i = 0; i < points.Count; i++)
        {
            if (!Finite(points[i])) continue;
            for (int j = i + 1; j < points.Count; j++)
            {
                if (!Finite(points[j])) continue;
                float x = points[i].X - points[j].X, y = points[i].Y - points[j].Y, z = points[i].Z - points[j].Z;
                float distance = x * x + y * y + z * z;
                if (!float.IsFinite(distance) || distance < minimumDistanceSquared) continue;
                // Earlier authored/grid pairs win equal-distance ties, as the previous stable sort did.
                long order = (long)i * points.Count + j;
                var priority = (distance, -order);
                if (retained.Count == limit)
                {
                    retained.TryPeek(out _, out var smallest);
                    if (priority.CompareTo(smallest) <= 0) continue;
                    retained.Dequeue();
                }
                retained.Enqueue(new Pair(i, j, distance), priority);
            }
        }
        return retained.UnorderedItems.Select(item => item.Element)
            .OrderByDescending(pair => pair.DistanceSquared).ThenBy(pair => pair.First).ThenBy(pair => pair.Second).ToArray();
    }

    internal CustomMapNavigation(IReadOnlyList<Vertex> vertices, IReadOnlyList<int> indices)
    {
        for (int i = 0; i + 2 < indices.Count; i += 3)
        {
            int a = indices[i], b = indices[i + 1], c = indices[i + 2];
            if (a < 0 || b < 0 || c < 0 || a >= vertices.Count || b >= vertices.Count || c >= vertices.Count) continue;
            var triangle = new Triangle(vertices[a], vertices[b], vertices[c]);
            if (!Finite(triangle.A) || !Finite(triangle.B) || !Finite(triangle.C) || Math.Abs(Determinant(triangle)) < 0.00001) continue;
            int x0 = Tile(Math.Min(triangle.A.X, Math.Min(triangle.B.X, triangle.C.X)));
            int x1 = Tile(Math.Max(triangle.A.X, Math.Max(triangle.B.X, triangle.C.X)));
            int z0 = Tile(Math.Min(triangle.A.Z, Math.Min(triangle.B.Z, triangle.C.Z)));
            int z1 = Tile(Math.Max(triangle.A.Z, Math.Max(triangle.B.Z, triangle.C.Z)));
            if ((long)x1 - x0 > 32 || (long)z1 - z0 > 32) large.Add(triangle);
            else for (int x = x0; x <= x1; x++) for (int z = z0; z <= z1; z++)
            {
                if (!cells.TryGetValue((x, z), out var list)) cells[(x, z)] = list = new();
                list.Add(triangle);
            }
            Any = true;
        }
    }

    internal float? Height(float x, float z, float reference)
    {
        if (!float.IsFinite(x) || !float.IsFinite(z) || !float.IsFinite(reference)) return null;
        float? closest = null;
        void Query(IEnumerable<Triangle> triangles)
        {
            foreach (var triangle in triangles)
            {
                var a = triangle.A; var b = triangle.B; var c = triangle.C;
                double determinant = Determinant(triangle);
                double u = ((b.Z - (double)c.Z) * (x - (double)c.X) + (c.X - (double)b.X) * (z - (double)c.Z)) / determinant;
                double v = ((c.Z - (double)a.Z) * (x - (double)c.X) + (a.X - (double)c.X) * (z - (double)c.Z)) / determinant;
                double w = 1 - u - v;
                if (u < -0.00001 || v < -0.00001 || w < -0.00001) continue;
                float y = (float)(u * a.Y + v * b.Y + w * c.Y);
                if (!float.IsFinite(y)) continue;
                if (closest == null || Math.Abs(y - reference) < Math.Abs(closest.Value - reference)) closest = y;
            }
        }
        if (cells.TryGetValue((Tile(x), Tile(z)), out var found)) Query(found);
        Query(large);
        return closest;
    }

    static int Tile(float value) => (int)Math.Clamp(Math.Floor((double)value / Cell), int.MinValue + 1d, int.MaxValue - 1d);
    static bool Finite(Vertex vertex) => float.IsFinite(vertex.X) && float.IsFinite(vertex.Y) && float.IsFinite(vertex.Z);
    static double Determinant(Triangle triangle) => (triangle.B.Z - (double)triangle.C.Z) * (triangle.A.X - (double)triangle.C.X)
        + (triangle.C.X - (double)triangle.B.X) * (triangle.A.Z - (double)triangle.C.Z);
}

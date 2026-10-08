namespace SprocketBattles;

/// Read-only navigation triangle lookup. A spatial index keeps each footprint query local to its own tile.
internal sealed class SpawnSurface
{
    internal readonly record struct Vertex(float X, float Y, float Z);
    readonly record struct Triangle(Vertex A, Vertex B, Vertex C);
    const float Cell = 64;
    readonly Dictionary<(int X, int Z), List<Triangle>> cells = new();
    readonly List<Triangle> large = new();
    internal RandomSpawnPlan.Area Bounds { get; private set; }
    internal bool Any { get; private set; }

    internal SpawnSurface(IReadOnlyList<Vertex> vertices, IReadOnlyList<int> indices)
    {
        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
        float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
        for (int i = 0; i + 2 < indices.Count; i += 3)
        {
            int a = indices[i], b = indices[i + 1], c = indices[i + 2];
            if (a < 0 || b < 0 || c < 0 || a >= vertices.Count || b >= vertices.Count || c >= vertices.Count) continue;
            var triangle = new Triangle(vertices[a], vertices[b], vertices[c]);
            if (!Finite(triangle.A) || !Finite(triangle.B) || !Finite(triangle.C)) continue;
            double det = Determinant(triangle);
            if (Math.Abs(det) < 0.0001) continue;
            float x0 = Math.Min(triangle.A.X, Math.Min(triangle.B.X, triangle.C.X));
            float x1 = Math.Max(triangle.A.X, Math.Max(triangle.B.X, triangle.C.X));
            float z0 = Math.Min(triangle.A.Z, Math.Min(triangle.B.Z, triangle.C.Z));
            float z1 = Math.Max(triangle.A.Z, Math.Max(triangle.B.Z, triangle.C.Z));
            int cx0 = Tile(x0), cx1 = Tile(x1), cz0 = Tile(z0), cz1 = Tile(z1);
            // An unusually large triangle is stored once, without creating millions of empty buckets.
            if ((long)cx1 - cx0 > 32 || (long)cz1 - cz0 > 32) large.Add(triangle);
            else for (int x = cx0; x <= cx1; x++) for (int z = cz0; z <= cz1; z++)
            {
                if (!cells.TryGetValue((x, z), out var list)) cells[(x, z)] = list = new();
                list.Add(triangle);
            }
            minX = Math.Min(minX, x0); maxX = Math.Max(maxX, x1);
            minZ = Math.Min(minZ, z0); maxZ = Math.Max(maxZ, z1); Any = true;
        }
        Bounds = new(minX, maxX, minZ, maxZ);
    }

    internal float? Height(float x, float z, float referenceY)
    {
        if (!float.IsFinite(x) || !float.IsFinite(z) || !float.IsFinite(referenceY)) return null;
        float? best = null;
        void Test(IEnumerable<Triangle> triangles)
        {
            foreach (var t in triangles)
            {
                double det = Determinant(t);
                double a = (((double)t.B.Z - t.C.Z) * (x - (double)t.C.X)
                    + ((double)t.C.X - t.B.X) * (z - (double)t.C.Z)) / det;
                double b = (((double)t.C.Z - t.A.Z) * (x - (double)t.C.X)
                    + ((double)t.A.X - t.C.X) * (z - (double)t.C.Z)) / det;
                double c = 1 - a - b;
                const double tolerance = 0.00001;
                if (a < -tolerance || b < -tolerance || c < -tolerance) continue;
                float height = (float)(a * t.A.Y + b * t.B.Y + c * t.C.Y);
                if (!float.IsFinite(height)) continue;
                if (best == null || Math.Abs((double)height - referenceY) < Math.Abs((double)best.Value - referenceY)) best = height;
            }
        }
        if (cells.TryGetValue((Tile(x), Tile(z)), out var found)) Test(found);
        Test(large);
        return best;
    }

    static int Tile(float coordinate) => (int)Math.Clamp(Math.Floor((double)coordinate / Cell), int.MinValue + 1d, int.MaxValue - 1d);
    static bool Finite(Vertex v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    static double Determinant(Triangle t) => ((double)t.B.Z - t.C.Z) * (t.A.X - (double)t.C.X)
        + ((double)t.C.X - t.B.X) * (t.A.Z - (double)t.C.Z);
}

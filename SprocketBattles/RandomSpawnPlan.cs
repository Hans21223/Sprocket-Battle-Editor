namespace SprocketBattles;

/// A single random layout for all contenders, independent of their original loading groups.
/// Minimum spacing applies to every pair; maximum spacing is the distance to a nearest opponent.
internal static class RandomSpawnPlan
{
    internal const int CandidateBudget = 4096;
    internal const int SurfaceBudget = 1024;
    internal readonly record struct Point(float X, float Z, float Yaw);
    internal readonly record struct Area(float MinX, float MaxX, float MinZ, float MaxZ)
    {
        internal bool Valid => float.IsFinite(MinX) && float.IsFinite(MaxX) && float.IsFinite(MinZ)
            && float.IsFinite(MaxZ) && MaxX > MinX && MaxZ > MinZ;
        internal bool Contains(float x, float z) => float.IsFinite(x) && float.IsFinite(z)
            && x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ;
    }

    internal static bool TryPlan(int count, float minimum, float maximum, Area area, int seed,
                                Func<Point, bool> usable, out Point[] result, out int candidates)
    {
        result = Array.Empty<Point>(); candidates = 0;
        if (count < 2 || count > 8 || !area.Valid || !float.IsFinite(minimum) || !float.IsFinite(maximum)
            || minimum <= 0 || maximum < minimum) return false;
        var random = new Random(seed);
        int queries = 0;
        // Float world coordinates round by fractions of a millimetre on ordinary maps. The tolerance also
        // allows an exact minimum == maximum range without requiring an unrepresentable float circle.
        double epsilon = Math.Max(0.001, maximum * 0.000001);
        double minSquared = Math.Pow(Math.Max(0, minimum - epsilon), 2), maxSquared = Math.Pow(maximum + epsilon, 2);
        // Start over if one locally valid point leaves no room for the remaining tanks. Both candidate work
        // and expensive surface queries have hard global limits, including all partial layouts.
        for (int restart = 0; restart < 16 && candidates < CandidateBudget && queries < SurfaceBudget; restart++)
        {
            var points = new List<Point>(count);
            for (int index = 0; index < count; index++)
            {
                bool found = false;
                for (int attempt = 0; attempt < 160 && candidates < CandidateBudget && queries < SurfaceBudget; attempt++)
                {
                    candidates++;
                    double x, z;
                    if (points.Count == 0)
                    {
                        x = area.MinX + ((double)area.MaxX - area.MinX) * random.NextDouble();
                        z = area.MinZ + ((double)area.MaxZ - area.MinZ) * random.NextDouble();
                    }
                    else
                    {
                        var neighbour = points[random.Next(points.Count)];
                        double angle = random.NextDouble() * Math.PI * 2;
                        double distance = minimum + ((double)maximum - minimum) * random.NextDouble();
                        x = neighbour.X + Math.Cos(angle) * distance;
                        z = neighbour.Z + Math.Sin(angle) * distance;
                    }
                    var candidate = new Point((float)x, (float)z, (float)(random.NextDouble() * 360));
                    if (!area.Contains(candidate.X, candidate.Z)) continue;
                    if (points.Any(p => DistanceSquared(p, candidate) < minSquared)) continue;
                    // Rounding float coordinates must not push an otherwise exact annulus endpoint outside
                    // the requested maximum. Reject rather than silently widen the player's settings.
                    if (points.Count > 0 && !points.Any(p => DistanceSquared(p, candidate) <= maxSquared)) continue;
                    queries++;
                    if (!usable(candidate)) continue;
                    points.Add(candidate); found = true; break;
                }
                if (!found) break;
            }
            if (points.Count != count) continue;
            result = points.ToArray(); return true;
        }
        return false;
    }

    internal static double DistanceSquared(Point a, Point b)
    {
        double x = (double)a.X - b.X, z = (double)a.Z - b.Z;
        return x * x + z * z;
    }
}

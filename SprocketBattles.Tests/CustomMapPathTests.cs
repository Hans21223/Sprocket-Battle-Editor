using SprocketMaps;
using Point = SprocketMaps.CustomMapPathRules.Point;

static class CustomMapPathTests
{
    static void Check(bool ok, string why) { if (!ok) throw new Exception("custom navigation: " + why); }
    internal static void Run()
    {
        var at = new Point(-290, 7.7f, -178);
        var to = new Point(242, 4.7f, -74);
        Check(CustomMapPathRules.Reaches(at, to, at, to, 7, true), "a connected route reaches both actual spawn areas");
        Check(CustomMapPathRules.Reaches(at, at, at, at, 1, true), "one-corner local paths can verify formation footprints");
        Check(!CustomMapPathRules.Reaches(at, to, at, to, 7, false), "partial paths cannot join disconnected spawn areas");
        Check(!CustomMapPathRules.Reaches(at, at, at, at, 0, true), "empty paths cannot claim navigation coverage");
        Check(!CustomMapPathRules.Reaches(at, to, at with { X = at.X + 0.6f }, to, 7, true),
            "a projected starting point on a nearby road cannot validate unsupported ground");
        Check(!CustomMapPathRules.Reaches(at, to, at, to with { Y = to.Y + 20 }, 7, true),
            "navigation on a roof cannot validate the street beneath it");
        Check(!CustomMapPathRules.Reaches(at, to, at, to with { Z = float.NaN }, 7, true)
            && !CustomMapPathRules.Reaches(at with { X = float.PositiveInfinity }, to, at, to, 7, true),
            "invalid native or requested coordinates are rejected");
        Check(CustomMapPathRules.Reaches(at, to, at with { Y = at.Y + 0.2f }, to, 7, true),
            "normal navigation voxel height offsets preserve supported spawn areas");
        var candidates = Enumerable.Range(0, 81).Select(i => new CustomMapNavigation.Vertex(
            i % 9 * 40, i % 3, i / 9 * 40)).ToArray();
        var expected = new List<CustomMapNavigation.Pair>();
        for (int i = 0; i < candidates.Length; i++)
            for (int j = i + 1; j < candidates.Length; j++)
            {
                var a = candidates[i]; var b = candidates[j];
                float x = a.X - b.X, y = a.Y - b.Y, z = a.Z - b.Z;
                float distance = x * x + y * y + z * z;
                if (distance >= 10000) expected.Add(new(i, j, distance));
            }
        foreach (int limit in new[] { 1, 16, 512, 4096 })
            Check(CustomMapNavigation.FarthestPairs(candidates, limit, 10000)
                .SequenceEqual(expected.OrderByDescending(pair => pair.DistanceSquared).Take(limit)),
                "bounded spawn ranking preserves the exhaustive ordering, including equal-distance ties");
        var invalid = new[] { new CustomMapNavigation.Vertex(float.NaN, 0, 0),
            new CustomMapNavigation.Vertex(0, 0, 0), new CustomMapNavigation.Vertex(120, 0, 0) };
        Check(CustomMapNavigation.FarthestPairs(invalid, 512, 10000).SequenceEqual(new[] { new CustomMapNavigation.Pair(1, 2, 14400) }),
            "invalid spawn coordinates never enter native physics/path validation");
        var grid = Enumerable.Range(0, 961).Select(i => new CustomMapNavigation.Vertex(i % 31 * 40, 0, i / 31 * 40)).ToArray();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var ranked = CustomMapNavigation.FarthestPairs(grid, 512, 10000);
        long allocation = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(ranked.Count == 512 && allocation < 1024 * 1024,
            "the full fallback grid retains 512 pairs without allocating an exhaustive pair list");
        Console.WriteLine($"CUSTOM_MAP_SPAWN_RANKING_OK: 961 candidates, 512 retained, {allocation} allocated bytes");
        Console.WriteLine("CUSTOM_MAP_PATH_TESTS_OK: real endpoints, local coverage, partial routes, stacked platforms, finite coordinates");
    }
}

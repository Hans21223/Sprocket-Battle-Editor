using SprocketBattles;

static class RandomSpawnTests
{
    static void Check(bool ok, string message)
    { if (!ok) throw new Exception("random spawns: " + message); }

    public static void Run()
    {
        var area = new RandomSpawnPlan.Area(-500, 500, -500, 500);
        Check(RandomSpawnPlan.TryPlan(8, 80, 250, area, 752, _ => true, out var points, out int attempts), "eight global contenders fit");
        Check(points.Length == 8 && attempts <= RandomSpawnPlan.CandidateBudget, "complete plan and bounded candidate work");
        Check(points.All(p => area.Contains(p.X, p.Z) && p.Yaw >= 0 && p.Yaw < 360), "positions and headings stay finite and inside map bounds");
        for (int i = 0; i < points.Length; i++)
        {
            var distances = points.Where((_, j) => i != j).Select(p => Math.Sqrt(RandomSpawnPlan.DistanceSquared(points[i], p))).ToArray();
            Check(distances.All(d => d >= 79.999), "every pair obeys minimum separation across both loading groups");
            Check(distances.Min() <= 250.001, "every tank has an opponent within the maximum spacing");
        }
        Check(RandomSpawnPlan.TryPlan(8, 80, 250, area, 752, _ => true, out var repeat, out _) && repeat.SequenceEqual(points), "same seed repeats the same layout");
        Check(RandomSpawnPlan.TryPlan(8, 80, 250, area, 753, _ => true, out var next, out _) && !next.SequenceEqual(points), "new seed changes the layout");

        // Narrow clear ground is embedded in a much larger area: obstacle rejection must not let an unsafe
        // candidate appear in the completed plan, and partial failed layouts must never escape.
        bool Clear(RandomSpawnPlan.Point p) => p.X >= -300 && p.X <= 300 && p.Z >= -100 && p.Z <= 100;
        Check(RandomSpawnPlan.TryPlan(6, 50, 120, area, 329, Clear, out var corridor, out _), "bounded sampling can find a safe corridor");
        Check(corridor.All(Clear), "all emitted points passed surface and obstacle validation");
        int calls = 0;
        Check(!RandomSpawnPlan.TryPlan(8, 80, 250, area, 7, _ => { calls++; return false; }, out var blocked, out attempts), "fully blocked ground is refused");
        Check(blocked.Length == 0 && attempts <= RandomSpawnPlan.CandidateBudget && calls <= RandomSpawnPlan.SurfaceBudget, "blocked maps fail within fixed budgets without partial results");
        Check(!RandomSpawnPlan.TryPlan(8, 80, 250, new(-5, 5, -5, 5), 7, _ => true, out blocked, out _), "impossible footprint spacing is refused");
        Check(blocked.Length == 0, "impossible spacing never silently collapses tank positions");
        Check(RandomSpawnPlan.TryPlan(2, 100, 100, area, 21, _ => true, out var exact, out _), "exact spacing remains usable with float precision");
        Check(Math.Abs(Math.Sqrt(RandomSpawnPlan.DistanceSquared(exact[0], exact[1])) - 100) <= 0.001, "equal endpoints remain the requested distance");

        foreach (var limits in new[] { (float.NaN, 100f), (10f, float.PositiveInfinity), (100f, 50f), (0f, 100f) })
        {
            calls = 0;
            Check(!RandomSpawnPlan.TryPlan(2, limits.Item1, limits.Item2, area, 7, _ => { calls++; return true; }, out _, out _)
                && calls == 0, "invalid ranges are rejected before physics queries");
        }
        foreach (int count in new[] { 0, 1, 9 })
            Check(!RandomSpawnPlan.TryPlan(count, 10, 100, area, 7, _ => true, out _, out _), "contender count is limited to native independent factions");
        Navigation();
        Console.WriteLine("RANDOM_SPAWN_TESTS_OK: global spacing, bounds, reproducible seeds, obstacle rejection, finite budgets, exact ranges, navigation coverage");
    }

    static void Navigation()
    {
        var vertices = new[]
        {
            new SpawnSurface.Vertex(-100, 0, -100), new SpawnSurface.Vertex(100, 20, -100),
            new SpawnSurface.Vertex(100, 20, 100), new SpawnSurface.Vertex(-100, 0, 100),
            new SpawnSurface.Vertex(-100, 100, -100), new SpawnSurface.Vertex(100, 100, -100),
            new SpawnSurface.Vertex(100, 100, 100), new SpawnSurface.Vertex(-100, 100, 100),
        };
        var surface = new SpawnSurface(vertices, new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7, -1, 8, 2 });
        Check(surface.Any && surface.Bounds.Contains(100, -100), "navigation bounds include triangle edges");
        Check(Math.Abs(surface.Height(0, 0, 0)!.Value - 10) < 0.0001, "triangle interpolation returns supporting slope height");
        Check(surface.Height(0, 0, 90) == 100, "stacked navigation chooses the layer nearest native reference height");
        Check(surface.Height(200, 0, 0) == null, "navigation coverage excludes off-map ground");
        Check(surface.Height(float.NaN, 0, 0) == null, "invalid navigation query remains unknown");
        Check(surface.Height(100, -100, 0) == 20, "triangle boundary support is retained");
        var missing = new SpawnSurface(new[] { new SpawnSurface.Vertex(float.NaN, 0, 0), new SpawnSurface.Vertex(0, 0, 0) }, new[] { 0, 1, 1 });
        Check(!missing.Any && missing.Height(0, 0, 0) == null, "invalid and degenerate triangles never invent a navigation surface");
        var huge = new SpawnSurface(new[] { new SpawnSurface.Vertex(-100000, 1, -100000), new SpawnSurface.Vertex(100000, 1, -100000),
            new SpawnSurface.Vertex(0, 1, 100000) }, new[] { 0, 1, 2 });
        Check(huge.Height(0, 0, 0) == 1, "oversized triangle indexing stays bounded and queryable");
    }
}

using SprocketBattles;

/// The root must clear terrain under the whole solid footprint, including its boundaries, before physics starts.
static class SpawnClearanceTests
{
    static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception("spawn clearance: " + message);
    }

    static float Height(float? result, string message)
    {
        Check(result is { } height && float.IsFinite(height), message);
        return result!.Value;
    }

    static void Near(float actual, float expected, string message)
        => Check(Math.Abs(actual - expected) < 0.0002f, $"{message} ({actual} instead of {expected})");

    public static void Run()
    {
        // The lowest local solid point, rather than the vehicle's origin, must sit above the ground.
        const float flatGround = 42, bottom = -1.6f;
        float flat = Height(SpawnClearance.RootHeight(-2, 2, -5, 5, bottom, (_, _) => flatGround), "flat terrain gives a placement");
        Near(flat + bottom, flatGround + SpawnClearance.Gap, "the solid bottom clears flat terrain by the safety gap");
        float belowSea = Height(SpawnClearance.RootHeight(0, 2, 0, 4, 0.8f, (_, _) => -12), "negative terrain height is supported");
        Near(belowSea + 0.8f, -12 + SpawnClearance.Gap, "positive local bottom and below-sea terrain retain their offsets");

        // Sampling only the centre leaves an uphill track embedded. The highest corner governs either slope direction.
        foreach (float direction in new[] { -1f, 1f })
        {
            float Ground(float x, float z) => direction * (0.4f * x + 0.6f * z);
            float slope = Height(SpawnClearance.RootHeight(-2, 2, -5, 5, bottom, (x, z) => Ground(x, z)), "sloped terrain gives a placement");
            foreach (float x in new[] { -2f, 0, 2 })
                foreach (float z in new[] { -5f, 0, 5 })
                    Check(slope + bottom >= Ground(x, z) + SpawnClearance.Gap - 0.0002f, "every slope corner has clearance");
            Check(slope > Ground(0, 0) - bottom + SpawnClearance.Gap + 3,
                "placement is above the unsafe centre-only result");
            Near(slope + bottom, 3.8f + SpawnClearance.Gap, "both slope directions find the highest boundary");
        }

        // A long gun/tank footprint must include its end, even if only the end rests on raised ground.
        bool frontVisited = false, rearVisited = false;
        float longTank = Height(SpawnClearance.RootHeight(-2, 2, -35, 47, -0.7f, (x, z) =>
        {
            if (z == -35) rearVisited = true;
            if (z == 47) { frontVisited = true; return 9; }
            return 0;
        }), "long footprint gives a placement");
        Check(frontVisited && rearVisited, "both ends of a long footprint are sampled");
        Near(longTank - 0.7f, 9 + SpawnClearance.Gap, "raised ground at the far end is respected");

        // The same shape must receive the same height at a translated world position.
        float original = Height(SpawnClearance.RootHeight(-3, 3, -7, 7, -1, (x, z) => 5 + 0.25f * x - 0.5f * z), "original terrain placement");
        float moved = Height(SpawnClearance.RootHeight(997, 1003, -207, -193, -1, (x, z) =>
            5 + 0.25f * (x - 1000) - 0.5f * (z + 200)), "translated terrain placement");
        Near(moved, original, "terrain coordinates are world coordinates rather than offsets from zero");

        // Missing ground must remain unknown; holes and invalid hits must not erase valid terrain support.
        Check(SpawnClearance.RootHeight(-2, 2, -2, 2, -1, (_, _) => null) == null, "unknown terrain does not invent a height");
        Check(SpawnClearance.RootHeight(-2, 2, -2, 2, -1, (_, _) => float.NaN) == null, "all invalid terrain remains unknown");
        float mixed = Height(SpawnClearance.RootHeight(-2, 2, -2, 2, -1, (x, z) =>
            x < 0 ? null : z < 0 ? float.PositiveInfinity : z == 0 ? float.NaN : 6), "finite support survives missing and invalid hits");
        Near(mixed - 1, 6 + SpawnClearance.Gap, "only finite known terrain contributes support");
        float point = Height(SpawnClearance.RootHeight(123, 123, -456, -456, -2, (x, z) =>
        {
            Check(x == 123 && z == -456, "a point footprint never leaves its single location");
            return 4;
        }), "a zero-area footprint is supported");
        Near(point - 2, 4 + SpawnClearance.Gap, "point support retains the bottom offset");

        InvalidGeometry();
        BoundedQueries();
        Console.WriteLine("SPAWN_CLEARANCE_TESTS_OK: full footprint, slopes, boundaries, world coordinates, missing terrain, finite geometry, bounded queries");
    }

    static void InvalidGeometry()
    {
        var invalid = new List<float[]>
        {
            new[] { 2f, -2, -3, 3, -1 },
            new[] { -2f, 2, 3, -3, -1 },
        };
        foreach (float nonfinite in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            for (int position = 0; position < 5; position++)
            {
                var bounds = new[] { -2f, 2, -3, 3, -1 };
                bounds[position] = nonfinite;
                invalid.Add(bounds);
            }
        foreach (var bounds in invalid)
        {
            int calls = 0;
            var result = SpawnClearance.RootHeight(bounds[0], bounds[1], bounds[2], bounds[3], bounds[4], (_, _) => { calls++; return 1; });
            Check(result == null && calls == 0, "invalid bounds/bottom are rejected before querying terrain");
        }

        // Finite endpoints can still overflow float subtraction. Never send a NaN coordinate into Unity physics.
        var extreme = SpawnClearance.RootHeight(-float.MaxValue, float.MaxValue, -1, 1, 0, (x, z) =>
        {
            Check(float.IsFinite(x) && float.IsFinite(z), "extreme finite geometry never queries nonfinite coordinates");
            return 0;
        });
        Check(extreme == null || float.IsFinite(extreme.Value), "extreme finite geometry returns unknown or a finite height");
        Check(SpawnClearance.RootHeight(-1, 1, -1, 1, -float.MaxValue, (_, _) => float.MaxValue) == null,
            "a root height that overflows is rejected rather than passed to a vehicle transform");
    }

    static void BoundedQueries()
    {
        int calls = 0;
        var corners = new HashSet<(float X, float Z)>();
        var result = SpawnClearance.RootHeight(-1_000_000, 1_000_000, -2_000_000, 2_000_000, -1, (x, z) =>
        {
            calls++;
            Check(float.IsFinite(x) && float.IsFinite(z), "large footprint samples stay finite");
            Check(x >= -1_000_000 && x <= 1_000_000 && z >= -2_000_000 && z <= 2_000_000, "queries stay inside the footprint");
            if (Math.Abs(x) == 1_000_000 && Math.Abs(z) == 2_000_000) corners.Add((x, z));
            return 3;
        });
        Check(calls > 0 && calls <= 300, "query work stays bounded for oversized imported designs");
        Check(corners.Count == 4, "a capped sampling budget still covers every corner");
        Near(Height(result, "large footprint result") - 1, 3 + SpawnClearance.Gap, "large footprint retains flat-ground clearance");
    }
}

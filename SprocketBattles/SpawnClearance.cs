namespace SprocketBattles;

/// Terrain support for a projected solid footprint. Independent of Unity so slope placement can be checked offline.
internal static class SpawnClearance
{
    internal const float Gap = 0.35f;

    internal static float? RootHeight(float minX, float maxX, float minZ, float maxZ, float bottom,
                                     Func<float, float, float?> ground)
    {
        if (!float.IsFinite(minX) || !float.IsFinite(maxX) || !float.IsFinite(minZ) || !float.IsFinite(maxZ)
            || !float.IsFinite(bottom) || maxX < minX || maxZ < minZ) return null;
        // At most 17 x 17 queries, including both boundaries, even for an unusually large design.
        double width = (double)maxX - minX, length = (double)maxZ - minZ;
        int nx = Steps(width), nz = Steps(length);
        float? support = null;
        for (int x = 0; x <= nx; x++)
            for (int z = 0; z <= nz; z++)
            {
                float px = (float)(minX + width * x / nx);
                float pz = (float)(minZ + length * z / nz);
                if (ground(px, pz) is { } height && float.IsFinite(height))
                    support = support is { } old ? Math.Max(old, height) : height;
            }
        if (support is not { } top) return null;
        float result = (float)((double)top - bottom + Gap);
        return float.IsFinite(result) ? result : null;
    }

    static int Steps(double span) => span >= 32 ? 16 : Math.Max(1, (int)Math.Ceiling(span / 2));
}

using SprocketMaps;

static class CustomMapCapacityTests
{
    static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
    static void Reject(Action read, string why)
    {
        bool rejected = false;
        try { read(); } catch { rejected = true; }
        Check(rejected, why);
    }
    internal static void Run()
    {
        Check(CustomMapManifest.Capacity(null) == 16, "existing bundles retain their default capacity");
        Check(CustomMapManifest.Capacity("{\"schemaVersion\":1,\"spawnCount\":4}") == 4,
            "map listing uses the authored capacity");
        foreach (var invalid in new[] { "{}", "[]", "{\"schemaVersion\":2,\"spawnCount\":4}",
            "{\"schemaVersion\":1,\"spawnCount\":0}", "{\"schemaVersion\":1,\"spawnCount\":17}",
            "{\"schemaVersion\":1,\"spawnCount\":4.5}", "{\"schemaVersion\":1,\"spawnCount\":\"4\"}" })
            Reject(() => CustomMapManifest.Capacity(invalid), "invalid metadata must not overstate usable slots");
        Check(SpawnFormation.Offset(1, 0, 8) == (0, 0) && SpawnFormation.FrontDistance(1, 8) == 0,
            "one tank is placed on the checked center");
        Check(SpawnFormation.Offset(4, 0, 8) == (-4, 0) && SpawnFormation.Offset(4, 3, 8) == (4, 8)
            && SpawnFormation.FrontDistance(4, 8) == 4, "four tank formation is compact around its authored center");
        Check(SpawnFormation.Offset(16, 0, 15) == (-22.5f, 0)
            && SpawnFormation.Offset(16, 15, 15) == (22.5f, 45), "native 4x4 placement remains identical");
        for (int count = 1; count <= 16; count++)
        {
            var points = Enumerable.Range(0, count).Select(i => SpawnFormation.Offset(count, i, 8)).ToArray();
            Check(points.Distinct().Count() == count, "every offered slot has its own checked position");
            for (int i = 0; i < count; i++)
                for (int j = i + 1; j < count; j++)
                    Check(Math.Pow(points[i].Side - points[j].Side, 2) + Math.Pow(points[i].Back - points[j].Back, 2) >= 64,
                        "neighboring tanks retain eight metres spacing");
        }
        Reject(() => SpawnFormation.Offset(4, 4, 8), "a native locator cannot consume unchecked extra slots");
        Console.WriteLine("CUSTOM_MAP_CAPACITY_TESTS_OK: metadata bounds, compact spacing, checked slot limits, native layout");
    }
}

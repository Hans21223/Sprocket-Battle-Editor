using SprocketMaps;

static class MapOwnershipTests
{
    static void Check(bool value, string why)
    { if (!value) throw new Exception("map ownership: " + why); }

    internal static void Run()
    {
        var unsupported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Sandbox", "Sandbox (Low performance)", "The Crossroad", "TheCrossroad", "Railway", "demo_city_night" };
        Check(MapOwnership.IsNativeMap("Sandbox") && MapOwnership.IsNativeMap("Sandbox (Low performance)"),
            "equal config and scene names do not identify a bundled map");
        Check(!MapOwnership.Quarantined("Sandbox", unsupported)
            && !MapOwnership.Quarantined("Sandbox (Low performance)", unsupported)
            && !MapOwnership.Quarantined("Railway", unsupported)
            && !MapOwnership.Quarantined("TheCrossroad", unsupported),
            "bundle file collisions cannot hide native maps or their config aliases");
        Check(MapOwnership.Quarantined("DEMO_CITY_NIGHT", unsupported), "unsupported bundle listings are excluded ignoring case");
        Check(!MapOwnership.Quarantined("Unknown native config", unsupported) && !MapOwnership.Quarantined(null, unsupported),
            "discovery cannot hide unrelated game or extension entries");
        Check(MapOwnership.CanBuildNativeScene("Sandbox") && MapOwnership.CanBuildNativeScene("the crossroad")
            && !MapOwnership.CanBuildNativeScene("demo_city_night") && !MapOwnership.CanBuildNativeScene("Railway"),
            "setup generation is restricted to its explicit native scene allowlist");
        Check(MapOwnership.BundleName("demo_city_night.bundle") == "demo_city_night"
            && MapOwnership.BundleName("CITY.UNITY3D") == "CITY"
            && MapOwnership.BundleName("demo_city_night.dll") == null && MapOwnership.BundleName(".bundle") == null,
            "only real bundle file extensions enter the discovery list");
        Console.WriteLine("MAP_OWNERSHIP_TESTS_OK: native maps, bundle collisions, quarantine, setup boundaries");
    }
}

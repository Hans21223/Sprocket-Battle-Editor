namespace SprocketMaps;

/// Native map identity is explicit. Matching a config identifier to its scene name does not make it an AssetBundle.
/// These names belong to the supported Sprocket 0.2.55.5 build and must never be claimed by a discovered bundle.
internal static class MapOwnership
{
    static readonly HashSet<string> NativeMaps = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ambush", "Defence", "Dunes", "Fields", "No Tank's Land", "Railway", "Taiga",
        "SilentBorder", "Silent Border", "TheCrossroad", "The Crossroad", "Sandbox", "Sandbox (Low performance)",
    };

    static readonly HashSet<string> GeneratedNativeScenes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Silent Border", "The Crossroad", "Sandbox", "Sandbox (Low performance)",
    };

    internal static bool IsNativeMap(string? identifier) => identifier != null && NativeMaps.Contains(identifier);
    internal static bool CanBuildNativeScene(string? scene) => scene != null && GeneratedNativeScenes.Contains(scene);

    internal static string? BundleName(string file)
    {
        var extension = Path.GetExtension(file);
        if (!string.Equals(extension, ".bundle", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".unity3d", StringComparison.OrdinalIgnoreCase)) return null;
        var name = Path.GetFileNameWithoutExtension(file);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    internal static bool Quarantined(string? identifier, ISet<string> unavailableBundles) =>
        identifier != null && !IsNativeMap(identifier) && unavailableBundles.Contains(identifier);
}
